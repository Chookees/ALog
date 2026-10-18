// C4 follow-up: the open ALOG0070 window is reported at shutdown, the first event after
// close reports ALOG0072, and healthy writers are disposed while a hanging call is not.
// Next file: ALog.Tests/C4QueueFixTests.cs.

using System.Diagnostics;
using System.Reflection;
using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using ALog.Writers.File;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

public sealed partial class C4QueueFixTests
{
    private const string ClosedLine =
        "Event dropped: ALog is shut down (the background queue was closed by Shutdown or Dispose). Later events are counted, not reported. Stop logging before you call Shutdown or Dispose.";

    /// <summary>
    /// The spec repro: 1 ms writer, 100,000 logs, 300 ms, 100,000 more, then Dispose.
    /// The sum of ALOG0070 counts must equal <see cref="BackgroundLogQueue.FullDropCount"/>,
    /// and a remainder line sits immediately before ALOG0071.
    /// A run with no drops adds no ALOG0070 line.
    /// </summary>
    /// <returns>A task that finishes after both engines shut down.</returns>
    [Fact(Timeout = 90000)]
    public async Task Shutdown_OpenDropWindow_ReportsRemainderBeforeAlog0071()
    {
        await AssertNoFullDropLineWhenNothingWasDropped();

        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new DelayWriter(TimeSpan.FromMilliseconds(1));
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 1000, 10)
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 100_000; i++)
                engine.Log("a", LogLevel.Info);

            // Spec repro pause. The first report is due after 1 ms; the second burst stays in the 60 s window.
            await Task.Delay(300);
            for (var i = 0; i < 100_000; i++)
                engine.Log("b", LogLevel.Info);

            engine.Dispose();
            AssertOpenWindowWasReported(lines, engine.Queue!);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Two shutdowns in a row, and two at once, each write the open-window ALOG0070 once, before ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after the parallel shutdown.</returns>
    [Fact(Timeout = 30000)]
    public async Task Shutdown_TwiceAndParallel_ReportsOpenWindowOnce()
    {
        var previous = SelfLog.Target;
        try
        {
            await AssertOpenWindowOnce(parallel: false);
            await AssertOpenWindowOnce(parallel: true);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// An event logged before close does not report ALOG0072. The first event after Dispose reports it once, verbatim.
    /// </summary>
    /// <returns>A task that finishes after the late event.</returns>
    [Fact(Timeout = 15000)]
    public async Task ClosedDrop_OneLateEvent_ReportsAlog0072Verbatim()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 20, 5, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            engine.Log("before", LogLevel.Info);
            await engine.FlushAsync();
            var calls = writer.Events.Count;
            lock (lines)
                Assert.DoesNotContain(lines, line => line.Contains("ALog is shut down", StringComparison.Ordinal));

            engine.Dispose();
            engine.Log("late", LogLevel.Info);
            Assert.Equal(calls, writer.Events.Count);
            Assert.Equal(1, engine.Queue!.ClosedDropCount);
            Assert.Equal(engine.Queue.AcceptedCount, engine.Queue.WrittenCount + engine.Queue.UnwrittenCount);
            lock (lines)
                Assert.Equal(ClosedLine, Assert.Single(lines, line => line.Contains("ALog is shut down", StringComparison.Ordinal)));
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Sixteen threads logging after the close point produce one ALOG0072 line and only closed-drop counts.
    /// </summary>
    /// <returns>A task that finishes when the workers return.</returns>
    [Fact(Timeout = 15000)]
    public async Task ClosedDrop_SixteenThreads_ReportsAlog0072Once()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 20, 5, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 5; i++)
                engine.Log("ok", LogLevel.Info);

            await engine.FlushAsync();
            var calls = writer.Events.Count;
            engine.Dispose();
            var errors = new List<Exception>();
            var workers = new Task[16];
            for (var t = 0; t < workers.Length; t++)
            {
                workers[t] = Task.Run(() =>
                {
                    try
                    {
                        for (var n = 0; n < 25; n++)
                            engine.Log("late", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                            errors.Add(ex);
                    }
                });
            }

            await Task.WhenAll(workers);
            Assert.Empty(errors);
            Assert.Equal(calls, writer.Events.Count);
            Assert.Equal(400, engine.Queue!.ClosedDropCount);
            Assert.Equal(engine.Queue.AcceptedCount, engine.Queue.WrittenCount + engine.Queue.UnwrittenCount);
            lock (lines)
                Assert.Equal(ClosedLine, Assert.Single(lines, line => line.Contains("ALog is shut down", StringComparison.Ordinal)));
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// After the deadline the file writer and both spies are disposed. The hanging call is not.
    /// The file contains the line that was written before the hang. The remainder is ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after shutdown.</returns>
    [Fact(Timeout = 20000)]
    public async Task Deadline_DisposesHealthyWriters_NotTheHangingCall()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        var path = Path.Combine(Path.GetTempPath(), "alog-c4-" + Guid.NewGuid().ToString("N") + ".log");
        var release = new ManualResetEventSlim(false);
        var front = new ProbeWriter();
        var file = new ProbeWriter(inner: new FileLogWriter(path));
        var hang = new ProbeWriter(release: release);
        var back = new ProbeWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(front)
            .AddWriter(file)
            .AddWriter(hang)
            .AddWriter(back)
            .UseBackgroundQueue(true, 10, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 3; i++)
                engine.Log("kept-line", LogLevel.Info);

            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(5)));
            var started = Stopwatch.StartNew();
            engine.Dispose();
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            Assert.False(hang.Disposed);
            Assert.False(hang.DisposeOverlappedWrite);
            Assert.True(front.Disposed);
            Assert.True(file.Disposed);
            Assert.True(back.Disposed);
            Assert.Equal(0, front.CallsAfterDispose);
            Assert.Contains("kept-line", File.ReadAllText(path), StringComparison.Ordinal);
            var report = Assert.Single(lines, line => line.StartsWith(DeadlinePrefix, StringComparison.Ordinal));
            var unwritten = ParseUnwritten(report);
            Assert.Equal(engine.Queue!.AcceptedCount, engine.Queue.WrittenCount + unwritten);
            Assert.True(unwritten >= 1);
        }
        finally
        {
            release.Set();
            SelfLog.Target = previous;
            if (File.Exists(path))
                File.Delete(path);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Releasing the hanging writer after the deadline does not call the spy that sits behind it,
    /// and does not call the spy that already finished.
    /// </summary>
    /// <returns>A task that finishes when the processor leaves.</returns>
    [Fact(Timeout = 20000)]
    public async Task NoWriterCall_AfterDeadline_WhenHangReturns()
    {
        var previous = SelfLog.Target;
        SelfLog.Enable(static _ => { });
        var release = new ManualResetEventSlim(false);
        var front = new ProbeWriter();
        var hang = new ProbeWriter(release: release);
        var back = new ProbeWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(front)
            .AddWriter(hang)
            .AddWriter(back)
            .UseBackgroundQueue(true, 10, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 3; i++)
                engine.Log("x", LogLevel.Info);

            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(5)));
            engine.Dispose();
            var frontFinished = front.Finished;
            release.Set();
            Assert.True(await WaitUntil(() => IsProcessorStopped(engine.Queue!), TimeSpan.FromSeconds(5)));
            Assert.Equal(0, back.Finished);
            Assert.Equal(frontFinished, front.Finished);
            Assert.Equal(0, front.CallsAfterDispose);
            Assert.False(hang.DisposeOverlappedWrite);
        }
        finally
        {
            release.Set();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A writer whose call takes 5.5 s is not disposed while that call runs.
    /// Shutdown returns inside 5.2 s. Dispose runs only after the call returns, and not during Write.
    /// </summary>
    /// <returns>A task that finishes after the slow call is disposed.</returns>
    [Fact(Timeout = 20000)]
    public async Task SlowCall_IsDisposedOnlyAfterItReturns()
    {
        var previous = SelfLog.Target;
        SelfLog.Enable(static _ => { });
        var slow = new ProbeWriter(delay: TimeSpan.FromMilliseconds(5500));
        var behind = new ProbeWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(slow)
            .AddWriter(behind)
            .UseBackgroundQueue(true, 4, 1, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            engine.Log("slow", LogLevel.Info);
            Assert.True(slow.Entered.Wait(TimeSpan.FromSeconds(5)));
            var started = Stopwatch.StartNew();
            engine.Dispose();
            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            Assert.False(slow.Disposed);
            Assert.False(slow.DisposeOverlappedWrite);
            Assert.True(await WaitUntil(
                () => slow.Disposed && slow.DisposeOrder > slow.FinishOrder,
                TimeSpan.FromSeconds(3)));
            Assert.False(slow.DisposeOverlappedWrite);
            Assert.True(behind.Disposed);
            Assert.Equal(0, behind.Finished);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A fast writer that accepts every event shuts down with no ALOG0070 line.
    /// </summary>
    /// <returns>A task that finishes after dispose.</returns>
    private static async Task AssertNoFullDropLineWhenNothingWasDropped()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(true, 100, 10, TimeSpan.FromHours(1))
            .UseAsync(false));
        try
        {
            for (var i = 0; i < 10; i++)
                engine.Log("kept", LogLevel.Info);

            await engine.FlushAsync();
            engine.Dispose();
            Assert.Equal(0, engine.Queue!.FullDropCount);
            lock (lines)
            {
                Assert.Equal(0, SumDropCounts(lines));
                Assert.DoesNotContain(lines, line => line.Contains(DropMarker, StringComparison.Ordinal));
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Fills a frozen-clock queue, drops seven events, and disposes once or twice in parallel.
    /// </summary>
    /// <param name="parallel">True runs two Dispose calls at the same time.</param>
    /// <returns>A task that finishes after the writer is released.</returns>
    private static async Task AssertOpenWindowOnce(bool parallel)
    {
        var lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var release = new ManualResetEventSlim(false);
        var hang = new ProbeWriter(release: release);
        var queue = new BackgroundLogQueue(new ILogWriter[] { hang }, 4, 1, TimeSpan.FromHours(1), clock);
        try
        {
            Assert.True(queue.TryEnqueue(Event("prime")));
            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 3; i++)
                Assert.True(queue.TryEnqueue(Event("fill")));

            for (var i = 0; i < 7; i++)
                Assert.False(queue.TryEnqueue(Event("drop")));

            Assert.Equal(7, queue.FullDropCount);
            var started = Stopwatch.StartNew();
            if (parallel)
            {
                var first = Task.Run(() => queue.Dispose());
                var second = Task.Run(() => queue.Dispose());
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
            }
            else
            {
                queue.Dispose();
                queue.Dispose();
            }

            Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(5200), "elapsed " + started.Elapsed.ToString());
            lock (lines)
            {
                Assert.Equal(2, lines.Count);
                Assert.Equal(
                    "7" + DropMarker + "4). The writers are slower than your log rate. Lower the log level or check the slowest writer.",
                    lines[0]);
                Assert.StartsWith(DeadlinePrefix, lines[1], StringComparison.Ordinal);
                Assert.Equal(queue.FullDropCount, SumDropCounts(lines));
            }
        }
        finally
        {
            release.Set();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Checks the drop sum and that ALOG0070 does not follow ALOG0071.
    /// </summary>
    /// <param name="lines">Captured SelfLog lines. Not null.</param>
    /// <param name="queue">The engine queue. Not null.</param>
    private static void AssertOpenWindowWasReported(List<string> lines, BackgroundLogQueue queue)
    {
        int deadlineAt;
        long reported;
        lock (lines)
        {
            reported = SumDropCounts(lines);
            deadlineAt = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith(DeadlinePrefix, StringComparison.Ordinal))
                    deadlineAt = i;
            }

            if (deadlineAt >= 0)
            {
                Assert.True(deadlineAt > 0);
                Assert.Contains(DropMarker, lines[deadlineAt - 1], StringComparison.Ordinal);
                for (var i = deadlineAt + 1; i < lines.Count; i++)
                    Assert.DoesNotContain(DropMarker, lines[i], StringComparison.Ordinal);
            }
        }

        Assert.True(queue.FullDropCount > 0);
        Assert.Equal(queue.FullDropCount, reported);
    }

    /// <summary>
    /// True when the processor has set its stopped handle. Used so a test can wait until a released hang has unwound.
    /// </summary>
    /// <param name="queue">The live queue. Not null.</param>
    /// <returns>True when the background loop has left.</returns>
    private static bool IsProcessorStopped(BackgroundLogQueue queue)
    {
        var field = typeof(BackgroundLogQueue).GetField("_stopped", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handle = Assert.IsType<ManualResetEventSlim>(field.GetValue(queue));
        return handle.IsSet;
    }

    /// <summary>
    /// Records dispose-during-write, optional sleep, and an optional inner writer.
    /// A release handle blocks the call until the test sets it, with a 15 s bound.
    /// </summary>
    private sealed class ProbeWriter : ILogWriter
    {
        private readonly TimeSpan _delay;
        private readonly ManualResetEventSlim? _release;
        private readonly ILogWriter? _inner;
        private int _finished;
        private int _inWrite;
        private int _disposed;
        private int _after;
        private int _overlap;
        private int _order;
        private int _finishOrder;
        private int _disposeOrder;

        /// <summary>Set once <see cref="Write"/> has started.</summary>
        internal ManualResetEventSlim Entered { get; } = new(false);

        /// <summary>
        /// Creates a probe. <paramref name="delay"/> blocks inside the call.
        /// <paramref name="release"/> blocks until set. <paramref name="inner"/> receives the event.
        /// </summary>
        /// <param name="delay">Sleep inside the call. Zero sleeps not at all.</param>
        /// <param name="release">Optional block. The wait is at most 15 s.</param>
        /// <param name="inner">Optional writer to forward to. Null writes nowhere.</param>
        internal ProbeWriter(TimeSpan delay = default, ManualResetEventSlim? release = null, ILogWriter? inner = null)
        {
            _delay = delay;
            _release = release;
            _inner = inner;
        }

        /// <summary>True after <see cref="Dispose"/>.</summary>
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>Calls that have returned.</summary>
        internal int Finished => Volatile.Read(ref _finished);

        /// <summary>Calls that observed dispose before they returned.</summary>
        internal int CallsAfterDispose => Volatile.Read(ref _after);

        /// <summary>True when <see cref="Dispose"/> ran while <see cref="Write"/> was inside the call.</summary>
        internal bool DisposeOverlappedWrite => Volatile.Read(ref _overlap) != 0;

        /// <summary>Order stamp taken when the call returns. Zero if the call has not returned.</summary>
        internal int FinishOrder => Volatile.Read(ref _finishOrder);

        /// <summary>Order stamp taken when dispose runs. Zero if dispose has not run.</summary>
        internal int DisposeOrder => Volatile.Read(ref _disposeOrder);

        /// <summary>Sleeps, waits for release, then forwards. A dispose during the call is recorded.</summary>
        /// <param name="logEvent">Forwarded to the inner writer. May be null.</param>
        /// <exception cref="TimeoutException">The release handle was not set within 15 s.</exception>
        public void Write(LogEvent logEvent)
        {
            Interlocked.Exchange(ref _inWrite, 1);
            Entered.Set();
            try
            {
                if (_delay > TimeSpan.Zero)
                    Thread.Sleep(_delay);

                if (_release is not null && !_release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The test did not release the writer within 15 s.");

                if (Volatile.Read(ref _disposed) != 0)
                    Interlocked.Increment(ref _after);

                _inner?.Write(logEvent);
            }
            finally
            {
                Volatile.Write(ref _finishOrder, Interlocked.Increment(ref _order));
                Interlocked.Exchange(ref _inWrite, 0);
                Interlocked.Increment(ref _finished);
            }
        }

        /// <summary>Same as <see cref="Write"/>.</summary>
        /// <param name="logEvent">Forwarded.</param>
        /// <returns>A completed task after <see cref="Write"/> returns.</returns>
        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Records overlap, forwards dispose, and stamps the order.</summary>
        public void Dispose()
        {
            if (Volatile.Read(ref _inWrite) != 0)
                Interlocked.Exchange(ref _overlap, 1);

            _inner?.Dispose();
            Volatile.Write(ref _disposeOrder, Interlocked.Increment(ref _order));
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
