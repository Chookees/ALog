namespace ALog.Writers.Http;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Formatters;
using ALog.Public.Interfaces;

/// <summary>
/// HTTP-based log writer for sending logs to web endpoints
/// </summary>
public class HttpLogWriter : ILogWriter, IFormattableLogWriter
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _endpoint;
    private readonly Dictionary<string, string> _headers;
    private readonly HttpMethod _method;
    private readonly TimeSpan _timeout;

    // A request with more headers than this is truncated. The body still sends.
    private const int MaxRequestHeaders = 64;

    public ILogFormatter? Formatter { get; set; }

    public HttpLogWriter(
        string endpoint,
        HttpMethod? method = null,
        Dictionary<string, string>? headers = null,
        ILogFormatter? formatter = null,
        TimeSpan? timeout = null,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _method = method ?? HttpMethod.Post;
        _headers = headers ?? new Dictionary<string, string>();
        Formatter = formatter;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();

        // Only mutate owned clients — injected/shared HttpClient must stay unchanged.
        if (_ownsHttpClient)
        {
            _httpClient.Timeout = _timeout;
            ApplyHeaders(request: null);
        }
    }

    /// <summary>
    /// Sends one event with <see cref="HttpClient.Send"/> and does not wait on a task.
    /// </summary>
    /// <param name="logEvent">Event to send. Null is reported and dropped.</param>
    /// <remarks>
    /// A handler without synchronous <c>Send</c> throws <see cref="NotSupportedException"/>.
    /// That stays on this writer's error path. The caller does not see it and the call does not hang.
    /// The caller blocks for the HTTP round trip. No continuation is posted to a
    /// <see cref="SynchronizationContext"/>.
    /// </remarks>
    public void Write(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            using var request = CreateRequest(logEvent);
            using var response = _httpClient.Send(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Sends one event with <see cref="HttpClient.SendAsync"/> and does not resume on the caller context.
    /// </summary>
    /// <param name="logEvent">Event to send. Null is reported and the task still completes.</param>
    /// <returns>A task that completes after the post, including when the post fails.</returns>
    /// <remarks>
    /// Failures are written to the error stream. The task does not fault.
    /// The await uses <c>ConfigureAwait(false)</c>.
    /// </remarks>
    public async Task WriteAsync(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            using var request = CreateRequest(logEvent);
            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Builds the outbound request, including per-request headers.
    /// </summary>
    /// <param name="logEvent">Event to format. Must not be null.</param>
    /// <returns>The request. The caller disposes it, which disposes the body.</returns>
    private HttpRequestMessage CreateRequest(LogEvent logEvent)
    {
        var content = PrepareContent(logEvent);
        var httpContent = new StringContent(content, Encoding.UTF8, GetContentType());
        var request = new HttpRequestMessage(_method, _endpoint)
        {
            Content = httpContent
        };

        ApplyHeaders(request);
        return request;
    }

    /// <summary>
    /// Copies at most <see cref="MaxRequestHeaders"/> headers onto the owned client or one request.
    /// </summary>
    /// <param name="request">Request that receives the headers. Null updates the owned client's defaults.</param>
    /// <remarks>
    /// The cap is 64 so a huge header map cannot grow the send path without a bound.
    /// A rejected header name is skipped. The event body is still sent.
    /// </remarks>
    private void ApplyHeaders(HttpRequestMessage? request)
    {
        var scanned = 0;
        foreach (var header in _headers)
        {
            if (scanned == MaxRequestHeaders)
                break;

            scanned++;
            if (request is null)
            {
                // Default headers are best-effort. A rejected name does not fail construction.
                _ = _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                continue;
            }

            // A rejected header is skipped. The event body is still sent.
            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <summary>
    /// Writes one HTTP failure to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from formatting or the HTTP call. Must not be null.</param>
    private static void Report(Exception ex)
    {
        Console.Error.WriteLine($"[HttpLogWriter] HTTP write failed: {ex}");
    }

    /// <summary>
    /// Builds the request body. A caller-supplied formatter wins.
    /// The default body uses <see cref="ExceptionReport.CreateBody"/> for the exception,
    /// so HTTP JSON matches <c>JsonFormatter</c> on <c>type</c>, <c>message</c>, <c>stackTrace</c>, <c>source</c>, and <c>inner</c>.
    /// </summary>
    /// <param name="logEvent">Event to send. Not null.</param>
    /// <returns>The body text. Compact JSON when no formatter is set.</returns>
    /// <remarks>
    /// Does not throw from the exception reader. Null <c>stackTrace</c> is still written, matching the previous default body.
    /// <c>source</c> and <c>inner</c> are omitted when they are null.
    /// </remarks>
    private string PrepareContent(LogEvent logEvent)
    {
        if (Formatter != null)
        {
            return Formatter.Format(logEvent);
        }

        var logData = new
        {
            timestamp = logEvent.Timestamp.ToString("o"),
            level = logEvent.Level.ToString(),
            message = logEvent.Message,
            exception = logEvent.Exception != null ? ExceptionReport.CreateBody(logEvent.Exception) : null,
            context = logEvent.Context
        };

        return JsonSerializer.Serialize(logData, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private string GetContentType()
    {
        if (Formatter is JsonFormatter)
            return "application/json";

        return "text/plain";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
