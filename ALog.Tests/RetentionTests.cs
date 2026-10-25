using System.Text.Json;
using System.Text.Json.Serialization;
using ALog.Config;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>
/// Shared empty bags stay immutable, and thread-static buffers do not keep a huge line.
/// </summary>
public sealed class RetentionTests
{
    /// <summary>
    /// The empty bag cannot be cast to a dictionary, and a later format still returns that same instance.
    /// </summary>
    /// <returns>A task that finishes after the second format.</returns>
    /// <remarks>
    /// The engine's no-property events use the same bag. An enricher is not required: the cast is the mutation attempt.
    /// Not thread-safe. The bag is process-wide and immutable.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task EmptyProperties_AreNotAMutableDictionary()
    {
        await Task.CompletedTask;
        var first = MessageTemplate.Format("plain");
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, object>)first.Properties);
        var second = MessageTemplate.Format("plain");
        Assert.Same(first.Properties, second.Properties);

        var writer = new RecordingWriter();
        using var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer).UseAsync(false));
        engine.Log("hello");
        engine.Log("again");
        Assert.Same(writer.Events[0].Properties, writer.Events[1].Properties);
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, object>)writer.Events[0].Properties!);
        Assert.Equal("hello", writer.Events[0].Message);
        Assert.Equal("again", writer.Events[1].Message);
    }

    /// <summary>An empty non-dictionary bag still takes the compact JSON path and writes <c>"properties":{}</c>.</summary>
    /// <returns>A task that finishes after the document is compared with the serializer.</returns>
    /// <remarks>The bag is the shared empty instance. A non-empty custom bag is not what this checks. Not thread-safe.</remarks>
    [Fact(Timeout = 30000)]
    public async Task EmptyPropertyBag_StaysOnTheJsonDirectPath()
    {
        await Task.CompletedTask;
        var when = new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc);
        var evt = new LogEvent(when, LogLevel.Info, "plain", null, null, MessageTemplate.Format("plain").Properties);
        var text = new JsonFormatter().Format(evt);
        Assert.Contains("\"properties\":{}", text, StringComparison.Ordinal);
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var expected = JsonSerializer.Serialize(new
        {
            timestamp = evt.Timestamp.ToString("o"),
            level = evt.Level.ToString(),
            message = evt.Message,
            messageTemplate = evt.MessageTemplate,
            category = evt.Category,
            exception = (object?)null,
            properties = evt.Properties,
            context = evt.Context,
        }, options);
        Assert.Equal(expected, text);
    }

    /// <summary>A line longer than the retained cap is still written, and the thread keeps at most 4096 scratch bytes.</summary>
    /// <returns>A task that finishes after the file is read.</returns>
    /// <remarks>The message is 20_000 ASCII characters. The bound is the retained array, not the file. Not thread-safe.</remarks>
    [Fact(Timeout = 30000)]
    public async Task HugeLine_DoesNotKeepTheLargeBuffer()
    {
        await Task.CompletedTask;
        var message = new string('A', 20_000);
        var directory = Path.Combine(Path.GetTempPath(), "alog-retain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "big.log");
            var formatter = new PlainTextFormatter("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var evt = new LogEvent(new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc), LogLevel.Info, message, null, null);
            using (var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero))
                writer.Write(evt);

            Assert.Contains(message, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.True(FileLogWriter.RetainedUtf8Bytes <= 4096);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A long template renders, and the thread-static render buffer is not left above 4096.</summary>
    /// <returns>A task that finishes after the render.</returns>
    /// <remarks>The literal is longer than the cap, plus one hole. Not thread-safe. The buffer belongs to this thread.</remarks>
    [Fact(Timeout = 30000)]
    public async Task HugeTemplate_DropsTheRenderBuffer()
    {
        await Task.CompletedTask;
        var text = new string('B', 5_000) + " {Id}";
        var rendered = MessageTemplate.Format(text, 4);
        Assert.EndsWith("4", rendered.Rendered, StringComparison.Ordinal);
        Assert.True(MessageTemplate.RetainedRenderCapacity <= MessageTemplate.MaxRetainedBuilder);
    }

    /// <summary>A long JSON message renders, and the thread-static builder is not left above 4096.</summary>
    /// <returns>A task that finishes after the document is read.</returns>
    /// <remarks>The message is ASCII, so it is eligible for the compact writer. Not thread-safe.</remarks>
    [Fact(Timeout = 30000)]
    public async Task HugeJson_DropsTheBuilder()
    {
        await Task.CompletedTask;
        var message = new string('C', 20_000);
        var evt = new LogEvent(new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc), LogLevel.Info, message, null, null);
        var text = new JsonFormatter().Format(evt);
        Assert.Contains(message, text, StringComparison.Ordinal);
        Assert.True(JsonFormatter.RetainedJsonCapacity <= 4096);
    }

    /// <summary>
    /// Two exceptions that compare equal still both appear. The frame cache does not treat them as one entry.
    /// </summary>
    /// <returns>A task that finishes after the text is read.</returns>
    /// <remarks>
    /// <see cref="AggregateException"/> lists the flattened entries. Equals and GetHashCode would collide in an ordinary dictionary.
    /// Not thread-safe. The cache is cleared before this returns.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task EqualExceptions_BothAppearInTheAggregate()
    {
        await Task.CompletedTask;
        var aggregate = new AggregateException(new EqualEx("one-side"), new EqualEx("two-side"));
        var text = ExceptionReport.FormatText(aggregate);
        Assert.Contains("one-side", text, StringComparison.Ordinal);
        Assert.Contains("two-side", text, StringComparison.Ordinal);
    }

    /// <summary>Compares equal to every other instance and hashes to one value.</summary>
    /// <remarks>Used only to show the frame cache must not use those members. The message is the text the line must keep.</remarks>
    private sealed class EqualEx : Exception
    {
        /// <summary>Stores <paramref name="message"/> on the base exception.</summary>
        /// <param name="message">Text that must survive formatting. Not null.</param>
        /// <remarks>Does not throw. Does not capture a stack beyond the base constructor.</remarks>
        internal EqualEx(string message)
            : base(message)
        {
        }

        /// <summary>True for any <see cref="EqualEx"/>.</summary>
        /// <param name="obj">The other object. May be null.</param>
        /// <returns>True when <paramref name="obj"/> is an <see cref="EqualEx"/>.</returns>
        /// <remarks>Does not throw. This is the collision the cache must ignore.</remarks>
        public override bool Equals(object? obj) => obj is EqualEx;

        /// <summary>One hash for every instance.</summary>
        /// <returns>1.</returns>
        /// <remarks>Does not throw. Paired with <see cref="Equals(object?)"/>.</remarks>
        public override int GetHashCode() => 1;
    }
}
