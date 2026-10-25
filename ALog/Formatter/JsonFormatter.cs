namespace ALog.Formatters;

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALog.Core;
using ALog.Public.Interfaces;

public class JsonFormatter : ILogFormatter
{
    private readonly JsonSerializerOptions _options;

    public JsonFormatter(bool pretty = false)
    {
        _options = new JsonSerializerOptions
        {
            WriteIndented = pretty,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Renders one event as JSON.
    /// The exception object is <see cref="ExceptionReport.CreateBody"/>, so <c>type</c>, <c>message</c>,
    /// and <c>stackTrace</c> stay the runtime values and <c>source</c> and <c>inner</c> are added beside them.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <returns>One JSON document. Compact form is a single line; pretty form is indented.</returns>
    /// <remarks>
    /// Does not throw from the exception reader. Null properties are omitted.
    /// The message inside <c>exception</c> is not escaped or truncated.
    /// </remarks>
    public string Format(LogEvent logEvent)
    {
        if (!_options.WriteIndented
            && logEvent.Exception == null
            && TryWriteSimple(logEvent, out var json))
            return json;

        return JsonSerializer.Serialize(Model(logEvent), _options);
    }

    /// <summary>The object <see cref="JsonSerializer"/> writes when the hand-built line cannot.</summary>
    private static object Model(LogEvent logEvent) => new
    {
        timestamp = logEvent.Timestamp.ToString("o"),
        level = logEvent.Level.ToString(),
        message = logEvent.Message,
        messageTemplate = logEvent.MessageTemplate,
        category = logEvent.Category,
        exception = logEvent.Exception != null ? ExceptionReport.CreateBody(logEvent.Exception) : null,
        properties = logEvent.Properties,
        context = logEvent.Context
    };

    [ThreadStatic]
    private static System.Text.StringBuilder? _json;

    /// <summary>Largest JSON builder kept on the thread. A bigger one is dropped after the call.</summary>
    private const int MaxRetainedJson = 4096;

    /// <summary>Capacity of the JSON builder still kept on this thread. Zero when none is kept.</summary>
    /// <remarks>Does not throw. Another call on this thread can change it. Other threads have their own builder.</remarks>
    internal static int RetainedJsonCapacity => _json?.Capacity ?? 0;

    /// <summary>The thread's JSON builder, replaced when the one still kept is larger than <see cref="MaxRetainedJson"/>.</summary>
    /// <returns>A cleared builder. Not null.</returns>
    /// <remarks>Does not throw. Only this thread reads and writes <see cref="_json"/>.</remarks>
    private static System.Text.StringBuilder TakeJson()
    {
        var builder = _json;
        if (builder == null || builder.Capacity > MaxRetainedJson)
        {
            builder = new System.Text.StringBuilder(256);
            _json = builder;
        }
        else
        {
            builder.Clear();
        }

        return builder;
    }

    /// <summary>Drops the builder when it grew past <see cref="MaxRetainedJson"/>.</summary>
    /// <param name="builder">The builder <see cref="TakeJson"/> returned. Not null.</param>
    /// <remarks>Does not throw. Only this thread calls it. A small builder stays for the next event.</remarks>
    private static void ReleaseJson(System.Text.StringBuilder builder)
    {
        if (builder.Capacity > MaxRetainedJson)
            _json = null;
    }

    /// <summary>
    /// Builds the compact document for an event with no exception and only string or int properties.
    /// </summary>
    /// <param name="logEvent">Event to render.</param>
    /// <param name="json">The document when the result is true.</param>
    /// <returns>False when <see cref="JsonSerializer"/> must write the event so the text stays the same.</returns>
    /// <remarks>
    /// Does not throw. Null fields are omitted, matching <see cref="JsonIgnoreCondition.WhenWritingNull"/>.
    /// A timestamp that is not UTC, a non-ASCII character, or a quote or backslash uses the serializer.
    /// </remarks>
    private static bool TryWriteSimple(LogEvent logEvent, out string json)
    {
        json = string.Empty;
        if (!TryAppendSimple(logEvent, out var builder))
            return false;

        json = builder.ToString();
        ReleaseJson(builder);
        return true;
    }

    /// <summary>
    /// Writes the compact document as UTF-8, without a trailing newline, when it matches <see cref="Format"/>.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <param name="destination">Buffer filled from the start. Ignored when the result is false.</param>
    /// <param name="written">Byte count when the result is true. Zero when false.</param>
    /// <returns>
    /// False when the document is pretty, has an exception, needs the serializer, or does not fit.
    /// The caller then uses <see cref="Format"/>.
    /// </returns>
    /// <remarks>
    /// Does not throw. The bytes are the UTF-8 of <see cref="Format"/> for the same event, with no newline.
    /// The simple document is ASCII, so each character is one byte. A false return does not promise that
    /// <paramref name="destination"/> is unchanged. Safe to call from several threads. The builder is thread-static.
    /// </remarks>
    internal bool TryWriteUtf8(LogEvent logEvent, Span<byte> destination, out int written)
    {
        written = 0;
        if (_options.WriteIndented || logEvent.Exception != null)
            return false;
        if (!TryAppendSimple(logEvent, out var builder))
            return false;

        if (builder.Length > destination.Length)
        {
            ReleaseJson(builder);
            return false;
        }

        CopyAscii(builder, destination);
        written = builder.Length;
        ReleaseJson(builder);
        return true;
    }

    /// <summary>
    /// Appends the compact document when <see cref="Format"/> would not call <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <param name="builder">The document when the result is true. Unspecified when false.</param>
    /// <returns>False when the serializer must write the event.</returns>
    /// <remarks>
    /// Does not throw. The caller releases <paramref name="builder"/> after a true result.
    /// A false return before any character does not use that builder. A false return after a partial document releases it.
    /// Only this thread uses the builder. The checks are the same ones <see cref="TryWriteSimple"/> used.
    /// </remarks>
    private static bool TryAppendSimple(LogEvent logEvent, out System.Text.StringBuilder builder)
    {
        if (!CanWriteSimple(logEvent))
        {
            builder = UnusedBuilder;
            return false;
        }

        builder = TakeJson();
        builder.Append("{\"timestamp\":\"");
        AppendUtcRoundTrip(builder, logEvent.Timestamp);
        builder.Append("\",\"level\":\"");
        builder.Append(LevelName(logEvent.Level));
        builder.Append("\",\"message\":\"");
        builder.Append(logEvent.Message);
        builder.Append('"');
        if (logEvent.MessageTemplate is not null)
        {
            builder.Append(",\"messageTemplate\":\"");
            builder.Append(logEvent.MessageTemplate);
            builder.Append('"');
        }

        if (logEvent.Category is not null)
        {
            builder.Append(",\"category\":\"");
            builder.Append(logEvent.Category);
            builder.Append('"');
        }

        if (logEvent.Properties is not null)
        {
            builder.Append(",\"properties\":{");
            if (!AppendBag(builder, logEvent.Properties))
            {
                ReleaseJson(builder);
                return false;
            }
            builder.Append('}');
        }

        if (logEvent.Context is not null)
        {
            builder.Append(",\"context\":{");
            if (logEvent.Context.Count > 0 && !AppendBag(builder, logEvent.Context))
            {
                ReleaseJson(builder);
                return false;
            }
            builder.Append('}');
        }

        builder.Append('}');
        return true;
    }

    /// <summary>True when the hand-built document matches <see cref="JsonSerializer"/> for this event.</summary>
    /// <param name="logEvent">Event to test. Not null.</param>
    /// <returns>False when the serializer must write it.</returns>
    /// <remarks>Does not throw. Does not allocate. Any thread may call it. The checks match the compact path.</remarks>
    private static bool CanWriteSimple(LogEvent logEvent)
    {
        if (logEvent.Message is null || logEvent.Timestamp.Kind != DateTimeKind.Utc)
            return false;
        if (logEvent.Properties is { Count: > 0 } && logEvent.Properties is not Dictionary<string, object>)
            return false;
        if (logEvent.Context is { Count: > 0 } && logEvent.Context is not Dictionary<string, object>)
            return false;

        return IsJsonSafe(logEvent.Message)
            && IsJsonSafe(logEvent.MessageTemplate)
            && IsJsonSafe(logEvent.Category);
    }

    /// <summary>Copies an ASCII document into <paramref name="destination"/>.</summary>
    /// <param name="builder">The document. Not null. Every character is ASCII.</param>
    /// <param name="destination">Buffer at least as long as <paramref name="builder"/>. The caller checked the length.</param>
    /// <remarks>
    /// Does not throw. A line of at most 1024 characters stays on the stack. A longer line rents a buffer and returns it.
    /// Only this thread calls it. The bytes are the same as UTF-8 of the builder text.
    /// </remarks>
    private static void CopyAscii(System.Text.StringBuilder builder, Span<byte> destination)
    {
        var length = builder.Length;
        if (length <= 1024)
        {
            Span<char> chars = stackalloc char[length];
            builder.CopyTo(0, chars, length);
            for (var i = 0; i < length; i++)
                destination[i] = (byte)chars[i];
            return;
        }

        var rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            var slice = rented.AsSpan(0, length);
            builder.CopyTo(0, slice, length);
            for (var i = 0; i < length; i++)
                destination[i] = (byte)slice[i];
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Out value when <see cref="TryAppendSimple"/> returns false. Not a document.</summary>
    private static readonly System.Text.StringBuilder UnusedBuilder = new(0);

    private static bool AppendBag(System.Text.StringBuilder builder, IReadOnlyDictionary<string, object> bag)
    {
        var first = true;
        foreach (var pair in bag)
        {
            if (!IsJsonSafe(pair.Key))
                return false;
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append('"');
            builder.Append(pair.Key);
            builder.Append("\":");
            if (!AppendJsonValue(builder, pair.Value))
                return false;
        }

        return true;
    }

    private static bool AppendJsonValue(System.Text.StringBuilder builder, object? value)
    {
        if (value is null)
        {
            builder.Append("null");
            return true;
        }

        if (value is string text)
        {
            if (!IsJsonSafe(text))
                return false;
            builder.Append('"');
            builder.Append(text);
            builder.Append('"');
            return true;
        }

        if (value is int number)
        {
            if (number < 0)
            {
                builder.Append('-');
                if (number == int.MinValue)
                    return false;
                number = -number;
            }

            AppendDigits(builder, number, DigitWidth(number));
            return true;
        }

        if (value is bool flag)
        {
            builder.Append(flag ? "true" : "false");
            return true;
        }

        return false;
    }

    private static void AppendUtcRoundTrip(System.Text.StringBuilder builder, DateTime timestamp)
    {
        AppendDigits(builder, timestamp.Year, 4);
        builder.Append('-');
        AppendDigits(builder, timestamp.Month, 2);
        builder.Append('-');
        AppendDigits(builder, timestamp.Day, 2);
        builder.Append('T');
        AppendDigits(builder, timestamp.Hour, 2);
        builder.Append(':');
        AppendDigits(builder, timestamp.Minute, 2);
        builder.Append(':');
        AppendDigits(builder, timestamp.Second, 2);
        builder.Append('.');
        AppendDigits(builder, (int)(timestamp.Ticks % TimeSpan.TicksPerSecond), 7);
        builder.Append('Z');
    }

    private static void AppendDigits(System.Text.StringBuilder builder, int value, int width)
    {
        Span<char> digits = stackalloc char[16];
        for (var i = width - 1; i >= 0; i--)
        {
            digits[i] = (char)('0' + (value % 10));
            value /= 10;
        }

        builder.Append(digits.Slice(0, width));
    }

    private static int DigitWidth(int value)
    {
        var width = 1;
        while (value >= 10)
        {
            value /= 10;
            width++;
        }

        return width;
    }

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
    /// True when the default serializer would write <paramref name="text"/> unchanged inside a JSON string.
    /// </summary>
    /// <param name="text">A message, template, category, property name, or string property. Null and empty are safe.</param>
    /// <returns>False when the hand-built line would differ from <see cref="JsonSerializer"/>.</returns>
    /// <remarks>
    /// Does not throw. The default encoder escapes <c>'</c> as <c>\u0027</c> and a backtick as <c>\u0060</c>,
    /// plus quotes, backslashes, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>+</c>, and anything outside ASCII 0x20..0x7E.
    /// Those characters take the serializer path so the bytes match the master library. Any thread may call this.
    /// The loop is bounded by the string length.
    /// </remarks>
    private static bool IsJsonSafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x20 || ch > 0x7E || ch is '"' or '\\' or '\'' or '`' or '<' or '>' or '&' or '+')
                return false;
        }

        return true;
    }
}
