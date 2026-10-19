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

    /// <summary>
    /// Renders one event as a single plain-text line.
    /// A property whose <c>ToString</c> throws is shown as <c>[capture failed: ExceptionType]</c>
    /// and does not drop the rest of the line.
    /// </summary>
    /// <param name="logEvent">Event to render. The writer passes the event it is formatting.</param>
    /// <returns>The line, without a trailing newline. Empty parts are omitted.</returns>
    /// <remarks>
    /// Does not call SelfLog and does not replace the property object on the event.
    /// Every property and context pair is rendered. The loop is bounded by that collection.
    /// A throwing context value uses the same capture placeholder as a property.
    /// Allocates the line. The formatter has no shared mutable state.
    /// </remarks>
    public string Format(LogEvent logEvent)
    {
        var parts = new[]
        {
            $"[{logEvent.Timestamp.ToString(_dateTimeFormat)}]",
            $"[{logEvent.Level}]",
            string.IsNullOrEmpty(logEvent.Category) ? null : $"[{logEvent.Category}]",
            logEvent.Message,
            logEvent.Exception != null ? FormatException(logEvent.Exception) : null,
            logEvent.Properties != null && logEvent.Properties.Count > 0
                ? "props: " + CaptureText.Join(logEvent.Properties)
                : null,
            logEvent.Context != null && logEvent.Context.Count > 0
                ? "ctx: " + CaptureText.Join(logEvent.Context)
                : null
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// Renders the exception as <c>EX: TypeName: message</c>.
    /// </summary>
    /// <param name="ex">Exception on the event. Not null.</param>
    /// <returns>The exception fragment, without a newline.</returns>
    /// <remarks>Does not throw. The message is the exception's own message.</remarks>
    private static string FormatException(Exception ex)
    {
        return $"EX: {ex.GetType().Name}: {ex.Message}";
    }
}
