namespace ALog.Writers.Cloud;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// Azure Application Insights writer using the public <c>/v2/track</c> ingestion protocol.
/// </summary>
public class AzureLogWriter : ILogWriter, IFormattableLogWriter
{
    public const string DefaultEndpoint = "https://dc.services.visualstudio.com/v2/track";

    private readonly string _instrumentationKey;
    private readonly string _endpoint;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ILogFormatter? Formatter { get; set; }

    public AzureLogWriter(
        string instrumentationKey,
        string? endpoint = null,
        ILogFormatter? formatter = null,
        HttpClient? httpClient = null)
    {
        _instrumentationKey = instrumentationKey ?? throw new ArgumentNullException(nameof(instrumentationKey));
        if (string.IsNullOrWhiteSpace(_instrumentationKey))
            throw new ArgumentException("instrumentationKey must not be empty.", nameof(instrumentationKey));

        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
        Formatter = formatter;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Sends one event with <see cref="HttpClient.Send"/> and does not wait on a task.
    /// </summary>
    /// <param name="logEvent">Event to send. Null is reported and dropped.</param>
    /// <remarks>
    /// A handler without synchronous <c>Send</c> throws <see cref="NotSupportedException"/>.
    /// That exception stays on this writer's error path. Callers do not see it.
    /// The call blocks the caller for the HTTP round trip. Safe on a thread that owns a
    /// <see cref="SynchronizationContext"/> because no continuation is posted back to it.
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
    /// Builds the track POST for one event.
    /// </summary>
    /// <param name="logEvent">Event whose payload is the body. Must not be null.</param>
    /// <returns>The request. The caller disposes it, which disposes the body.</returns>
    private HttpRequestMessage CreateRequest(LogEvent logEvent)
    {
        var payload = BuildTrackPayload(logEvent);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
    }

    /// <summary>
    /// Writes one Azure failure to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from payload building or the HTTP call. Must not be null.</param>
    private static void Report(Exception ex)
    {
        System.Console.Error.WriteLine($"[AzureLogWriter] Application Insights write failed: {ex}");
    }

    internal string BuildTrackPayload(LogEvent logEvent)
    {
        var message = Formatter?.Format(logEvent) ?? logEvent.Message;
        var severityLevel = MapSeverity(logEvent.Level);

        var properties = new Dictionary<string, string>
        {
            ["level"] = logEvent.Level.ToString()
        };

        if (logEvent.Context is not null)
        {
            foreach (var pair in logEvent.Context)
            {
                properties[pair.Key] = pair.Value?.ToString() ?? string.Empty;
            }
        }

        if (logEvent.Exception is not null)
        {
            properties["exceptionType"] = logEvent.Exception.GetType().FullName ?? logEvent.Exception.GetType().Name;
            properties["exceptionMessage"] = logEvent.Exception.Message;
        }

        // Correct track contract: iKey in body, MessageData, no Content-Encoding:gzip unless body is gzipped.
        var envelope = new Dictionary<string, object?>
        {
            ["name"] = $"Microsoft.ApplicationInsights.{_instrumentationKey}.Message",
            ["time"] = logEvent.Timestamp.ToUniversalTime().ToString("o"),
            ["iKey"] = _instrumentationKey,
            ["data"] = new Dictionary<string, object?>
            {
                ["baseType"] = "MessageData",
                ["baseData"] = new Dictionary<string, object?>
                {
                    ["ver"] = 2,
                    ["message"] = message,
                    ["severityLevel"] = severityLevel,
                    ["properties"] = properties
                }
            }
        };

        return JsonSerializer.Serialize(envelope);
    }

    private static int MapSeverity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => 0,
        LogLevel.Info => 1,
        LogLevel.Warn => 2,
        LogLevel.Error => 3,
        LogLevel.Fatal => 4,
        _ => 1
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
