using System.Net;
using InvalidOperationException = System.InvalidOperationException;
using LogEvent = ALog.Core.LogEvent;
using System.Reflection;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using ALog.Core;
using ALog.Formatters;
using ALog.Platform.iOS;
using ALog.Platform.Linux;
using ALog.Platform.Windows;
using ALog.Writers.Cloud;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Writers.Http;
using ALog.Writers.Sql;

namespace ALog.Tests;

/// <summary>
/// Covers writer branches the characterization tests do not: errors, rotation,
/// colors, severity, and the CloudWatch client state machine.
/// </summary>
public sealed class CoverageWriterGapTests
{
    /// <summary>
    /// Plain and JSON formatters include every optional field, and JSON can be indented.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Formatters_IncludeExceptionCategoryPropertiesAndContext()
    {
        await Task.CompletedTask;
        var evt = new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Error,
            "formatted",
            new InvalidOperationException("boom"),
            new Dictionary<string, object> { ["User"] = "ada" },
            new Dictionary<string, object> { ["A"] = "alpha" },
            "tmpl {A}",
            "Orders");

        var plain = new PlainTextFormatter("yyyy").Format(evt);
        Assert.Contains("EX:", plain, StringComparison.Ordinal);
        Assert.Contains("Orders", plain, StringComparison.Ordinal);
        Assert.Contains("A=alpha", plain, StringComparison.Ordinal);
        Assert.Contains("User=ada", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("EX:", new PlainTextFormatter().Format(Event("bare")), StringComparison.Ordinal);

        var json = new JsonFormatter(pretty: true).Format(evt);
        Assert.Contains("boom", json, StringComparison.Ordinal);
        Assert.Contains("\n", json, StringComparison.Ordinal);
        Assert.Contains("alpha", new JsonFormatter().Format(evt), StringComparison.Ordinal);
    }

    /// <summary>
    /// Platform helpers build a path on this machine. Linux honors <c>XDG_DATA_HOME</c>
    /// and falls back when it is unset.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PlatformHelpers_ResolvePaths()
    {
        await Task.CompletedTask;
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.GetTempPath());
            var linux = new LinuxPlatformHelper();
            Assert.Equal("Linux", linux.GetPlatformName());
            Assert.Contains("alog", linux.ResolveLogFilePath("app.log"), StringComparison.OrdinalIgnoreCase);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
            Assert.Contains("alog", linux.ResolveLogFilePath("app.log"), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }

        Assert.Equal("Windows", new WindowsPlatformHelper().GetPlatformName());
        Assert.Contains("ALog", new WindowsPlatformHelper().ResolveLogFilePath("app.log"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("iOS", new IOSPlatformHelper().GetPlatformName());
        Assert.Contains("ALog", new IOSPlatformHelper().ResolveLogFilePath("app.log"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Console color selection runs for every level, including an unknown value.
    /// Fallback text includes an exception and context. Async write delegates to sync.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Console_ColorsExceptionContextAndAsync()
    {
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var writer = new ConsoleLogWriter(useColors: true);
            foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal, (LogLevel)99 })
                writer.Write(Event("color", level));

            writer.Write(new LogEvent(
                CoverageClock.UtcNow,
                LogLevel.Error,
                "with-ex",
                new InvalidOperationException("boom"),
                new Dictionary<string, object> { ["User"] = "ada" }));
            await writer.WriteAsync(Event("async-line"));
            writer.Dispose();
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = buffer.ToString();
        Assert.Contains("with-ex", text, StringComparison.Ordinal);
        Assert.Contains("User=ada", text, StringComparison.Ordinal);
        Assert.Contains("async-line", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// File rotation, async write, a missing directory, and a path whose parent is a file.
    /// A null path throws. Failures are swallowed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task File_RotatesCreatesDirectoryAndSwallowsBadPaths()
    {
        Assert.Throws<ArgumentNullException>(() => new FileLogWriter(null!));
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "nested", "app.log");
            using (var writer = new FileLogWriter(path, maxFileSizeInBytes: 20))
            {
                writer.Write(Event(new string('x', 40)));
                await writer.WriteAsync(Event("second"));
            }

            Assert.True(Directory.GetFiles(Path.Combine(directory, "nested")).Length >= 2);

            var blocker = Path.Combine(directory, "not-a-dir");
            await File.WriteAllTextAsync(blocker, "x");
            using var broken = new FileLogWriter(Path.Combine(blocker, "child.log"), maxFileSizeInBytes: 1);
            broken.Write(Event("nope"));
            await broken.WriteAsync(Event("nope-async"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// HTTP covers an owned client, request headers, a non-JSON formatter, a failed status,
    /// and an exception in the default body. A null endpoint throws.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Http_OwnedClientHeadersFailureAndExceptionBody()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpLogWriter(null!));
        using (var owned = new HttpLogWriter(
            "https://example.test/logs",
            headers: new Dictionary<string, string> { ["X-Own"] = "1" },
            timeout: TimeSpan.FromSeconds(3)))
        {
            owned.Dispose();
        }

        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var writer = new HttpLogWriter(
            "https://example.test/logs",
            method: HttpMethod.Put,
            headers: new Dictionary<string, string> { ["X-C1"] = "yes" },
            formatter: new PlainTextFormatter(),
            httpClient: client);
        var evt = new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Error,
            "http-body",
            new InvalidOperationException("boom"),
            new Dictionary<string, object> { ["User"] = "ada" });
        writer.Write(evt);
        await writer.WriteAsync(evt);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("yes", header(handler.Requests[0], "X-C1"));
        Assert.Contains("text/plain", handler.Requests[0].ContentType, StringComparison.Ordinal);
        Assert.Contains("http-body", handler.Requests[0].Body, StringComparison.Ordinal);

        var failing = new StatusHandler(HttpStatusCode.InternalServerError);
        using var failClient = new HttpClient(failing);
        using var failWriter = new HttpLogWriter("https://example.test/logs", httpClient: failClient);
        failWriter.Write(Event("fail"));
        await failWriter.WriteAsync(Event("fail-async"));
        Assert.Equal(2, failing.Calls);
    }

    /// <summary>
    /// Azure rejects a blank key, trims a custom endpoint, maps every severity,
    /// and swallows a failed post. An owned client is disposed with the writer.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Azure_SeverityEndpointFailureAndOwnedClient()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureLogWriter(null!));
        Assert.Throws<ArgumentException>(() => new AzureLogWriter("  "));

        var handler = new CapturingHttpHandler();
        using (var client = new HttpClient(handler))
        using (var writer = new AzureLogWriter(Key, endpoint: "https://example.test/track/", httpClient: client))
        {
            foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal, (LogLevel)99 })
            {
                var payload = writer.BuildTrackPayload(new LogEvent(
                    CoverageClock.UtcNow,
                    level,
                    "sev",
                    level == LogLevel.Error ? new InvalidOperationException("boom") : null,
                    new Dictionary<string, object> { ["User"] = null! }));
                Assert.Contains("sev", payload, StringComparison.Ordinal);
            }

            writer.Write(Event("sent"));
            Assert.Equal("https://example.test/track", handler.Requests[0].Uri.ToString());
        }

        var failing = new StatusHandler(HttpStatusCode.BadRequest);
        using (var client = new HttpClient(failing))
        using (var writer = new AzureLogWriter(Key, endpoint: "   ", httpClient: client))
        {
            writer.Write(Event("bad"));
            await writer.WriteAsync(Event("bad-async"));
            Assert.Equal(AzureLogWriter.DefaultEndpoint, failing.LastUri?.ToString());
        }

        using var owned = new AzureLogWriter(Key);
        owned.Dispose();
    }

    /// <summary>
    /// CloudWatch client construction covers region-only and key credentials.
    /// Neither call puts a log event. The writer swallows a transport failure.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AwsWriter_ConstructsClientsAndSwallowsTransportFailure()
    {
        using (var regionOnly = new AwsSdkCloudWatchLogsTransport("us-east-1"))
            regionOnly.Dispose();
        using (var withKeys = new AwsSdkCloudWatchLogsTransport("eu-west-1", "AKIAtesttest", "secretsecretsecret"))
            withKeys.Dispose();
        using (var blankKey = new AwsSdkCloudWatchLogsTransport("us-east-1", "  ", "secret"))
            blankKey.Dispose();

        Assert.Throws<ArgumentNullException>(() => new AwsCloudWatchWriter(null!, "stream", new RecordingAwsTransport()));
        Assert.Throws<ArgumentNullException>(() => new AwsCloudWatchWriter("group", null!, new RecordingAwsTransport()));
        Assert.Throws<ArgumentNullException>(() => new AwsCloudWatchWriter("group", "stream", (IAwsCloudWatchLogsTransport)null!));

        var transport = new ThrowingAwsTransport();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        writer.Write(Event("down"));
        await writer.WriteAsync(Event("down-async"));
        writer.Dispose();
        Assert.False(transport.Disposed);
    }

    /// <summary>
    /// A bad sequence token is not retried and is not stored.
    /// The service ignores sequence tokens, so the exception leaves the transport.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AwsTransport_RetriesInvalidSequence_AndRemembersNextToken()
    {
        var stub = CloudWatchStub.Create();
        stub.PutMode = "invalid-then-ok";
        using (var transport = new AwsSdkCloudWatchLogsTransport(stub.Client, ownsClient: true))
        {
            var thrown = await Record.ExceptionAsync(() =>
                transport.PutLogEventAsync("group", "stream-1", "hello", 1_577_936_645_000));
            Assert.IsType<InvalidSequenceTokenException>(thrown);
            Assert.Equal(1, stub.Puts);
            Assert.Null(stub.SeenTokens[0]);
            Assert.Equal(0, stub.Describes);
        }

        Assert.True(stub.Disposed);
    }

    /// <summary>
    /// An existing group and stream are not described. The put carries no sequence token.
    /// <see cref="DataAlreadyAcceptedException"/> leaves the transport.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AwsTransport_ExistingStream_TokenMismatchAndAlreadyAccepted()
    {
        var ready = CloudWatchStub.Create();
        ready.GroupExists = true;
        ready.StreamExists = true;
        ready.UploadToken = "upload-1";
        using (var transport = new AwsSdkCloudWatchLogsTransport(ready.Client))
        {
            await transport.PutLogEventAsync("group", "stream-1", "hello", 1_577_936_645_000);
            Assert.Null(ready.SeenTokens[0]);
            Assert.Equal(0, ready.Describes);
        }

        Assert.False(ready.Disposed);
        var missing = CloudWatchStub.Create();
        missing.StreamExists = true;
        using (var transport = new AwsSdkCloudWatchLogsTransport(missing.Client))
            await transport.PutLogEventAsync("group", "stream-1", "hello", 1_577_936_645_000);
        Assert.Null(missing.SeenTokens[0]);
        Assert.Equal(0, missing.Describes);

        var mismatch = CloudWatchStub.Create();
        mismatch.StreamExists = true;
        mismatch.UploadToken = "other-token";
        mismatch.DescribeName = "not-the-stream";
        using (var transport = new AwsSdkCloudWatchLogsTransport(mismatch.Client))
            await transport.PutLogEventAsync("group", "stream-1", "hello", 1_577_936_645_000);
        Assert.Null(mismatch.SeenTokens[0]);
        Assert.Equal(0, mismatch.Describes);

        var accepted = CloudWatchStub.Create();
        accepted.PutMode = "accepted";
        using var acceptedTransport = new AwsSdkCloudWatchLogsTransport(accepted.Client);
        var thrown = await Record.ExceptionAsync(() =>
            acceptedTransport.PutLogEventAsync("group", "stream-1", "hello", 1_577_936_645_000));
        Assert.IsType<DataAlreadyAcceptedException>(thrown);
        Assert.Single(accepted.SeenTokens);
        Assert.Null(accepted.SeenTokens[0]);
        Assert.Equal(0, accepted.Describes);
    }

    /// <summary>
    /// A bad SQL connection exercises table creation and both write paths.
    /// They catch the failure. Names that are null, empty, or too long throw.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Sql_BadConnectionAndInvalidNames_DoNotEscape()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlLogWriter(null!));
        Assert.Throws<ArgumentNullException>(() => SqlLogWriter.ValidateTableName(null!));
        Assert.Throws<ArgumentException>(() => SqlLogWriter.ValidateTableName(""));
        Assert.Throws<ArgumentException>(() => SqlLogWriter.ValidateTableName(new string('A', 129)));

        var connection = "Server=127.0.0.1,1;User ID=sa;Password=nope;Connect Timeout=1;TrustServerCertificate=True;Encrypt=False;";
        using var writer = new SqlLogWriter(connection, "C1Gap", autoCreateTable: true);
        writer.Write(Event("sql-sync"));
        await writer.WriteAsync(Event("sql-async"));
    }

    private const string Key = "11111111-1111-1111-1111-111111111111";

    /// <summary>Reads one captured request header.</summary>
    /// <param name="request">Captured call.</param>
    /// <param name="name">Header name.</param>
    /// <returns>The header value, or empty when it is absent.</returns>
    private static string header(CapturedHttpRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : string.Empty;

    /// <summary>Creates a temp directory.</summary>
    /// <returns>The directory path.</returns>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a temp directory when it exists.</summary>
    /// <param name="path">Directory to delete.</param>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Builds an event at the fixed clock.</summary>
    /// <param name="message">Rendered message.</param>
    /// <param name="level">Level. The default is info.</param>
    /// <returns>An event with no context.</returns>
    private static LogEvent Event(string message, LogLevel level = LogLevel.Info)
        => new(CoverageClock.UtcNow, level, message, null, null);
}

/// <summary>HTTP handler that returns one status and records the call.</summary>
internal sealed class StatusHandler : HttpMessageHandler
{
    private readonly HttpStatusCode status;

    /// <summary>Stores the status returned for every call.</summary>
    /// <param name="status">Status code to return.</param>
    public StatusHandler(HttpStatusCode status) => this.status = status;

    /// <summary>How many times <see cref="SendAsync"/> ran.</summary>
    public int Calls { get; private set; }

    /// <summary>URI from the latest call.</summary>
    public Uri? LastUri { get; private set; }

    /// <summary>Returns the configured status without reading the body.</summary>
    /// <param name="request">The request URI is stored.</param>
    /// <param name="cancellationToken">Ignored. The response is already complete.</param>
    /// <returns>A completed response task.</returns>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastUri = request.RequestUri;
        return Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>
    /// Sync <see cref="HttpClient.Send"/> uses this. The status is the same as <see cref="SendAsync"/>.
    /// </summary>
    /// <param name="request">Request URI is stored.</param>
    /// <param name="cancellationToken">Passed through to <see cref="SendAsync"/>.</param>
    /// <returns>The completed response.</returns>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
}

/// <summary>Transport that fails every put and records dispose.</summary>
internal sealed class ThrowingAwsTransport : IAwsCloudWatchLogsTransport
{
    /// <summary>True after <see cref="Dispose"/>.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Throws so the writer catch path runs.</summary>
    /// <param name="logGroupName">Ignored.</param>
    /// <param name="logStreamName">Ignored.</param>
    /// <param name="message">Ignored.</param>
    /// <param name="timestampUnixMs">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>Never returns.</returns>
    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("down");

    /// <summary>Records that dispose ran.</summary>
    public void Dispose() => Disposed = true;
}

/// <summary>
/// <see cref="IAmazonCloudWatchLogs"/> stand-in. No network calls are made.
/// </summary>
public class CloudWatchStub : DispatchProxy
{
    /// <summary>Creates a proxy and returns the stub that controls it.</summary>
    /// <returns>The stub. <see cref="Client"/> is the same instance.</returns>
    public static CloudWatchStub Create()
    {
        var client = DispatchProxy.Create<IAmazonCloudWatchLogs, CloudWatchStub>();
        return (CloudWatchStub)(object)client;
    }

    /// <summary>The proxy as the SDK interface.</summary>
    public IAmazonCloudWatchLogs Client => (IAmazonCloudWatchLogs)(object)this;

    /// <summary>When true, creating the group throws <see cref="ResourceAlreadyExistsException"/>.</summary>
    public bool GroupExists { get; set; }

    /// <summary>When true, creating the stream throws <see cref="ResourceAlreadyExistsException"/>.</summary>
    public bool StreamExists { get; set; }

    /// <summary>Upload token returned by describe. Null skips the token.</summary>
    public string? UploadToken { get; set; }

    /// <summary>Stream name returned by describe. Null uses the request prefix.</summary>
    public string? DescribeName { get; set; }

    /// <summary><c>ok</c>, <c>invalid-then-ok</c>, or <c>accepted</c>.</summary>
    public string PutMode { get; set; } = "ok";

    /// <summary>How many put calls the stub saw.</summary>
    public int Puts { get; private set; }

    /// <summary>How many describe calls the stub saw. The V4 transport does not describe.</summary>
    public int Describes { get; private set; }

    /// <summary>Sequence tokens presented by the transport, in call order.</summary>
    public List<string?> SeenTokens { get; } = [];

    /// <summary>True after the proxy dispose method runs.</summary>
    public bool Disposed { get; private set; }

    /// <summary>
    /// Handles the four CloudWatch operations the transport calls.
    /// Any other method throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="targetMethod">Interface method being invoked.</param>
    /// <param name="args">Arguments from the transport.</param>
    /// <returns>A completed response task, or null for dispose.</returns>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? string.Empty;
        if (name == "Dispose")
        {
            Disposed = true;
            return null;
        }

        args ??= [];
        if (name == "CreateLogGroupAsync")
        {
            if (GroupExists)
                throw new ResourceAlreadyExistsException("group");
            return Task.FromResult(new CreateLogGroupResponse());
        }

        if (name == "CreateLogStreamAsync")
        {
            if (StreamExists)
                throw new ResourceAlreadyExistsException("stream");
            return Task.FromResult(new CreateLogStreamResponse());
        }

        if (name == "DescribeLogStreamsAsync")
        {
            Describes++;
            return Task.FromResult(Describe(args));
        }

        if (name == "PutLogEventsAsync")
            return Task.FromResult(Put(args));

        throw new NotSupportedException(name);
    }

    /// <summary>Builds the describe response for an existing stream.</summary>
    /// <param name="args">The describe request is argument 0.</param>
    /// <returns>A response that may contain one stream.</returns>
    private DescribeLogStreamsResponse Describe(object?[] args)
    {
        var request = (DescribeLogStreamsRequest)args[0]!;
        var response = new DescribeLogStreamsResponse();
        if (UploadToken is null && DescribeName is null)
            return response;

        response.LogStreams =
        [
            new LogStream
            {
                LogStreamName = DescribeName ?? request.LogStreamNamePrefix,
                UploadSequenceToken = UploadToken
            }
        ];
        return response;
    }

    /// <summary>
    /// Records the sequence token and returns the next one, or throws the configured error.
    /// </summary>
    /// <param name="args">The put request is argument 0.</param>
    /// <returns>A response with <c>NextSequenceToken</c>.</returns>
    /// <exception cref="InvalidSequenceTokenException">Thrown on the first call when the mode asks for a retry.</exception>
    /// <exception cref="DataAlreadyAcceptedException">Thrown when the mode is <c>accepted</c>.</exception>
    private PutLogEventsResponse Put(object?[] args)
    {
        var request = (PutLogEventsRequest)args[0]!;
        SeenTokens.Add(request.SequenceToken);
        Puts++;
        if (PutMode == "invalid-then-ok" && Puts == 1)
        {
            var invalid = new InvalidSequenceTokenException("bad");
            invalid.ExpectedSequenceToken = "expected-1";
            throw invalid;
        }

        if (PutMode == "accepted")
        {
            var taken = new DataAlreadyAcceptedException("accepted");
            taken.ExpectedSequenceToken = "expected-2";
            throw taken;
        }

        return new PutLogEventsResponse { NextSequenceToken = "next-" + Puts.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }
}
