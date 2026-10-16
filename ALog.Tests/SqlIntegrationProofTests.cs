namespace ALog.Tests;

/// <summary>
/// Checks the one-line SQL integration proof without opening a connection.
/// The live test prints that line once after a successful write.
/// </summary>
public sealed class SqlIntegrationProofTests
{
    /// <summary>
    /// Keeps only the first line of a SQL Server version string.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run in parallel with other tests.
    /// </remarks>
    [Fact]
    public void FormatSqlIntegrationLine_UsesTheFirstLineOnly()
    {
        var line = SqlLiveIntegrationTests.FormatSqlIntegrationLine(
            "Microsoft SQL Server 2022 (RTM)\r\nCopyright (C) 2022 Microsoft Corporation\r\n");

        Assert.Equal("ALOG-SQL-INTEGRATION ran server=Microsoft SQL Server 2022 (RTM)", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    /// <summary>
    /// Rejects a blank version so the proof line cannot be empty.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run in parallel with other tests.
    /// </remarks>
    [Fact]
    public void FormatSqlIntegrationLine_RejectsEmptyText()
    {
        var error = Assert.Throws<ArgumentException>(
            () => SqlLiveIntegrationTests.FormatSqlIntegrationLine("  \r\n"));

        Assert.Equal("versionText", error.ParamName);
    }
}
