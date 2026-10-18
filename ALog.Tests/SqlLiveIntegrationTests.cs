using System.Security.Cryptography;
using ALog;
using ALog.Config;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.Sql;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ALog.Tests;

/// <summary>
/// Live SQL Server integration for LIM-003.
/// Prefers <c>ALOG_SQL_TEST_CONNECTION</c> (CI generates that string and masks the password).
/// Otherwise Testcontainers starts <see cref="SqlServerImage"/> with a random password.
/// When ALOG_REQUIRE_SQL=1, a missing server fails the test. When that variable is not 1, Skip.If skips it.
/// The image digest is the single reference in this file; the workflow holds the other one.
/// </summary>
public sealed class SqlLiveIntegrationTests : IAsyncLifetime
{
    // Manifest digest shared with ALOG_SQL_IMAGE in the CI workflow.
    internal const string SqlServerImage =
        "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718";

    // Characters that would split an ADO.NET connection string or a shell assignment.
    private static readonly char[] ForbiddenPasswordCharacters =
    {
        ';', '=', ' ', '\'', '"', '{', '}', '$', '`', '\\', '\r', '\n'
    };
    private static int _integrationProofWritten;

    private static bool RequiresSql =>
        string.Equals(Environment.GetEnvironmentVariable("ALOG_REQUIRE_SQL"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Skip text xUnit shows when this machine has no SQL Server.
    /// The variable is the one CI writes after it starts SQL Server.
    /// </summary>
    internal const string SqlUnavailableSkipReason =
        "SQL Server not available: set ALOG_SQL_TEST_CONNECTION";


    private MsSqlContainer? _container;
    private string? _connectionString;
    private string? _unavailableReason;

    /// <summary>
    /// Decides whether a live SQL test runs or is skipped.
    /// CI passes <paramref name="requireSql"/> true, so the test runs and fails if SQL stays down.
    /// A connection string that does not open is a skip, even when Docker is present, because the fixture does not fall through to a container.
    /// </summary>
    /// <param name="requireSql">True when <c>ALOG_REQUIRE_SQL</c> is <c>1</c>.</param>
    /// <param name="hasConnectionString">True when <c>ALOG_SQL_TEST_CONNECTION</c> is set.</param>
    /// <param name="connectionReachable">True when that connection string opened.</param>
    /// <param name="dockerAvailable">True when the Docker endpoint Testcontainers would use is present.</param>
    /// <returns><see cref="SqlUnavailableSkipReason"/>, or null when the test must run.</returns>
    /// <remarks>
    /// No I/O. Callers probe SQL and Docker before they pass the flags. Safe to call from any thread.
    /// </remarks>
    internal static string? SkipReason(
        bool requireSql,
        bool hasConnectionString,
        bool connectionReachable,
        bool dockerAvailable)
    {
        if (requireSql)
            return null;
        if (hasConnectionString)
            return connectionReachable ? null : SqlUnavailableSkipReason;
        if (dockerAvailable)
            return null;
        return SqlUnavailableSkipReason;
    }

    /// <summary>
    /// Builds the failure text used when <c>ALOG_REQUIRE_SQL</c> is <c>1</c> and SQL Server is down.
    /// CI sets that variable on the job that starts SQL Server, so a dead server fails the build.
    /// </summary>
    /// <param name="detail">What the connection attempt reported. Empty is rejected.</param>
    /// <returns>One line that names <c>ALOG_REQUIRE_SQL=1</c> and then the detail.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="detail"/> is empty.</exception>
    /// <remarks>
    /// No I/O. The detail is capped at 400 characters. Safe to call from any thread.
    /// </remarks>
    internal static string RequiredSqlFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("SQL failure detail is empty.", nameof(detail));

        const int maxDetail = 400;
        if (detail.Length > maxDetail)
            detail = detail.Substring(0, maxDetail);
        return "SQL Server is not reachable and ALOG_REQUIRE_SQL=1. " + detail;
    }

    /// <summary>
    /// Builds a SQL Server password for the Testcontainers path.
    /// The prefix supplies an upper-case letter, a lower-case letter, a digit, and a symbol,
    /// which is the policy SQL Server enforces, and the tail is 24 random hex characters.
    /// </summary>
    /// <returns>A 28-character password that contains no connection-string separator.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entropy is the wrong length or a separator slipped into the result.
    /// </exception>
    /// <remarks>
    /// Allocates the returned string on purpose; this runs once per local container, not on the log path.
    /// Reads OS randomness. Does not log the password. Safe to call from any thread.
    /// </remarks>
    internal static string CreateSqlPassword()
    {
        const int entropyBytes = 12;
        var bytes = new byte[entropyBytes];
        RandomNumberGenerator.Fill(bytes);
        var entropy = Convert.ToHexString(bytes);
        if (entropy.Length != entropyBytes * 2)
        {
            throw new InvalidOperationException("SQL password entropy was not 24 hex characters.");
        }

        var password = "Aa1!" + entropy;
        for (var i = 0; i < ForbiddenPasswordCharacters.Length; i++)
        {
            if (password.IndexOf(ForbiddenPasswordCharacters[i]) >= 0)
            {
                throw new InvalidOperationException("SQL password contains a connection-string separator.");
            }
        }

        return password;
    }

    /// <summary>
    /// Connects to the SQL Server this fixture will write to.
    /// <c>ALOG_REQUIRE_SQL=1</c> does not change this method. The test method fails when the server stays down.
    /// Without that value, Testcontainers starts the pinned image with <see cref="CreateSqlPassword"/>.
    /// A failed attempt is stored so the test can skip or fail.
    /// </summary>
    /// <returns>A completed task after the connection string is stored, or after a failure is recorded.</returns>
    /// <remarks>
    /// May start a Docker container and open a TCP connection. Does not throw when SQL is missing.
    /// </remarks>
    public async Task InitializeAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ALOG_SQL_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            try
            {
                await WaitForSqlAsync(fromEnv, TimeSpan.FromMinutes(2));
                _connectionString = fromEnv;
                return;
            }
            catch (Exception ex)
            {
                _unavailableReason = $"ALOG_SQL_TEST_CONNECTION set but SQL not reachable: {ex.Message}";
                return;
            }
        }

        try
        {
            _container = new MsSqlBuilder(SqlServerImage)
                .WithPassword(CreateSqlPassword())
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            _unavailableReason =
                $"Live SQL requires Docker (Testcontainers) or ALOG_SQL_TEST_CONNECTION. {ex.GetType().Name}: {ex.Message}";
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    /// <summary>
    /// Skips this test when SQL Server is down and <c>ALOG_REQUIRE_SQL</c> is not <c>1</c>.
    /// When that variable is <c>1</c>, a missing server fails the test instead of skipping it.
    /// CI sets <c>ALOG_REQUIRE_SQL</c> to <c>1</c> on the job that starts SQL Server.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>ALOG_REQUIRE_SQL</c> is <c>1</c> and no server was reached.
    /// </exception>
    /// <remarks>
    /// Calls <c>Skip.If</c> from Xunit.SkippableFact. That call does not return when it skips.
    /// xUnit 2.9 reports <c>Assert.Skip</c> as a failure, so this method does not call it.
    /// </remarks>
    private void SkipIfSqlIsUnavailable()
    {
        if (_unavailableReason is null)
            return;
        Skip.If(!RequiresSql, SqlUnavailableSkipReason);
        throw new InvalidOperationException(RequiredSqlFailure(_unavailableReason));
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [SkippableFact]
    public async Task LIM003_SqlLogWriter_CreatesTable_And_InsertsRow()
    {
        SkipIfSqlIsUnavailable();

        Assert.False(string.IsNullOrWhiteSpace(_connectionString));
        var tableName = "ALogLiveLogs";

        using (var writer = new SqlLogWriter(_connectionString!, tableName, autoCreateTable: true))
        {
            var evt = new LogEvent(
                DateTime.UtcNow,
                LogLevel.Info,
                "live-sql-message",
                null,
                new Dictionary<string, object> { ["run"] = "lim-003" });

            writer.Write(evt);
            await writer.WriteAsync(new LogEvent(
                DateTime.UtcNow,
                LogLevel.Warn,
                "live-sql-async",
                new Exception("sample"),
                null));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM [{tableName}]", connection);
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        Assert.Equal(2, count);

        await using var msgCmd = new SqlCommand(
            $"SELECT Message FROM [{tableName}] WHERE Message = @m",
            connection);
        msgCmd.Parameters.AddWithValue("@m", "live-sql-message");
        var message = (string?)await msgCmd.ExecuteScalarAsync();
        Assert.Equal("live-sql-message", message);
        await WriteSqlIntegrationProofOnceAsync();
    }

    /// <summary>
    /// SQL keeps the rendered message and the context JSON. The template property
    /// bag has no column. A <see cref="JsonFormatter"/> on the config is not applied,
    /// because this writer is not <see cref="ALog.Public.Interfaces.IFormattableLogWriter"/>.
    /// </summary>
    /// <remarks>
    /// Uses the live server when one is available. Does not print a second
    /// <c>ALOG-SQL-INTEGRATION</c> line. With no SQL server, and without
    /// <c>ALOG_REQUIRE_SQL=1</c>, <c>Skip.If</c> skips the test.
    /// </remarks>
    [SkippableFact(Timeout = 60000)]
    public async Task Sql_DefaultAndJsonConfig_OmitTemplateProperties()
    {
        SkipIfSqlIsUnavailable();

        var table = "C1Prop" + Guid.NewGuid().ToString("N");
        var writer = new SqlLogWriter(_connectionString!, table, autoCreateTable: true);
        writer.Write(SqlSample("direct alpha"));
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .SetMinimumLevel(LogLevel.Trace)
            .SetFormatter(new JsonFormatter()));
        try
        {
            using (LogContext.Push(new KeyValuePair<string, object?>[] { new("User", "ada") }))
                Log.Write(LogLevel.Info, "hello {A}", "alpha");
        }
        finally
        {
            Log.Shutdown();
        }

        await AssertSqlRowsOmitTemplateBagAsync(table);
    }

    /// <summary>
    /// Reads the two characterization rows and checks that each context has User
    /// and neither row stores the template property A.
    /// </summary>
    /// <param name="table">Alphanumeric table created for this test.</param>
    /// <returns>A completed task after both rows have been checked.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the table does not contain two rows.</exception>
    private async Task AssertSqlRowsOmitTemplateBagAsync(string table)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT Message, Context FROM [{table}] ORDER BY Message",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var seen = 0;
        while (await reader.ReadAsync())
        {
            seen++;
            var message = reader.GetString(0);
            var context = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            Assert.Contains("alpha", message, StringComparison.Ordinal);
            Assert.Contains("User", context, StringComparison.Ordinal);
            Assert.Contains("ada", context, StringComparison.Ordinal);
            // Ist-Verhalten 3a113bf, Befund an DC gemeldet
            Assert.DoesNotContain("\"A\"", context, StringComparison.Ordinal);
            Assert.DoesNotContain("\"A\"", message, StringComparison.Ordinal);
        }

        if (seen != 2)
            throw new InvalidOperationException($"Expected 2 SQL rows, found {seen}.");
    }

    /// <summary>
    /// Builds a direct SQL event. Properties hold A and context holds User.
    /// </summary>
    /// <param name="message">Rendered message stored in the Message column.</param>
    /// <returns>An info event at the fixed coverage clock.</returns>
    private static LogEvent SqlSample(string message)
        => new(
            CoverageClock.UtcNow,
            LogLevel.Info,
            message,
            null,
            new Dictionary<string, object> { ["User"] = "ada" },
            new Dictionary<string, object> { ["A"] = "alpha" },
            "hello {A}");

    /// <summary>
    /// Builds the single SQL proof line from <c>@@VERSION</c>.
    /// Only the first line is kept, so the test log contains exactly one proof line.
    /// </summary>
    /// <param name="versionText">Raw <c>@@VERSION</c> text, which SQL Server returns as several lines.</param>
    /// <returns><c>ALOG-SQL-INTEGRATION ran server=</c> plus the first version line.</returns>
    /// <exception cref="ArgumentException">Thrown when the text is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the first line is empty or longer than 400 characters.</exception>
    /// <remarks>
    /// No I/O. Does not print. Safe to call from any thread; it does not share state.
    /// </remarks>
    internal static string FormatSqlIntegrationLine(string versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            throw new ArgumentException("SQL version text is empty.", nameof(versionText));
        if (versionText.Length > 4000)
            throw new InvalidOperationException("SQL version text is too long.");

        var first = versionText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var line = first.Split('\n')[0].Trim();
        if (line.Length == 0 || line.Length > 400)
            throw new InvalidOperationException("SQL version line is not usable.");
        return "ALOG-SQL-INTEGRATION ran server=" + line;
    }

    /// <summary>
    /// Prints the SQL proof line once per process after the live write succeeds.
    /// </summary>
    /// <returns>A completed task after the line is printed, or immediately when it was already printed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the version query returns nothing usable.</exception>
    /// <remarks>
    /// Opens one extra connection. The static flag stops a second test in the same process from printing again.
    /// </remarks>
    private async Task WriteSqlIntegrationProofOnceAsync()
    {
        if (Volatile.Read(ref _integrationProofWritten) != 0)
            return;
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("SQL integration proof has no connection.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT @@VERSION", connection);
        var raw = (string?)await command.ExecuteScalarAsync();
        var line = FormatSqlIntegrationLine(raw ?? string.Empty);
        if (Interlocked.Exchange(ref _integrationProofWritten, 1) != 0)
            return;
        Console.WriteLine(line);
    }

    private static async Task WaitForSqlAsync(string connectionString, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(2000);
            }
        }

        throw new InvalidOperationException(
            $"SQL Server not ready within {timeout}. Last error: {last?.Message}", last);
    }
}
