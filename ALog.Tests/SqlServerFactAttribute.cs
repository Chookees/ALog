namespace ALog.Tests;

/// <summary>
/// Runs a live SQL test, or skips it when no SQL Server is available.
/// <c>ALOG_REQUIRE_SQL=1</c> forces the test to run. CI sets that variable after it starts SQL Server.
/// Without it, the test runs when <c>ALOG_SQL_TEST_CONNECTION</c> opens or Docker can start the pinned image.
/// Otherwise xUnit reports the test as skipped and does not construct the fixture.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    /// <summary>
    /// Records the skip reason for this process before the test method runs.
    /// A skipped test is not constructed, so the fixture does not start a container.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="SqlLiveIntegrationTests.CurrentSkipReason"/> once per process.
    /// xUnit 2 stores <see cref="FactAttribute.Skip"/> at discovery. <c>Assert.Skip</c> does not
    /// report a skip on this runner; it fails the test.
    /// </remarks>
    public SqlServerFactAttribute()
    {
        var reason = SqlLiveIntegrationTests.CurrentSkipReason();
        if (reason is not null)
            Skip = reason;
    }
}
