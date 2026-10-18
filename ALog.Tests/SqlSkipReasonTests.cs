using System.Reflection;

namespace ALog.Tests;

/// <summary>
/// Checks when a live SQL test is skipped and when it must run.
/// The decision is a pure function so CI and a machine without Docker share one rule.
/// </summary>
public sealed class SqlSkipReasonTests
{
    /// <summary>
    /// CI sets <c>ALOG_REQUIRE_SQL=1</c>. The live tests run even when nothing else is configured.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void SkipReason_RunsWhenSqlIsRequired()
    {
        var reason = SqlLiveIntegrationTests.SkipReason(
            requireSql: true,
            hasConnectionString: false,
            connectionReachable: false,
            dockerAvailable: false);

        Assert.Null(reason);
    }

    /// <summary>
    /// A reachable <c>ALOG_SQL_TEST_CONNECTION</c> is enough to run the live tests.
    /// </summary>
    /// <remarks>
    /// No I/O. The connection check itself lives in the fixture, not here.
    /// </remarks>
    [Fact]
    public void SkipReason_RunsWhenTheConnectionOpened()
    {
        var reason = SqlLiveIntegrationTests.SkipReason(
            requireSql: false,
            hasConnectionString: true,
            connectionReachable: true,
            dockerAvailable: false);

        Assert.Null(reason);
    }

    /// <summary>
    /// Docker can start the pinned image, so the live tests run without a connection string.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void SkipReason_RunsWhenDockerCanStartSql()
    {
        var reason = SqlLiveIntegrationTests.SkipReason(
            requireSql: false,
            hasConnectionString: false,
            connectionReachable: false,
            dockerAvailable: true);

        Assert.Null(reason);
    }

    /// <summary>
    /// A connection string that does not open does not fall through to Docker.
    /// The fixture uses that string and does not start a container after it fails.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void SkipReason_SkipsWhenTheConnectionStringDoesNotOpen()
    {
        var reason = SqlLiveIntegrationTests.SkipReason(
            requireSql: false,
            hasConnectionString: true,
            connectionReachable: false,
            dockerAvailable: true);

        Assert.Equal(SqlLiveIntegrationTests.SqlUnavailableSkipReason, reason);
    }

    /// <summary>
    /// No required SQL, no connection string, and no Docker is the local clone.
    /// The reason names the variable a developer sets to point at a server.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void SkipReason_SkipsWhenNoServerIsAvailable()
    {
        var reason = SqlLiveIntegrationTests.SkipReason(
            requireSql: false,
            hasConnectionString: false,
            connectionReachable: false,
            dockerAvailable: false);

        Assert.Equal("SQL Server not available: set ALOG_SQL_TEST_CONNECTION", reason);
    }

    /// <summary>
    /// Both live SQL methods wear <see cref="SkippableFactAttribute"/> so <c>Skip.If</c> can skip them.
    /// A plain fact cannot report a runtime skip on xUnit 2.
    /// </summary>
    /// <remarks>
    /// Reads attributes only. Does not open a connection or start Docker.
    /// </remarks>
    [Fact]
    public void LiveSqlTests_UseSkippableFact()
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        var methods = typeof(SqlLiveIntegrationTests).GetMethods(flags);
        var facts = 0;
        for (var i = 0; i < methods.Length && i < 20; i++)
        {
            var fact = methods[i].GetCustomAttribute<FactAttribute>();
            if (fact is null)
                continue;
            facts++;
            Assert.IsType<SkippableFactAttribute>(fact);
        }

        Assert.Equal(2, facts);
    }

    /// <summary>
    /// A missing server with <c>ALOG_REQUIRE_SQL=1</c> fails, and the message names that variable.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void RequiredSqlFailure_NamesTheExistingVariable()
    {
        var message = SqlLiveIntegrationTests.RequiredSqlFailure("Docker is not running.");

        Assert.Contains("ALOG_REQUIRE_SQL=1", message, StringComparison.Ordinal);
        Assert.Contains("Docker is not running.", message, StringComparison.Ordinal);
    }
}
