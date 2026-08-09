using ALog.Core;
using ALog.Public.Interfaces;

namespace ALog.Tests;

internal sealed class RecordingWriter : ILogWriter
{
    private readonly object _lock = new();
    public List<LogEvent> Events { get; } = new();
    public bool Disposed { get; private set; }
    public int SyncWriteCount { get; private set; }
    public int AsyncWriteCount { get; private set; }

    public void Write(LogEvent logEvent)
    {
        lock (_lock)
        {
            SyncWriteCount++;
            Events.Add(logEvent);
        }
    }

    public Task WriteAsync(LogEvent logEvent)
    {
        lock (_lock)
        {
            AsyncWriteCount++;
            Events.Add(logEvent);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

/// <summary>
/// Blocks Write/WriteAsync until <see cref="Release"/> so the background queue can fill.
/// </summary>
internal sealed class BlockingWriter : ILogWriter
{
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly object _lock = new();
    public List<LogEvent> Events { get; } = new();
    public int EnteredCount { get; private set; }
    public int WriteThreadId { get; private set; } = -1;

    public void Release() => _gate.Set();

    public void Write(LogEvent logEvent)
    {
        lock (_lock)
        {
            EnteredCount++;
            WriteThreadId = Environment.CurrentManagedThreadId;
        }
        _gate.Wait();
        lock (_lock)
            Events.Add(logEvent);
    }

    public Task WriteAsync(LogEvent logEvent)
    {
        Write(logEvent);
        return Task.CompletedTask;
    }

    public void Dispose() => _gate.Dispose();
}

internal sealed class TrackingFormatter : ILogFormatter
{
    public string Prefix { get; }
    public int CallCount { get; private set; }

    public TrackingFormatter(string prefix = "FMT")
    {
        Prefix = prefix;
    }

    public string Format(LogEvent logEvent)
    {
        CallCount++;
        return $"{Prefix}({logEvent.Message})";
    }
}

internal sealed class TrackingHttpMessageHandler : HttpMessageHandler
{
    public int SendCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendCount++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}

internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    public List<CapturedHttpRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var contentHeaders = request.Content?.Headers.ToDictionary(
            h => h.Key,
            h => string.Join(",", h.Value),
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Requests.Add(new CapturedHttpRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            headers,
            contentHeaders));

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"itemsReceived\":1,\"itemsAccepted\":1}")
        };
    }
}

internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    string Body,
    string ContentType,
    Dictionary<string, string> Headers,
    Dictionary<string, string> ContentHeaders);

internal sealed class RecordingAwsTransport : ALog.Writers.Cloud.IAwsCloudWatchLogsTransport
{
    public List<(string LogGroup, string LogStream, string Message, long TimestampMs)> Calls { get; } = new();

    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((logGroupName, logStreamName, message, timestampUnixMs));
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

internal sealed class DisposableHttpClient : HttpClient
{
    public bool WasDisposed { get; private set; }

    public DisposableHttpClient(HttpMessageHandler handler) : base(handler)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            WasDisposed = true;
        base.Dispose(disposing);
    }
}
