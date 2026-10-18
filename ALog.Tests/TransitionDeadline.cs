// Transition window for a deadline that still uses the wall clock (arc v5.22c).
// The product deadline is unchanged. Twice the deadline is the failure line, not a new deadline.
// Next file: the test that calls AssertFiredInWindow.

namespace ALog.Tests;

/// <summary>
/// Checks a wall-clock deadline until the product grows a clock entry point for that wait.
/// </summary>
internal static class TransitionDeadline
{
    /// <summary>How far before the deadline a return still counts as on time. Timer resolution, from v5.22c.</summary>
    private static readonly TimeSpan EarlySlack = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Fails when <paramref name="elapsed"/> is more than 50 ms before <paramref name="deadline"/>, or later than twice that deadline.
    /// </summary>
    /// <param name="elapsed">Measured wait. Not negative.</param>
    /// <param name="deadline">The promised deadline, unchanged. Must be longer than 50 ms.</param>
    /// <remarks>
    /// A deadline of 50 ms or less throws <see cref="ArgumentOutOfRangeException"/> because the early slack would swallow it.
    /// Not thread-safe. Only the test thread calls it.
    /// Later than twice the deadline is a real miss, so this method does not stretch the upper side.
    /// </remarks>
    internal static void AssertFiredInWindow(TimeSpan elapsed, TimeSpan deadline)
    {
        if (deadline <= EarlySlack)
            throw new ArgumentOutOfRangeException(nameof(deadline), deadline, "Deadline must be longer than the 50 ms early slack.");

        AssertBetween(elapsed, deadline - EarlySlack, deadline);
    }

    /// <summary>
    /// Fails when <paramref name="elapsed"/> is before <paramref name="earliest"/> or later than twice <paramref name="deadline"/>.
    /// </summary>
    /// <param name="elapsed">Measured wait.</param>
    /// <param name="earliest">Soonest acceptable return. The 5 s deadline uses deadline minus 50 ms. The slow-writer path passes the new-writer-call cutoff minus 50 ms.</param>
    /// <param name="deadline">Promised deadline. Twice this value is the failure line. Must be positive.</param>
    /// <remarks>
    /// A deadline of zero or less throws <see cref="ArgumentOutOfRangeException"/>.
    /// Not thread-safe. Only the test thread calls it.
    /// The upper side is not widened past twice the promised deadline.
    /// </remarks>
    internal static void AssertBetween(TimeSpan elapsed, TimeSpan earliest, TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deadline), deadline, "Deadline must be positive.");

        var latest = deadline + deadline;
        Assert.True(elapsed >= earliest, "early " + elapsed.ToString());
        Assert.True(elapsed <= latest, "late " + elapsed.ToString());
    }

    /// <summary>
    /// Fails when <paramref name="elapsed"/> is not under twice <paramref name="bound"/>.
    /// A fast return is success: this is an upper bound for a call that must not block, not a deadline that must be waited out.
    /// </summary>
    /// <param name="elapsed">Measured call. Not negative.</param>
    /// <param name="bound">The old upper bound. Must be positive. Twice this value is the new line.</param>
    /// <remarks>
    /// A bound of zero or less throws <see cref="ArgumentOutOfRangeException"/>.
    /// Not thread-safe. Only the test thread calls it.
    /// Does not accept a wider line than twice the bound.
    /// </remarks>
    internal static void AssertNoLongerThanTwice(TimeSpan elapsed, TimeSpan bound)
    {
        if (bound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bound), bound, "Bound must be positive.");

        var latest = bound + bound;
        Assert.True(elapsed < latest, "late " + elapsed.ToString());
    }
}
