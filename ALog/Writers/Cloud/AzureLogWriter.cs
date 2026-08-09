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

    public void Write(LogEvent logEvent)
    {
        WriteInternal(logEvent, asyncMode: false).GetAwaiter().GetResult();
    }

    public Task WriteAsync(LogEvent logEvent) => WriteInternal(logEvent, asyncMode: true);

    private async Task WriteInternal(LogEvent logEvent, bool asyncMode)
    {
        try
        {
            var payload = BuildTrackPayload(logEvent);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };

            HttpResponseMessage response;
            if (asyncMode)
            {
                response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            }
            else
            {
                response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[AzureLogWriter] Application Insights write failed: {ex}");
        }
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
