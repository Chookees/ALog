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

            foreach (var header in _headers)
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    public void Write(LogEvent logEvent)
    {
        WriteInternal(logEvent, asyncMode: false).GetAwaiter().GetResult();
    }

    public async Task WriteAsync(LogEvent logEvent)
    {
        await WriteInternal(logEvent, asyncMode: true);
    }

    private async Task WriteInternal(LogEvent logEvent, bool asyncMode)
    {
        try
        {
            var content = PrepareContent(logEvent);
            var httpContent = new StringContent(content, Encoding.UTF8, GetContentType());

            using var request = new HttpRequestMessage(_method, _endpoint)
            {
                Content = httpContent
            };

            foreach (var header in _headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (asyncMode)
            {
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                using var response = _httpClient.Send(request);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HttpLogWriter] HTTP write failed: {ex}");
        }
    }

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
            exception = logEvent.Exception != null ? new
            {
                type = logEvent.Exception.GetType().FullName,
                message = logEvent.Exception.Message,
                stackTrace = logEvent.Exception.StackTrace
            } : null,
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
