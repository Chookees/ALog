using System.Text.Json;
using System.Text.Json.Serialization;
using ALog.Core;
using ALog.Formatters;

namespace ALog.Tests;

/// <summary>
/// Compact JSON must match <see cref="JsonSerializer"/> for characters the default encoder escapes.
/// </summary>
public sealed class JsonByteTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// An apostrophe and a backtick take the serializer path, so the document matches the master bytes.
    /// </summary>
    /// <returns>A task that finishes after the documents are compared.</returns>
    /// <remarks>
    /// The default encoder writes <c>\u0027</c> and <c>\u0060</c>. A hand-built line that copied those characters raw would differ.
    /// A plain message stays on the compact path and still matches the serializer, with no unicode escape.
    /// Not thread-safe. The formatter has no shared instance state beyond a thread-static builder.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ApostropheAndBacktick_MatchTheSerializer()
    {
        await Task.CompletedTask;
        var when = new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc);
        var escaped = new LogEvent(
            when,
            LogLevel.Info,
            "don't `tick`",
            null,
            null,
            new Dictionary<string, object> { ["note"] = "it's `fine`" },
            "don't `tick`",
            "app");
        var formatter = new JsonFormatter();
        Assert.Equal(Serialized(escaped), formatter.Format(escaped));

        var plain = new LogEvent(when, LogLevel.Info, "plain-line", null, null, null, "plain-line", "app");
        var text = formatter.Format(plain);
        Assert.Equal(Serialized(plain), text);
        Assert.Contains("plain-line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    /// <summary>The document <see cref="JsonFormatter"/> builds for the serializer path.</summary>
    /// <param name="logEvent">Event to model. Not null. No exception.</param>
    /// <returns>Compact JSON using the formatter's options.</returns>
    /// <remarks>
    /// Does not throw. Property order matches the formatter's model: timestamp, level, message, template, category, properties.
    /// Null fields are omitted. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static string Serialized(LogEvent logEvent)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp = logEvent.Timestamp.ToString("o"),
            level = logEvent.Level.ToString(),
            message = logEvent.Message,
            messageTemplate = logEvent.MessageTemplate,
            category = logEvent.Category,
            exception = (object?)null,
            properties = logEvent.Properties,
            context = logEvent.Context,
        }, Options);
    }
}
