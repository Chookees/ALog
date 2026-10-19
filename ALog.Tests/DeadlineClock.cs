// Moves a test clock across the shutdown and flush bounds.
// One tick short of a bound must not finish the wait. The next tick must.
// Wall time appears only as the 30 s hang guard. Next file: ALog.Tests/C4QueueFixTests.cs.

using System.Diagnostics;
using ALog.Core;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// Shared steps for shutdown, flush, and cutoff tests that inject <see cref="FakeTimeProvider"/>.
/// </summary>
internal static class DeadlineClock
{
    /// <summary>One provider tick. The smallest step that can cross a bound.</summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    /// <summary>Hang guard for a wait that should already be unblocked. Not a product deadline.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to let a pool thread finish after a clock step that must not release it.
    /// The deadline callback only wakes the waiter; the operation then ends on another thread.
    /// A timer that has not fired cannot finish during this pause, so it is not a product bound.
    /// </summary>
    private static readonly TimeSpan EarlyObserve = TimeSpan.FromMilliseconds(100);

    /// <summary>Most polls inside <see cref="HangGuard"/>. The loop stops at this count even if the clock jumps.</summary>
    private const int MaxPolls = 4000;

    /// <summary>Failure text when shutdown finishes on the tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/>.</summary>
    internal const string ShutdownReturnedEarly =
        "shutdown returned before ShutdownDeadline (at deadline - 1 tick)";

    /// <summary>Failure text when shutdown is still running at <see cref="BackgroundLogQueue.ShutdownDeadline"/>.</summary>
    internal const string ShutdownNotReturned =
        "shutdown has not returned at ShutdownDeadline";

    /// <summary>Failure text when flush finishes on the tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/>.</summary>
    internal const string FlushReturnedEarly =
        "flush returned before ShutdownDeadline (at deadline - 1 tick)";

    /// <summary>Failure text when flush is still running at <see cref="BackgroundLogQueue.ShutdownDeadline"/>.</summary>
    internal const string FlushNotReturned =
        "flush has not returned at ShutdownDeadline";

    /// <summary>Failure text when the new-write cutoff closes on the tick before <see cref="BackgroundLogQueue.NewWriteBudget"/>.</summary>
    internal const string CutoffBlockedEarly = "new writes blocked before NewWriteBudget";

    /// <summary>Failure text when a new writer call is still allowed at <see cref="BackgroundLogQueue.NewWriteBudget"/>.</summary>
    internal const string CutoffStillOpen = "new writes still open at NewWriteBudget";

    /// <summary>
    /// Pins the product bounds at 5000 ms and 4900 ms.
    /// A one-millisecond edit of either constant fails the tests that call this, even though those tests advance by the constant.
    /// </summary>
    /// <remarks>
    /// Does not throw except through the assertion. Not thread-safe. Only the test thread calls it.
    /// The values are the C4 contract. They are not a new deadline.
    /// </remarks>
    internal static void AssertValues()
    {
        Assert.Equal(5000d, BackgroundLogQueue.ShutdownDeadline.TotalMilliseconds);
        Assert.Equal(4900d, BackgroundLogQueue.NewWriteBudget.TotalMilliseconds);
        Assert.True(BackgroundLogQueue.NewWriteBudget < BackgroundLogQueue.ShutdownDeadline);
    }

    /// <summary>
    /// Builds a clock that starts at 2026-06-01 UTC and does not move until <see cref="FakeTimeProvider.Advance"/>.
    /// </summary>
    /// <returns>A clock with no automatic advance.</returns>
    /// <remarks>Does not throw. The date is fixed so a test can compare <see cref="TimeProvider.GetUtcNow"/> after a run.</remarks>
    internal static FakeTimeProvider Create()
        => new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Polls <paramref name="ready"/> until it is true, or for <see cref="HangGuard"/>.
    /// </summary>
    /// <param name="ready">Condition. Called from the test thread. Must not be null. A throw fails the test.</param>
    /// <param name="message">Assertion text when the guard wins. Not null or empty.</param>
    /// <returns>A task that finishes when the condition was seen or the assertion fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ready"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    /// <remarks>
    /// The 10 ms pause is the poll, not a product bound and not a new wall-clock limit under 30 s.
    /// The loop is capped at <see cref="MaxPolls"/>. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static async Task WaitFor(Func<bool> ready, string message)
    {
        ArgumentNullException.ThrowIfNull(ready);
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("Wait message is required.", nameof(message));

        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < MaxPolls; i++)
        {
            if (ready())
                return;

            if (Stopwatch.GetElapsedTime(started) >= HangGuard)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(ready(), message);
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the pool and waits until <paramref name="armed"/> is at least 1.
    /// </summary>
    /// <param name="body">The dispose or flush start. Not null. May block until the clock moves.</param>
    /// <param name="armed">How many clock waits have been registered. Not null.</param>
    /// <param name="name">Test name used in failure text. Not empty.</param>
    /// <returns>The pool task. It is still running when this method returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="armed"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <remarks>
    /// A faulted body is awaited so its exception is the failure. Returning before the clock moves fails the test.
    /// The arm wait is the 30 s hang guard. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static async Task<Task> RunUntilArmed(Action body, Func<int> armed, string name)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(armed);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Test name is required.", nameof(name));

        var task = Task.Run(body);
        await WaitFor(() => armed() >= 1 || task.IsCompleted, name + " did not arm the clock wait");
        if (task.IsFaulted)
            await task.ConfigureAwait(false);

        Assert.True(armed() >= 1, name + " did not arm the clock wait");
        Assert.False(task.IsCompleted, name + " returned before the clock moved");
        return task;
    }

    /// <summary>
    /// Advances to one tick before <paramref name="bound"/> and fails if <paramref name="operation"/> finishes.
    /// </summary>
    /// <param name="clock">Clock the product is waiting on. Not null. Not advanced past <paramref name="bound"/> yet.</param>
    /// <param name="operation">Dispose or flush started on another thread. Not null. Must already be waiting.</param>
    /// <param name="bound">Product bound measured from the clock's current time. Longer than one tick.</param>
    /// <param name="message">Assertion text when the operation finishes on this step. Not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is not longer than one tick.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    /// <remarks>
    /// The clock callback runs on this thread and only wakes the waiter. <see cref="AssertStillRunning"/>
    /// then lets that waiter finish if the timer already fired. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static void AssertNotReturnedBefore(FakeTimeProvider clock, Task operation, TimeSpan bound, string message)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("Failure message is required.", nameof(message));

        if (bound <= Tick)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Bound must be longer than one tick.");

        clock.Advance(bound - Tick);
        AssertStillRunning(operation, message);
    }

    /// <summary>
    /// Fails when <paramref name="operation"/> has finished or finishes during <see cref="EarlyObserve"/>.
    /// </summary>
    /// <param name="operation">Dispose or flush that must still be waiting. Not null.</param>
    /// <param name="message">Assertion text. Not empty. Used as the whole failure line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    /// <remarks>
    /// A fault counts as finished, and the assertion text replaces the exception, because this step
    /// only asks whether the wait ended. The pause is not a product deadline. Not thread-safe.
    /// Only the test thread calls it. Blocks that thread for at most <see cref="EarlyObserve"/>.
    /// </remarks>
    internal static void AssertStillRunning(Task operation, string message)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("Failure message is required.", nameof(message));

        if (operation.IsCompleted)
            Assert.Fail(message);

        var finished = false;
        try
        {
            finished = operation.Wait(EarlyObserve);
        }
        catch (AggregateException)
        {
            finished = true;
        }

        Assert.False(finished, message);
    }

    /// <summary>
    /// Advances one tick, which lands on the bound the previous step stopped short of, then waits for <paramref name="operation"/>.
    /// </summary>
    /// <param name="clock">Clock the product is waiting on. Not null.</param>
    /// <param name="operation">Dispose or flush that must finish on this tick. Not null.</param>
    /// <param name="message">Assertion text when the operation is still running after the hang guard. Not empty.</param>
    /// <returns>A task that finishes when the operation finishes or the hang guard fails.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    /// <remarks>
    /// The operation is not required to be complete on the same statement as the advance.
    /// The follow-up wait is the 30 s hang guard. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static async Task AssertReturnedAt(FakeTimeProvider clock, Task operation, string message)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("Failure message is required.", nameof(message));

        clock.Advance(Tick);
        await WaitForCompletion(operation, message);
    }

    /// <summary>
    /// Waits until <paramref name="operation"/> finishes, with the 30 s hang guard.
    /// </summary>
    /// <param name="operation">Work that should already be unblocked. Not null.</param>
    /// <param name="message">Assertion text when the guard wins. Not empty. Used as the whole failure line.</param>
    /// <returns>A task that finishes when <paramref name="operation"/> finishes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty.</exception>
    /// <remarks>
    /// Awaits the operation after the guard so a fault is reported. Does not move a test clock.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static async Task WaitForCompletion(Task operation, string message)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("Failure message is required.", nameof(message));

        var finished = await Task.WhenAny(operation, Task.Delay(HangGuard)) == operation;
        Assert.True(finished, message);
        await operation;
    }
}
