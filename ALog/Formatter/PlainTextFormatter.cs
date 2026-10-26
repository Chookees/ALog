namespace ALog.Formatters;

using System.Globalization;
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
    private readonly StampKind _stampKind;
    private readonly CultureInfo _probedCulture;
    private readonly bool _probedFastOk;

    /// <summary>UTC millisecond pattern. Hand-written as 24 bytes with a <c>T</c>, millis, and <c>Z</c>.</summary>
    private const string UtcMillisPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Default pattern. Hand-written as 19 bytes with a space and no millis or zone.</summary>
    private const string LocalSecondsPattern = "yyyy-MM-dd HH:mm:ss";

    /// <summary>How many lines of <see cref="Exception.ToString"/> are indented. The rest are dropped.</summary>
    private const int MaxStackLines = 256;

    /// <summary>Which hand-written stamp <see cref="TryWriteUtf8"/> may use for this formatter's pattern.</summary>
    private enum StampKind : byte
    {
        /// <summary>Pattern is not one of the two fast stamps; <see cref="TryWriteUtf8"/> always returns false.</summary>
        None = 0,

        /// <summary><see cref="UtcMillisPattern"/> — 24 bytes.</summary>
        UtcMillis = 1,

        /// <summary><see cref="LocalSecondsPattern"/> — 19 bytes.</summary>
        LocalSeconds = 2,
    }

    /// <summary>
    /// Creates a formatter that writes one line and does not append the full stack.
    /// The time format is <c>yyyy-MM-dd HH:mm:ss</c>.
    /// </summary>
    /// <remarks>
    /// This constructor is the call <c>new PlainTextFormatter()</c>. It chains to the two-argument
    /// constructor so the default format lives in one place.
    /// </remarks>
    public PlainTextFormatter()
        : this(LocalSecondsPattern, false)
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
        : this(LocalSecondsPattern, includeStackTrace)
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
    /// <remarks>
    /// Both values are stored. Neither is validated; an unusable format fails when a line is rendered.
    /// The current culture is probed once here. <see cref="TryWriteUtf8"/> uses that result only while the same
    /// culture is still current, and probes again when a later write sees a different culture.
    /// The probe is stored before the constructor returns. After that the fields are not written.
    /// Any thread may construct a formatter. The probe reads only the constructing thread's culture.
    /// </remarks>
    public PlainTextFormatter(string dateTimeFormat, bool includeStackTrace)
    {
        _dateTimeFormat = dateTimeFormat;
        _includeStackTrace = includeStackTrace;
        if (dateTimeFormat == UtcMillisPattern)
            _stampKind = StampKind.UtcMillis;
        else if (dateTimeFormat == LocalSecondsPattern)
            _stampKind = StampKind.LocalSeconds;
        else
            _stampKind = StampKind.None;
        _probedCulture = CultureInfo.CurrentCulture;
        _probedFastOk = _stampKind != StampKind.None && AllowsFastStamp(_probedCulture);
    }

    /// <summary>
    /// True when the hand-written stamp matches <see cref="DateTime.ToString(string)"/> for <paramref name="culture"/>.
    /// </summary>
    /// <param name="culture">The culture of the thread that is about to write. Not null.</param>
    /// <returns>
    /// True only for a Gregorian calendar whose time separator is <c>:</c> and whose negative sign is <c>-</c>.
    /// </returns>
    /// <remarks>
    /// Does not throw. The stamp always uses <c>:</c> and a dot before the milliseconds, which is what
    /// <see cref="DateTime.ToString(string)"/> writes for this pattern when the time separator is <c>:</c>.
    /// The dot is a literal in that pattern, not <see cref="System.Globalization.NumberFormatInfo.NumberDecimalSeparator"/>.
    /// A different time separator, a non-Gregorian calendar, or a negative sign other than <c>-</c> would make an integer
    /// property or the clock differ, so the whole fast path stays off and <see cref="Format"/> is used.
    /// Does not allocate. Safe to call from any thread. The culture object is not stored.
    /// </remarks>
    private static bool AllowsFastStamp(CultureInfo culture)
    {
        if (culture.Calendar is not GregorianCalendar)
            return false;
        if (culture.DateTimeFormat.TimeSeparator != ":")
            return false;
        if (culture.NumberFormat.NegativeSign != "-")
            return false;
        return true;
    }

    /// <summary>
    /// True when this formatter's pattern is one of the two hand-written stamps and the writing thread's culture can use it.
    /// </summary>
    /// <returns>False when <see cref="Format"/> must render the line.</returns>
    /// <remarks>
    /// Does not throw. The constructor's probe is reused when <see cref="CultureInfo.CurrentCulture"/> is the same instance.
    /// A culture changed after construction is probed again. Safe to call from several threads: the probe fields are not written here.
    /// Both <see cref="StampKind.UtcMillis"/> and <see cref="StampKind.LocalSeconds"/> share <see cref="AllowsFastStamp"/>.
    /// </remarks>
    private bool FastStampAllowed()
    {
        if (_stampKind == StampKind.None)
            return false;

        var culture = CultureInfo.CurrentCulture;
        if (ReferenceEquals(culture, _probedCulture))
            return _probedFastOk;

        return AllowsFastStamp(culture);
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
    /// The category, the message, context, and <c>props:</c> are escaped so <c>|</c> and <c>--&gt;</c> cannot look like separators.
    /// A backslash in those fields is doubled only when it would read as an escape.
    /// <see cref="Exception.ToString"/> runs only when this formatter was constructed with
    /// <c>includeStackTrace: true</c>. A throw from that call becomes a capture placeholder
    /// and the line above it is kept.
    /// Allocates the line. The formatter has no shared mutable state.
    /// </remarks>
    public string Format(LogEvent logEvent)
    {
        var line = logEvent.Exception == null
            ? FormatWithoutException(logEvent)
            : ExceptionReport.JoinLine(
                FormatHead(logEvent),
                ExceptionReport.FormatText(logEvent.Exception),
                FormatProps(logEvent.Properties),
                ExceptionReport.FormatContext(logEvent.Context));
        if (!_includeStackTrace || logEvent.Exception == null)
            return line;

        var stack = ReadFullStack(logEvent.Exception);
        if (stack == null)
            return line;
        return line + "\n" + stack;
    }

    /// <summary>
    /// Writes the plain line as UTF-8, without a trailing newline, when it matches <see cref="Format"/>.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <param name="destination">Buffer filled from the start. Ignored when the result is false.</param>
    /// <param name="written">Byte count when the result is true. Zero when false.</param>
    /// <returns>
    /// False when the line has an exception, a context bag, an escape, a non-ASCII character,
    /// a value this writer does not render, or the buffer is too small. The caller then uses <see cref="Format"/>.
    /// </returns>
    /// <remarks>
    /// Does not throw. The timestamp is either <c>yyyy-MM-dd'T'HH:mm:ss.fff'Z'</c> or
    /// <c>yyyy-MM-dd HH:mm:ss</c>, matching the pattern stored at construction.
    /// The clock digits use <c>:</c> (and a dot for the UTC-millis form). That matches
    /// <see cref="Format"/> only when <see cref="FastStampAllowed"/> is true for the culture
    /// of this thread at the call.
    /// A false return does not promise that <paramref name="destination"/> is unchanged.
    /// Safe to call from several threads.
    /// </remarks>
    internal bool TryWriteUtf8(LogEvent logEvent, Span<byte> destination, out int written)
    {
        written = 0;
        if (!FastStampAllowed() || logEvent.Exception != null || logEvent.Context is { Count: > 0 })
            return false;

        var index = 0;
        if (!WriteByte(destination, ref index, (byte)'['))
            return false;
        if (_stampKind == StampKind.UtcMillis)
        {
            if (!WriteUtcStamp(destination, logEvent.Timestamp, ref index))
                return false;
        }
        else if (!WriteLocalSecondsStamp(destination, logEvent.Timestamp, ref index))
        {
            return false;
        }

        if (!WriteAscii(destination, ref index, "] ["))
            return false;
        if (!WriteAscii(destination, ref index, LevelName(logEvent.Level)))
            return false;
        if (!WriteByte(destination, ref index, (byte)']'))
            return false;

        if (!string.IsNullOrEmpty(logEvent.Category))
        {
            if (!WriteAscii(destination, ref index, " ["))
                return false;
            if (!WritePlain(destination, ref index, logEvent.Category))
                return false;
            if (!WriteByte(destination, ref index, (byte)']'))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(logEvent.Message))
        {
            if (!WriteByte(destination, ref index, (byte)' '))
                return false;
            if (!WritePlain(destination, ref index, logEvent.Message))
                return false;
        }

        if (logEvent.Properties is { Count: > 0 })
        {
            if (!WriteAscii(destination, ref index, " | props: "))
                return false;
            if (!WriteProps(destination, logEvent.Properties, ref index))
                return false;
        }

        written = index;
        return true;
    }

    /// <summary>Enum name. Matches <see cref="LogLevel.ToString"/> for the known levels.</summary>
    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info",
        LogLevel.Warn => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Fatal => "Fatal",
        _ => level.ToString(),
    };

    /// <summary>
    /// Writes <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> with a colon and a dot, never the culture's separators.
    /// </summary>
    /// <param name="destination">Buffer. Not checked for null. The caller passes a span.</param>
    /// <param name="timestamp">Clock fields. The year is the Gregorian year on <see cref="DateTime"/>.</param>
    /// <param name="index">Start index. Advanced by 24 when the stamp fits.</param>
    /// <returns>False when fewer than 24 bytes remain. <paramref name="index"/> is then unchanged.</returns>
    /// <remarks>
    /// Does not throw. Called only after <see cref="FastStampAllowed"/> so these separators match <see cref="Format"/>.
    /// Does not read the culture. Safe to call from any thread.
    /// </remarks>
    private static bool WriteUtcStamp(Span<byte> destination, DateTime timestamp, ref int index)
    {
        if (index + 24 > destination.Length)
            return false;
        Write4(destination, index, timestamp.Year);
        destination[index + 4] = (byte)'-';
        Write2(destination, index + 5, timestamp.Month);
        destination[index + 7] = (byte)'-';
        Write2(destination, index + 8, timestamp.Day);
        destination[index + 10] = (byte)'T';
        Write2(destination, index + 11, timestamp.Hour);
        destination[index + 13] = (byte)':';
        Write2(destination, index + 14, timestamp.Minute);
        destination[index + 16] = (byte)':';
        Write2(destination, index + 17, timestamp.Second);
        destination[index + 19] = (byte)'.';
        Write3(destination, index + 20, timestamp.Millisecond);
        destination[index + 23] = (byte)'Z';
        index += 24;
        return true;
    }

    /// <summary>
    /// Writes <c>yyyy-MM-dd HH:mm:ss</c> with a space and colons, never the culture's separators.
    /// </summary>
    /// <param name="destination">Buffer. Not checked for null. The caller passes a span.</param>
    /// <param name="timestamp">Clock fields. The year is the Gregorian year on <see cref="DateTime"/>.</param>
    /// <param name="index">Start index. Advanced by 19 when the stamp fits.</param>
    /// <returns>False when fewer than 19 bytes remain. <paramref name="index"/> is then unchanged.</returns>
    /// <remarks>
    /// Does not throw. Called only after <see cref="FastStampAllowed"/> so these separators match <see cref="Format"/>.
    /// Does not read the culture. Safe to call from any thread. Milliseconds and a zone letter are omitted.
    /// </remarks>
    private static bool WriteLocalSecondsStamp(Span<byte> destination, DateTime timestamp, ref int index)
    {
        if (index + 19 > destination.Length)
            return false;
        Write4(destination, index, timestamp.Year);
        destination[index + 4] = (byte)'-';
        Write2(destination, index + 5, timestamp.Month);
        destination[index + 7] = (byte)'-';
        Write2(destination, index + 8, timestamp.Day);
        destination[index + 10] = (byte)' ';
        Write2(destination, index + 11, timestamp.Hour);
        destination[index + 13] = (byte)':';
        Write2(destination, index + 14, timestamp.Minute);
        destination[index + 16] = (byte)':';
        Write2(destination, index + 17, timestamp.Second);
        index += 19;
        return true;
    }

    private static void Write4(Span<byte> destination, int index, int value)
    {
        destination[index] = (byte)('0' + value / 1000);
        destination[index + 1] = (byte)('0' + (value / 100) % 10);
        destination[index + 2] = (byte)('0' + (value / 10) % 10);
        destination[index + 3] = (byte)('0' + value % 10);
    }

    private static void Write3(Span<byte> destination, int index, int value)
    {
        destination[index] = (byte)('0' + value / 100);
        destination[index + 1] = (byte)('0' + (value / 10) % 10);
        destination[index + 2] = (byte)('0' + value % 10);
    }

    private static void Write2(Span<byte> destination, int index, int value)
    {
        destination[index] = (byte)('0' + value / 10);
        destination[index + 1] = (byte)('0' + value % 10);
    }

    private static bool WriteAscii(Span<byte> destination, ref int index, string text)
    {
        if (index + text.Length > destination.Length)
            return false;
        for (var i = 0; i < text.Length; i++)
            destination[index++] = (byte)text[i];
        return true;
    }

    private static bool WriteByte(Span<byte> destination, ref int index, byte value)
    {
        if (index >= destination.Length)
            return false;
        destination[index++] = value;
        return true;
    }

    /// <summary>False when the text needs the escaping rules or is not ASCII.</summary>
    private static bool WritePlain(Span<byte> destination, ref int index, string text)
    {
        if (index + text.Length > destination.Length)
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '\\' or '|' or '\r' or '\n' || ch > 127)
                return false;
            if (ch == '-' && i + 2 < text.Length && text[i + 1] == '-' && text[i + 2] == '>')
                return false;
            destination[index++] = (byte)ch;
        }

        return true;
    }

    private static bool WriteProps(Span<byte> destination, IReadOnlyDictionary<string, object> properties, ref int index)
    {
        var first = true;
        foreach (var pair in properties)
        {
            if (!first)
            {
                if (!WriteAscii(destination, ref index, ", "))
                    return false;
            }

            first = false;
            if (!WritePlain(destination, ref index, pair.Key))
                return false;
            if (!WriteByte(destination, ref index, (byte)'='))
                return false;
            if (!WriteValue(destination, ref index, pair.Value))
                return false;
        }

        return true;
    }

    private static bool WriteValue(Span<byte> destination, ref int index, object? value)
    {
        if (value is null)
            return true;
        if (value is string text)
            return WritePlain(destination, ref index, text);
        if (value is int number)
            return WriteInt(destination, ref index, number);
        return false;
    }

    private static bool WriteInt(Span<byte> destination, ref int index, int value)
    {
        if (value == int.MinValue)
            return false;
        if (value < 0)
        {
            if (!WriteByte(destination, ref index, (byte)'-'))
                return false;
            value = -value;
        }

        Span<byte> digits = stackalloc byte[10];
        var count = 0;
        do
        {
            digits[count++] = (byte)('0' + (value % 10));
            value /= 10;
        }
        while (value > 0);

        if (index + count > destination.Length)
            return false;
        for (var i = count - 1; i >= 0; i--)
            destination[index++] = digits[i];
        return true;
    }

    /// <summary>Joins time, level, category, and message with spaces. Blank pieces are left out.</summary>
    /// <param name="logEvent">Event being rendered. Not null.</param>
    /// <returns>The head of the line, before the first <c> | </c>.</returns>
    /// <remarks>
    /// Does not throw. The time format is the one given to the constructor. At most four pieces are joined.
    /// The category and the message are escaped with the same rules as an exception message.
    /// Neither is cut to 500 characters.
    /// </remarks>
    private string FormatHead(LogEvent logEvent)
    {
        var parts = new List<string>(4);
        parts.Add("[" + logEvent.Timestamp.ToString(_dateTimeFormat) + "]");
        parts.Add("[" + logEvent.Level + "]");
        if (!string.IsNullOrEmpty(logEvent.Category))
            parts.Add("[" + ExceptionReport.EscapeLine(logEvent.Category) + "]");
        if (!string.IsNullOrWhiteSpace(logEvent.Message))
            parts.Add(ExceptionReport.EscapeLine(logEvent.Message));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Renders an event that has no exception with one space join, the same shape as a line that has no tail.
    /// Property and context segments already start with <c>| </c>, so the join still reads as <c> | </c>.
    /// </summary>
    /// <param name="logEvent">Event being rendered. Its exception is null.</param>
    /// <returns>The line, without a trailing newline.</returns>
    /// <remarks>
    /// Does not throw. Does not call <see cref="Exception.ToString"/>.
    /// The category, the message, keys, and values are escaped. The category and the message are not cut to 500 characters.
    /// Blank slots are left out of the join. The present pieces are copied into one array, then joined once.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private string FormatWithoutException(LogEvent logEvent)
    {
        var parts = new string[6];
        var count = 0;
        parts[count++] = "[" + logEvent.Timestamp.ToString(_dateTimeFormat) + "]";
        parts[count++] = "[" + logEvent.Level + "]";
        if (!string.IsNullOrEmpty(logEvent.Category))
            parts[count++] = "[" + ExceptionReport.EscapeLine(logEvent.Category) + "]";
        if (!string.IsNullOrWhiteSpace(logEvent.Message))
            parts[count++] = ExceptionReport.EscapeLine(logEvent.Message);
        var props = ExceptionReport.FormatProps(logEvent.Properties, leadingPipe: true);
        if (!string.IsNullOrWhiteSpace(props))
            parts[count++] = props;
        var context = ExceptionReport.FormatContext(logEvent.Context, leadingPipe: true);
        if (!string.IsNullOrWhiteSpace(context))
            parts[count++] = context;
        return string.Join(" ", parts.AsSpan(0, count));
    }

    /// <summary>Renders template properties as <c>props: name=value</c>. Values are escaped like context values.</summary>
    /// <param name="properties">Property bag. Null or empty omits the segment.</param>
    /// <returns>The props segment, or null.</returns>
    /// <remarks>
    /// The text comes from <see cref="ExceptionReport.FormatProps"/>, the same helper the line uses for context.
    /// JSON output does not call this method. The loop is bounded by the bag.
    /// </remarks>
    private static string? FormatProps(IReadOnlyDictionary<string, object>? properties)
    {
        return ExceptionReport.FormatProps(properties);
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
            // shortcut: Exception.ToString walks inners with no cycle guard. A self-referencing exception built via reflection can overflow inside .NET's ToString().
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
