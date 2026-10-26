using System.Net;
using System.Text;
using ALog;
using ALog.Config;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Writers.Http;
using ALog.Writers.Sql;

/// <summary>
/// Smoke-tests an unpacked Lite zip: Console, File, Queue, SelfLog, HTTP, Init(ALogOptions),
/// and a SqlLogWriter FileNotFoundException negative case.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the Lite smoke suite and returns a process exit code.
    /// </summary>
    /// <param name="args">Unused.</param>
    /// <returns>0 on success; non-zero when a positive or negative check fails.</returns>
    private static int Main(string[] args)
    {
        try
        {
            RunMainPath();
            RunOptionsInit();
            RunHttpListener();
            RunSqlNegative();
            Console.WriteLine("LITE-SMOKE ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LITE-SMOKE failed: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Exercises Console, File, background queue, and SelfLog without FileNotFound/FileLoad.
    /// </summary>
    private static void RunMainPath()
    {
        var selfLog = new List<string>();
        SelfLog.Enable(selfLog.Add);
        var logPath = Path.Combine(Path.GetTempPath(), $"alog-lite-smoke-{Guid.NewGuid():N}.log");
        try
        {
            var config = new LoggerConfig()
                .AddWriter(new ConsoleLogWriter())
                .AddWriter(new FileLogWriter(logPath))
                .UseBackgroundQueue(true);
            Log.Init(config);
            Log.Write("lite-main");
            Log.FlushAsync().GetAwaiter().GetResult();
            Log.Shutdown();
            if (!File.Exists(logPath) || !File.ReadAllText(logPath).Contains("lite-main", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("File writer did not persist lite-main");
            }

            Console.WriteLine($"LITE-SMOKE main file ok selfLog={selfLog.Count}");
        }
        finally
        {
            SelfLog.Disable();
            TryDelete(logPath);
        }
    }

    /// <summary>
    /// Calls <see cref="Log.Init(ALogOptions)"/> with Console and File enabled.
    /// </summary>
    private static void RunOptionsInit()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"alog-lite-opts-{Guid.NewGuid():N}.log");
        try
        {
            Log.Init(new ALogOptions
            {
                Console = new ConsoleWriterOptions { Enabled = true },
                File = new FileWriterOptions { Enabled = true, Path = logPath },
                BackgroundQueueEnabled = true,
            });
            Log.Write("lite-options");
            Log.FlushAsync().GetAwaiter().GetResult();
            Log.Shutdown();
            if (!File.ReadAllText(logPath).Contains("lite-options", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("ALogOptions init did not write lite-options");
            }

            Console.WriteLine("LITE-SMOKE options ok");
        }
        finally
        {
            TryDelete(logPath);
        }
    }

    /// <summary>
    /// Posts one event through <see cref="HttpLogWriter"/> to a local <see cref="HttpListener"/>.
    /// </summary>
    private static void RunHttpListener()
    {
        var marker = $"lite-http-{Guid.NewGuid():N}";
        using var listener = new HttpListener();
        var port = FindFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        string? body = null;
        var receive = Task.Run(() =>
        {
            var context = listener.GetContext();
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = reader.ReadToEnd();
            context.Response.StatusCode = 204;
            context.Response.Close();
        });

        try
        {
            var config = new LoggerConfig()
                .AddWriter(new HttpLogWriter(prefix + "ingest"));
            Log.Init(config);
            Log.Write(marker);
            Log.FlushAsync().GetAwaiter().GetResult();
            Log.Shutdown();
            if (!receive.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("HttpListener did not receive a Lite HTTP post");
            }

            if (body is null || !body.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"HttpLogWriter body missing marker. body={body ?? "<null>"}");
            }

            Console.WriteLine($"LITE-SMOKE http ok port={port}");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Expects <see cref="SqlLogWriter"/> construction (autoCreateTable true) to throw FileNotFoundException.
    /// </summary>
    private static void RunSqlNegative()
    {
        try
        {
            using var writer = new SqlLogWriter(
                "Server=127.0.0.1,1433;User Id=sa;Password=x;TrustServerCertificate=True;Encrypt=False;",
                "LiteSmokeLogs",
                autoCreateTable: true);
            throw new InvalidOperationException("SqlLogWriter should FileNotFoundException on Lite");
        }
        catch (FileNotFoundException ex) when (ex.Message.Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal))
        {
            Console.WriteLine("LITE-SMOKE sql-negative ok FileNotFoundException");
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
    /// Deletes a path when it exists; ignores cleanup failures.
    /// </summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
