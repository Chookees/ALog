using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
/// Smoke-tests an unpacked Full zip: AddALog/ILogger, AWS region ctor, Azure App Insights, and SQL.
/// On linux-x64 writes a real row and SELECTs it back. On win-x64, ctor plus connect
/// where SqlException for a missing server is success only for expected error numbers.
/// </summary>
internal static class Program
{
    /// <summary>
    /// SqlException numbers that all mean "server unreachable" on Windows (no SQL listener).
    /// Kept as one named set: 258 (timeout), 2 / 53 (network), -2 (timeout variant).
    /// </summary>
    private static readonly HashSet<int> ServerUnreachableSqlExceptionNumbers =
        new() { 258, 2, 53, -2 };

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
            RequireOsMatchesRid(rid);
            RequireSniMatchesRid(rid);
            RunAddALog();
            RunAwsRegionCtor();
            RunAzureListener();
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
    /// Fail when the process OS does not match the zip RID under test.
    /// </summary>
    /// <param name="rid">Expected zip RID.</param>
    private static void RequireOsMatchesRid(string rid)
    {
        if (rid == "win-x64" && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                $"RID win-x64 requires Windows; OSDescription={RuntimeInformation.OSDescription}");
        }

        if (rid == "linux-x64" && !OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException(
                $"RID linux-x64 requires Linux; OSDescription={RuntimeInformation.OSDescription}");
        }

        Console.WriteLine(
            $"FULL-SMOKE OS ok rid={rid} os={RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Fail when SNI presence in the app base directory does not match the RID.
    /// </summary>
    /// <param name="rid">Expected zip RID.</param>
    private static void RequireSniMatchesRid(string rid)
    {
        var sni = Path.Combine(AppContext.BaseDirectory, "Microsoft.Data.SqlClient.SNI.dll");
        var present = File.Exists(sni);
        if (rid == "win-x64" && !present)
        {
            throw new InvalidOperationException(
                "win-x64 Full smoke requires Microsoft.Data.SqlClient.SNI.dll beside the app");
        }

        if (rid == "linux-x64" && present)
        {
            throw new InvalidOperationException(
                "linux-x64 Full smoke must not load Microsoft.Data.SqlClient.SNI.dll");
        }

        Console.WriteLine($"FULL-SMOKE SNI ok rid={rid} present={present}");
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
    /// Posts one Application Insights track payload through <see cref="AzureLogWriter"/> to a local listener.
    /// Requires POST, path <c>/v2/track</c>, and a valid JSON body that contains the marker.
    /// </summary>
    private static void RunAzureListener()
    {
        var marker = $"full-azure-{Guid.NewGuid():N}";
        using var listener = new HttpListener();
        var port = FindFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        string? body = null;
        string? method = null;
        string? path = null;
        var receive = Task.Run(() =>
        {
            var context = listener.GetContext();
            method = context.Request.HttpMethod;
            path = context.Request.Url?.AbsolutePath;
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = reader.ReadToEnd();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        try
        {
            var config = new LoggerConfig()
                .AddWriter(new AzureLogWriter("full-smoke-ikey", endpoint: prefix + "v2/track"));
            Log.Init(config);
            Log.Write(marker);
            Log.FlushAsync().GetAwaiter().GetResult();
            Log.Shutdown();
            if (!receive.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("HttpListener did not receive a Full Azure post");
            }

            RequireAzureTrackRequest(method, path, body, marker);
            Console.WriteLine($"FULL-SMOKE azure ok port={port}");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Fail unless the Azure listener saw POST <c>/v2/track</c> with valid JSON containing
    /// <paramref name="marker"/>.
    /// </summary>
    /// <param name="method">HTTP method from the listener.</param>
    /// <param name="path">Absolute path from the listener.</param>
    /// <param name="body">Raw body text.</param>
    /// <param name="marker">Log marker that must appear in the JSON body.</param>
    private static void RequireAzureTrackRequest(
        string? method, string? path, string? body, string marker)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Azure track method must be POST, got {method ?? "<null>"}");
        }

        if (!string.Equals(path, "/v2/track", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure track path must be /v2/track, got {path ?? "<null>"}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Azure track body is empty");
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Azure track body is not valid JSON", ex);
        }

        if (!body.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AzureLogWriter body missing marker. body={body}");
        }
    }

    /// <summary>
    /// Binds a temporary TCP listener on 127.0.0.1:0 and returns the assigned port.
    /// </summary>
    /// <returns>A free TCP port on the loopback interface.</returns>
    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
    /// Constructs SqlLogWriter and opens a SqlConnection with no server.
    /// Success is a SqlException in <see cref="ServerUnreachableSqlExceptionNumbers"/>.
    /// </summary>
    private static void RunSqlWindowsConnect()
    {
        const string cs =
            "Server=127.0.0.1,1433;User Id=sa;Password=ALogSmoke_NoServer_1;TrustServerCertificate=True;Encrypt=False;Connect Timeout=3;";

        // Ctor may or may not open a connection; only unexpected exception types fail here.
        try
        {
            using var writer = new SqlLogWriter(cs, "ALogReleaseSmokeWin", autoCreateTable: true);
        }
        catch (SqlException ex)
        {
            RequireServerUnreachableNumber(ex, "sql-ctor");
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
            throw new InvalidOperationException(
                "expected SqlException for missing server, but SqlConnection.Open succeeded");
        }
        catch (SqlException ex)
        {
            RequireServerUnreachableNumber(ex, "sql-open");
            Console.WriteLine(
                $"FULL-SMOKE win sql constructed, connect failed as expected: SqlException {ex.Number}");
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

    /// <summary>
    /// Fail unless <paramref name="ex"/> is in <see cref="ServerUnreachableSqlExceptionNumbers"/>.
    /// </summary>
    /// <param name="ex">SqlException from ctor or Open.</param>
    /// <param name="phase">Short label for diagnostics.</param>
    private static void RequireServerUnreachableNumber(SqlException ex, string phase)
    {
        if (!ServerUnreachableSqlExceptionNumbers.Contains(ex.Number))
        {
            throw new InvalidOperationException(
                $"unexpected SqlException number {ex.Number} during {phase}; "
                + $"accepted server-unreachable: "
                + $"{string.Join(", ", ServerUnreachableSqlExceptionNumbers.OrderBy(n => n))}",
                ex);
        }
    }
}
