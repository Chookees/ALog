using ALog;
using ALog.Config;
using ALog.Extensions.Logging;
using ALog.Writers.Cloud;
using ALog.Writers.Console;
using ALog.Writers.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Smoke-tests an unpacked Full zip: AddALog/ILogger, AWS region ctor, and SQL.
/// On linux-x64 writes a real row and SELECTs it back. On win-x64, ctor plus connect
/// where SqlException for a missing server is success.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Non-static category type for <see cref="ILogger{TCategoryName}"/> (static Program cannot be T).
    /// </summary>
    private sealed class SmokeCategory
    {
    }

    /// <summary>
    /// Runs the Full smoke suite for the current RID.
    /// </summary>
    /// <param name="args">Optional: <c>linux-x64</c> or <c>win-x64</c>. Defaults from RID env or OS.</param>
    /// <returns>0 on success; non-zero on failure.</returns>
    private static int Main(string[] args)
    {
        try
        {
            var rid = ResolveRid(args);
            RunAddALog();
            RunAwsRegionCtor();
            if (rid == "linux-x64")
            {
                RunSqlRoundTrip();
            }
            else if (rid == "win-x64")
            {
                RunSqlWindowsConnect();
            }
            else
            {
                throw new InvalidOperationException($"unsupported smoke RID: {rid}");
            }

            Console.WriteLine($"FULL-SMOKE ok rid={rid}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FULL-SMOKE failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Chooses the smoke RID from args, ALOG_RELEASE_SMOKE_RID, or the OS.
    /// </summary>
    /// <param name="args">CLI args; first token may be a RID.</param>
    /// <returns><c>linux-x64</c> or <c>win-x64</c>.</returns>
    private static string ResolveRid(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return args[0].Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable("ALOG_RELEASE_SMOKE_RID");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    }

    /// <summary>
    /// Builds a tiny DI container with <c>AddALog</c> and writes one <see cref="ILogger{T}"/> line.
    /// </summary>
    private static void RunAddALog()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddALog(options =>
            {
                options.Console = new ConsoleWriterOptions { Enabled = true };
                options.MinimumLevel = "Info";
            });
        });
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<SmokeCategory>>();
        logger.LogInformation("full-addalog");
        Console.WriteLine("FULL-SMOKE AddALog ok");
    }

    /// <summary>
    /// Constructs <see cref="AwsCloudWatchWriter"/> with a region (loads AWSSDK assemblies).
    /// </summary>
    private static void RunAwsRegionCtor()
    {
        using var writer = new AwsCloudWatchWriter("alog-smoke", "stream", "eu-central-1");
        Console.WriteLine("FULL-SMOKE aws-region ok");
    }

    /// <summary>
    /// Writes one SQL row through <see cref="SqlLogWriter"/> and reads it back with SELECT.
    /// Requires <c>ALOG_SQL_TEST_CONNECTION</c>.
    /// </summary>
    private static void RunSqlRoundTrip()
    {
        var cs = Environment.GetEnvironmentVariable("ALOG_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException(
                "ALOG_SQL_TEST_CONNECTION is required for Full linux-x64 SQL smoke");
        }

        var marker = $"full-sql-{Guid.NewGuid():N}";
        var table = "ALogReleaseSmoke";
        using (var writer = new SqlLogWriter(cs, table, autoCreateTable: true))
        {
            var config = new LoggerConfig().AddWriter(writer);
            Log.Init(config);
            Log.Write(marker);
            Log.FlushAsync().GetAwaiter().GetResult();
            Log.Shutdown();
        }

        using var connection = new SqlConnection(cs);
        connection.Open();
        using var command = new SqlCommand(
            $"SELECT TOP 1 Message FROM [{table}] WHERE Message = @m ORDER BY Id DESC",
            connection);
        command.Parameters.AddWithValue("@m", marker);
        var value = command.ExecuteScalar() as string;
        if (!string.Equals(value, marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SQL round-trip failed: expected {marker}, got {value ?? "<null>"}");
        }

        Console.WriteLine($"FULL-SMOKE sql-line ok message={marker}");
    }

    /// <summary>
    /// Constructs SqlLogWriter and opens a SqlConnection. SqlException (no server) is success;
    /// PlatformNotSupportedException or FileNotFoundException is failure.
    /// </summary>
    private static void RunSqlWindowsConnect()
    {
        const string cs =
            "Server=127.0.0.1,1433;User Id=sa;Password=ALogSmoke_NoServer_1;TrustServerCertificate=True;Encrypt=False;Connect Timeout=3;";
        try
        {
            using var writer = new SqlLogWriter(cs, "ALogReleaseSmokeWin", autoCreateTable: true);
            Console.WriteLine("FULL-SMOKE win sql writer constructed (server reachable)");
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"FULL-SMOKE win sql-ctor SqlException ok: {ex.Number}");
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException("SqlClient PlatformNotSupportedException on win-x64", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException("SqlClient FileNotFoundException on win-x64", ex);
        }

        try
        {
            using var connection = new SqlConnection(cs);
            connection.Open();
            Console.WriteLine("FULL-SMOKE win sql-open connected");
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"FULL-SMOKE win sql-open SqlException ok: {ex.Number}");
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException("SqlConnection PlatformNotSupportedException on win-x64", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException("SqlConnection FileNotFoundException on win-x64", ex);
        }
    }
}
