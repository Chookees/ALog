// CLK-07: every wait creates its timer and timestamp on the injected clock.
// TimeProvider.System stays in ProductionClock only. Next file: ALog.Tests/DeadlineClock.cs.

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
