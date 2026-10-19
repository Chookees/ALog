// C4 follow-up: the open ALOG0070 window is reported at shutdown, the first event after
// close reports ALOG0072, and healthy writers are disposed while a hanging call is not.
// Next file: ALog.Tests/C4QueueFixTests.cs.

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
    /// The spec repro: 1 ms writer, 100,000 logs, the first ALOG0070 line, 100,000 more, then Dispose.
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

            // The first ALOG0070 line is the signal that the 60 s window is open. The second burst stays inside it.
            Assert.True(
                await WaitUntil(() =>
                {
                    lock (lines)
                        return lines.Exists(line => line.Contains(DropMarker, StringComparison.Ordinal));
                }, TimeSpan.FromSeconds(30)),
                "ALOG0070 did not open the window");
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
    [Fact(Timeout = 90000)]
    public async Task Shutdown_TwiceAndParallel_ReportsOpenWindowOnce()
    {
        DeadlineClock.AssertValues();
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
    [Fact(Timeout = 30000)]
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
            await DeadlineClock.WaitFor(() => WriterCount(writer) == 1, "flush did not write the event queued before close");
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
    [Fact(Timeout = 30000)]
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
            await DeadlineClock.WaitFor(() => WriterCount(writer) == 5, "flush did not write the five events queued before close");
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
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> dispose has not returned.
    /// The file contains the line that was written before the hang. The remainder is ALOG0071.
    /// </summary>
    /// <returns>A task that finishes after shutdown.</returns>
    /// <remarks>The clock is <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.</remarks>
    [Fact(Timeout = 90000)]
    public async Task Deadline_DisposesHealthyWriters_NotTheHangingCall()
    {
        DeadlineClock.AssertValues();
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        var path = Path.Combine(Path.GetTempPath(), "alog-c4-" + Guid.NewGuid().ToString("N") + ".log");
        var release = new ManualResetEventSlim(false);
        var clock = DeadlineClock.Create();
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
            .UseAsync(false), clock);
        Task? dispose = null;
        try
        {
            for (var i = 0; i < 3; i++)
                engine.Log("kept-line", LogLevel.Info);

            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(30)));
            dispose = await ArmDispose(engine, nameof(Deadline_DisposesHealthyWriters_NotTheHangingCall));
            await FinishAtShutdownDeadline(clock, dispose);
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
            await ReleaseDispose(clock, dispose);
            SelfLog.Target = previous;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Releasing the hanging writer after the deadline does not call the spy that sits behind it,
    /// and does not call the spy that already finished.
    /// </summary>
    /// <returns>A task that finishes when the processor leaves.</returns>
    [Fact(Timeout = 60000)]
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

            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(30)));
            engine.Dispose();
            var frontFinished = front.Finished;
            release.Set();
            Assert.True(await WaitUntil(() => IsProcessorStopped(engine.Queue!), TimeSpan.FromSeconds(30)));
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
    /// The same blocking instance registered twice is not disposed while either registration is inside Write.
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> dispose has not returned. The next tick returns it.
    /// After the call returns the instance is disposed at most once.
    /// </summary>
    /// <returns>A task that finishes after the blocked call is released.</returns>
    /// <remarks>The clock is <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.</remarks>
    [Fact(Timeout = 90000)]
    public async Task DuplicateWriter_IsNotDisposedWhileACallIsRunning()
    {
        DeadlineClock.AssertValues();
        var previous = SelfLog.Target;
        SelfLog.Enable(static _ => { });
        var release = new ManualResetEventSlim(false);
        var clock = DeadlineClock.Create();
        var writer = new ProbeWriter(release: release);
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .AddWriter(writer)
            .UseBackgroundQueue(true, 10, 1, TimeSpan.FromHours(1))
            .UseAsync(false), clock);
        Task? dispose = null;
        try
        {
            for (var i = 0; i < 5; i++)
                engine.Log("dup", LogLevel.Info);

            Assert.True(writer.Entered.Wait(TimeSpan.FromSeconds(30)));
            dispose = await ArmDispose(engine, nameof(DuplicateWriter_IsNotDisposedWhileACallIsRunning));
            await FinishAtShutdownDeadline(clock, dispose);
            Assert.False(writer.DisposeOverlappedWrite);
            Assert.False(writer.Disposed);
            Assert.Equal(0, writer.DisposeCount);
            release.Set();
            Assert.True(await WaitUntil(
                () => writer.DisposeCount == 1 && writer.DisposeOrder > writer.FinishOrder && !writer.DisposeOverlappedWrite,
                TimeSpan.FromSeconds(30)));
            Assert.Equal(1, writer.DisposeCount);
            var queue = engine.Queue!;
            var unwritten = queue.UnwrittenCount;
            Assert.Equal(queue.AcceptedCount, queue.WrittenCount + unwritten);
            Assert.True(unwritten >= 1);
        }
        finally
        {
            release.Set();
            await ReleaseDispose(clock, dispose);
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A writer that is still inside Write is not disposed while that call runs.
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> dispose has not returned. The next tick returns it.
    /// Dispose runs only after the call returns, and not during Write.
    /// </summary>
    /// <returns>A task that finishes after the slow call is disposed.</returns>
    /// <remarks>
    /// The release signal keeps the call inside Write until this test lets it finish.
    /// The clock is <see cref="FakeTimeProvider"/>. The 30 s waits are hang guards. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task SlowCall_IsDisposedOnlyAfterItReturns()
    {
        DeadlineClock.AssertValues();
        var previous = SelfLog.Target;
        SelfLog.Enable(static _ => { });
        var release = new ManualResetEventSlim(false);
        var clock = DeadlineClock.Create();
        var slow = new ProbeWriter(release: release);
        var behind = new ProbeWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(slow)
            .AddWriter(behind)
            .UseBackgroundQueue(true, 4, 1, TimeSpan.FromHours(1))
            .UseAsync(false), clock);
        Task? dispose = null;
        try
        {
            engine.Log("slow", LogLevel.Info);
            Assert.True(slow.Entered.Wait(TimeSpan.FromSeconds(30)));
            dispose = await ArmDispose(engine, nameof(SlowCall_IsDisposedOnlyAfterItReturns));
            await FinishAtShutdownDeadline(clock, dispose);
            Assert.False(slow.Disposed);
            Assert.False(slow.DisposeOverlappedWrite);
            release.Set();
            Assert.True(await WaitUntil(
                () => slow.Disposed && slow.DisposeOrder > slow.FinishOrder,
                TimeSpan.FromSeconds(30)));
            Assert.False(slow.DisposeOverlappedWrite);
            Assert.True(behind.Disposed);
            Assert.Equal(0, behind.Finished);
        }
        finally
        {
            release.Set();
            await ReleaseDispose(clock, dispose);
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
    /// The open-window line is still absent when dispose starts, so shutdown is what publishes it
    /// unless the deadline step also crosses the 1 ms report delay.
    /// </summary>
    /// <param name="parallel">True runs two Dispose calls at the same time.</param>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> dispose has not returned.
    /// The flush interval is one hour, so the deadline step does not tick it. Not thread-safe.
    /// </remarks>
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
            Assert.True(hang.Entered.Wait(TimeSpan.FromSeconds(30)));
            for (var i = 0; i < 3; i++)
                Assert.True(queue.TryEnqueue(Event("fill")));

            for (var i = 0; i < 7; i++)
                Assert.False(queue.TryEnqueue(Event("drop")));

            Assert.Equal(7, queue.FullDropCount);
            lock (lines)
                Assert.DoesNotContain(lines, line => line.Contains(DropMarker, StringComparison.Ordinal));
            await DisposeOpenWindow(queue, clock, parallel);
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
    /// Disposes <paramref name="queue"/> on the clock. A parallel pair both wait for the same deadline.
    /// </summary>
    /// <param name="queue">Queue whose writer is already blocked. Not null.</param>
    /// <param name="clock">The queue clock. Not null. Not yet advanced across the deadline.</param>
    /// <param name="parallel">True starts two Dispose calls.</param>
    /// <returns>A task that finishes when every dispose has returned.</returns>
    /// <remarks>
    /// Does not advance the report window on its own. The deadline step is long enough to cross the 1 ms first-report delay,
    /// so the drop line can be written by that timer during the step. Shutdown still publishes whatever remains.
    /// A failed step advances the deadline so the dispose thread can leave. Not thread-safe.
    /// </remarks>
    private static async Task DisposeOpenWindow(BackgroundLogQueue queue, FakeTimeProvider clock, bool parallel)
    {
        Task? first = null;
        Task? second = null;
        try
        {
            first = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(Shutdown_TwiceAndParallel_ReportsOpenWindowOnce));
            if (parallel)
                second = Task.Run(() => queue.Dispose());

            DeadlineClock.AssertNotReturnedBefore(
                clock, first, BackgroundLogQueue.ShutdownDeadline, DeadlineClock.ShutdownReturnedEarly);
            if (second is not null)
                DeadlineClock.AssertStillRunning(second, DeadlineClock.ShutdownReturnedEarly);

            await DeadlineClock.AssertReturnedAt(clock, first, DeadlineClock.ShutdownNotReturned);
            if (second is not null)
                await DeadlineClock.WaitForCompletion(second, DeadlineClock.ShutdownNotReturned);
            else
                queue.Dispose();
        }
        finally
        {
            await ReleaseDispose(clock, first);
            await ReleaseDispose(clock, second);
        }
    }

    /// <summary>
    /// Reads the writer count. A concurrent add makes the list throw, which this treats as not ready yet.
    /// </summary>
    /// <param name="writer">Writer being filled. Not null.</param>
    /// <returns>The current count, or -1 when the list was mid-update.</returns>
    /// <remarks>Does not throw. The caller waits until the count matches the events queued before flush.</remarks>
    private static int WriterCount(RecordingWriter writer)
    {
        try
        {
            return writer.Events.Count;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
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
    /// A release handle blocks the call until the test sets it.
    /// </summary>
    private sealed class ProbeWriter : ILogWriter
    {
        /// <summary>
        /// How long <see cref="Write"/> waits for release before it gives up.
        /// Sixty seconds is past the 30 s deadline hang guard, so a shutdown timer that is late
        /// is reported by that guard. A writer that gave up at the same 30 s would let dispose
        /// return, and the failure would be a later dispose flag instead of the deadline step.
        /// </summary>
        private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _delay;
        private readonly ManualResetEventSlim? _release;
        private readonly ILogWriter? _inner;
        private int _finished;
        private int _inWrite;
        private int _disposed;
        private int _after;
        private int _overlap;
        private int _disposeCount;
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
        /// <param name="release">Optional block. The wait is at most <see cref="ReleaseBound"/>, past the deadline hang guard.</param>
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

        /// <summary>How many times <see cref="Dispose"/> has been entered. A shared instance must be disposed at most once.</summary>
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        /// <summary>Order stamp taken when the call returns. Zero if the call has not returned.</summary>
        internal int FinishOrder => Volatile.Read(ref _finishOrder);

        /// <summary>Order stamp taken when dispose runs. Zero if dispose has not run.</summary>
        internal int DisposeOrder => Volatile.Read(ref _disposeOrder);

        /// <summary>Sleeps, waits for release, then forwards. A dispose during the call is recorded.</summary>
        /// <param name="logEvent">Forwarded to the inner writer. May be null.</param>
        /// <exception cref="TimeoutException">The release handle was not set within <see cref="ReleaseBound"/>.</exception>
        public void Write(LogEvent logEvent)
        {
            Interlocked.Exchange(ref _inWrite, 1);
            Entered.Set();
            try
            {
                if (_delay > TimeSpan.Zero)
                    Thread.Sleep(_delay);

                if (_release is not null && !_release.Wait(ReleaseBound))
                    throw new TimeoutException("The test did not release the writer within 60 s.");

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

            Interlocked.Increment(ref _disposeCount);
            _inner?.Dispose();
            Volatile.Write(ref _disposeOrder, Interlocked.Increment(ref _order));
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
