namespace ALog.Formatters;

using System;
using System.Linq;
using ALog.Core;
using ALog.Public.Interfaces;

public class PlainTextFormatter : ILogFormatter
{
    // shortcut: plain text renders at most 1024 properties; later ones are omitted so the loop has a fixed bound.
    private const int MaxRenderedProperties = 1024;

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
    /// At most <see cref="MaxRenderedProperties"/> properties are rendered.
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
                ? "props: " + FormatProperties(logEvent.Properties)
                : null,
            logEvent.Context != null && logEvent.Context.Any()
                ? $"ctx: {string.Join(", ", logEvent.Context.Select(kv => $"{kv.Key}={kv.Value}"))}"
                : null
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// Renders property pairs as <c>name=text</c>, separated by a comma and a space.
    /// </summary>
    /// <param name="properties">Properties on the event. Not null. Not modified.</param>
    /// <returns>The property list. Empty when the bag is empty.</returns>
    /// <remarks>
    /// Stops at <see cref="MaxRenderedProperties"/>. A throwing value becomes the capture placeholder.
    /// Allocates one string per rendered property.
    /// </remarks>
    private static string FormatProperties(IReadOnlyDictionary<string, object> properties)
    {
        var capacity = properties.Count < MaxRenderedProperties ? properties.Count : MaxRenderedProperties;
        var rendered = new List<string>(capacity);
        foreach (var pair in properties)
        {
            if (rendered.Count >= MaxRenderedProperties)
                break;

            rendered.Add(pair.Key + "=" + ReadProperty(pair.Value));
        }

        return string.Join(", ", rendered);
    }

    /// <summary>
    /// Reads one property for the plain-text line.
    /// Null becomes empty, which is what string interpolation used to produce.
    /// A throw becomes <c>[capture failed: ExceptionType]</c> using the short type name.
    /// </summary>
    /// <param name="value">Property value from the event. Null is an empty property.</param>
    /// <returns>Text safe to place after <c>=</c>. Never null.</returns>
    /// <remarks>Does not call SelfLog. Does not throw. The exception message is not included.</remarks>
    private static string ReadProperty(object? value)
    {
        if (value is null)
            return string.Empty;

        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return "[capture failed: " + ex.GetType().Name + "]";
        }
    }

    private static string FormatException(Exception ex)
    {
        return $"EX: {ex.GetType().Name}: {ex.Message}";
    }
}
