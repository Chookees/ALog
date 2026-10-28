using System.Net;
using System.Net.Sockets;
using ALog.Core;
using ALog.Writers.Cloud;
using ALog.Writers.Http;
using ALog.Writers.Sql;

namespace ALog.Tests;

/// <summary>
/// Lite and dead-endpoint writers must report through <see cref="SelfLog"/>, matching AwsCloudWatchWriter.
/// </summary>
/// <remarks>
/// Not safe beside another SelfLog test. The assembly already disables parallelization.
/// Each test restores <see cref="SelfLog"/> before it returns.
/// </remarks>
public sealed class LiteWriterSelfLogTests
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private const string InstrumentationKey = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// <see cref="SqlLogWriter"/> reports a simulated missing Microsoft.Data.SqlClient assembly via SelfLog.
    /// </summary>
    /// <remarks>
    /// The test project loads SqlClient, so the seam throws <see cref="FileNotFoundException"/> from
    /// <see cref="SqlLogWriter.CreateConnection"/> instead of removing the assembly.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlWrite_MissingSqlClient_ReportsSelfLog()
    {
        await Task.CompletedTask;
        using var writer = new SqlLogWriter(
            "Server=127.0.0.1,1;User ID=sa;Password=unused;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True;Pooling=false",
            autoCreateTable: false);
        writer.CreateConnection = static _ =>
            throw new FileNotFoundException(
                "Could not load file or assembly 'Microsoft.Data.SqlClient, Version=0.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'.");

        var lines = CaptureSelfLog(() => writer.Write(Event("sql-lite")));
        Assert.Single(lines);
        Assert.Contains("SqlLogWriter", lines[0], StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", lines[0], StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.SqlClient", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[ALog] SqlLogWriter failed:", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="HttpLogWriter"/> reports a closed local port through SelfLog exactly once.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HttpWrite_ClosedPort_ReportsSelfLog()
    {
        await Task.CompletedTask;
        var endpoint = ClosedLoopbackUrl();
        using var writer = new HttpLogWriter(endpoint, timeout: TimeSpan.FromSeconds(2));
        var lines = CaptureSelfLog(() => writer.Write(Event("http-dead")));
        Assert.Single(lines);
        Assert.Contains("HttpLogWriter", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[ALog] HttpLogWriter failed:", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AzureLogWriter"/> reports a closed local port through SelfLog exactly once.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AzureWrite_ClosedPort_ReportsSelfLog()
    {
        await Task.CompletedTask;
        var endpoint = ClosedLoopbackUrl();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var writer = new AzureLogWriter(InstrumentationKey, endpoint: endpoint, httpClient: client);
        var lines = CaptureSelfLog(() => writer.Write(Event("azure-dead")));
        Assert.Single(lines);
        Assert.Contains("AzureLogWriter", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[ALog] AzureLogWriter failed:", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Binds a loopback port, releases it, and returns an HTTP URL that should refuse connections.
    /// </summary>
    /// <returns>An <c>http://127.0.0.1:port/</c> URL for a port that is not listening.</returns>
    /// <remarks>Does not throw when the OS can bind. The caller must tolerate a rare race if something rebinds the port.</remarks>
    private static string ClosedLoopbackUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
    }

    /// <summary>
    /// Enables SelfLog for <paramref name="body"/> and returns the captured lines.
    /// </summary>
    /// <param name="body">Work that should report through SelfLog. Must not be null.</param>
    /// <returns>Lines delivered to SelfLog during <paramref name="body"/>.</returns>
    /// <remarks>Restores the previous SelfLog target even when <paramref name="body"/> throws.</remarks>
    private static List<string> CaptureSelfLog(Action body)
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        try
        {
            body();
            return lines;
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Builds one info event with a fixed timestamp.
    /// </summary>
    /// <param name="message">Message text. Must not be null.</param>
    /// <returns>An event with no exception, context, or properties.</returns>
    private static LogEvent Event(string message)
        => new(Stamp, LogLevel.Info, message, null, null);
}
