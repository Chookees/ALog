namespace ALog.Writers.Console;

using System;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

public class ConsoleLogWriter : ILogWriter, IFormattableLogWriter
{
    private readonly bool _useColors;

    public ILogFormatter? Formatter { get; set; }

    public ConsoleLogWriter(bool useColors = false, ILogFormatter? formatter = null)
    {
        _useColors = useColors;
        Formatter = formatter;
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
        Write(logEvent);
        return Task.CompletedTask;
    }

    private string Format(LogEvent logEvent)
    {
        return Formatter?.Format(logEvent) ?? FormatFallback(logEvent);
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

    /// <summary>
    /// Renders the line used when no formatter was attached.
    /// The exception segment comes from <see cref="ExceptionReport"/>, the same helper the other text sinks use.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <returns>One line, without a trailing newline.</returns>
    /// <remarks>
    /// Does not throw. Category and template properties stay off this fallback; the configured
    /// <see cref="PlainTextFormatter"/> is what prints them. Context values are escaped.
    /// A blank message is omitted so the line does not gain a double space.
    /// </remarks>
    private static string FormatFallback(LogEvent logEvent)
    {
        var head = "[" + logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + logEvent.Level + "]";
        if (!string.IsNullOrWhiteSpace(logEvent.Message))
            head += " " + logEvent.Message;
        var exception = logEvent.Exception == null ? null : ExceptionReport.FormatText(logEvent.Exception);
        return ExceptionReport.JoinLine(head, exception, props: null, ExceptionReport.FormatContext(logEvent.Context));
    }

    public void Dispose()
    {
        // no unmanaged resources
    }
}
