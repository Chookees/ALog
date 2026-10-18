using System.Net.Sockets;
using System.Security.Cryptography;
using ALog;
using ALog.Config;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.Sql;
using DotNet.Testcontainers.Configurations;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ALog.Tests;

/// <summary>
/// Live SQL Server integration for LIM-003.
/// Prefers <c>ALOG_SQL_TEST_CONNECTION</c> (CI generates that string and masks the password).
/// Otherwise Testcontainers starts <see cref="SqlServerImage"/> with a random password.
/// When ALOG_REQUIRE_SQL=1, missing SQL fails the test. Without that variable, and with no reachable server, xUnit skips the test.
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

    private const int MaxConnectionStringLength = 2000;
    private const int ProbeConnectTimeoutSeconds = 2;
    private const int MaxConnectionParts = 40;
    private const int DockerProbeMilliseconds = 500;
    private static readonly object SkipGate = new();
    private static bool _skipKnown;
    private static string? _cachedSkipReason;

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
    /// Reads this process once and returns the skip reason for the live SQL tests.
    /// The first call probes. Later calls return the same answer so discovery and the tests agree.
    /// </summary>
    /// <returns><see cref="SqlUnavailableSkipReason"/>, or null when the tests must run.</returns>
    /// <remarks>
    /// May open one SQL connection for at most two seconds, or check that a Docker socket exists.
    /// Does not log a connection string. Not safe to call reentrantly on the same thread.
    /// </remarks>
    internal static string? CurrentSkipReason()
    {
        lock (SkipGate)
        {
            if (_skipKnown)
                return _cachedSkipReason;
            _cachedSkipReason = ComputeCurrentSkipReason();
            _skipKnown = true;
            return _cachedSkipReason;
        }
    }

    /// <summary>
    /// Probes the environment for a SQL Server this process can use.
    /// <c>ALOG_REQUIRE_SQL=1</c> skips the probe so CI always runs the tests.
    /// </summary>
    /// <returns>The skip reason, or null when a server is required or reachable.</returns>
    /// <remarks>
    /// Reads environment variables. May open a SQL connection or stat a Docker socket.
    /// Called only while <see cref="SkipGate"/> is held.
    /// </remarks>
    private static string? ComputeCurrentSkipReason()
    {
        if (RequiresSql)
            return SkipReason(requireSql: true, hasConnectionString: false, connectionReachable: false, dockerAvailable: false);

        var connection = Environment.GetEnvironmentVariable("ALOG_SQL_TEST_CONNECTION");
        var hasConnection = !string.IsNullOrWhiteSpace(connection);
        var reachable = hasConnection && ConnectionOpens(connection!);
        var docker = !hasConnection && DockerEndpointExists();
        return SkipReason(RequiresSql, hasConnection, reachable, docker);
    }

    /// <summary>
    /// Opens <paramref name="connectionString"/> with a two-second connect timeout.
    /// A string that is empty, too long, or rejected by the client counts as closed.
    /// </summary>
    /// <param name="connectionString">The <c>ALOG_SQL_TEST_CONNECTION</c> value. It is not logged.</param>
    /// <returns>True when <see cref="SqlConnection.Open"/> returns.</returns>
    /// <remarks>
    /// Opens a TCP connection. The timeout is the ADO.NET connect timeout, not a second clock.
    /// Safe to call from any thread. Does not share the fixture's connection.
    /// </remarks>
    private static bool ConnectionOpens(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Length > MaxConnectionStringLength)
            return false;

        var probe = WithConnectTimeout(connectionString);
        try
        {
            using var connection = new SqlConnection(probe);
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds <c>Connect Timeout=2</c> when the string does not already name a connect timeout.
    /// An existing timeout is left as the caller wrote it.
    /// </summary>
    /// <param name="connectionString">A connection string no longer than <see cref="MaxConnectionStringLength"/>.</param>
    /// <returns>The string to open. The original is returned when it already has a timeout or the addition would exceed the cap.</returns>
    /// <remarks>
    /// No I/O. Does not log the string. Safe to call from any thread.
    /// </remarks>
    private static string WithConnectTimeout(string connectionString)
    {
        if (HasConnectTimeout(connectionString))
            return connectionString;

        var suffix = ";Connect Timeout=" + ProbeConnectTimeoutSeconds.ToString();
        if (connectionString.Length + suffix.Length > MaxConnectionStringLength)
            return connectionString;
        return connectionString + suffix;
    }

    /// <summary>
    /// Reports whether the connection string already sets a connect timeout.
    /// At most <see cref="MaxConnectionParts"/> semicolon-separated parts are read.
    /// </summary>
    /// <param name="connectionString">The candidate connection string.</param>
    /// <returns>True when a part is named <c>Connect Timeout</c> or <c>Connection Timeout</c>.</returns>
    /// <remarks>
    /// No I/O. Safe to call from any thread.
    /// </remarks>
    private static bool HasConnectTimeout(string connectionString)
    {
        var start = 0;
        for (var part = 0; part < MaxConnectionParts && start <= connectionString.Length; part++)
        {
            var end = connectionString.IndexOf(';', start);
            if (end < 0)
                end = connectionString.Length;
            var length = end - start;
            if (length > 0 && length < 80)
            {
                var eq = connectionString.IndexOf('=', start, length);
                if (eq > start)
                {
                    var keyLength = eq - start;
                    var key = connectionString.Substring(start, keyLength).Trim();
                    if (key.Equals("Connect Timeout", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Connection Timeout", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            if (end >= connectionString.Length)
                break;
            start = end + 1;
        }

        return false;
    }

    /// <summary>
    /// Checks the Docker endpoint Testcontainers would use to start SQL Server.
    /// A missing socket or a refused TCP port means Docker cannot start the image.
    /// </summary>
    /// <returns>True when the endpoint exists or accepts a connection within half a second.</returns>
    /// <remarks>
    /// Stats a Unix socket or opens one TCP connection. Does not start a container.
    /// Any failure is treated as Docker being absent. Safe to call from any thread.
    /// </remarks>
    private static bool DockerEndpointExists()
    {
        try
        {
            var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
            if (endpoint is null || string.IsNullOrEmpty(endpoint.Scheme))
                return false;

            if (endpoint.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase)
                || endpoint.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                var path = endpoint.AbsolutePath;
                return path.Length > 0 && path.Length < 512 && File.Exists(path);
            }

            if (endpoint.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase))
                return File.Exists(@"\\.\pipe\docker_engine");

            if (endpoint.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                || endpoint.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return TcpPortAccepts(endpoint.Host, endpoint.Port);

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a TCP connection and gives up after <see cref="DockerProbeMilliseconds"/>.
    /// </summary>
    /// <param name="host">Docker host name. Empty is not a server.</param>
    /// <param name="port">Docker port. Values outside 1..65535 are not a server.</param>
    /// <returns>True when the connect finishes and the socket is connected.</returns>
    /// <remarks>
    /// Blocks the caller for at most half a second. Does not retry. Safe to call from any thread.
    /// </remarks>
    private static bool TcpPortAccepts(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            return false;

        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(DockerProbeMilliseconds)))
                return false;
            return connect.IsCompletedSuccessfully && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
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
    /// <c>ALOG_REQUIRE_SQL=1</c> fails the test when SQL stays unreachable. CI sets that variable.
    /// Without the variable, Testcontainers starts the pinned image with <see cref="CreateSqlPassword"/>.
    /// A machine with no server never reaches this method: <see cref="SqlServerFactAttribute"/> skips first.
    /// </summary>
    /// <returns>A completed task after the connection string is stored.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SQL does not become reachable.</exception>
    /// <remarks>
    /// May start a Docker container and open a TCP connection. xUnit builds one instance per test that is not skipped.
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
                throw new InvalidOperationException(_unavailableReason, ex);
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

            throw new InvalidOperationException(_unavailableReason, ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [SqlServerFact]
    public async Task LIM003_SqlLogWriter_CreatesTable_And_InsertsRow()
    {
        if (_unavailableReason is not null)
            throw new InvalidOperationException(_unavailableReason);

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
    /// <c>ALOG-SQL-INTEGRATION</c> line. With no SQL server the attribute skips
    /// the test, so this method does not run and does not pass.
    /// </remarks>
    [SqlServerFact(Timeout = 60000)]
    public async Task Sql_DefaultAndJsonConfig_OmitTemplateProperties()
    {
        if (_unavailableReason is not null)
            throw new InvalidOperationException(_unavailableReason);

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
