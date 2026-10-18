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
    /// A missing server with SQL required fails with the exact unreachable-server sentence.
    /// The reason is appended unchanged so the log shows why the connection failed.
    /// </summary>
    /// <remarks>
    /// No I/O. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void RequiredSqlFailure_NamesTheMissingServer()
    {
        var message = SqlLiveIntegrationTests.RequiredSqlFailure("Docker is not running.");

        Assert.Equal(
            "ALOG_REQUIRE_SQL requires SQL, but no server was reachable: Docker is not running.",
            message);
    }

    /// <summary>
    /// <c>1</c>, <c>true</c>, and <c>TRUE</c> require SQL. With no server the failure is the unreachable sentence.
    /// With a server the same values run the test.
    /// </summary>
    /// <remarks>
    /// No I/O. Does not read the process environment. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void RequireSql_OneAndTrue_RequireAServer()
    {
        const string down = "Docker is not running.";
        const string expected =
            "ALOG_REQUIRE_SQL requires SQL, but no server was reachable: Docker is not running.";

        AssertRequired(SqlLiveIntegrationTests.DecideSqlAvailability("1", down), expected);
        AssertRequired(SqlLiveIntegrationTests.DecideSqlAvailability("true", down), expected);
        AssertRequired(SqlLiveIntegrationTests.DecideSqlAvailability("TRUE", down), expected);
        AssertRequired(SqlLiveIntegrationTests.DecideSqlAvailability("True", down), expected);

        AssertRuns(SqlLiveIntegrationTests.DecideSqlAvailability("1", null));
        AssertRuns(SqlLiveIntegrationTests.DecideSqlAvailability("true", null));
        AssertRuns(SqlLiveIntegrationTests.DecideSqlAvailability("TRUE", null));
    }

    /// <summary>
    /// <c>yes</c> and <c>2</c> are not switches. Each fails with the invalid-value sentence, even when a server is up.
    /// </summary>
    /// <remarks>
    /// No I/O. The value is copied into the message without trimming.
    /// </remarks>
    [Fact]
    public void RequireSql_YesAndTwo_FailWithTheExactSentence()
    {
        AssertInvalid("yes");
        AssertInvalid("2");
    }

    /// <summary>
    /// Unset and empty skip when no server is reachable, and run when one is.
    /// </summary>
    /// <remarks>
    /// No I/O. Empty is not the same as <c>1</c>. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void RequireSql_UnsetOrEmpty_SkipsWhenNoServerIsReachable()
    {
        AssertSkips(SqlLiveIntegrationTests.DecideSqlAvailability(null, "down"));
        AssertSkips(SqlLiveIntegrationTests.DecideSqlAvailability(string.Empty, "down"));
        AssertRuns(SqlLiveIntegrationTests.DecideSqlAvailability(null, null));
        AssertRuns(SqlLiveIntegrationTests.DecideSqlAvailability(string.Empty, null));
    }

    /// <summary>
    /// A required decision fails with the expected sentence and does not skip.
    /// </summary>
    /// <param name="decision">Result from <see cref="SqlLiveIntegrationTests.DecideSqlAvailability"/>.</param>
    /// <param name="expected">Exact failure text.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the failure or the skip text differs.</exception>
    /// <remarks>
    /// No I/O.
    /// </remarks>
    private static void AssertRequired(
        (string? Failure, string? SkipReason) decision,
        string expected)
    {
        Assert.Equal(expected, decision.Failure);
        Assert.Null(decision.SkipReason);
    }

    /// <summary>
    /// An invalid value fails with the exact sentence, whether or not a server was reached.
    /// </summary>
    /// <param name="value">The non-empty variable text.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when either side does not match the sentence.</exception>
    /// <remarks>
    /// No I/O. Calls the decision twice, once with a reason and once without.
    /// </remarks>
    private static void AssertInvalid(string value)
    {
        var expected = "ALOG_REQUIRE_SQL is set to '" + value
            + "'. Use 1 or true to require SQL, or unset it to skip the SQL tests.";
        var down = SqlLiveIntegrationTests.DecideSqlAvailability(value, "Docker is not running.");
        var up = SqlLiveIntegrationTests.DecideSqlAvailability(value, null);
        Assert.Equal(expected, down.Failure);
        Assert.Null(down.SkipReason);
        Assert.Equal(expected, up.Failure);
        Assert.Null(up.SkipReason);
        Assert.Equal(SqlRequirement.Invalid, SqlLiveIntegrationTests.ClassifyRequireSql(value));
    }

    /// <summary>
    /// An optional decision skips with the existing skip sentence.
    /// </summary>
    /// <param name="decision">Result from <see cref="SqlLiveIntegrationTests.DecideSqlAvailability"/>.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the test would run or fail instead of skip.</exception>
    /// <remarks>
    /// No I/O.
    /// </remarks>
    private static void AssertSkips((string? Failure, string? SkipReason) decision)
    {
        Assert.Null(decision.Failure);
        Assert.Equal(SqlLiveIntegrationTests.SqlUnavailableSkipReason, decision.SkipReason);
    }

    /// <summary>
    /// A decision that both runs and does not fail.
    /// </summary>
    /// <param name="decision">Result from <see cref="SqlLiveIntegrationTests.DecideSqlAvailability"/>.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when a failure or a skip reason is set.</exception>
    /// <remarks>
    /// No I/O.
    /// </remarks>
    private static void AssertRuns((string? Failure, string? SkipReason) decision)
    {
        Assert.Null(decision.Failure);
        Assert.Null(decision.SkipReason);
    }
}
