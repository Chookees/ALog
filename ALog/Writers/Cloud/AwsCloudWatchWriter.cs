namespace ALog.Writers.Cloud;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// AWS CloudWatch log writer
/// </summary>
public class AwsCloudWatchWriter : ILogWriter
{
    private readonly string _logGroupName;
    private readonly string _logStreamName;
    private readonly string _region;
    private readonly ILogFormatter? _formatter;
    private readonly AwsCloudWatchClient _client;

    public AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        string region = "us-east-1",
        ILogFormatter? formatter = null,
        string? accessKey = null,
        string? secretKey = null)
    {
        _logGroupName = logGroupName ?? throw new ArgumentNullException(nameof(logGroupName));
        _logStreamName = logStreamName ?? throw new ArgumentNullException(nameof(logStreamName));
        _region = region;
        _formatter = formatter;
        _client = new AwsCloudWatchClient(region, accessKey, secretKey);
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
            var message = _formatter?.Format(logEvent) ?? FormatFallback(logEvent);
            
            var logEventData = new
            {
                timestamp = ((DateTimeOffset)logEvent.Timestamp).ToUnixTimeMilliseconds(),
                message = message,
                level = logEvent.Level.ToString(),
                context = logEvent.Context
            };

            var json = JsonSerializer.Serialize(logEventData, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (asyncMode)
            {
                await _client.PutLogEventAsync(_logGroupName, _logStreamName, json);
            }
            else
            {
                _client.PutLogEvent(_logGroupName, _logStreamName, json);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AwsCloudWatchWriter] CloudWatch write failed: {ex}");
        }
    }

    private string FormatFallback(LogEvent logEvent)
    {
        var parts = new[]
        {
            $"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}]",
            $"[{logEvent.Level}]",
            logEvent.Message,
            logEvent.Exception != null ? $"EX: {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}" : null,
            logEvent.Context != null && logEvent.Context.Count > 0
                ? $"ctx: {string.Join(", ", logEvent.Context.Select(kv => $"{kv.Key}={kv.Value}"))}"
                : null
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}

/// <summary>
/// Simple AWS CloudWatch client for logging
/// </summary>
internal class AwsCloudWatchClient : IDisposable
{
    private readonly string _region;
    private readonly string? _accessKey;
    private readonly string? _secretKey;

    public AwsCloudWatchClient(string region, string? accessKey = null, string? secretKey = null)
    {
        _region = region;
        _accessKey = accessKey;
        _secretKey = secretKey;
    }

    public void PutLogEvent(string logGroupName, string logStreamName, string message)
    {
        PutLogEventInternal(logGroupName, logStreamName, message, asyncMode: false).GetAwaiter().GetResult();
    }

    public async Task PutLogEventAsync(string logGroupName, string logStreamName, string message)
    {
        await PutLogEventInternal(logGroupName, logStreamName, message, asyncMode: true);
    }

    private async Task PutLogEventInternal(string logGroupName, string logStreamName, string message, bool asyncMode)
    {
        // This is a simplified implementation
        // In a real implementation, you would use AWS SDK for .NET
        // For now, we'll just log to console as a placeholder
        
        Console.WriteLine($"[CloudWatch] LogGroup: {logGroupName}, LogStream: {logStreamName}");
        Console.WriteLine($"[CloudWatch] Message: {message}");
        
        // TODO: Implement actual AWS CloudWatch API calls using AWS SDK
        // This would require adding AWS SDK dependencies to the project
    }

    public void Dispose()
    {
        // Cleanup resources if needed
    }
}
