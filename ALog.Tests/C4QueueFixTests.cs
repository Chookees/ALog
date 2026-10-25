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
    /// Call index inside <see cref="SampleFullQueueSyncEnqueue"/>.
    /// The cumulative hang guard reads it from the test thread while the loop runs on a pool thread.
    /// </summary>
    private int _fullQueueSyncCall;

    /// <summary>
    /// Call index inside <see cref="EnqueueWarmCallsAlreadyComplete"/>.
    /// The cumulative hang guard reads it from the test thread while the loop runs on a pool thread.
    /// </summary>
    private int _fullQueueAsyncCall;

    /// <summary>
    /// Capacity 100 and 1000 writes drop 900 immediately, then one ALOG0070 line carries 900.
    /// Write returns while the blocked writer has finished nothing.
    /// After the first call, the median warm call and at least 990 of the 999 warm calls are under 10 ms, and no call reaches the 30 s hang guard.
    /// The 1000 calls share one 30 s hang guard. Flush is completed by releasing the blocked writer, and it writes no ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after flush and dispose.</returns>
    /// <remarks>
    /// An assertion failure fails the test. The finally block still releases the writer and the queue.
    /// SelfLog is restored to the previous target. Not safe to run beside another test that replaces SelfLog.
    /// The fact timeout does not interrupt a synchronous loop on the test thread, so the loop is a task.
    /// On timeout the message is the test name and <see cref="_fullQueueSyncCall"/>.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task FullQueue_DropsWithoutWaiting_ReportsAlog0070_FlushStaysSilent()
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
            // One cumulative hang guard for all 1000 calls. A per-call 5 s block stays under the
            // per-call line and would otherwise run until the job timeout.
            var warm = new TimeSpan[999];
            Volatile.Write(ref _fullQueueSyncCall, 0);
            var enqueueLoop = Task.Run(() => SampleFullQueueSyncEnqueue(queue, warm));
            var enqueueFinished = await Task.WhenAny(enqueueLoop, Task.Delay(TimeSpan.FromSeconds(30))) == enqueueLoop;
            Assert.True(
                enqueueFinished,
                nameof(FullQueue_DropsWithoutWaiting_ReportsAlog0070_FlushStaysSilent)
                + " hung at call "
                + Volatile.Read(ref _fullQueueSyncCall).ToString(CultureInfo.InvariantCulture));
            await enqueueLoop;
            Assert.Empty(writer.Events);
            AssertWarmEnqueueSamplesStayUnderTenMilliseconds(warm);
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

            await AssertFlushCompletesWhenWriterIsReleased(queue, writer, lines);
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
    /// The 999 WriteAsync calls after the first on a full queue return a task that is already successful.
    /// The 1000 calls share one 30 s hang guard. No call is timed on its own.
    /// </summary>
    /// <returns>A task that finishes after the overflow calls.</returns>
    /// <remarks>
    /// Completion is the state of the returned task, read on the same statement as the call.
    /// The first call is outside the 999 because it starts the processor. It is still inside the hang guard.
    /// The finally block releases the writer and disposes the queue. Not safe to run two copies at once.
    /// The fact limit is 90 seconds so the hang guard can fail the loop before xUnit stops the test.
    /// On timeout the message is the test name and <see cref="_fullQueueAsyncCall"/>.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task WriteAsync_FullQueue_CompletesSynchronously()
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
            Volatile.Write(ref _fullQueueAsyncCall, 0);
            var enqueueLoop = Task.Run(() => EnqueueWarmCallsAlreadyComplete(queue));
            var enqueueFinished = await Task.WhenAny(enqueueLoop, Task.Delay(TimeSpan.FromSeconds(30))) == enqueueLoop;
            Assert.True(
                enqueueFinished,
                nameof(WriteAsync_FullQueue_CompletesSynchronously)
                + " hung at call "
                + Volatile.Read(ref _fullQueueAsyncCall).ToString(CultureInfo.InvariantCulture));
            await enqueueLoop;
            Assert.Equal(100, queue.AcceptedCount);
            Assert.Equal(900, queue.FullDropCount);
        }
        finally
        {
            writer.Release();
            queue.Dispose();
        }
    }

    /// <summary>
    /// Enqueues 1000 events and checks that each call after the first is already successful.
    /// </summary>
    /// <param name="queue">Queue whose writer is blocked. Not null. Not disposed here.</param>
    /// <remarks>
    /// Writes <see cref="_fullQueueAsyncCall"/> before each call so the cumulative hang guard can name the call it is inside.
    /// The first call is not one of the 999. Nothing in this method reads a clock.
    /// Not safe to run two copies at once. The pool thread writes the call index; the test thread only reads it.
    /// </remarks>
    private void EnqueueWarmCallsAlreadyComplete(BackgroundLogQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var checkedCalls = 0;
        for (var i = 0; i < 1000; i++)
        {
            Volatile.Write(ref _fullQueueAsyncCall, i);
            var pending = queue.EnqueueAsync(Event("a-" + i.ToString(CultureInfo.InvariantCulture)));
            if (i == 0)
                continue;

            Assert.True(
                pending.IsCompletedSuccessfully,
                "call " + i.ToString(CultureInfo.InvariantCulture) + " is still running");
            checkedCalls++;
        }

        Assert.Equal(999, checkedCalls);
    }

    /// <summary>
    /// 32 threads dropping 10,000 events each produce one line per 60 s window, and the counts sum to the counter.
    /// A token that is already cancelled is neither accepted nor dropped and writes no line.
    /// </summary>
    /// <returns>A task that finishes after the second window is advanced.</returns>
    /// <remarks>
    /// The processor is inside the writer before the clock moves, so the report timer is not armed during startup.
    /// Each advance also wakes an overdue timer that lost that race. The counts are not relaxed.
    /// </remarks>
    [Fact(Timeout = 30000)]
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
            await WaitUntilWriterEntered(writer);
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
            AdvanceReports(clock, TimeSpan.FromMilliseconds(1));
            lock (lines)
                Assert.Equal("320000" + DropMarker + "1). The writers are slower than your log rate. Lower the log level or check the slowest writer.", Assert.Single(lines));

            for (var i = 0; i < 50; i++)
                Assert.False(queue.TryEnqueue(Event("later")));

            AdvanceReports(clock, TimeSpan.FromSeconds(60));
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
    /// A writer held on a gate, and 1,000 queued events, report one ALOG0071 line.
    /// One tick before <see cref="BackgroundLogQueue.NewWriteBudget"/> the cutoff is still open and dispose has not returned.
    /// At that budget the next call is blocked, the in-flight call finishes, and dispose returns without the rest of the deadline.
    /// Written plus the reported remainder is 1,000. A fast writer of 100,000 events reports nothing and does not move the clock.
    /// Flush after dispose does not add a line. A log after dispose adds one ALOG0072 and no second ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after both engines shut down.</returns>
    /// <remarks>
    /// The clock is <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.
    /// The finally block releases the gate so a failed assert cannot leave dispose blocked.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task Shutdown_SlowWriter_ReportsRemainder_OtherWriter_ReportsNothing()
    {
        DeadlineClock.AssertValues();
        var previous = SelfLog.Target;
        try
        {
            await AssertSlowShutdownReportsRemainder();
            await AssertFastShutdownReportsNothing();
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Eight threads writing during shutdown get no exception. Events after the close are counted.
    /// A writer still inside its call is not disposed, and a later write does not reach it.
    /// </summary>
    /// <returns>A task that finishes after shutdown returns.</returns>
    /// <remarks>
    /// Closing the gate and Dispose returning are signals. The waits are 30 s hang guards.
    /// An 8 s wait would fail a Dispose that is still inside the 10 s shutdown ceiling.
    /// Not thread-safe. The finally block releases the writer and restores SelfLog.
    /// </remarks>
    [Fact(Timeout = 60000)]
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
            Assert.True(await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(30)));
            var errors = new List<Exception>();
            var dispose = Task.Run(() => engine.Dispose());
            Assert.True(await WaitUntil(() =>
            {
                engine.Queue.TryEnqueue(Event("probe"));
                return engine.Queue.ClosedDropCount > 0;
            }, TimeSpan.FromSeconds(30)));
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
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));
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
    /// reports ALOG0072 once for a later write, and does not dispose a call that is still running.
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> the first dispose has not returned.
    /// The second call returns without waiting. The next tick returns the first.
    /// </summary>
    /// <returns>A task that finishes after the parallel shutdown.</returns>
    /// <remarks>
    /// Both halves use <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.
    /// The finally block releases the sequential gate and restores SelfLog.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task Shutdown_TwiceSequentialAndParallel_ThenWrite_NoException()
    {
        DeadlineClock.AssertValues();
        var previous = SelfLog.Target;
        try
        {
            await AssertSequentialShutdownThenWrite();
            await AssertParallelShutdownThenWrite();
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A writer that throws on event 3 still receives events 4 through 10. The other writer receives all 10.
    /// </summary>
    /// <returns>A task that finishes after the batch.</returns>
    [Fact(Timeout = 30000)]
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
    /// A writer blocked forever lets shutdown return at <see cref="BackgroundLogQueue.ShutdownDeadline"/>.
    /// One tick earlier it has not returned. The writer is not disposed. The remainder is one ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes after shutdown.</returns>
    /// <remarks>
    /// The clock is <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.
    /// A failed step advances the deadline in the finally block so the dispose thread can leave.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task HangingWriter_ShutdownReturns_AndDoesNotDispose()
    {
        DeadlineClock.AssertValues();
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        var clock = DeadlineClock.Create();
        var writer = new ForeverWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 10, 1, TimeSpan.FromHours(1))
            .UseAsync(false), clock);
        Task? dispose = null;
        try
        {
            for (var i = 0; i < 5; i++)
                engine.Log("h-" + i.ToString(CultureInfo.InvariantCulture), LogLevel.Info);

            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(30)));
            dispose = await DeadlineClock.RunUntilArmed(
                engine.Dispose,
                () => engine.Queue!.DisposeWaitsStarted,
                nameof(HangingWriter_ShutdownReturns_AndDoesNotDispose));
            await DeadlineClock.AssertNotElapsed(
                clock,
                () => engine.Queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.AssertElapsed(
                clock,
                () => engine.Queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
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
            await ReleaseDispose(clock, dispose);
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A child process with a hanging writer returns from Main and the process ends.
    /// The writer is not disposed, and stdout contains one ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes when the child exits or the time limit kills it.</returns>
    /// <remarks>
    /// The exit wait is 30 s. The grace after <c>Kill</c> is a 30 s hang guard, not a product deadline.
    /// The fact limit is 120 s so that grace can finish and the test can report the hung probe.
    /// </remarks>
    [Fact(Timeout = 120000)]
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
        var exited = process.WaitForExit(30_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(30_000);
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
    /// <remarks>
    /// The processor is inside the writer before the first advance. Later advances wake an overdue report
    /// that lost the race with the processor. The drop total is still required to match the reported sum.
    /// </remarks>
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
            await WaitUntilWriterEntered(writer);
            Assert.False(queue.TryEnqueue(Event("drop")));
            AdvanceReports(clock, TimeSpan.FromMilliseconds(1));
            AdvanceReports(clock, TimeSpan.FromSeconds(60));
            var near = TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2);
            AdvanceReports(clock, near);
            Assert.False(queue.TryEnqueue(Event("near-window")));
            AdvanceReports(clock, TimeSpan.FromMilliseconds(2));
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

    /// <summary>
    /// Waits until <paramref name="writer"/> is inside its first call, so the processor has finished starting.
    /// </summary>
    /// <param name="writer">The writer the queue is blocked on. Not null.</param>
    /// <returns>A task that finishes when that call has been entered.</returns>
    /// <remarks>
    /// Does not throw except through the assertion. The 5 s bound is a hang guard, not a product deadline.
    /// The processor is inside the writer and is not touching the test clock, so a later advance reaches the report timer.
    /// </remarks>
    private static async Task WaitUntilWriterEntered(BlockingWriter writer)
    {
        Assert.True(await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// Moves <paramref name="clock"/> and then wakes timers that are already due.
    /// </summary>
    /// <param name="clock">The queue's clock. Not null.</param>
    /// <param name="by">How far to move it. Zero only wakes timers that are already due.</param>
    /// <remarks>
    /// Does not throw. The extra wakes are bounded by 8. A callback that lost the race with another wake still runs
    /// before the caller asserts. A timer that is not yet due stays unfired. Safe to call from the test thread.
    /// </remarks>
    private static void AdvanceReports(FakeTimeProvider clock, TimeSpan by)
    {
        clock.Advance(by);
        for (var i = 0; i < 8; i++)
            clock.Advance(TimeSpan.Zero);
    }

    /// <summary>
    /// Two accepts held between the slot reservation and the enqueue are counted by the caller
    /// after dispose has finished. Both are ALOG0072. Neither is written. No deadline line is emitted.
    /// </summary>
    /// <returns>A task that finishes after both accepts return.</returns>
    /// <remarks>
    /// The hook blocks both callers until <see cref="BackgroundLogQueue.Dispose"/> returns and the processor has left.
    /// The 20 second waits are hang guards. Not thread-safe. The finally block releases the hook and restores SelfLog.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task DirectAccept_AfterProcessorExit_CallerCountsTheEvent()
    {
        const string ClosedLine =
            "Event dropped: ALog is shut down (the background queue was closed by Shutdown or Dispose). Later events are counted, not reported. Stop logging before you call Shutdown or Dispose.";
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new RecordingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            capacity: 8,
            batchSize: 1,
            flushInterval: TimeSpan.FromHours(1));
        var waiting = 0;
        using var bothWaiting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        queue.BeforeEnqueue = () =>
        {
            if (Interlocked.Increment(ref waiting) == 2)
                bothWaiting.Set();

            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Dispose did not finish while the accept was held.");
        };
        try
        {
            var first = Task.Run(() => queue.TryEnqueue(Event("race-a")));
            var second = Task.Run(() => queue.TryEnqueue(Event("race-b")));
            var started = Stopwatch.StartNew();
            while (!bothWaiting.IsSet && started.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(1);

            Assert.True(bothWaiting.IsSet, "both accepts did not reach the hook");
            queue.Dispose();
            Assert.True(queue.ProcessorHasLeft);
            release.Set();
            var both = Task.WhenAll(first, second);
            var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20))) == both;
            Assert.True(finished, "accepts did not return");
            var accepted = await both;
            Assert.True(accepted[0]);
            Assert.True(accepted[1]);
            Assert.Empty(writer.Events);
            Assert.Equal(2, queue.ClosedDropCount);
            Assert.Equal(0, queue.UnwrittenCount);
            string joined;
            lock (lines)
                joined = string.Join('\n', lines);

            Assert.DoesNotContain("queued events were not written", joined, StringComparison.Ordinal);
            Assert.Contains(ClosedLine, joined, StringComparison.Ordinal);
            Assert.Equal(2, writer.Events.Count + queue.ClosedDropCount + queue.FullDropCount);
        }
        finally
        {
            release.Set();
            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// An accept that lands while dispose is still waiting stays in the shutdown snapshot.
    /// The caller does not report it. Dispose writes it once the blocked writer returns.
    /// </summary>
    /// <returns>A task that finishes after dispose returns.</returns>
    /// <remarks>
    /// The hook holds the second accept until the shutdown wait is armed, then lets it enqueue.
    /// The processor is inside the first write, so it has not left and the snapshot has not run.
    /// The clock does not advance, so the new-write cutoff does not fire. The 20 second waits are hang guards.
    /// Not thread-safe. The finally block releases the hook, the writer, and SelfLog.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task DirectAccept_WhileDisposeWaits_StaysInTheSnapshot()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, TimeSpan.FromHours(1), false, clock));
        using var release = new ManualResetEventSlim(false);
        var inHook = 0;
        Task? disposing = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("owned-a")));
            Assert.True(
                await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(20)),
                "processor never entered the blocked writer");
            queue.BeforeEnqueue = () =>
            {
                Interlocked.Increment(ref inHook);
                release.Wait(TimeSpan.FromSeconds(20));
            };
            Assert.NotNull(queue.BeforeEnqueue);

            var second = Task.Run(() => queue.TryEnqueue(Event("owned-b")));
            Assert.True(
                await WaitUntil(() => Volatile.Read(ref inHook) >= 1, TimeSpan.FromSeconds(20)),
                "second accept did not reach the hook");
            disposing = Task.Run(() => queue.Dispose());
            Assert.True(
                await WaitUntil(() => queue.DisposeWaitsStarted >= 1, TimeSpan.FromSeconds(20)),
                "dispose did not arm the shutdown wait");
            Assert.False(queue.ProcessorHasLeft);
            release.Set();
            var finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(20))) == second;
            Assert.True(finished, "second accept did not return");
            Assert.True(await second);
            Assert.False(queue.ProcessorHasLeft);
            Assert.Equal(2, queue.AcceptedCount);
            writer.Release();
            var disposed = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(20))) == disposing;
            Assert.True(disposed, "dispose did not return after the writer was released");
            await disposing;
            Assert.Equal(2, writer.Events.Count);
            Assert.Contains(writer.Events, item => item.Message == "owned-a");
            Assert.Contains(writer.Events, item => item.Message == "owned-b");
            Assert.Equal(0, queue.UnwrittenCount);
            Assert.Equal(0, queue.ClosedDropCount);
            lock (lines)
                Assert.Empty(lines);

            Assert.Equal(2, writer.Events.Count + queue.ClosedDropCount + queue.FullDropCount);
        }
        finally
        {
            release.Set();
            writer.Release();
            if (disposing is not null)
                await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(20)));

            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>Builds an info event. The timestamp is fixed so tests do not depend on the clock.</summary>
    /// <param name="message">Rendered message. Not null.</param>
    /// <returns>An event with no exception.</returns>
    private static LogEvent Event(string message)
        => new(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>
    /// Enqueues 1000 events and stores the 999 warm durations. The first call is not a warm sample.
    /// </summary>
    /// <param name="queue">Queue whose writer is blocked. Not null. Not disposed here.</param>
    /// <param name="warm">Exactly 999 slots. Filled in call order. Not null.</param>
    /// <remarks>
    /// Writes <see cref="_fullQueueSyncCall"/> before each call so the cumulative hang guard can name the call it is inside.
    /// The first call is left out of the 10 ms sample because it may pay for JIT. Each call still has the per-call 30 s guard.
    /// A call that blocks for 5 s passes that per-call guard. The caller caps the whole loop at 30 s.
    /// Not safe to run two copies at once. The pool thread writes the call index; the test thread only reads it.
    /// </remarks>
    private void SampleFullQueueSyncEnqueue(BackgroundLogQueue queue, TimeSpan[] warm)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(warm);
        if (warm.Length != 999)
            throw new ArgumentException("Warm sample length must be 999.", nameof(warm));

        var warmCount = 0;
        for (var i = 0; i < 1000; i++)
        {
            Volatile.Write(ref _fullQueueSyncCall, i);
            var started = Stopwatch.GetTimestamp();
            queue.TryEnqueue(Event("n-" + i.ToString(CultureInfo.InvariantCulture)));
            var elapsed = Stopwatch.GetElapsedTime(started);
            AssertWithinHangGuard(elapsed);
            if (i == 0)
                continue;

            warm[warmCount] = elapsed;
            warmCount++;
        }

        Assert.Equal(999, warmCount);
    }

    /// <summary>
    /// A flush whose writer stays blocked does not return one tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/>.
    /// The next tick returns it. The flush writes no ALOG0071. The writer is released only after that check.
    /// </summary>
    /// <returns>A task that finishes after the deadline step and cleanup.</returns>
    /// <remarks>
    /// The clock is <see cref="FakeTimeProvider"/>. A flush that never returns fails the 30 s hang guard.
    /// Not safe to run beside another test that replaces SelfLog. Only the test thread calls it.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task Flush_BlockedWriter_ReturnsInsideShutdownDeadline_WritesNoAlog0071()
    {
        DeadlineClock.AssertValues();
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = DeadlineClock.Create();
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(1, 1, TimeSpan.FromSeconds(30), false, clock));
        try
        {
            Assert.True(queue.TryEnqueue(Event("blocked")));
            Assert.True(
                await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(30)),
                "processor never entered the blocked writer");
            var flush = queue.FlushAsync();
            await DeadlineClock.WaitFor(
                () => queue.FlushWaitsStarted >= 1,
                nameof(Flush_BlockedWriter_ReturnsInsideShutdownDeadline_WritesNoAlog0071) + " did not arm the flush wait");
            await DeadlineClock.AssertNotElapsed(
                clock,
                () => queue.FlushDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "flush");
            await DeadlineClock.AssertElapsed(
                clock,
                () => queue.FlushDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "flush");
            await DeadlineClock.WaitForCompletion(flush, "flush hung");
            lock (lines)
                Assert.DoesNotContain(lines, line => line.Contains(DeadlinePrefix, StringComparison.Ordinal));
        }
        finally
        {
            writer.Release();
            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Releases the blocked writer and waits until flush has written every accepted event.
    /// Flush must still be waiting while the writer is blocked, and it must not write ALOG0071.
    /// </summary>
    /// <param name="queue">Open queue whose writer is blocked. Not null. Not disposed here.</param>
    /// <param name="writer">Writer held inside its first call. Not null. <see cref="BlockingWriter.Release"/> is idempotent.</param>
    /// <param name="lines">SelfLog lines captured for this queue. Not null. The lock is this list.</param>
    /// <returns>A task that finishes when flush has returned and the silence checks have passed.</returns>
    /// <remarks>
    /// Fails the test when the processor never enters the writer, when flush returns while the writer is still blocked,
    /// or when ALOG0071 appears. Does not dispose the queue. Only the test thread calls it.
    /// The entry wait is a signal cap shared with <see cref="WaitUntil"/>, not a limit on how fast flush must return.
    /// This check is the idle signal. The deadline window is <see cref="Flush_BlockedWriter_ReturnsInsideShutdownDeadline_WritesNoAlog0071"/>.
    /// </remarks>
    private static async Task AssertFlushCompletesWhenWriterIsReleased(
        BackgroundLogQueue queue,
        BlockingWriter writer,
        List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lines);
        Assert.True(
            await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(30)),
            "processor never entered the blocked writer");
        // Flush stays open while the writer is blocked. Releasing the writer is the idle signal that ends it.
        // The deadline itself is asserted by Flush_BlockedWriter_ReturnsInsideShutdownDeadline_WritesNoAlog0071.
        var flush = queue.FlushAsync();
        Assert.True(flush.IsCompleted == false, "flush returned before the writer was released.");
        writer.Release();
        await flush;
        Assert.Equal(100, queue.WrittenCount);
        Assert.Equal(100, writer.Events.Count);
        Assert.Equal(900, queue.FullDropCount);
        lock (lines)
        {
            Assert.Single(lines);
            Assert.DoesNotContain(lines, line => line.Contains(DeadlinePrefix, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Checks the C4-01 plausibility bound: the median is under 10 ms, at least 990 of 999 warm calls are too, and none reached the 30 s hang guard.
    /// </summary>
    /// <param name="samples">
    /// One duration per warm call, in any order. Exactly 999 values. Not null.
    /// This method sorts the array and does not restore the original order.
    /// </param>
    /// <remarks>
    /// A null array throws <see cref="ArgumentNullException"/>. A wrong length, a slow median, or a 30 s sample fails the test.
    /// Not thread-safe. Only the test thread calls it.
    /// Nine samples may exceed 10 ms so one scheduler stall does not fail the run. A sample of 30 seconds or more is a hang, not a stall.
    /// There is no strict maximum under 10 ms. Whether the call blocked is decided by the caller, from the task or from the writer still being inside its first write.
    /// </remarks>
    private static void AssertWarmEnqueueSamplesStayUnderTenMilliseconds(TimeSpan[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        // 999 warm calls: 1000 writes, minus the first call, which C4-01 leaves out for JIT.
        const int requiredSamples = 999;
        // 990 leaves nine stalls. A 20 ms stall was observed under 16 busy threads.
        const int minimumUnderLimit = 990;
        Assert.Equal(requiredSamples, samples.Length);
        Array.Sort(samples);
        var limit = TimeSpan.FromMilliseconds(10);
        var median = samples[requiredSamples / 2];
        var under = 0;
        for (var i = 0; i < requiredSamples; i++)
        {
            if (samples[i] >= limit)
                break;

            under++;
        }

        Assert.True(median < limit, "median " + median.ToString());
        Assert.True(
            under >= minimumUnderLimit,
            "under10ms " + under.ToString(CultureInfo.InvariantCulture) + " of " + requiredSamples.ToString(CultureInfo.InvariantCulture));
        AssertWithinHangGuard(samples[requiredSamples - 1]);
    }

    /// <summary>
    /// Fails when one call lasted 30 seconds or longer. That is a single-call hang guard, not the 10 ms plausibility check.
    /// </summary>
    /// <param name="elapsed">Duration of one call. A negative value still passes, because it is not a hang.</param>
    /// <remarks>
    /// Does not throw except through the assertion. Not thread-safe. Only the test thread calls it.
    /// There is no promised product deadline on enqueue. A call that blocks for 5 s is under this line and passes.
    /// The synchronous 1000-call loop is capped separately, by one cumulative 30 s guard that names the test and the call index.
    /// </remarks>
    private static void AssertWithinHangGuard(TimeSpan elapsed)
    {
        Assert.True(elapsed < TimeSpan.FromSeconds(30), "hang " + elapsed.ToString());
    }

    /// <summary>
    /// Polls <paramref name="ready"/> until it is true or <paramref name="budget"/> passes.
    /// </summary>
    /// <param name="ready">Condition. Called until it returns true or the budget passes. Must not throw.</param>
    /// <param name="budget">Upper bound. The loop stops when this elapses.</param>
    /// <returns>True when the condition was seen. False when the budget won.</returns>
    /// <remarks>
    /// A budget of zero checks once. Not thread-safe. Only the test thread calls it.
    /// The 10 ms pause is the poll, not a product bound.
    /// </remarks>
    private static async Task<bool> WaitUntil(Func<bool> ready, TimeSpan budget)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < budget)
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

    /// <summary>
    /// Holds the first writer call, fills the queue, and shuts down on <see cref="BackgroundLogQueue.NewWriteBudget"/>.
    /// </summary>
    /// <returns>A task that finishes after ALOG0072 is checked.</returns>
    /// <remarks>
    /// One tick before the budget, dispose has not returned and a new call may still start.
    /// A cutoff that is already closed fails with <see cref="DeadlineClock.EarlyFire"/>.
    /// At the budget the second call is the last one, or the test fails with <see cref="DeadlineClock.LateFire"/>.
    /// Dispose then returns because the processor stops, without advancing the rest of the shutdown deadline.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static async Task AssertSlowShutdownReportsRemainder()
    {
        var lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = DeadlineClock.Create();
        var slow = new BudgetGateWriter();
        var engine = EngineOnClock(slow, 1000, 1, clock);
        var origin = clock.GetUtcNow();
        Task? dispose = null;
        try
        {
            engine.Log("e-0", LogLevel.Info);
            Assert.True(await WaitUntil(() => slow.Entered == 1, TimeSpan.FromSeconds(30)));
            for (var i = 1; i < 1000; i++)
                engine.Log("e-" + i.ToString(CultureInfo.InvariantCulture), LogLevel.Info);

            dispose = await DeadlineClock.RunUntilArmed(
                engine.Dispose,
                () => engine.Queue!.DisposeWaitsStarted,
                nameof(Shutdown_SlowWriter_ReportsRemainder_OtherWriter_ReportsNothing));
            clock.Advance(BackgroundLogQueue.NewWriteBudget - DeadlineClock.Tick);
            Assert.False(
                dispose.IsCompleted,
                "shutdown returned while the writer was still held");
            Assert.False(
                engine.Queue!.NewWriterCallsBlocked,
                DeadlineClock.EarlyFire("cutoff", BackgroundLogQueue.NewWriteBudget - DeadlineClock.Tick));
            slow.Grant(1);
            Assert.True(await WaitUntil(() => slow.Entered == 2, TimeSpan.FromSeconds(30)));
            Assert.False(
                dispose.IsCompleted,
                "shutdown returned while the writer was still held");
            Assert.False(
                engine.Queue.NewWriterCallsBlocked,
                DeadlineClock.EarlyFire("cutoff", BackgroundLogQueue.NewWriteBudget - DeadlineClock.Tick));
            clock.Advance(DeadlineClock.Tick);
            Assert.True(
                engine.Queue.NewWriterCallsBlocked,
                DeadlineClock.LateFire("cutoff", BackgroundLogQueue.NewWriteBudget));
            Assert.False(
                dispose.IsCompleted,
                "shutdown returned while the writer was still held");
            slow.Grant(1);
            await DeadlineClock.WaitForCompletion(dispose, "slow shutdown hung");
            Assert.Equal(origin + BackgroundLogQueue.NewWriteBudget, clock.GetUtcNow());
            await AssertSlowRemainder(lines, slow, engine);
        }
        finally
        {
            slow.Grant(1000);
            await ReleaseDispose(clock, dispose);
        }
    }

    /// <summary>
    /// Checks the slow-writer shutdown report: two calls finished, the rest are the ALOG0071 remainder, then one ALOG0072.
    /// </summary>
    /// <param name="lines">Captured SelfLog lines. Not null. The lock is this list.</param>
    /// <param name="slow">Writer that was released twice. Not null.</param>
    /// <param name="engine">Engine already disposed once. Not null. Flushed and disposed again here.</param>
    /// <returns>A task that finishes after the late log.</returns>
    /// <remarks>Does not move the clock. A second dispose must not add another ALOG0071. Not thread-safe.</remarks>
    private static async Task AssertSlowRemainder(List<string> lines, BudgetGateWriter slow, LoggerEngine engine)
    {
        Assert.Equal(2, slow.Entered);
        Assert.Equal(2, slow.Finished);
        string report;
        lock (lines)
            report = Assert.Single(lines);
        var unwritten = ParseUnwritten(report);
        Assert.Equal(DeadlinePrefix + unwritten.ToString(CultureInfo.InvariantCulture) + DeadlineSuffix, report);
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

    /// <summary>
    /// Shuts a fast writer down on a frozen clock. Dispose returns because the processor stopped, with no ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after the empty-report checks.</returns>
    /// <remarks>
    /// The clock is not advanced. A dispose that is still waiting fails the 30 s hang guard.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static async Task AssertFastShutdownReportsNothing()
    {
        var lines = new List<string>();
        SelfLog.Enable(lines.Add);
        var clock = DeadlineClock.Create();
        var fast = new RecordingWriter();
        var engine = EngineOnClock(fast, 100_000, 50, clock);
        var origin = clock.GetUtcNow();
        Task? dispose = null;
        try
        {
            for (var i = 0; i < 100_000; i++)
                engine.Log("f", LogLevel.Info);

            dispose = Task.Run(() => engine.Dispose());
            await DeadlineClock.WaitForCompletion(dispose, "fast shutdown hung");
            Assert.Equal(origin, clock.GetUtcNow());
            Assert.Equal(100_000, fast.Events.Count);
            Assert.Empty(lines);
            Assert.True(fast.Disposed);
            Assert.Equal(0, engine.Queue!.UnwrittenCount);
        }
        finally
        {
            await ReleaseDispose(clock, dispose);
        }
    }

    /// <summary>
    /// Shuts down once at the deadline, then again, then logs. The second shutdown adds no ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after ALOG0072 is checked.</returns>
    /// <remarks>
    /// The writer stays inside its first call until the deadline, so dispose cannot return early.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static async Task AssertSequentialShutdownThenWrite()
    {
        var lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = DeadlineClock.Create();
        var slow = new BudgetGateWriter();
        var engine = EngineOnClock(slow, 1000, 10, clock);
        Task? dispose = null;
        try
        {
            engine.Log("s", LogLevel.Info);
            Assert.True(await WaitUntil(() => slow.Entered == 1, TimeSpan.FromSeconds(30)));
            for (var i = 1; i < 1000; i++)
                engine.Log("s", LogLevel.Info);

            dispose = await ArmDispose(engine, nameof(Shutdown_TwiceSequentialAndParallel_ThenWrite_NoException));
            await FinishAtShutdownDeadline(clock, engine, dispose);
            engine.Dispose();
            engine.Log("after", LogLevel.Info);
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
            slow.Grant(1000);
            await ReleaseDispose(clock, dispose);
        }
    }

    /// <summary>
    /// Starts two shutdowns while a writer is blocked. The second returns at once. The first returns on the deadline.
    /// </summary>
    /// <returns>A task that finishes after ALOG0072 is checked.</returns>
    /// <remarks>
    /// The second call does not wait and does not add an ALOG0071 line. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static async Task AssertParallelShutdownThenWrite()
    {
        var lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = DeadlineClock.Create();
        var hanging = new ForeverWriter();
        var engine = EngineOnClock(hanging, 8, 1, clock);
        Task? first = null;
        Task? second = null;
        try
        {
            engine.Log("hang", LogLevel.Info);
            Assert.True(hanging.Entered.Wait(TimeSpan.FromSeconds(30)));
            first = Task.Run(() => engine.Dispose());
            second = Task.Run(() => engine.Dispose());
            await DeadlineClock.WaitFor(
                () => engine.Queue!.DisposeWaitsStarted >= 1,
                "parallel shutdown did not arm the clock wait");
            await DeadlineClock.WaitFor(
                () => first.IsCompleted || second.IsCompleted,
                "second dispose did not return while the first wait was armed");
            Assert.False(first.IsCompleted && second.IsCompleted, "both dispose calls returned before the deadline");
            var running = first.IsCompleted ? second! : first;
            var returned = first.IsCompleted ? first : second!;
            await returned;
            await DeadlineClock.AssertNotElapsed(
                clock,
                () => engine.Queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.AssertElapsed(
                clock,
                () => engine.Queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.WaitForCompletion(running, "shutdown hung");
            engine.Log("after", LogLevel.Info);
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
            await ReleaseDispose(clock, first);
            await ReleaseDispose(clock, second);
        }
    }

    /// <summary>
    /// Builds an engine whose queue uses <paramref name="clock"/> and does not flush on its own during a deadline step.
    /// </summary>
    /// <param name="writer">The only writer. Not null.</param>
    /// <param name="capacity">Queue capacity. At least 1.</param>
    /// <param name="batchSize">Batch size. The slow-cutoff test uses 1 so one grant starts one call.</param>
    /// <param name="clock">Clock for shutdown and flush. Not null.</param>
    /// <returns>An engine that has not been disposed.</returns>
    /// <remarks>Does not throw. Synchronous writes. The flush interval is one hour so a deadline step does not tick it.</remarks>
    private static LoggerEngine EngineOnClock(ILogWriter writer, int capacity, int batchSize, TimeProvider clock)
    {
        return new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, capacity, batchSize, TimeSpan.FromHours(1))
            .UseAsync(false), clock);
    }

    /// <summary>
    /// Starts <see cref="LoggerEngine.Dispose"/> and waits until the shutdown wait is armed.
    /// </summary>
    /// <param name="engine">Engine whose writer is already blocked. Not null.</param>
    /// <param name="name">Test name used when the wait is not armed.</param>
    /// <returns>The dispose task. Still running.</returns>
    /// <remarks>Does not move the clock. The arm wait is the 30 s hang guard.</remarks>
    private static Task<Task> ArmDispose(LoggerEngine engine, string name)
    {
        return DeadlineClock.RunUntilArmed(
            engine.Dispose,
            () => engine.Queue!.DisposeWaitsStarted,
            name);
    }

    /// <summary>
    /// Asserts the shutdown flag is clear one tick before the deadline, set on that tick, then waits for dispose.
    /// </summary>
    /// <param name="clock">Clock the dispose is waiting on. Not null.</param>
    /// <param name="engine">Engine whose queue owns the flag. Not null. The queue is not null.</param>
    /// <param name="dispose">Armed dispose. Not null.</param>
    /// <returns>A task that finishes when dispose returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> has no queue.</exception>
    /// <remarks>
    /// An early flag uses <see cref="DeadlineClock.EarlyFire"/> for shutdown.
    /// A flag that is still clear at the bound uses <see cref="DeadlineClock.LateFire"/> after the 30 s hang guard.
    /// The final wait is a separate 30 s hang guard, and its message is not the late sentence.
    /// Does not grant a writer.
    /// </remarks>
    private static async Task FinishAtShutdownDeadline(FakeTimeProvider clock, LoggerEngine engine, Task dispose)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var queue = engine.Queue;
        if (queue is null)
            throw new ArgumentNullException(nameof(engine), "Engine has no queue.");

        await DeadlineClock.AssertNotElapsed(
            clock,
            () => queue.ShutdownDeadlineElapsed != 0,
            BackgroundLogQueue.ShutdownDeadline,
            "shutdown");
        await DeadlineClock.AssertElapsed(
            clock,
            () => queue.ShutdownDeadlineElapsed != 0,
            BackgroundLogQueue.ShutdownDeadline,
            "shutdown");
        await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
    }

    /// <summary>
    /// Unblocks a dispose that is still waiting on <paramref name="clock"/>, then waits up to the hang guard.
    /// </summary>
    /// <param name="clock">Clock the dispose was given. Not null. Advanced only when the task is still running.</param>
    /// <param name="dispose">The dispose task, or null when it was never started.</param>
    /// <returns>A task that finishes when dispose has finished or the guard elapsed.</returns>
    /// <remarks>
    /// Does not assert. A second failure here would hide the original one. The advance is the product deadline,
    /// which is already the bound the test uses. Not thread-safe.
    /// </remarks>
    private static async Task ReleaseDispose(FakeTimeProvider clock, Task? dispose)
    {
        if (dispose is null)
            return;

        if (!dispose.IsCompleted)
            clock.Advance(BackgroundLogQueue.ShutdownDeadline);

        _ = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Blocks each write until <see cref="Grant"/> releases one permit.
    /// Counts calls, finished calls, and calls that saw dispose.
    /// </summary>
    private sealed class BudgetGateWriter : ILogWriter
    {
        private readonly SemaphoreSlim _gate = new(0);
        private int _entered;
        private int _finished;
        private int _afterDispose;
        private int _disposed;

        /// <summary>Calls that have entered <see cref="Write"/>, including the one still waiting.</summary>
        internal int Entered => Volatile.Read(ref _entered);

        /// <summary>Calls that have returned.</summary>
        internal int Finished => Volatile.Read(ref _finished);

        /// <summary>Calls that observed dispose before they returned.</summary>
        internal int WritesAfterDispose => Volatile.Read(ref _afterDispose);

        /// <summary>True after <see cref="Dispose"/>.</summary>
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Releases <paramref name="count"/> waiting calls.
        /// </summary>
        /// <param name="count">How many calls may pass the gate. At least 1.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
        /// <remarks>Does not throw for a call that has not arrived yet. The permit stays until that call waits. Not itself a wait.</remarks>
        internal void Grant(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Grant count must be at least 1.");

            _gate.Release(count);
        }

        /// <summary>
        /// Counts the entry, waits for one permit, then records the call.
        /// </summary>
        /// <param name="logEvent">Ignored.</param>
        /// <remarks>
        /// The wait has no wall-clock timeout. The test grants the permit, including from a finally block.
        /// A call that sees dispose increments <see cref="WritesAfterDispose"/>.
        /// </remarks>
        public void Write(LogEvent logEvent)
        {
            Interlocked.Increment(ref _entered);
            _gate.Wait();
            if (Volatile.Read(ref _disposed) != 0)
                Interlocked.Increment(ref _afterDispose);

            Interlocked.Increment(ref _finished);
        }

        /// <summary>Same gate as <see cref="Write"/>.</summary>
        /// <param name="logEvent">Passed to <see cref="Write"/>.</param>
        /// <returns>A completed task after the permit is taken.</returns>
        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Marks the writer disposed. Does not release a call that is waiting.</summary>
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
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
