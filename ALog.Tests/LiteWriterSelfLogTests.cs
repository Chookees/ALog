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
/// Each test disables <see cref="SelfLog"/> before it returns.
/// </remarks>
public sealed class LiteWriterSelfLogTests
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private const string InstrumentationKey = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Closed SQL Server port with a one-second connect timeout and no connect retries.
    /// </summary>
    /// <remarks>
    /// Port 1 is not listening. Connect Timeout=1 and ConnectRetryCount=0 keep the failure fast and deterministic.
    /// </remarks>
    private const string DeadSql =
        "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True;User Id=sa;Password=x;ConnectRetryCount=0;Pooling=false";

    /// <summary>
    /// <see cref="SqlLogWriter"/> reports a simulated missing Microsoft.Data.SqlClient assembly via SelfLog.
    /// </summary>
    /// <remarks>
    /// The test project loads SqlClient, so <see cref="SqlLogWriter.CreateConnectionOverride"/> throws
    /// <see cref="FileNotFoundException"/> instead of removing the assembly. Construction with
    /// <c>autoCreateTable: false</c> must succeed before the seam is set.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlWrite_MissingSqlClient_ReportsSelfLog()
    {
        await Task.CompletedTask;
        using var writer = new SqlLogWriter(DeadSql, autoCreateTable: false);
        writer.CreateConnectionOverride = static _ =>
            throw new FileNotFoundException(
                "Could not load file or assembly 'Microsoft.Data.SqlClient, Version=0.0.0.0, Culture=neutral, PublicKeyToken=23ec7fc2d6eaa4a5'.");

        var (lines, _) = CaptureSelfLogAndError(() => writer.Write(Event("sql-lite")));
        Assert.Single(lines);
        Assert.Contains("SqlLogWriter", lines[0], StringComparison.Ordinal);
        Assert.Contains("FileNotFoundException", lines[0], StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.SqlClient", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[ALog] SqlLogWriter failed:", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Construction with <c>autoCreateTable: true</c> against a closed SQL port reports one SelfLog line
    /// and the existing stderr create-table line; nothing escapes to the caller.
    /// </summary>
    /// <remarks>
    /// Proves the <c>EnsureTableExists</c> catch calls <see cref="SelfLog.Report"/>. A mutant that
    /// removes that Report leaves this test red.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlCtor_DeadEndpoint_AutoCreateTable_ReportsSelfLog()
    {
        await Task.CompletedTask;
        var thrown = Record.Exception(() =>
        {
            var (lines, error) = CaptureSelfLogAndError(() =>
            {
                using var writer = new SqlLogWriter(DeadSql, "LiteDeadCtor", autoCreateTable: true);
            });
            Assert.Single(lines);
            Assert.StartsWith("[ALog] SqlLogWriter failed: SqlException", lines[0], StringComparison.Ordinal);
            Assert.Contains("[SqlLogWriter] Failed to create table:", error, StringComparison.Ordinal);
            Assert.Contains("SqlException", error, StringComparison.Ordinal);
        });
        Assert.Null(thrown);
    }

    /// <summary>
    /// <see cref="SqlLogWriter.Write"/> against a closed SQL port reports one SelfLog line and the
    /// existing stderr write-failed line; nothing escapes to the caller.
    /// </summary>
    /// <remarks>
    /// Uses <c>autoCreateTable: false</c> so construction does not touch the network. Proves the
    /// write-path <see cref="SelfLog.Report"/>.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlWrite_DeadEndpoint_ReportsSelfLog()
    {
        await Task.CompletedTask;
        using var writer = new SqlLogWriter(DeadSql, autoCreateTable: false);
        var thrown = Record.Exception(() =>
        {
            var (lines, error) = CaptureSelfLogAndError(() => writer.Write(Event("sql-dead-sync")));
            Assert.Single(lines);
            Assert.StartsWith("[ALog] SqlLogWriter failed: SqlException", lines[0], StringComparison.Ordinal);
            Assert.Contains("[SqlLogWriter] SQL write failed:", error, StringComparison.Ordinal);
            Assert.Contains("SqlException", error, StringComparison.Ordinal);
        });
        Assert.Null(thrown);
    }

    /// <summary>
    /// <see cref="SqlLogWriter.WriteAsync"/> against a closed SQL port reports one SelfLog line and the
    /// existing stderr write-failed line; the task completes without faulting.
    /// </summary>
    /// <returns>A task that finishes after the failed open and the asserts.</returns>
    /// <remarks>
    /// Uses <c>autoCreateTable: false</c>. The await uses <c>ConfigureAwait(false)</c> in the product path.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlWriteAsync_DeadEndpoint_ReportsSelfLog()
    {
        using var writer = new SqlLogWriter(DeadSql, autoCreateTable: false);
        Exception? thrown = null;
        List<string> lines = [];
        var error = string.Empty;
        try
        {
            (lines, error) = await CaptureSelfLogAndErrorAsync(() => writer.WriteAsync(Event("sql-dead-async")));
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Null(thrown);
        Assert.Single(lines);
        Assert.StartsWith("[ALog] SqlLogWriter failed: SqlException", lines[0], StringComparison.Ordinal);
        Assert.Contains("[SqlLogWriter] SQL write failed:", error, StringComparison.Ordinal);
        Assert.Contains("SqlException", error, StringComparison.Ordinal);
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
        var (lines, _) = CaptureSelfLogAndError(() => writer.Write(Event("http-dead")));
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
        var (lines, _) = CaptureSelfLogAndError(() => writer.Write(Event("azure-dead")));
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
    /// Enables SelfLog and redirects stderr for <paramref name="body"/>, then disables SelfLog.
    /// </summary>
    /// <param name="body">Work that should report through SelfLog. Must not be null.</param>
    /// <returns>SelfLog lines and the stderr text captured during <paramref name="body"/>.</returns>
    /// <remarks>
    /// Always calls <see cref="SelfLog.Disable"/> and restores stderr, even when <paramref name="body"/> throws.
    /// </remarks>
    private static (List<string> Lines, string Error) CaptureSelfLogAndError(Action body)
    {
        var lines = new List<string>();
        var previousError = Console.Error;
        var buffer = new StringWriter();
        SelfLog.Enable(lines.Add);
        Console.SetError(buffer);
        try
        {
            body();
            return (lines, buffer.ToString());
        }
        finally
        {
            Console.SetError(previousError);
            SelfLog.Disable();
        }
    }

    /// <summary>
    /// Enables SelfLog and redirects stderr for one asynchronous action, then disables SelfLog.
    /// </summary>
    /// <param name="body">Work that should report through SelfLog. Must not be null.</param>
    /// <returns>SelfLog lines and the stderr text captured during <paramref name="body"/>.</returns>
    /// <remarks>
    /// The await uses <c>ConfigureAwait(false)</c>. Always calls <see cref="SelfLog.Disable"/> and restores stderr.
    /// </remarks>
    private static async Task<(List<string> Lines, string Error)> CaptureSelfLogAndErrorAsync(Func<Task> body)
    {
        var lines = new List<string>();
        var previousError = Console.Error;
        var buffer = new StringWriter();
        SelfLog.Enable(lines.Add);
        Console.SetError(buffer);
        try
        {
            await body().ConfigureAwait(false);
            return (lines, buffer.ToString());
        }
        finally
        {
            Console.SetError(previousError);
            SelfLog.Disable();
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
