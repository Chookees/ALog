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

    public string Format(LogEvent logEvent)
    {
        var jsonModel = new
        {
            timestamp = logEvent.Timestamp.ToString("o"),
            level = logEvent.Level.ToString(),
            message = logEvent.Message,
            exception = logEvent.Exception != null ? new
            {
                type = logEvent.Exception.GetType().FullName,
                message = logEvent.Exception.Message,
                stackTrace = logEvent.Exception.StackTrace
            } : null,
            context = logEvent.Context
        };

        return JsonSerializer.Serialize(jsonModel, _options);
    }
}
