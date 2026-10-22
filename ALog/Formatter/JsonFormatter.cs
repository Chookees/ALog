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
        var jsonModel = new
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

        return JsonSerializer.Serialize(jsonModel, _options);
    }
}
