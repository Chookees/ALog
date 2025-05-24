namespace ALog.Formatters;

using System;
using System.Linq;
using ALog.Core;
using ALog.Public.Interfaces;

public class PlainTextFormatter : ILogFormatter
{
    private readonly string _dateTimeFormat;

    public PlainTextFormatter(string dateTimeFormat = "yyyy-MM-dd HH:mm:ss")
    {
        _dateTimeFormat = dateTimeFormat;
    }

    public string Format(LogEvent logEvent)
    {
        var parts = new[]
        {
            $"[{logEvent.Timestamp.ToString(_dateTimeFormat)}]",
            $"[{logEvent.Level}]",
            logEvent.Message,
            logEvent.Exception != null ? FormatException(logEvent.Exception) : null,
            logEvent.Context != null && logEvent.Context.Any()
                ? $"ctx: {string.Join(", ", logEvent.Context.Select(kv => $"{kv.Key}={kv.Value}"))}"
                : null
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string FormatException(Exception ex)
    {
        return $"EX: {ex.GetType().Name}: {ex.Message}";
    }
}
