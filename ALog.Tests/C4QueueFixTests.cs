// C4 queue contract: drop on overflow, deadline on shutdown, one failure does not end a batch.
// The clock is injected. Waits are bounded. Next file: ALog/Core/BackgroundLogQueue.cs.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// The C4 queue contract. Overflow drops, shutdown reports the remainder, and a batch continues after a throw.
/// </summary>
public sealed partial class C4QueueFixTests
{
    private const string DropMarker = " events dropped: the background queue is full (capacity ";
    private const string DeadlinePrefix = "Shutdown deadline of 5 s reached: ";
    private const string DeadlineSuffix = " queued events were not written. A writer is too slow to drain the queue in time; check the target or reduce the log volume before shutdown.";

    /// <summary>
    /// Capacity 100 and 1000 writes drop 900 immediately, then one ALOG0070 line carries 900.
    /// The 10 ms bound is measured after the first call. Flush while the writer is blocked stays silent: no ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after flush and dispose.</returns>
    [Fact(Timeout = 20000)]
    public async Task FullQueue_DropsImmediately_ReportsAlog0070_FlushStaysSilent()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            100,
            1,
            TimeSpan.FromSeconds(30),
            clock);
        try
        {
            var slowest = TimeSpan.Zero;
            for (var i = 0; i < 1000; i++)
            {
                var started = Stopwatch.GetTimestamp();
                queue.TryEnqueue(Event("n-" + i.ToString(CultureInfo.InvariantCulture)));
                // C4-01: the 10 ms limit is warm. The first call in the process may pay for JIT.
                if (i == 0)
                    continue;

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed > slowest)
                    slowest = elapsed;
            }

            Assert.True(slowest < TimeSpan.FromMilliseconds(10), "slowest " + slowest.ToString());
            Assert.Equal(100, queue.AcceptedCount);
            Assert.Equal(900, queue.FullDropCount);
            Assert.Equal(0, queue.ClosedDropCount);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            string report;
            lock (lines)
                report = Assert.Single(lines);
            Assert.Equal(
                "900" + DropMarker + "100). The writers are slower than your log rate. Lower the log level or check the slowest writer.",
                report);

            var flushStarted = Stopwatch.StartNew();
            await queue.FlushAsync();
            Assert.True(flushStarted.Elapsed < TimeSpan.FromMilliseconds(5200), "flush " + flushStarted.Elapsed.ToString());
            lock (lines)
                Assert.DoesNotContain(lines, line => line.Contains(DeadlinePrefix, StringComparison.Ordinal));

            await CoverQueueBounds();
        }
        finally
        {
            writer.Release();
            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Each WriteAsync on a full queue is already complete when the call returns.
    /// The 10 ms bound starts at the second call.
    /// </summary>
    /// <returns>A task that finishes after the overflow calls.</returns>
    [Fact(Timeout = 15000)]
    public async Task WriteAsync_FullQueue_CompletesWithin10ms()
    {
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            capacity: 100,
            batchSize: 1,
            flushInterval: TimeSpan.FromSeconds(30),
            asyncEnabled: false);
        try
        {
            for (var i = 0; i < 1000; i++)
            {
                var started = Stopwatch.GetTimestamp();
                var pending = queue.EnqueueAsync(Event("a-" + i.ToString(CultureInfo.InvariantCulture)));
                // C4-01: the 10 ms limit is warm. The first call in the process may pay for JIT.
                if (i > 0)
                    Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(10));

                Assert.True(pending.IsCompletedSuccessfully);
            }

            Assert.Equal(100, queue.AcceptedCount);
            Assert.Equal(900, queue.FullDropCount);
        }
        finally
        {
            writer.Release();
            queue.Dispose();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 32 threads dropping 10,000 events each produce one line per 60 s window, and the counts sum to the counter.
    /// A token that is already cancelled is neither accepted nor dropped and writes no line.
    /// </summary>
    /// <returns>A task that finishes after the second window is advanced.</returns>
    [Fact(Timeout = 20000)]
    public async Task Throttle_SumsReportedCounts_AndCancelledEntryIsNeither()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            1,
            1,
            TimeSpan.FromHours(1),
            clock);
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var skipped = queue.EnqueueAsync(Event("skip"), cancelled.Token);
            Assert.True(skipped.IsCanceled);
            Assert.Equal(0, queue.AcceptedCount);
            Assert.Equal(0, queue.FullDropCount);
            Assert.Equal(0, queue.ClosedDropCount);

            Assert.True(queue.TryEnqueue(Event("prime")));
            var acceptedByMistake = 0;
            Parallel.For(0, 32, _ =>
            {
                for (var i = 0; i < 10_000; i++)
                {
                    if (queue.TryEnqueue(Event("d")))
                        Interlocked.Increment(ref acceptedByMistake);
                }
            });

            Assert.Equal(0, acceptedByMistake);
            Assert.Equal(320_000, queue.FullDropCount);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            lock (lines)
                Assert.Equal("320000" + DropMarker + "1). The writers are slower than your log rate. Lower the log level or check the slowest writer.", Assert.Single(lines));

            for (var i = 0; i < 50; i++)
                Assert.False(queue.TryEnqueue(Event("later")));

            clock.Advance(TimeSpan.FromSeconds(60));
            long reported;
            lock (lines)
            {
                Assert.Equal(2, lines.Count);
                reported = SumDropCounts(lines);
            }

            Assert.Equal(queue.FullDropCount, reported);
            Assert.Equal(320_050, reported);
            await CoverReportEdges();
        }
        finally
        {
            writer.Release();
            queue.Dispose();
            SelfLog.Target = previous;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// A 10 ms writer and 1,000 queued events return inside 5.2 s with one ALOG0071 line.
    /// Written plus the reported remainder is 1,000. A fast writer of 100,000 events reports nothing.
    /// Flush after dispose does not add a line. A log after dispose adds one ALOG0072 and no second ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after both engines shut down.</returns>
    [Fact(Timeout = 60000)]
    public async Task Shutdown_SlowWriter_ReportsRemainder_FastWriter_ReportsNothing()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var slow = new DelayWriter(TimeSpan.FromMilliseconds(10));
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(slow)
            .UseBackgroundQueue(true, 1000, 10, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 1000; i++)
                engine.Log("e-" + i.ToString(CultureInfo.InvariantCulture), LogLevel.Info);

            var started = Stopwatch.StartNew();
            engine.Dispose();
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            string report;
            lock (lines)
                report = Assert.Single(lines);
            var unwritten = ParseUnwritten(report);
            Assert.Equal(
                DeadlinePrefix + unwritten.ToString(CultureInfo.InvariantCulture) + DeadlineSuffix,
                report);
            Assert.Equal(1000, slow.Finished + unwritten);
            Assert.Equal(0, slow.WritesAfterDispose);
            Assert.True(slow.Disposed);
            Assert.Equal(engine.Queue!.AcceptedCount, engine.Queue.WrittenCount + engine.Queue.UnwrittenCount);

            await engine.FlushAsync();
            engine.Dispose();
            engine.Log("after-shutdown", LogLevel.Info);
            lock (lines)
            {
                Assert.Equal(2, lines.Count);
                Assert.StartsWith(DeadlinePrefix, lines[0], StringComparison.Ordinal);
                Assert.Equal(ClosedLine, lines[1]);
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        var fastLines = new List<string>();
        SelfLog.Enable(fastLines.Add);
        var fast = new RecordingWriter();
        var fastEngine = new LoggerEngine(new LoggerConfig()
            .AddWriter(fast)
            .UseBackgroundQueue(true, 100_000, 50, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 100_000; i++)
                fastEngine.Log("f", LogLevel.Info);

            fastEngine.Dispose();
            Assert.Equal(100_000, fast.Events.Count);
            Assert.Empty(fastLines);
            Assert.True(fast.Disposed);
            Assert.Equal(0, fastEngine.Queue!.UnwrittenCount);
        }
        finally
        {
            SelfLog.Target = previous;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Eight threads writing during shutdown get no exception. Events after the close are counted.
    /// A writer still inside its call is not disposed, and a later write does not reach it.
    /// </summary>
    /// <returns>A task that finishes after shutdown returns.</returns>
    [Fact(Timeout = 20000)]
    public async Task Shutdown_EightWriters_DropsLateEvents_AndDoesNotDisposeARunningCall()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new BlockingWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 100, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            Assert.True(engine.Queue!.TryEnqueue(Event("prime")));
            Assert.True(await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(5)));
            var errors = new List<Exception>();
            var dispose = Task.Run(() => engine.Dispose());
            Assert.True(await WaitUntil(() =>
            {
                engine.Queue.TryEnqueue(Event("probe"));
                return engine.Queue.ClosedDropCount > 0;
            }, TimeSpan.FromSeconds(2)));
            var workers = new Task[8];
            for (var t = 0; t < workers.Length; t++)
            {
                workers[t] = Task.Run(() =>
                {
                    try
                    {
                        for (var n = 0; n < 50; n++)
                            engine.Log("late-" + n.ToString(CultureInfo.InvariantCulture), LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                            errors.Add(ex);
                    }
                });
            }

            await Task.WhenAll(workers);
            await dispose.WaitAsync(TimeSpan.FromSeconds(8));
            lock (errors)
                Assert.Empty(errors);

            var queue = engine.Queue;
            Assert.Equal(queue.AcceptedCount, queue.WrittenCount + queue.UnwrittenCount);
            Assert.True(queue.ClosedDropCount > 0);
            Assert.False(writer.Disposed);
            var calls = writer.EnteredCount;
            engine.Log("after", LogLevel.Info);
            Assert.Equal(calls, writer.EnteredCount);
            Assert.True(queue.ClosedDropCount > 0);
            lock (lines)
                Assert.Single(lines, line => line.StartsWith(DeadlinePrefix, StringComparison.Ordinal));
        }
        finally
        {
            writer.Release();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Shutdown twice in a row, and twice in parallel, does not throw, reports ALOG0071 at most once,
    /// reports ALOG0072 once for a later write, and does not write after the writer is disposed.
    /// </summary>
    /// <returns>A task that finishes after the parallel shutdown.</returns>
    [Fact(Timeout = 30000)]
    public async Task Shutdown_TwiceSequentialAndParallel_ThenWrite_NoException()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var slow = new DelayWriter(TimeSpan.FromMilliseconds(10));
        var sequential = new LoggerEngine(new LoggerConfig()
            .AddWriter(slow)
            .UseBackgroundQueue(true, 1000, 10, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 1000; i++)
                sequential.Log("s", LogLevel.Info);

            sequential.Dispose();
            sequential.Dispose();
            sequential.Log("after", LogLevel.Info);
            Assert.Equal(0, slow.WritesAfterDispose);
            lock (lines)
            {
                Assert.Equal(2, lines.Count);
                Assert.StartsWith(DeadlinePrefix, lines[0], StringComparison.Ordinal);
                Assert.Equal(ClosedLine, lines[1]);
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var hanging = new ForeverWriter();
        var parallel = new LoggerEngine(new LoggerConfig()
            .AddWriter(hanging)
            .UseBackgroundQueue(true, 8, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            parallel.Log("hang", LogLevel.Info);
            Assert.True(hanging.Entered.Wait(TimeSpan.FromSeconds(5)));
            var started = Stopwatch.StartNew();
            var first = Task.Run(() => parallel.Dispose());
            var second = Task.Run(() => parallel.Dispose());
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            parallel.Log("after", LogLevel.Info);
            Assert.False(hanging.Disposed);
            lock (lines)
            {
                Assert.Equal(2, lines.Count);
                Assert.StartsWith(DeadlinePrefix, lines[0], StringComparison.Ordinal);
                Assert.Equal(ClosedLine, lines[1]);
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// A writer that throws on event 3 still receives events 4 through 10. The other writer receives all 10.
    /// </summary>
    /// <returns>A task that finishes after the batch.</returns>
    [Fact(Timeout = 15000)]
    public async Task Batch_ThrowOnEvent3_ContinuesAndSecondWriterGetsAll()
    {
        var error = new StringWriter();
        var previous = Console.Error;
        Console.SetError(error);
        try
        {
            var throwing = new ThrowOnThirdWriter();
            var other = new RecordingWriter();
            var dispatcher = new LogDispatcher(new ILogWriter[] { throwing, other }, asyncEnabled: true);
            var batch = new List<LogEvent>(10);
            for (var i = 1; i <= 10; i++)
                batch.Add(Event("n-" + i.ToString(CultureInfo.InvariantCulture)));

            await dispatcher.DispatchBatchAsync(batch);

            Assert.Equal(10, throwing.Seen.Count);
            Assert.Equal(10, other.Events.Count);
            Assert.Contains("[ALog Dispatcher] Batch write failed for ThrowOnThirdWriter:", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>
    /// A writer blocked forever lets shutdown return inside 5.2 s. The writer is not disposed.
    /// The remainder is one ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes after shutdown.</returns>
    [Fact(Timeout = 20000)]
    public async Task HangingWriter_ShutdownReturns_AndDoesNotDispose()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        var writer = new ForeverWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 10, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 5; i++)
                engine.Log("h-" + i.ToString(CultureInfo.InvariantCulture), LogLevel.Info);

            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(5)));
            var started = Stopwatch.StartNew();
            engine.Dispose();
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            Assert.False(writer.Disposed);
            Assert.False(engine.Queue!.ReleaseWriters);
            var report = Assert.Single(lines);
            var unwritten = ParseUnwritten(report);
            Assert.Equal(DeadlinePrefix + unwritten.ToString(CultureInfo.InvariantCulture) + DeadlineSuffix, report);
            Assert.True(unwritten >= 1);
            Assert.Equal(engine.Queue.AcceptedCount, engine.Queue.WrittenCount + unwritten);
        }
        finally
        {
            SelfLog.Target = previous;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// A child process with a hanging writer returns from Main and the process ends.
    /// The writer is not disposed, and stdout contains one ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes when the child exits or the time limit kills it.</returns>
    [Fact(Timeout = 40000)]
    public async Task HangingWriter_ChildProcess_ExitsAfterShutdown()
    {
        var root = FindRepoRoot();
        var config = typeof(C4QueueFixTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        Assert.False(string.IsNullOrEmpty(config));

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run --project eng/c4-hang-probe/ALog.HangProbe.csproj -c " + config + " --no-build --nologo",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(20_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(exited, "probe did not exit. stdout=" + stdout + " stderr=" + stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("MAIN_RETURN", stdout, StringComparison.Ordinal);
        Assert.Contains(DeadlinePrefix, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPOSED", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hits the default interval, a zero batch, a batch above the read cap, an idle timer tick,
    /// and a flush interval the timer rejects. Each queue is disposed inside the deadline.
    /// </summary>
    /// <returns>A task that finishes after the queues drain.</returns>
    private static async Task CoverQueueBounds()
    {
        var defaults = new RecordingWriter();
        var defaultQueue = new BackgroundLogQueue(new ILogWriter[] { defaults });
        Assert.True(defaultQueue.TryEnqueue(Event("default")));
        await defaultQueue.FlushAsync();
        defaultQueue.Dispose();
        Assert.Single(defaults.Events);

        var small = new RecordingWriter();
        using (var zeroBatch = new BackgroundLogQueue(
            new ILogWriter[] { small },
            4,
            0,
            TimeSpan.FromMilliseconds(20),
            false))
        {
            Assert.True(zeroBatch.TryEnqueue(Event("zero")));
            await zeroBatch.FlushAsync();
        }

        Assert.Single(small.Events);

        var wide = new RecordingWriter();
        using (var wideQueue = new BackgroundLogQueue(
            new ILogWriter[] { wide },
            4,
            100_001,
            TimeSpan.FromMilliseconds(20),
            false))
        {
            Assert.True(wideQueue.TryEnqueue(Event("wide")));
            await wideQueue.FlushAsync();
        }

        Assert.Single(wide.Events);

        var idle = new RecordingWriter();
        var ticking = new BackgroundLogQueue(
            new ILogWriter[] { idle },
            4,
            1,
            TimeSpan.FromMilliseconds(15),
            false);
        await Task.Delay(40);
        Assert.True(ticking.TryEnqueue(Event("tick")));
        await ticking.FlushAsync();
        ticking.Dispose();
        Assert.Single(idle.Events);

        var broken = new RecordingWriter();
        var badInterval = new BackgroundLogQueue(
            new ILogWriter[] { broken },
            4,
            1,
            TimeSpan.Zero,
            false);
        badInterval.Dispose();
        Assert.Empty(broken.Events);
    }

    /// <summary>
    /// A drop inside the report callback is included in the next window.
    /// A drop in the last fraction of a millisecond of that window still arms a report.
    /// </summary>
    /// <returns>A task that finishes after the clock is advanced.</returns>
    private static async Task CoverReportEdges()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var lines = new List<string>();
        var previous = SelfLog.Target;
        var writer = new BlockingWriter();
        BackgroundLogQueue? queue = null;
        var reentered = 0;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
            if (reentered == 0 && line.Contains("events dropped:", StringComparison.Ordinal))
            {
                reentered++;
                queue!.TryEnqueue(Event("during-report"));
            }
        });
        try
        {
            queue = new BackgroundLogQueue(new ILogWriter[] { writer }, 1, 1, TimeSpan.FromHours(1), clock);
            Assert.True(queue.TryEnqueue(Event("prime")));
            Assert.False(queue.TryEnqueue(Event("drop")));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            clock.Advance(TimeSpan.FromSeconds(60));
            var near = TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2);
            clock.Advance(near);
            Assert.False(queue.TryEnqueue(Event("near-window")));
            clock.Advance(TimeSpan.FromMilliseconds(2));
            lock (lines)
                Assert.Equal(queue.FullDropCount, SumDropCounts(lines));
            Assert.Equal(3, queue.FullDropCount);
        }
        finally
        {
            writer.Release();
            queue?.Dispose();
            SelfLog.Target = previous;
        }

        await Task.CompletedTask;
    }

    /// <summary>Builds an info event. The timestamp is fixed so tests do not depend on the clock.</summary>
    /// <param name="message">Rendered message. Not null.</param>
    /// <returns>An event with no exception.</returns>
    private static LogEvent Event(string message)
        => new(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>
    /// Polls <paramref name="ready"/> until it is true or <paramref name="budget"/> passes.
    /// </summary>
    /// <param name="ready">Condition. Called at most 500 times. Must not throw.</param>
    /// <param name="budget">Upper bound. The loop also stops after 500 tries.</param>
    /// <returns>True when the condition was seen. False when the budget won.</returns>
    private static async Task<bool> WaitUntil(Func<bool> ready, TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 500 && Stopwatch.GetElapsedTime(started) < budget; i++)
        {
            if (ready())
                return true;

            await Task.Delay(10);
        }

        return ready();
    }

    /// <summary>Reads the {N} from an ALOG0071 line and checks the fixed sentence around it.</summary>
    /// <param name="line">The whole SelfLog line. Not null.</param>
    /// <returns>The unwritten count.</returns>
    private static long ParseUnwritten(string line)
    {
        Assert.StartsWith(DeadlinePrefix, line, StringComparison.Ordinal);
        Assert.EndsWith(DeadlineSuffix, line, StringComparison.Ordinal);
        var number = line.AsSpan(DeadlinePrefix.Length, line.Length - DeadlinePrefix.Length - DeadlineSuffix.Length);
        return long.Parse(number, CultureInfo.InvariantCulture);
    }

    /// <summary>Adds the leading counts of ALOG0070 lines.</summary>
    /// <param name="lines">Captured lines. Not null.</param>
    /// <returns>The sum. Zero when none match.</returns>
    private static long SumDropCounts(List<string> lines)
    {
        long sum = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var at = lines[i].IndexOf(DropMarker, StringComparison.Ordinal);
            if (at <= 0)
                continue;

            sum += long.Parse(lines[i].AsSpan(0, at), CultureInfo.InvariantCulture);
        }

        return sum;
    }

    /// <summary>Walks up from the test output directory until <c>ALog.sln</c> is found.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The walk passed 12 parents without finding the solution.</exception>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found above the test output.");
    }

    /// <summary>Sleeps a fixed amount on every write, and counts calls that happen after Dispose.</summary>
    private sealed class DelayWriter : ILogWriter
    {
        private readonly TimeSpan _delay;
        private int _finished;
        private int _afterDispose;
        private int _disposed;

        /// <summary>Creates a writer whose every call waits <paramref name="delay"/>.</summary>
        /// <param name="delay">How long each write blocks. Ten milliseconds in the shutdown test.</param>
        internal DelayWriter(TimeSpan delay) => _delay = delay;

        /// <summary>Writes that returned, including any that returned after Dispose.</summary>
        internal int Finished => Volatile.Read(ref _finished);

        /// <summary>Writes that observed Dispose before they returned.</summary>
        internal int WritesAfterDispose => Volatile.Read(ref _afterDispose);

        /// <summary>True after Dispose.</summary>
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>Blocks, then records the call. A call that sees Dispose increments the after-dispose count.</summary>
        /// <param name="logEvent">Ignored.</param>
        public void Write(LogEvent logEvent)
        {
            Thread.Sleep(_delay);
            if (Volatile.Read(ref _disposed) != 0)
                Interlocked.Increment(ref _afterDispose);

            Interlocked.Increment(ref _finished);
        }

        /// <summary>Same wait as <see cref="Write"/>.</summary>
        /// <param name="logEvent">Ignored.</param>
        /// <returns>A completed task.</returns>
        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Marks the writer disposed. Does not unblock a call that is already inside <see cref="Write"/>.</summary>
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>Blocks in the first write until the process ends. Dispose only sets a flag.</summary>
    private sealed class ForeverWriter : ILogWriter
    {
        private int _disposed;

        /// <summary>Set once <see cref="Write"/> has been entered.</summary>
        internal ManualResetEventSlim Entered { get; } = new(false);

        /// <summary>True if Dispose ran. The C4 hang contract says this stays false.</summary>
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Blocks the caller. The bound is the test process, which is why the child-process test exists.
        /// </summary>
        /// <param name="logEvent">Ignored.</param>
        public void Write(LogEvent logEvent)
        {
            Entered.Set();
            Thread.Sleep(Timeout.Infinite);
        }

        /// <summary>Same block as <see cref="Write"/>.</summary>
        /// <param name="logEvent">Ignored.</param>
        /// <returns>A task that does not complete.</returns>
        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Records dispose. Does not release the blocked write.</summary>
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>Throws on the third write and records every call, including the ones after the throw.</summary>
    private sealed class ThrowOnThirdWriter : ILogWriter
    {
        /// <summary>Messages seen, in order, including the one that threw.</summary>
        internal List<string> Seen { get; } = new();

        /// <summary>Records the message and throws when the third call arrives.</summary>
        /// <param name="logEvent">Event whose message is stored. Not null in these tests.</param>
        public void Write(LogEvent logEvent)
        {
            Seen.Add(logEvent.Message);
            if (Seen.Count == 3)
                throw new InvalidOperationException("event-3");
        }

        /// <summary>Same as <see cref="Write"/>, as a task.</summary>
        /// <param name="logEvent">Passed to <see cref="Write"/>.</param>
        /// <returns>A completed task, or a faulted one when <see cref="Write"/> throws.</returns>
        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Nothing to release.</summary>
        public void Dispose()
        {
        }
    }
}
