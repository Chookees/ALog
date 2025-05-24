namespace ALog.Writers.Console;

using System;
using System.Linq;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

public class ConsoleLogWriter : ILogWriter
{
    private readonly ILogFormatter? _formatter;
    private readonly bool _useColors;

    public ConsoleLogWriter(bool useColors = false, ILogFormatter? formatter = null)
    {
        _useColors = useColors;
        _formatter = formatter;
    }

    public void Write(LogEvent logEvent)
    {
        var line = Format(logEvent);
        if (_useColors)
        {
            var original = Console.ForegroundColor;
            Console.ForegroundColor = GetColor(logEvent.Level);
            Console.WriteLine(line);
            Console.ForegroundColor = original;
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    public Task WriteAsync(LogEvent logEvent)
    {
        Write(logEvent); // Console.WriteLine ist synchron, also kein echter Vorteil hier
        return Task.CompletedTask;
    }

    private string Format(LogEvent logEvent)
    {
        return _formatter?.Format(logEvent) ?? FormatFallback(logEvent);
    }

    private static ConsoleColor GetColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.White,
        LogLevel.Debug => ConsoleColor.White,
        LogLevel.Info => ConsoleColor.White,
        LogLevel.Warn => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Fatal => ConsoleColor.Magenta,
        _ => ConsoleColor.Gray
    };

    private static string FormatFallback(LogEvent logEvent)
    {
        var parts = new[]
        {
            $"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}]",
            $"[{logEvent.Level}]",
            logEvent.Message,
            logEvent.Exception != null ? $"EX: {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}" : null,
            logEvent.Context != null && logEvent.Context.Any()
                ? $"ctx: {string.Join(", ", logEvent.Context.Select(kv => $"{kv.Key}={kv.Value}"))}"
                : null
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
