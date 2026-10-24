using System.Diagnostics;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// The idle wait must wake on an enqueue, and dispose must stop the processor on every clock.
/// </summary>
public sealed class QueueIdleLifecycleTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>
    /// An event queued while the processor is inside a one-hour idle wait is written without moving the clock.
    /// </summary>
    /// <returns>A task that finishes after the event is written and the queue is disposed.</returns>
    /// <remarks>
    /// The flush interval is one hour and the clock is never advanced, so a lost wake leaves the writer empty until the test times out.
    /// The idle flag is exchanged, not stored and then loaded, so the enqueue cannot miss the wait.
    /// Not thread-safe beyond the processor thread the queue starts. The spin bounds are hang guards, not the pass condition.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task IdleEnqueue_WritesWithoutAdvancingTheClock()
    {
        await Task.CompletedTask;
        var clock = NewClock();
        var writer = new RecordingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, Hour, false, clock));
        try
        {
            Assert.True(WaitUntil(() => queue.IsIdleWaitEntered), "processor never entered the idle wait");
            Assert.True(queue.TryEnqueue(Event("woke")));
            Assert.True(WaitUntil(() => writer.Events.Count == 1), "event stayed queued until the flush interval");
            Assert.Equal("woke", writer.Events[0].Message);
        }
        finally
        {
            queue.Dispose();
        }

        Assert.True(queue.ReleaseWriters);
        Assert.True(writer.Disposed);
    }

    /// <summary>
    /// Dispose of an idle queue on a frozen clock stops the processor and does not require the clock to move.
    /// </summary>
    /// <returns>A task that finishes after dispose returns.</returns>
    /// <remarks>
    /// The processor is inside the one-hour idle wait. Dispose cancels that wait. A path that waits for the timer
    /// never returns, because this clock is not advanced. <see cref="BackgroundLogQueue.ReleaseWriters"/> is set only
    /// after the processor has stopped, which is also when the wake and shutdown sources are disposed.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task FrozenClock_IdleDispose_StopsTheProcessor()
    {
        await Task.CompletedTask;
        var clock = NewClock();
        var writer = new RecordingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, Hour, false, clock));
        Assert.True(WaitUntil(() => queue.IsIdleWaitEntered), "processor never entered the idle wait");
        queue.Dispose();
        Assert.True(queue.ReleaseWriters);
        Assert.True(writer.Disposed);
        Assert.Empty(writer.Events);
    }

    /// <summary>
    /// Dispose on the production clock stops each dedicated processor instead of leaving it in the idle timer.
    /// </summary>
    /// <returns>A task that finishes after the thread count has settled.</returns>
    /// <remarks>
    /// Each queue uses <see cref="ProductionClock"/> via the public constructor and a one-hour flush interval.
    /// The event is written, then dispose returns only after the processor has stopped.
    /// Twenty-four queues that leaked their thread would leave the process well above the bound.
    /// The bound allows a few extra pool threads. The wait after the loop is bounded by two seconds.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task ProductionClock_IdleDispose_DoesNotLeakProcessorThreads()
    {
        await Task.CompletedTask;
        var before = Process.GetCurrentProcess().Threads.Count;
        const int rounds = 24;
        for (var i = 0; i < rounds; i++)
        {
            var writer = new RecordingWriter();
            var queue = new BackgroundLogQueue(
                new ILogWriter[] { writer },
                capacity: 8,
                batchSize: 1,
                flushInterval: Hour,
                asyncEnabled: false);
            Assert.True(queue.TryEnqueue(Event("n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            queue.Dispose();
            Assert.True(queue.ReleaseWriters);
            Assert.True(writer.Disposed);
            Assert.Single(writer.Events);
        }

        var limit = before + 8;
        var after = Process.GetCurrentProcess().Threads.Count;
        var spins = 0;
        while (after > limit && spins < 200)
        {
            await Task.Delay(10);
            after = Process.GetCurrentProcess().Threads.Count;
            spins++;
        }

        Assert.True(after <= limit, "processor threads stayed up: before " + before.ToString(System.Globalization.CultureInfo.InvariantCulture) + " after " + after.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Spins until <paramref name="ready"/> or the bound is exhausted.</summary>
    /// <param name="ready">Condition the processor or the writer publishes. Not null.</param>
    /// <returns>True when the condition became true inside the bound.</returns>
    /// <remarks>
    /// Does not throw. The bound is one million spins, so a lost wake or a stuck processor returns false
    /// and the caller asserts. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static bool WaitUntil(Func<bool> ready)
    {
        for (var i = 0; i < 1_000_000; i++)
        {
            if (ready())
                return true;

            Thread.SpinWait(50);
        }

        return ready();
    }

    /// <summary>A frozen clock at 2026-06-01 UTC. It is never advanced.</summary>
    /// <returns>The clock. Not null.</returns>
    /// <remarks>Does not throw. Does not move time.</remarks>
    private static FakeTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Builds an info event. The timestamp is fixed so the test does not read the clock.</summary>
    /// <param name="message">Rendered message. Not null.</param>
    /// <returns>An event with no exception.</returns>
    /// <remarks>Does not throw. Not thread-safe. Only the test thread calls it.</remarks>
    private static LogEvent Event(string message)
        => new(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), LogLevel.Info, message, null, null);
}
