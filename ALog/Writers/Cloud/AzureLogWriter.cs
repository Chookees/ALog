namespace ALog.Writers.Cloud;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// Azure Application Insights log writer
/// </summary>
public class AzureLogWriter : ILogWriter
{
    private readonly HttpClient _httpClient;
    private readonly string _instrumentationKey;
    private readonly string _endpoint;
    private readonly ILogFormatter? _formatter;

    public AzureLogWriter(
        string instrumentationKey,
        string? endpoint = null,
        ILogFormatter? formatter = null,
        HttpClient? httpClient = null)
    {
        _instrumentationKey = instrumentationKey ?? throw new ArgumentNullException(nameof(instrumentationKey));
        _endpoint = endpoint ?? "https://dc.applicationinsights.azure.com/v2/track";
        _formatter = formatter;
        _httpClient = httpClient ?? new HttpClient();
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
            var telemetryData = CreateTelemetryData(logEvent);
            var json = JsonSerializer.Serialize(telemetryData, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.Add("Content-Encoding", "gzip");

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = content
            };

            request.Headers.Add("X-API-Key", _instrumentationKey);

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
            Console.Error.WriteLine($"[AzureLogWriter] Azure write failed: {ex}");
        }
    }

    private object CreateTelemetryData(LogEvent logEvent)
    {
        var severityLevel = logEvent.Level switch
        {
            LogLevel.Trace => "Verbose",
            LogLevel.Debug => "Verbose",
            LogLevel.Info => "Information",
            LogLevel.Warn => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Fatal => "Critical",
            _ => "Information"
        };

        var telemetry = new
        {
            name = "Microsoft.ApplicationInsights.Message",
            time = logEvent.Timestamp.ToString("o"),
            iKey = _instrumentationKey,
            tags = new
            {
                "ai.cloud.roleInstance" = Environment.MachineName,
                "ai.cloud.role" = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") ?? "ALog"
            },
            data = new
            {
                baseType = "MessageData",
                baseData = new
                {
                    ver = 2,
                    message = logEvent.Message,
                    severityLevel = severityLevel,
                    properties = CreateProperties(logEvent)
                }
            }
        };

        return telemetry;
    }

    private Dictionary<string, string> CreateProperties(LogEvent logEvent)
    {
        var properties = new Dictionary<string, string>
        {
            ["LogLevel"] = logEvent.Level.ToString(),
            ["Timestamp"] = logEvent.Timestamp.ToString("o")
        };

        if (logEvent.Exception != null)
        {
            properties["ExceptionType"] = logEvent.Exception.GetType().FullName ?? "Unknown";
            properties["ExceptionMessage"] = logEvent.Exception.Message;
            properties["StackTrace"] = logEvent.Exception.StackTrace ?? "";
        }

        if (logEvent.Context != null)
        {
            foreach (var kvp in logEvent.Context)
            {
                properties[$"Context_{kvp.Key}"] = kvp.Value?.ToString() ?? "";
            }
        }

        return properties;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
