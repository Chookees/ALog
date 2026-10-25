using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.File;

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

    /// <summary>
    /// The UTF-8 buffer and the file both match <see cref="JsonFormatter.Format"/>.
    /// A short buffer, a pretty formatter, an apostrophe, and an exception stay on the string path.
    /// A line longer than 1024 characters still matches.
    /// </summary>
    /// <returns>A task that finishes after the file is compared and deleted.</returns>
    /// <remarks>
    /// Does not throw for a missing file after the delete. The file writer is disposed before the read.
    /// Not thread-safe. Only the test thread calls the formatter.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Utf8Bytes_MatchFormat_AndTheFileUsesThem()
    {
        var when = new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc);
        var evt = new LogEvent(
            when,
            LogLevel.Info,
            "User 7 logged in from 192.168.1.10",
            null,
            null,
            new Dictionary<string, object> { ["UserId"] = 7, ["Ip"] = "192.168.1.10" },
            "User {UserId} logged in from {Ip}",
            "Bench");
        var formatter = new JsonFormatter();
        var text = formatter.Format(evt);
        var expected = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[expected.Length];
        Assert.True(formatter.TryWriteUtf8(evt, buffer, out var written));
        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, buffer);

        Assert.False(formatter.TryWriteUtf8(evt, buffer.AsSpan(0, 8), out _));
        Assert.False(new JsonFormatter(pretty: true).TryWriteUtf8(evt, buffer, out _));
        var broken = new LogEvent(when, LogLevel.Info, "don't", null);
        Assert.False(formatter.TryWriteUtf8(broken, buffer, out _));
        var thrown = new LogEvent(when, LogLevel.Error, "plain-line", new InvalidOperationException("x"));
        Assert.False(formatter.TryWriteUtf8(thrown, buffer, out _));

        var longMessage = new string('a', 1100);
        var longEvent = new LogEvent(when, LogLevel.Info, longMessage, null, null, null, longMessage, "app");
        var longText = formatter.Format(longEvent);
        var longExpected = Encoding.UTF8.GetBytes(longText);
        var longBuffer = new byte[longExpected.Length];
        Assert.True(formatter.TryWriteUtf8(longEvent, longBuffer, out var longWritten));
        Assert.Equal(longExpected.Length, longWritten);
        Assert.Equal(longExpected, longBuffer);

        var path = Path.Combine(Path.GetTempPath(), "alog-json-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(evt);
                writer.Write(broken);
            }

            var file = await File.ReadAllBytesAsync(path);
            var brokenText = formatter.Format(broken);
            var expectedFile = Encoding.UTF8.GetBytes(text + Environment.NewLine + brokenText + Environment.NewLine);
            Assert.Equal(expectedFile, file);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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
