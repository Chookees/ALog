using ALog.Core;
using ALog.Writers.Sql;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace ALog.Tests;

/// <summary>
/// Live SQL Server integration for LIM-003.
/// Prefers <c>ALOG_SQL_TEST_CONNECTION</c> (CI service); otherwise Testcontainers (local Docker).
/// On CI, missing SQL fails the test. Locally without Docker/SQL the test is a no-op pass with a console note.
/// </summary>
public sealed class SqlLiveIntegrationTests : IAsyncLifetime
{
    private static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    private MsSqlContainer? _container;
    private string? _connectionString;
    private string? _unavailableReason;

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
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
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
