// Lines the round-5 suite still missed. Each test hits a branch the product already has.
// Next file: ALog/Core/BackgroundLogQueue.cs.

using System.Collections;
using ALog.Config;
using ALog.Core;
using ALog.Internal;
using ALog.Public.Interfaces;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>Covers branches the round-5 measurement still reported as missed.</summary>
public sealed class Round5CoverageTests
{
    /// <summary>
    /// An idle queue completes <see cref="BackgroundLogQueue.FlushAsync"/> without arming a deadline timer.
    /// </summary>
    /// <returns>A task that finishes after dispose.</returns>
    /// <remarks>No writer call. The finally block disposes the queue. Not thread-safe.</remarks>
    [Fact(Timeout = 10000)]
    public async Task FlushAsync_IdleQueue_ReturnsACompletedTask()
    {
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { new RecordingWriter() },
            capacity: 8,
            batchSize: 1,
            flushInterval: TimeSpan.FromHours(1));
        try
        {
            var flush = queue.FlushAsync();
            Assert.True(flush.IsCompletedSuccessfully);
            await flush;
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>
    /// A property bag that is not a <see cref="Dictionary{TKey, TValue}"/> is copied before the event is stored.
    /// </summary>
    /// <returns>A completed task. The body does not wait.</returns>
    /// <remarks>No I/O. Synchronous writes. The engine is disposed. Not thread-safe.</remarks>
    [Fact(Timeout = 10000)]
    public async Task LogCaptured_CopiesAPropertyBagThatIsNotADictionary()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        using var engine = new LoggerEngine(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace).UseAsync(false));
        var props = new SortedDictionary<string, object> { ["A"] = "alpha" };
        engine.LogCaptured(new LoggerEngine.CapturedLog
        {
            Category = "R5",
            Level = LogLevel.Info,
            Message = "copied",
            MessageTemplate = "copied {A}",
            Properties = props
        });

        var evt = Assert.Single(writer.Events);
        Assert.Equal("alpha", evt.Properties!["A"]);
        Assert.IsType<Dictionary<string, object>>(evt.Properties);
    }

    /// <summary>
    /// The non-generic walk of the shared empty bag produces no pairs.
    /// </summary>
    /// <returns>A completed task. The body does not wait.</returns>
    /// <remarks>No I/O. Does not replace SelfLog. Safe to call from the test thread.</remarks>
    [Fact(Timeout = 10000)]
    public async Task EmptyPropertyBag_NonGenericEnumerator_IsEmpty()
    {
        await Task.CompletedTask;
        var enumerator = ((IEnumerable)EmptyPropertyBag.Instance).GetEnumerator();
        Assert.False(enumerator.MoveNext());
    }

    /// <summary>
    /// An unknown empty scope list is a no-op, and a list past the property cap drops the extra key.
    /// </summary>
    /// <returns>A completed task. The body does not wait.</returns>
    /// <remarks>
    /// The sequences are iterators, so they are not counted collections. The scope is disposed.
    /// SelfLog may record the cap. Not thread-safe. Only the test thread pushes.
    /// </remarks>
    [Fact(Timeout = 10000)]
    public async Task LogContext_UnknownSequence_EmptyIsNoOp_AndOverCapDropsTheRest()
    {
        await Task.CompletedTask;
        var empty = LogContext.Push(None());
        empty.Dispose();

        using var scope = LogContext.Push(TooMany());
        var context = ContextManager.GetContext();
        Assert.Equal(128, context.Count);
        Assert.False(context.ContainsKey("k128"));
        Assert.Equal(0, context["k0"]);
    }

    /// <summary>
    /// A custom ASCII line grows the retained scratch once, then caps the next growth at 4096 bytes.
    /// </summary>
    /// <remarks>
    /// Runs on a fresh thread so an earlier test's scratch cannot hide the cap.
    /// The file is deleted. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 10000)]
    public async Task FileScratch_RepeatedAsciiGrowth_StopsAtFourKiB()
    {
        var done = Task.Run(WriteTwoAsciiLines);
        var finished = await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(8))) == done;
        Assert.True(finished, "ascii scratch writes did not return");
        await done;
    }

    /// <summary>Writes two ASCII lines on this thread and checks the retained scratch length.</summary>
    /// <remarks>The newline length is included in the needed size. Deletes the file. Only this thread writes.</remarks>
    private static void WriteTwoAsciiLines()
    {
        var newline = Environment.NewLine.Length;
        var formatter = new FixedAsciiFormatter();
        var dir = Path.Combine(Path.GetTempPath(), "alog-r5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scratch.log");
        var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero);
        try
        {
            formatter.Text = new string('a', 3000 - newline);
            writer.Write(Line());
            formatter.Text = new string('b', 4000 - newline);
            writer.Write(Line());
            Assert.Equal(4096, FileLogWriter.RetainedUtf8Bytes);
            var text = File.ReadAllText(path);
            Assert.Contains(formatter.Text, text, StringComparison.Ordinal);
        }
        finally
        {
            writer.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>An info event. The text comes from the formatter, not the message.</summary>
    /// <returns>An event with a fixed timestamp.</returns>
    private static LogEvent Line()
        => new(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), LogLevel.Info, "m", null, null);

    /// <summary>An empty iterator. Not an <see cref="IReadOnlyCollection{T}"/>.</summary>
    /// <returns>No pairs.</returns>
    private static IEnumerable<KeyValuePair<string, object?>> None()
    {
        yield break;
    }

    /// <summary>129 pairs. One past the scope cap of 128.</summary>
    /// <returns>Pairs named <c>k0</c> through <c>k128</c>.</returns>
    private static IEnumerable<KeyValuePair<string, object?>> TooMany()
    {
        for (var i = 0; i < 129; i++)
            yield return new KeyValuePair<string, object?>(
                "k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i);
    }

    /// <summary>Returns one fixed ASCII line. Not a built-in formatter, so the file writer uses the ASCII scratch.</summary>
    private sealed class FixedAsciiFormatter : ILogFormatter
    {
        /// <summary>The line, without a newline. ASCII. Not null.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Returns <see cref="Text"/>.</summary>
        /// <param name="logEvent">Ignored.</param>
        /// <returns>The fixed line. Not null.</returns>
        /// <remarks>Does not throw. Safe to call from the test thread.</remarks>
        public string Format(LogEvent logEvent) => Text;
    }
}
