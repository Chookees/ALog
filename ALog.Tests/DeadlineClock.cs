// Moves a test clock across the shutdown, flush, and cutoff bounds.
// The product sets a flag inside the timer callback, on the thread that calls Advance.
// One tick short of a bound leaves the flag clear. The next tick sets it.
// Wall time appears only as the 30 s hang guard after the flag is already set.
// Next file: ALog.Tests/C4QueueFixTests.cs.

using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Most polls inside <see cref="HangGuard"/>. The loop stops at this count even if the clock jumps.</summary>
    private const int MaxPolls = 4000;

    /// <summary>
    /// Pins the product bounds at 5000 ms and 4900 ms.
    /// A one-millisecond edit of either constant fails the tests that call this, even though those tests advance by the constant.
    /// </summary>
    /// <remarks>
    /// The failure names the constant and the unit, for example "ShutdownDeadline is 5001 ms, expected 5000 ms."
    /// Does not throw except through the assertion. Not thread-safe. Only the test thread calls it.
    /// The values are the C4 contract. They are not a new deadline.
    /// </remarks>
    internal static void AssertValues()
    {
        var shutdownMs = Milliseconds(BackgroundLogQueue.ShutdownDeadline);
        var budgetMs = Milliseconds(BackgroundLogQueue.NewWriteBudget);
        Assert.True(shutdownMs == "5000", "ShutdownDeadline is " + shutdownMs + " ms, expected 5000 ms.");
        Assert.True(budgetMs == "4900", "NewWriteBudget is " + budgetMs + " ms, expected 4900 ms.");
        Assert.True(
            BackgroundLogQueue.NewWriteBudget < BackgroundLogQueue.ShutdownDeadline,
            "NewWriteBudget must be shorter than ShutdownDeadline.");
    }

    /// <summary>
    /// Builds the failure text when <paramref name="operation"/> finishes one tick before its bound.
    /// </summary>
    /// <param name="operation">shutdown, flush, cutoff, or another wait name. Not null or empty.</param>
    /// <param name="elapsed">Time since the wait started. For this step that is the bound minus one tick.</param>
    /// <returns>The whole assertion sentence, including <paramref name="elapsed"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty.</exception>
    /// <remarks>
    /// Does not throw otherwise. Does not read a clock. Not thread-safe. Only the test thread calls it.
    /// The word is "cutoff" when <paramref name="operation"/> is cutoff, and "deadline" otherwise.
    /// </remarks>
    internal static string EarlyFire(string operation, TimeSpan elapsed)
    {
        if (string.IsNullOrEmpty(operation))
            throw new ArgumentException("Operation name is required.", nameof(operation));

        var word = BoundWord(operation);
        return operation
            + " returned at "
            + word
            + " - 1 tick ("
            + FormatElapsed(elapsed)
            + "); it must keep waiting until the "
            + word
            + ".";
    }

    /// <summary>
    /// Builds the failure text when <paramref name="operation"/> is still waiting at its bound.
    /// </summary>
    /// <param name="operation">shutdown, flush, cutoff, or another wait name. Not null or empty.</param>
    /// <param name="elapsed">Time since the wait started. For this step that is exactly the bound.</param>
    /// <returns>The whole assertion sentence, including <paramref name="elapsed"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty.</exception>
    /// <remarks>
    /// Does not throw otherwise. Does not read a clock. Not thread-safe. Only the test thread calls it.
    /// The word is "cutoff" when <paramref name="operation"/> is cutoff, and "deadline" otherwise.
    /// </remarks>
    internal static string LateFire(string operation, TimeSpan elapsed)
    {
        if (string.IsNullOrEmpty(operation))
            throw new ArgumentException("Operation name is required.", nameof(operation));

        var word = BoundWord(operation);
        return operation
            + " was still waiting at the "
            + word
            + " ("
            + FormatElapsed(elapsed)
            + "); it must stop at the "
            + word
            + ".";
    }

    /// <summary>
    /// Builds the failure text when a new write is blocked one tick before the cutoff.
    /// </summary>
    /// <param name="elapsed">Time since dispose started. For this step that is the cutoff minus one tick.</param>
    /// <returns>The whole assertion sentence, including <paramref name="elapsed"/>.</returns>
    /// <remarks>Does not throw. Does not read a clock. Not thread-safe. Only the test thread calls it.</remarks>
    internal static string ComparisonEarly(TimeSpan elapsed)
        => "new write blocked at cutoff - 1 tick ("
            + FormatElapsed(elapsed)
            + "); it must still be accepted.";

    /// <summary>
    /// Builds the failure text when a new write is still accepted at the cutoff.
    /// </summary>
    /// <param name="elapsed">Time since dispose started. For this step that is exactly the cutoff.</param>
    /// <returns>The whole assertion sentence, including <paramref name="elapsed"/>.</returns>
    /// <remarks>Does not throw. Does not read a clock. Not thread-safe. Only the test thread calls it.</remarks>
    internal static string ComparisonLate(TimeSpan elapsed)
        => "new write accepted at the cutoff ("
            + FormatElapsed(elapsed)
            + "); it must be blocked.";

    /// <summary>
    /// The noun in an early or late sentence. Cutoff stays cutoff. Every other wait is a deadline.
    /// </summary>
    /// <param name="operation">The wait name. Not null. Empty is not cutoff.</param>
    /// <returns><c>cutoff</c> or <c>deadline</c>.</returns>
    /// <remarks>Does not throw. Does not read a clock.</remarks>
    private static string BoundWord(string operation)
    {
        if (operation == "cutoff")
            return "cutoff";

        return "deadline";
    }

    /// <summary>
    /// Formats a bound as a whole number of milliseconds.
    /// </summary>
    /// <param name="value">A product bound. Fractional milliseconds are rounded to the nearest millisecond.</param>
    /// <returns>The number with no unit, in invariant digits.</returns>
    /// <remarks>Does not throw. Used so a pin failure can say "5001 ms" rather than a raw double.</remarks>
    private static string Milliseconds(TimeSpan value)
        => value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats <paramref name="elapsed"/> for an early or late failure sentence.
    /// </summary>
    /// <param name="elapsed">Time since the wait started. May be one tick under a whole second.</param>
    /// <returns>An invariant <c>hh:mm:ss.fffffff</c> value.</returns>
    /// <remarks>Does not throw. The fraction keeps a one-tick remainder visible.</remarks>
    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);

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
    /// Advances to one tick before <paramref name="bound"/> and fails if <paramref name="elapsed"/> is already set.
    /// </summary>
    /// <param name="clock">Clock the product is waiting on. Not null. Not advanced past <paramref name="bound"/> yet.</param>
    /// <param name="elapsed">The product flag set inside the timer callback. Not null. Read on this thread.</param>
    /// <param name="bound">Product bound measured from the clock's current time. Longer than one tick.</param>
    /// <param name="operationName">shutdown, flush, cutoff, or another wait name. Not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="elapsed"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is not longer than one tick.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty.</exception>
    /// <remarks>
    /// <see cref="FakeTimeProvider.Advance"/> runs due callbacks before it returns, so the flag is already
    /// updated when this checks it. There is no wall-clock pause. The failure text is <see cref="EarlyFire"/>.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static void AssertNotElapsed(FakeTimeProvider clock, Func<bool> elapsed, TimeSpan bound, string operationName)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elapsed);
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name is required.", nameof(operationName));

        if (bound <= Tick)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Bound must be longer than one tick.");

        clock.Advance(bound - Tick);
        AssertNotElapsedNow(elapsed, bound, operationName);
    }

    /// <summary>
    /// Fails if <paramref name="elapsed"/> is set. Does not move <paramref name="bound"/>'s clock.
    /// </summary>
    /// <param name="elapsed">The product flag. Not null. The clock is already one tick short of the bound.</param>
    /// <param name="bound">The bound the previous step stopped one tick short of. Longer than one tick.</param>
    /// <param name="operationName">shutdown, flush, cutoff, or another wait name. Not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="elapsed"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is not longer than one tick.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty.</exception>
    /// <remarks>
    /// Used for a second flag that was due on the same tick as the flag <see cref="AssertNotElapsed"/> just checked.
    /// The failure text is <see cref="EarlyFire"/> with elapsed time <paramref name="bound"/> minus one tick.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static void AssertNotElapsedNow(Func<bool> elapsed, TimeSpan bound, string operationName)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name is required.", nameof(operationName));

        if (bound <= Tick)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Bound must be longer than one tick.");

        if (elapsed())
            Assert.Fail(EarlyFire(operationName, bound - Tick));
    }

    /// <summary>
    /// Advances one tick, which lands on the bound the previous step stopped short of, and fails if the flag is still clear.
    /// </summary>
    /// <param name="clock">Clock the product is waiting on. Not null. Currently one tick short of <paramref name="bound"/>.</param>
    /// <param name="elapsed">The product flag set inside the timer callback. Not null.</param>
    /// <param name="bound">The deadline the previous step stopped one tick short of.</param>
    /// <param name="operationName">shutdown, flush, cutoff, or another wait name. Not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="elapsed"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty.</exception>
    /// <remarks>
    /// The failure is immediate. It does not wait for the operation task and it does not use the 30 s hang guard.
    /// The failure text is <see cref="LateFire"/> with elapsed time <paramref name="bound"/>.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static void AssertElapsed(FakeTimeProvider clock, Func<bool> elapsed, TimeSpan bound, string operationName)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(elapsed);
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name is required.", nameof(operationName));

        clock.Advance(Tick);
        AssertElapsedNow(elapsed, bound, operationName);
    }

    /// <summary>
    /// Fails immediately if <paramref name="elapsed"/> is still clear. Does not move the clock.
    /// </summary>
    /// <param name="elapsed">The product flag. Not null. The clock is already on the bound.</param>
    /// <param name="bound">The bound the clock has reached.</param>
    /// <param name="operationName">shutdown, flush, cutoff, or another wait name. Not empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="elapsed"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="operationName"/> is empty.</exception>
    /// <remarks>
    /// Used for a second flag that was due on the same tick <see cref="AssertElapsed"/> just crossed.
    /// The failure text is <see cref="LateFire"/>. There is no 30 s wait.
    /// Not thread-safe. Only the test thread calls it.
    /// </remarks>
    internal static void AssertElapsedNow(Func<bool> elapsed, TimeSpan bound, string operationName)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name is required.", nameof(operationName));

        if (!elapsed())
            Assert.Fail(LateFire(operationName, bound));
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
    /// Call this only after the deadline flag is set. The message is a hang, not the late-deadline sentence.
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
