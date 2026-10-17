using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// Fixed clock for coverage tests. Production writers still read
/// <see cref="DateTime.UtcNow"/> for file rotation and SQL <c>CreatedAt</c>.
/// </summary>
internal static class CoverageClock
{
    private static readonly FakeTimeProvider clock =
        new(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero));

    /// <summary>
    /// Returns 2020-01-02 03:04:05 UTC from <see cref="FakeTimeProvider.GetUtcNow"/>.
    /// </summary>
    /// <returns>The same timestamp on every call. The clock is not advanced.</returns>
    /// <remarks>Does not touch the system clock and does not allocate a new provider.</remarks>
    internal static DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
}
