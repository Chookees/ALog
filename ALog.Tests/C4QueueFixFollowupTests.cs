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
            await FinishAtShutdownDeadline(clock, engine, dispose);
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
            await FinishAtShutdownDeadline(clock, engine, dispose);
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
            await FinishAtShutdownDeadline(clock, engine, dispose);
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
    /// Disposes <paramref name="queue"/> on the clock. A parallel second call returns without waiting.
    /// </summary>
    /// <param name="queue">Queue whose writer is already blocked. Not null.</param>
    /// <param name="clock">The queue clock. Not null. Not yet advanced across the deadline.</param>
    /// <param name="parallel">True starts two Dispose calls.</param>
    /// <returns>A task that finishes when every dispose has returned.</returns>
    /// <remarks>
    /// Does not advance the report window on its own. The deadline step is long enough to cross the 1 ms first-report delay,
    /// so the drop line can be written by that timer during the step. Shutdown still publishes whatever remains.
    /// A parallel second call runs on the pool under the 30 s hang guard. The clock timestamp stays the same and the first task stays running.
    /// A failed step advances the deadline so the dispose thread can leave. Not thread-safe.
    /// </remarks>
    private static async Task DisposeOpenWindow(BackgroundLogQueue queue, FakeTimeProvider clock, bool parallel)
    {
        Task? first = null;
        try
        {
            first = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(Shutdown_TwiceAndParallel_ReportsOpenWindowOnce));
            if (parallel)
                await AssertSecondDisposeDoesNotWait(clock, first!, queue.Dispose);

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
            await DeadlineClock.WaitForCompletion(first, "shutdown hung");
            if (!parallel)
                queue.Dispose();
        }
        finally
        {
            await ReleaseDispose(clock, first);
        }
    }

    /// <summary>
    /// The cutoff timer flag stays clear one tick before <see cref="BackgroundLogQueue.NewWriteBudget"/> and is set on that tick.
    /// The elapsed comparison is not read here.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The flag is set on the thread that advances the clock. A late timer uses the late sentence.
    /// The helper may wait out the 30 s hang guard. The message does not say the cutoff hung.
    /// The writer stays blocked until the flag checks have finished. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task CutoffTimer_StaysClearUntilNewWriteBudget()
    {
        DeadlineClock.AssertValues();
        var clock = DeadlineClock.Create();
        var writer = new BlockingWriter();
        var queue = QueueOnClock(writer, clock);
        Task? dispose = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(CutoffTimer_StaysClearUntilNewWriteBudget));
            await DeadlineClock.AssertNotElapsed(
                clock,
                () => queue.CutoffElapsed != 0,
                BackgroundLogQueue.NewWriteBudget,
                "cutoff");
            await DeadlineClock.AssertElapsed(
                clock,
                () => queue.CutoffElapsed != 0,
                BackgroundLogQueue.NewWriteBudget,
                "cutoff");
        }
        finally
        {
            writer.Release();
            await ReleaseDispose(clock, dispose);
        }
    }

    /// <summary>
    /// A new write is still accepted one tick before <see cref="BackgroundLogQueue.NewWriteBudget"/> and blocked on that tick.
    /// The cutoff timer is not read here.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The check is the shared elapsed comparison. A timer that fires early does not change this result one tick before the budget.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task NewWriteBudget_Comparison_BlocksAtTheBound()
    {
        DeadlineClock.AssertValues();
        var clock = DeadlineClock.Create();
        var writer = new BlockingWriter();
        var queue = QueueOnClock(writer, clock);
        Task? dispose = null;
        try
        {
            Assert.False(queue.NewWriteBudgetComparisonSpent);
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(NewWriteBudget_Comparison_BlocksAtTheBound));
            var early = BackgroundLogQueue.NewWriteBudget - DeadlineClock.Tick;
            clock.Advance(early);
            if (queue.NewWriteBudgetComparisonSpent)
                Assert.Fail(DeadlineClock.ComparisonEarly(early));

            clock.Advance(DeadlineClock.Tick);
            if (!queue.NewWriteBudgetComparisonSpent)
                Assert.Fail(DeadlineClock.ComparisonLate(BackgroundLogQueue.NewWriteBudget));
        }
        finally
        {
            writer.Release();
            await ReleaseDispose(clock, dispose);
        }
    }

    /// <summary>
    /// A second queue <see cref="BackgroundLogQueue.Dispose"/> returns on the test thread while the first call is still waiting.
    /// It does not move the clock and it does not add an ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes after the first call returns.</returns>
    /// <remarks>
    /// The writer stays blocked so the first call is armed. The second call runs on the pool under the 30 s hang guard.
    /// The clock timestamp stays the same and the first task stays running.
    /// The first call still stops at <see cref="BackgroundLogQueue.ShutdownDeadline"/>. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task DisposeAgain_Queue_ReturnsWhileTheFirstWaitIsArmed()
    {
        await AssertSecondDisposeReturns(queueOnly: true);
    }

    /// <summary>
    /// A second engine <see cref="LoggerEngine.Dispose"/> returns on the test thread while the first call is still waiting.
    /// It does not move the clock and it does not add an ALOG0071 line.
    /// </summary>
    /// <returns>A task that finishes after the first call returns.</returns>
    /// <remarks>
    /// The writer stays blocked so the first call is inside the queue. The second call runs on the pool under the 30 s hang guard.
    /// The clock timestamp stays the same and the first task stays running.
    /// The first call still stops at <see cref="BackgroundLogQueue.ShutdownDeadline"/>. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task DisposeAgain_Engine_ReturnsWhileTheFirstWaitIsArmed()
    {
        await AssertSecondDisposeReturns(queueOnly: false);
    }

    /// <summary>
    /// Runs a second dispose on the pool and fails when it is still running after the hang guard.
    /// </summary>
    /// <param name="clock">Clock the first dispose is waiting on. Not null. This method does not advance it.</param>
    /// <param name="first">The armed dispose. Not null. Must still be running when the second call returns.</param>
    /// <param name="secondDispose">The second dispose. Not null. Must return without waiting for <paramref name="first"/>.</param>
    /// <returns>A task that finishes when the second dispose has returned.</returns>
    /// <remarks>
    /// The fake-clock timestamp must be unchanged, and <paramref name="first"/> must still be running.
    /// The 30 s wait is a hang guard. It is not a product deadline.
    /// A second dispose that waits for the first call stays inside this guard, because the clock is not advanced.
    /// Not thread-safe beyond the pool thread that runs <paramref name="secondDispose"/>.
    /// </remarks>
    private static async Task AssertSecondDisposeDoesNotWait(FakeTimeProvider clock, Task first, Action secondDispose)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(secondDispose);

        var stamp = clock.GetTimestamp();
        var second = Task.Run(secondDispose);
        var returned = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30))) == second;
        Assert.True(returned, "second Dispose waited for the first deadline; it must return immediately");
        await second;
        Assert.Equal(stamp, clock.GetTimestamp());
        Assert.False(first.IsCompleted, "first dispose returned before the deadline");
    }

    /// <summary>
    /// Arms one dispose on a blocked writer, calls dispose again on the pool, then finishes the first at the deadline.
    /// </summary>
    /// <param name="queueOnly">True disposes the queue. False disposes the engine.</param>
    /// <returns>A task that finishes after the single ALOG0071 line is checked.</returns>
    /// <remarks>
    /// The second call runs on the pool. The late sentence belongs to the first call only.
    /// Not thread-safe. The pool thread runs the second call.
    /// </remarks>
    private static async Task AssertSecondDisposeReturns(bool queueOnly)
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
        BackgroundLogQueue? queue = null;
        LoggerEngine? engine = null;
        Task? first = null;
        try
        {
            if (queueOnly)
            {
                queue = QueueOnClock(writer, clock);
                Assert.True(queue.TryEnqueue(Event("held")));
                Assert.True(queue.TryEnqueue(Event("queued")));
            }
            else
            {
                engine = EngineOnClock(writer, 4, 1, clock);
                engine.Log("held", LogLevel.Info);
                engine.Log("queued", LogLevel.Info);
                queue = engine.Queue;
            }

            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            first = await DeadlineClock.RunUntilArmed(
                queueOnly ? queue!.Dispose : engine!.Dispose,
                () => queue!.DisposeWaitsStarted,
                queueOnly
                    ? nameof(DisposeAgain_Queue_ReturnsWhileTheFirstWaitIsArmed)
                    : nameof(DisposeAgain_Engine_ReturnsWhileTheFirstWaitIsArmed));
            await AssertSecondDisposeDoesNotWait(
                clock,
                first,
                queueOnly ? queue!.Dispose : engine!.Dispose);
            lock (lines)
                Assert.DoesNotContain(lines, line => line.StartsWith(DeadlinePrefix, StringComparison.Ordinal));
            await DeadlineClock.AssertNotElapsed(
                clock,
                () => queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.AssertElapsed(
                clock,
                () => queue!.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.WaitForCompletion(first, "shutdown hung");
            lock (lines)
                Assert.Single(lines, line => line.StartsWith(DeadlinePrefix, StringComparison.Ordinal));
        }
        finally
        {
            writer.Release();
            await ReleaseDispose(clock, first);
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// The shutdown wait rejects a null clock, signal, or deadline callback, and does not arm a timer.
    /// </summary>
    [Fact]
    public void ShutdownWait_RejectsNullArguments()
    {
        using var signaled = new ManualResetEventSlim(false);
        var clock = DeadlineClock.Create();
        var armed = 0;
        Assert.Throws<ArgumentNullException>(() =>
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(null!, signaled, static () => { }, ref armed));
        Assert.Throws<ArgumentNullException>(() =>
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(clock, null!, static () => { }, ref armed));
        Assert.Throws<ArgumentNullException>(() =>
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(clock, signaled, null!, ref armed));
        Assert.Equal(0, armed);
    }

    /// <summary>
    /// A signal that is already set returns without registering a deadline timer.
    /// </summary>
    [Fact]
    public void ShutdownWait_ReturnsWhenAlreadySignaled()
    {
        using var signaled = new ManualResetEventSlim(true);
        var clock = DeadlineClock.Create();
        var armed = 0;
        var fired = 0;
        BackgroundLogQueue.WaitUntilSignaledOrDeadline(clock, signaled, () => fired++, ref armed);
        Assert.Equal(0, armed);
        Assert.Equal(0, fired);
    }

    /// <summary>
    /// A signal that wins on the deadline tick still records the deadline when the timer callback has not run.
    /// </summary>
    /// <returns>A task that finishes when the wait returns.</returns>
    /// <remarks>
    /// The clock does not run the callback. The product records the deadline from the same due value as the timer.
    /// Not thread-safe. Only the test thread moves the clock.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ShutdownWait_RecordsTheDeadlineWhenTheSignalWinsOnTheBound()
    {
        using var signaled = new ManualResetEventSlim(false);
        var clock = new ManualClock();
        var fired = 0;
        var armed = 0;
        var wait = Task.Run(() =>
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(
                clock,
                signaled,
                () => Interlocked.Increment(ref fired),
                ref armed));
        await DeadlineClock.WaitFor(
            () => Volatile.Read(ref armed) >= 1,
            nameof(ShutdownWait_RecordsTheDeadlineWhenTheSignalWinsOnTheBound) + " did not arm");
        clock.SetElapsed(BackgroundLogQueue.ShutdownDeadline);
        signaled.Set();
        await DeadlineClock.WaitForCompletion(wait, "shutdown wait hung");
        Assert.Equal(1, Volatile.Read(ref fired));
        Assert.Equal(0, clock.CallbackRuns);
    }

    /// <summary>
    /// A callback that arrives after the wait has returned does not escape a disposed wake handle.
    /// </summary>
    /// <returns>A task that finishes after the callback.</returns>
    /// <remarks>
    /// The signal wins before the deadline, so the wait does not record it. The callback then runs on a disposed handle.
    /// Not thread-safe. Only the test thread invokes the callback.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ShutdownWait_CallbackAfterTheWaitReturnedDoesNotThrow()
    {
        using var signaled = new ManualResetEventSlim(false);
        var clock = new ManualClock();
        var fired = 0;
        var armed = 0;
        var wait = Task.Run(() =>
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(
                clock,
                signaled,
                () => Interlocked.Increment(ref fired),
                ref armed));
        await DeadlineClock.WaitFor(
            () => Volatile.Read(ref armed) >= 1,
            nameof(ShutdownWait_CallbackAfterTheWaitReturnedDoesNotThrow) + " did not arm");
        signaled.Set();
        await DeadlineClock.WaitForCompletion(wait, "shutdown wait hung");
        Assert.Equal(0, Volatile.Read(ref fired));
        clock.InvokeCallback();
        Assert.Equal(1, Volatile.Read(ref fired));
        Assert.Equal(1, clock.CallbackRuns);
    }

    /// <summary>
    /// Builds a queue whose flush interval cannot tick during one deadline step.
    /// </summary>
    /// <param name="writer">The only writer. Not null. Not disposed here.</param>
    /// <param name="clock">Clock for shutdown, flush, and the cutoff. Not null.</param>
    /// <returns>An open queue.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="clock"/> is null.</exception>
    /// <remarks>Does not throw otherwise. Synchronous writes. The caller disposes the queue.</remarks>
    private static BackgroundLogQueue QueueOnClock(BlockingWriter writer, FakeTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        return new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(4, 1, TimeSpan.FromHours(1), false, clock));
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
    /// A clock whose time moves only when the test says so, and whose timer callback runs only when the test invokes it.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private long _stamp;
        private TimerCallback? _callback;
        private object? _state;
        private int _callbackRuns;

        /// <summary>Stamps are <see cref="TimeSpan"/> ticks.</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>The stamp last stored by <see cref="SetElapsed"/>.</summary>
        /// <returns>The current stamp. Zero until <see cref="SetElapsed"/>.</returns>
        public override long GetTimestamp() => Volatile.Read(ref _stamp);

        /// <summary>How many times <see cref="InvokeCallback"/> has run the stored callback.</summary>
        internal int CallbackRuns => Volatile.Read(ref _callbackRuns);

        /// <summary>
        /// Moves the clock to <paramref name="elapsed"/> from zero. Does not run a callback.
        /// </summary>
        /// <param name="elapsed">Time since the clock started. Not negative.</param>
        internal void SetElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");

            Volatile.Write(ref _stamp, elapsed.Ticks);
        }

        /// <summary>
        /// Runs the callback stored by <see cref="CreateTimer"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">No timer has been registered.</exception>
        internal void InvokeCallback()
        {
            TimerCallback callback;
            object? state;
            lock (_gate)
            {
                if (_callback is null)
                    throw new InvalidOperationException("No timer was registered.");

                callback = _callback;
                state = _state;
            }

            Interlocked.Increment(ref _callbackRuns);
            callback(state);
        }

        /// <summary>
        /// Stores the callback and returns a timer that does not fire by itself.
        /// </summary>
        /// <param name="callback">Stored and later passed to <see cref="InvokeCallback"/>. Not null.</param>
        /// <param name="state">Passed back to <paramref name="callback"/>.</param>
        /// <param name="dueTime">Ignored. The test moves time with <see cref="SetElapsed"/>.</param>
        /// <param name="period">Ignored. The timer does not repeat.</param>
        /// <returns>A timer whose <see cref="ITimer.Change"/> reports success and whose dispose does nothing.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> is null.</exception>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                _callback = callback;
                _state = state;
            }

            return new IdleTimer();
        }

        /// <summary>A timer that never fires and has nothing to dispose.</summary>
        private sealed class IdleTimer : ITimer
        {
            /// <summary>Accepts a new due time and does not schedule it.</summary>
            /// <param name="dueTime">Ignored.</param>
            /// <param name="period">Ignored.</param>
            /// <returns>True. The product does not call this.</returns>
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            /// <summary>Does nothing.</summary>
            public void Dispose()
            {
            }

            /// <summary>Does nothing.</summary>
            /// <returns>A completed task.</returns>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Records dispose-during-write, optional sleep, and an optional inner writer.
    /// A release handle blocks the call until the test sets it.
    /// </summary>
    private sealed class ProbeWriter : ILogWriter
    {
        /// <summary>
        /// How long <see cref="Write"/> waits for release before it gives up.
        /// This is the 30 s hang guard. It is not a product deadline.
        /// </summary>
        private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(30);
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
        /// <param name="release">Optional block. The wait is at most <see cref="ReleaseBound"/>, the 30 s hang guard.</param>
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
                    throw new TimeoutException("The test did not release the writer within 30 s.");

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
