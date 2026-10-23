namespace ALog.Formatters;

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
        if (logEvent.Message is null || logEvent.Timestamp.Kind != DateTimeKind.Utc)
            return false;
        if (logEvent.Properties is not null && logEvent.Properties is not Dictionary<string, object>)
            return false;
        if (logEvent.Context is { Count: > 0 } && logEvent.Context is not Dictionary<string, object>)
            return false;
        if (!IsJsonSafe(logEvent.Message)
            || !IsJsonSafe(logEvent.MessageTemplate)
            || !IsJsonSafe(logEvent.Category))
            return false;

        var builder = _json ??= new System.Text.StringBuilder(256);
        builder.Clear();
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
                return false;
            builder.Append('}');
        }

        if (logEvent.Context is not null)
        {
            builder.Append(",\"context\":{");
            if (logEvent.Context.Count > 0 && !AppendBag(builder, logEvent.Context))
                return false;
            builder.Append('}');
        }

        builder.Append('}');
        json = builder.ToString();
        return true;
    }

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

    /// <summary>True when the serializer would write the text unchanged inside a JSON string.</summary>
    private static bool IsJsonSafe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x20 || ch > 0x7E || ch is '"' or '\\' or '<' or '>' or '&' or '+')
                return false;
        }

        return true;
    }
}
