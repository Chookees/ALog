namespace ALog.Formatters;

using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// Renders one event as a single plain-text line.
/// An exception is <c>Type: Message</c> plus the origin, from <see cref="ExceptionReport"/>,
/// so this formatter cannot drift from the console and file fallbacks.
/// </summary>
public class PlainTextFormatter : ILogFormatter
{
    private readonly string _dateTimeFormat;
    private readonly bool _includeStackTrace;

    /// <summary>How many lines of <see cref="Exception.ToString"/> are indented. The rest are dropped.</summary>
    private const int MaxStackLines = 256;

    /// <summary>
    /// Creates a formatter that writes one line and does not append the full stack.
    /// The time format is <c>yyyy-MM-dd HH:mm:ss</c>.
    /// </summary>
    /// <remarks>
    /// This constructor is the call <c>new PlainTextFormatter()</c>. It chains to the two-argument
    /// constructor so the default format lives in one place.
    /// </remarks>
    public PlainTextFormatter()
        : this("yyyy-MM-dd HH:mm:ss", false)
    {
    }

    /// <summary>
    /// Creates a formatter that writes one line and does not append the full stack.
    /// </summary>
    /// <param name="dateTimeFormat">
    /// Format passed to <see cref="DateTime.ToString(string)"/>. Callers that want the default pass
    /// <c>yyyy-MM-dd HH:mm:ss</c> or use <see cref="PlainTextFormatter()"/>.
    /// </param>
    /// <remarks>
    /// Kept as its own constructor so a program built against the previous one-argument constructor
    /// still binds after this package is replaced.
    /// </remarks>
    public PlainTextFormatter(string dateTimeFormat)
        : this(dateTimeFormat, false)
    {
    }

    /// <summary>
    /// Creates a formatter that uses <c>yyyy-MM-dd HH:mm:ss</c> and optionally appends the full stack.
    /// </summary>
    /// <param name="includeStackTrace">
    /// When true, <see cref="Exception.ToString"/> is appended after the line, each of its lines indented
    /// by four spaces. When false, <see cref="Exception.ToString"/> is not called.
    /// </param>
    /// <remarks>
    /// This is the constructor named <c>PlainTextFormatter(includeStackTrace: true)</c>.
    /// </remarks>
    public PlainTextFormatter(bool includeStackTrace)
        : this("yyyy-MM-dd HH:mm:ss", includeStackTrace)
    {
    }

    /// <summary>
    /// Creates a formatter with an explicit time format and an explicit stack choice.
    /// </summary>
    /// <param name="dateTimeFormat">Format passed to <see cref="DateTime.ToString(string)"/>.</param>
    /// <param name="includeStackTrace">
    /// When true, <see cref="Exception.ToString"/> is appended after the line, each of its lines indented
    /// by four spaces. When false, <see cref="Exception.ToString"/> is not called.
    /// </param>
    /// <remarks>Both values are stored. Neither is validated; an unusable format fails when a line is rendered.</remarks>
    public PlainTextFormatter(string dateTimeFormat, bool includeStackTrace)
    {
        _dateTimeFormat = dateTimeFormat;
        _includeStackTrace = includeStackTrace;
    }

    /// <summary>
    /// Renders one event as a single plain-text line.
    /// A property whose <c>ToString</c> throws is shown as <c>[capture failed: ExceptionType]</c>
    /// and does not drop the rest of the line.
    /// The exception segment, when present, is separated with <c> | </c>.
    /// </summary>
    /// <param name="logEvent">Event to render. The writer passes the event it is formatting.</param>
    /// <returns>
    /// The line, without a trailing newline. Empty parts are omitted.
    /// When stack traces were requested, a newline and the indented stack follow the line.
    /// </returns>
    /// <remarks>
    /// Does not call SelfLog and does not replace the property object on the event.
    /// Every property and context pair is rendered. The property loop is bounded by that collection.
    /// Context values are escaped so <c>|</c> and <c>--&gt;</c> cannot look like separators.
    /// <see cref="Exception.ToString"/> runs only when this formatter was constructed with
    /// <c>includeStackTrace: true</c>. A throw from that call becomes a capture placeholder
    /// and the line above it is kept.
    /// Allocates the line. The formatter has no shared mutable state.
    /// </remarks>
    public string Format(LogEvent logEvent)
    {
        var line = ExceptionReport.JoinLine(
            FormatHead(logEvent),
            FormatException(logEvent),
            FormatProps(logEvent.Properties),
            ExceptionReport.FormatContext(logEvent.Context));
        if (!_includeStackTrace || logEvent.Exception == null)
            return line;

        var stack = ReadFullStack(logEvent.Exception);
        if (stack == null)
            return line;
        return line + "\n" + stack;
    }

    /// <summary>Joins time, level, category, and message with spaces. Blank pieces are left out.</summary>
    /// <param name="logEvent">Event being rendered. Not null.</param>
    /// <returns>The head of the line, before the first <c> | </c>.</returns>
    /// <remarks>Does not throw. The time format is the one given to the constructor. At most four pieces are joined.</remarks>
    private string FormatHead(LogEvent logEvent)
    {
        var parts = new List<string>(4);
        parts.Add("[" + logEvent.Timestamp.ToString(_dateTimeFormat) + "]");
        parts.Add("[" + logEvent.Level + "]");
        if (!string.IsNullOrEmpty(logEvent.Category))
            parts.Add("[" + logEvent.Category + "]");
        if (!string.IsNullOrWhiteSpace(logEvent.Message))
            parts.Add(logEvent.Message);
        return string.Join(" ", parts);
    }

    /// <summary>The shared exception segment, or null when the event has no exception.</summary>
    /// <param name="logEvent">Event being rendered. Not null.</param>
    /// <returns>The segment from <see cref="ExceptionReport.FormatText"/>, or null.</returns>
    /// <remarks>Does not build the text itself. Does not throw.</remarks>
    private static string? FormatException(LogEvent logEvent)
    {
        if (logEvent.Exception == null)
            return null;
        return ExceptionReport.FormatText(logEvent.Exception);
    }

    /// <summary>Renders template properties as <c>props: name=value</c>. Values are not separator-escaped.</summary>
    /// <param name="properties">Property bag. Null or empty omits the segment.</param>
    /// <returns>The props segment, or null.</returns>
    /// <remarks>
    /// A throwing value uses <see cref="CaptureText"/>. The loop is bounded by the bag.
    /// Separator escaping is reserved for exception messages and context values.
    /// </remarks>
    private static string? FormatProps(IReadOnlyDictionary<string, object>? properties)
    {
        if (properties == null || properties.Count == 0)
            return null;
        return "props: " + CaptureText.Join(properties);
    }

    /// <summary>
    /// Reads <see cref="Exception.ToString"/> and indents every line by four spaces.
    /// This is the only place a text sink calls <see cref="Exception.ToString"/>.
    /// </summary>
    /// <param name="exception">Exception on the event. Not null.</param>
    /// <returns>The indented stack, or null when <see cref="Exception.ToString"/> returns empty.</returns>
    /// <remarks>
    /// Does not throw. A throwing <see cref="Exception.ToString"/> becomes one indented capture placeholder.
    /// At most 256 lines are kept so a pathological stack cannot grow the record without a bound.
    /// </remarks>
    private static string? ReadFullStack(Exception exception)
    {
        string? text;
        try
        {
            text = exception.ToString();
        }
        catch (Exception failure)
        {
            return "    [capture failed: " + failure.GetType().Name + "]";
        }

        if (string.IsNullOrEmpty(text))
            return null;
        return IndentStack(text);
    }

    /// <summary>Prefixes each line with four spaces so the stack sits under the summary line.</summary>
    /// <param name="text">Result of <see cref="Exception.ToString"/>. Not null or empty.</param>
    /// <returns>The indented block, lines separated by <c>\n</c>.</returns>
    /// <remarks>Does not throw. A trailing newline does not add a blank indented line. The loop stops at 256 lines.</remarks>
    private static string IndentStack(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 16);
        var start = 0;
        var lines = 0;
        for (var i = 0; i <= text.Length && lines < MaxStackLines; i++)
        {
            var atEnd = i == text.Length;
            if (atEnd && start >= text.Length)
                break;
            if (!atEnd && text[i] != '\n')
                continue;

            var end = i;
            if (end > start && text[end - 1] == '\r')
                end--;
            if (lines > 0)
                builder.Append('\n');
            builder.Append("    ");
            builder.Append(text, start, end - start);
            lines++;
            start = i + 1;
        }

        return builder.ToString();
    }
}
