// CLK-07: every wait creates its timer and timestamp on the injected clock.
// TimeProvider.System stays in ProductionClock only. Next file: ALog.Tests/DeadlineClock.cs.

using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// Counts timer and timestamp calls on the injected clock for each wait.
/// </summary>
public sealed class ClockCompositionTests
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The full-queue report timer and its timestamp read are on the injected clock.
    /// </summary>
    /// <returns>A task that finishes after the drop.</returns>
    /// <remarks>
    /// A timer created on <c>TimeProvider.System</c> does not increment the count.
    /// The 30 s waits are hang guards. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task ReportTimer_IsCreatedOnTheInjectedClock()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var queue = Queue(writer, clock, capacity: 1);
        try
        {
            Assert.True(
                clock.Count(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan) >= 1,
                "report timer was not created on the injected clock");
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            var utcBefore = clock.UtcReads;
            Assert.True(queue.TryEnqueue(Event("dropped")) == false, "the full event was accepted");
            Assert.True(clock.UtcReads > utcBefore, "report timestamp was not read from the injected clock");
        }
        finally
        {
            writer.Release();
            queue.Dispose();
        }
    }

    /// <summary>
    /// The idle flush-interval timer is created on the injected clock.
    /// </summary>
    /// <returns>A task that finishes after the processor enters the idle wait.</returns>
    /// <remarks>
    /// <see cref="PeriodicTimer"/> calls <see cref="TimeProvider.CreateTimer"/> with the interval as both due and period.
    /// A system timer does not increment the count. The 30 s wait is a hang guard. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task FlushInterval_IsCreatedOnTheInjectedClock()
    {
        var clock = new CountingClock();
        var writer = new RecordingWriter();
        var queue = Queue(writer, clock, capacity: 4);
        try
        {
            await DeadlineClock.WaitFor(
                () => queue.IsIdleWaitEntered,
                "processor never entered the idle wait");
            Assert.True(
                clock.Count(IdleInterval, IdleInterval) >= 1,
                "flush interval timer was not created on the injected clock");
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>
    /// The flush deadline timer is created on the injected clock.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The count is read when the flush wait is armed, before the clock moves.
    /// A system timer leaves the count unchanged. The 30 s waits are hang guards. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task FlushDeadline_IsCreatedOnTheInjectedClock()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var queue = Queue(writer, clock, capacity: 4);
        try
        {
            var before = clock.Count(BackgroundLogQueue.ShutdownDeadline, Timeout.InfiniteTimeSpan);
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            var flush = queue.FlushAsync();
            await DeadlineClock.WaitFor(
                () => queue.FlushWaitsStarted >= 1,
                nameof(FlushDeadline_IsCreatedOnTheInjectedClock) + " did not arm the flush wait");
            Assert.True(
                clock.Count(BackgroundLogQueue.ShutdownDeadline, Timeout.InfiniteTimeSpan) > before,
                "flush timer was not created on the injected clock");
            writer.Release();
            await DeadlineClock.WaitForCompletion(flush, "flush hung");
        }
        finally
        {
            writer.Release();
            queue.Dispose();
        }
    }

    /// <summary>
    /// The new-write cutoff timer is created on the injected clock.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The shutdown timer is not checked here. A system cutoff timer leaves this count unchanged.
    /// The 30 s waits are hang guards. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task CutoffTimer_IsCreatedOnTheInjectedClock()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var queue = Queue(writer, clock, capacity: 4);
        Task? dispose = null;
        try
        {
            var before = clock.Count(BackgroundLogQueue.NewWriteBudget, Timeout.InfiniteTimeSpan);
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(CutoffTimer_IsCreatedOnTheInjectedClock));
            Assert.True(
                clock.Count(BackgroundLogQueue.NewWriteBudget, Timeout.InfiniteTimeSpan) > before,
                "cutoff timer was not created on the injected clock");
        }
        finally
        {
            writer.Release();
            if (dispose is not null)
                await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
        }
    }

    /// <summary>
    /// The shutdown deadline timer and its timestamp read are on the injected clock.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The cutoff timer is not checked here. A system shutdown wait leaves the deadline-timer count unchanged.
    /// The 30 s waits are hang guards. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task ShutdownTimer_IsCreatedOnTheInjectedClock()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var queue = Queue(writer, clock, capacity: 4);
        Task? dispose = null;
        try
        {
            var timersBefore = clock.Count(BackgroundLogQueue.ShutdownDeadline, Timeout.InfiniteTimeSpan);
            var stampsBefore = clock.TimestampReads;
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(ShutdownTimer_IsCreatedOnTheInjectedClock));
            Assert.True(
                clock.Count(BackgroundLogQueue.ShutdownDeadline, Timeout.InfiniteTimeSpan) > timersBefore,
                "shutdown timer was not created on the injected clock");
            Assert.True(
                clock.TimestampReads > stampsBefore,
                "shutdown timestamp was not read from the injected clock");
        }
        finally
        {
            writer.Release();
            if (dispose is not null)
                await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
        }
    }

    /// <summary>
    /// A second queue dispose does not enter the deadline wait.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The entry count does not depend on which clock a second call would pass.
    /// The 30 s wait is a hang guard. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task DisposeAgain_Queue_DoesNotEnterTheDeadlineWait()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var queue = Queue(writer, clock, capacity: 4);
        Task? dispose = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(DisposeAgain_Queue_DoesNotEnterTheDeadlineWait));
            await AssertSecondDisposeLeavesTheWait(clock, dispose, queue.Dispose);
        }
        finally
        {
            writer.Release();
            if (dispose is not null)
                await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
        }
    }

    /// <summary>
    /// A second engine dispose does not enter the deadline wait.
    /// </summary>
    /// <returns>A task that finishes after the writer is released.</returns>
    /// <remarks>
    /// The engine's second call returns before the queue wait. The entry count stays on the first call.
    /// The 30 s wait is a hang guard. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task DisposeAgain_Engine_DoesNotEnterTheDeadlineWait()
    {
        var clock = new CountingClock();
        var writer = new BlockingWriter();
        var engine = Engine(writer, clock);
        Task? dispose = null;
        try
        {
            engine.Log("held", LogLevel.Info);
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            dispose = await DeadlineClock.RunUntilArmed(
                engine.Dispose,
                () => engine.Queue!.DisposeWaitsStarted,
                nameof(DisposeAgain_Engine_DoesNotEnterTheDeadlineWait));
            await AssertSecondDisposeLeavesTheWait(clock, dispose, engine.Dispose);
        }
        finally
        {
            writer.Release();
            if (dispose is not null)
                await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
        }
    }

    /// <summary>
    /// Runs a second dispose and fails when that call enters the deadline wait.
    /// </summary>
    /// <param name="clock">Clock the first dispose is waiting on. Not null. This method does not advance it.</param>
    /// <param name="first">The armed dispose. Not null. Must still be running when the second call returns.</param>
    /// <param name="secondDispose">The second dispose. Not null.</param>
    /// <returns>A task that finishes when the second dispose has returned.</returns>
    /// <remarks>
    /// The entry count is taken before the second call. A call that passes another clock still moves it.
    /// The 30 s wait is a hang guard. Not thread-safe beyond the pool thread that runs the second call.
    /// </remarks>
    private static async Task AssertSecondDisposeLeavesTheWait(FakeTimeProvider clock, Task first, Action secondDispose)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(secondDispose);

        var entries = BackgroundLogQueue.DeadlineWaitEntries;
        var second = Task.Run(secondDispose);
        var returned = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(30))) == second;
        Assert.True(returned, "second Dispose waited for the first deadline; it must return immediately.");
        await second;
        Assert.True(
            BackgroundLogQueue.DeadlineWaitEntries == entries,
            "second Dispose entered a deadline wait; it must return immediately.");
        Assert.True(first.IsCompleted == false, "first dispose returned before the deadline");
    }

    /// <summary>Builds a queue on <paramref name="clock"/> with a one-hour idle interval.</summary>
    /// <param name="writer">The only writer. Not null.</param>
    /// <param name="clock">Clock that counts its own calls. Not null.</param>
    /// <param name="capacity">Channel capacity. At least 1.</param>
    /// <returns>An open queue. The caller disposes it.</returns>
    private static BackgroundLogQueue Queue(ILogWriter writer, CountingClock clock, int capacity)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        return new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(capacity, 1, IdleInterval, false, clock));
    }

    /// <summary>Builds an engine whose queue uses <paramref name="clock"/> and a one-hour idle interval.</summary>
    /// <param name="writer">The only writer. Not null.</param>
    /// <param name="clock">Clock that counts its own calls. Not null.</param>
    /// <returns>An engine that has not been disposed.</returns>
    private static LoggerEngine Engine(ILogWriter writer, CountingClock clock)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        return new LoggerEngine(
            new LoggerConfig()
                .AddWriter(writer)
                .UseBackgroundQueue(true, 4, 1, IdleInterval)
                .UseAsync(false),
            clock);
    }

    /// <summary>Builds an info event. The timestamp is fixed so the test does not read the clock.</summary>
    /// <param name="message">Rendered message. Not null.</param>
    /// <returns>An event with no exception.</returns>
    private static LogEvent Event(string message)
        => new(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that counts timers and timestamp reads.
    /// </summary>
    private sealed class CountingClock : FakeTimeProvider
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan Due, TimeSpan Period)> _timers = new();
        private int _timestamps;
        private int _utcReads;

        /// <summary>Starts at 2026-06-01 UTC and does not move until Advance.</summary>
        public CountingClock()
            : base(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
        {
        }

        /// <summary>How many times <see cref="GetTimestamp"/> has run.</summary>
        public int TimestampReads => Volatile.Read(ref _timestamps);

        /// <summary>How many times <see cref="GetUtcNow"/> has run.</summary>
        public int UtcReads => Volatile.Read(ref _utcReads);

        /// <summary>How many timers were created with this due time and period.</summary>
        /// <param name="due">Due time passed to <see cref="CreateTimer"/>.</param>
        /// <param name="period">Period passed to <see cref="CreateTimer"/>.</param>
        /// <returns>The matching count.</returns>
        public int Count(TimeSpan due, TimeSpan period)
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var call in _timers)
                {
                    if (call.Due == due && call.Period == period)
                        count++;
                }

                return count;
            }
        }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
                _timers.Add((dueTime, period));

            return base.CreateTimer(callback, state, dueTime, period);
        }

        /// <inheritdoc />
        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestamps);
            return base.GetTimestamp();
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _utcReads);
            return base.GetUtcNow();
        }
    }
}
