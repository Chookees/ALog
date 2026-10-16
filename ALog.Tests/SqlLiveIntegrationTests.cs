using System.Security.Cryptography;
using ALog.Core;
using ALog.Writers.Sql;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ALog.Tests;

/// <summary>
/// Live SQL Server integration for LIM-003.
/// Prefers <c>ALOG_SQL_TEST_CONNECTION</c> (CI generates that string and masks the password).
/// Otherwise Testcontainers starts <see cref="SqlServerImage"/> with a random password.
/// On CI, missing SQL fails the test. Locally without Docker the test is a no-op pass with a console note.
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
    private static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    private MsSqlContainer? _container;
    private string? _connectionString;
    private string? _unavailableReason;

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
    /// CI uses <c>ALOG_SQL_TEST_CONNECTION</c> and fails the test when that server stays unreachable.
    /// Without the variable, Testcontainers starts the pinned image with <see cref="CreateSqlPassword"/>.
    /// </summary>
    /// <returns>A completed task after the connection string is stored, or after a local skip is recorded.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown on CI when SQL does not become reachable. Local runs record the reason and return.
    /// </exception>
    /// <remarks>
    /// May start a Docker container and open a TCP connection. xUnit builds one instance per test.
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
                if (IsCi)
                    throw new InvalidOperationException(_unavailableReason, ex);
                return;
            }
        }

        try
        {
            _container = new MsSqlBuilder()
                .WithImage(SqlServerImage)
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

            if (IsCi)
                throw new InvalidOperationException(_unavailableReason, ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task LIM003_SqlLogWriter_CreatesTable_And_InsertsRow()
    {
        if (_unavailableReason is not null)
        {
            // Local only — CI throws in InitializeAsync instead.
            Console.WriteLine($"[SqlLiveIntegrationTests] skipped: {_unavailableReason}");
            return;
        }

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
