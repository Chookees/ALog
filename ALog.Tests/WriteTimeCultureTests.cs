using System.Globalization;
using ALog.Core;
using ALog.Formatters;
using ALog.Public.Interfaces;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>Culture tests share the process culture and must not run beside each other.</summary>
[CollectionDefinition("Culture", DisableParallelization = true)]
public sealed class CultureCollectionDefinition
{
}

/// <summary>
/// File bytes for the UTC millisecond pattern must match <see cref="PlainTextFormatter.Format"/>
/// under the culture of the thread that writes, including a culture set after the formatter was built.
/// </summary>
[Collection("Culture")]
public sealed class WriteTimeCultureTests
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly string[] Cultures = { "en-US", "de-DE", "fr-FR", "fi-FI", "sv-SE", "da-DK", "th-TH" };

    /// <summary>
    /// Each listed culture writes the same bytes with a direct file writer and through the background queue.
    /// </summary>
    /// <returns>A task that finishes after every culture has been compared.</returns>
    /// <remarks>
    /// The queue is constructed while that culture is current, so its processor thread inherits it.
    /// The flush interval is one hour. Dispose drains the event without waiting for that interval.
    /// The culture is restored when the scope ends, including when an assert fails.
    /// Not safe to run in parallel with another test that changes <see cref="CultureInfo.CurrentCulture"/>.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task EachCulture_DirectAndQueued_MatchFormat()
    {
        await Task.CompletedTask;
        var when = new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc);
        var evt = new LogEvent(when, LogLevel.Info, "hello", null, null, null, null, "app");
        foreach (var name in Cultures)
        {
            using var scope = new CultureScope(name);
            var formatter = new PlainTextFormatter(Pattern);
            var expected = formatter.Format(evt) + Environment.NewLine;
            Assert.Equal(expected, WriteDirect(formatter, evt));
            Assert.Equal(expected, WriteQueued(new PlainTextFormatter(Pattern), evt));
        }
    }

    /// <summary>
    /// A formatter built under en-US still follows fi-FI when the write happens under fi-FI.
    /// </summary>
    /// <returns>A task that finishes after the file is compared with <see cref="PlainTextFormatter.Format"/>.</returns>
    /// <remarks>
    /// fi-FI uses a dot as the time separator, so a probe cached only at construction would write colons.
    /// The culture is restored afterwards. Not parallel-safe with other culture tests.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task CultureChangedAfterConstruction_UsesTheWriteTimeCulture()
    {
        await Task.CompletedTask;
        PlainTextFormatter formatter;
        using (new CultureScope("en-US"))
            formatter = new PlainTextFormatter(Pattern);

        var evt = new LogEvent(
            new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc),
            LogLevel.Info,
            "later",
            null,
            null,
            null,
            null,
            "app");
        using (new CultureScope("fi-FI"))
        {
            var text = WriteDirect(formatter, evt);
            Assert.Equal(formatter.Format(evt) + Environment.NewLine, text);
            Assert.Contains("T20.17.35.268Z", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Two lines in one file follow the culture that was current for each write.
    /// </summary>
    /// <returns>A task that finishes after both lines are compared.</returns>
    /// <remarks>
    /// en-US keeps colons. fi-FI uses dots. The writer stays open across the culture change.
    /// The culture is restored afterwards. Not parallel-safe with other culture tests.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task MixedFile_UsesEachWriteTimeCulture()
    {
        await Task.CompletedTask;
        var formatter = new PlainTextFormatter(Pattern);
        var first = new LogEvent(new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc), LogLevel.Info, "one", null, null);
        var second = new LogEvent(new DateTime(2026, 10, 23, 21, 0, 1, 2, DateTimeKind.Utc), LogLevel.Info, "two", null, null);
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "mixed.log");
            using var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero);
            string expected;
            using (new CultureScope("en-US"))
            {
                writer.Write(first);
                expected = formatter.Format(first) + Environment.NewLine;
            }

            using (new CultureScope("fi-FI"))
            {
                writer.Write(second);
                expected += formatter.Format(second) + Environment.NewLine;
            }

            writer.Flush();
            var text = File.ReadAllText(path);
            Assert.Equal(expected, text);
            Assert.Contains("T20:17:35.268Z", text, StringComparison.Ordinal);
            Assert.Contains("T21.00.01.002Z", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A negative int uses the culture's negative sign, which is <c>-</c> for en-US and U+2212 for fi-FI and sv-SE.
    /// </summary>
    /// <returns>A task that finishes after the three cultures are compared.</returns>
    /// <remarks>
    /// The file is compared with <see cref="PlainTextFormatter.Format"/>, then the sign character is checked.
    /// A fast path that always writes ASCII <c>-</c> fails fi-FI and sv-SE. The culture is restored afterwards.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task NegativeInt_MatchesFormat()
    {
        await Task.CompletedTask;
        var evt = new LogEvent(
            new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc),
            LogLevel.Info,
            "count",
            null,
            null,
            new Dictionary<string, object> { ["n"] = -5 });
        using (new CultureScope("en-US"))
        {
            var formatter = new PlainTextFormatter(Pattern);
            var text = WriteDirect(formatter, evt);
            Assert.Equal(formatter.Format(evt) + Environment.NewLine, text);
            Assert.Contains("n=-5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2212", text, StringComparison.Ordinal);
        }

        foreach (var name in new[] { "fi-FI", "sv-SE" })
        {
            using var scope = new CultureScope(name);
            var formatter = new PlainTextFormatter(Pattern);
            var text = WriteDirect(formatter, evt);
            Assert.Equal(formatter.Format(evt) + Environment.NewLine, text);
            Assert.Contains("n=\u22125", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The queue's processor, started under de-DE, writes the same bytes as a direct write under de-DE.
    /// fi-FI is included because its time separator differs, so a processor left on the default culture cannot pass.
    /// </summary>
    /// <returns>A task that finishes after both cultures are compared.</returns>
    /// <remarks>
    /// The culture is set before the queue is constructed and restored afterwards.
    /// Not parallel-safe with other culture tests.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ConsumerCulture_MatchesDirectWrite()
    {
        await Task.CompletedTask;
        var evt = new LogEvent(
            new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc),
            LogLevel.Info,
            "queued",
            null,
            null,
            null,
            null,
            "app");
        foreach (var name in new[] { "de-DE", "fi-FI" })
        {
            string direct;
            string queued;
            using (new CultureScope(name))
                direct = WriteDirect(new PlainTextFormatter(Pattern), evt);
            using (new CultureScope(name))
                queued = WriteQueued(new PlainTextFormatter(Pattern), evt);
            Assert.Equal(direct, queued);
        }
    }

    /// <summary>
    /// A subclass that replaces <see cref="ILogFormatter.Format"/> is written with that text, not the base fast path.
    /// </summary>
    /// <returns>A task that finishes after the file is read.</returns>
    /// <remarks>
    /// <c>is PlainTextFormatter</c> is true for the subclass. The file writer requires the exact type so this text is kept.
    /// Does not change the process culture. Not parallel-safe only because it lives in the culture collection.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SubclassFormat_IsWhatTheFileContains()
    {
        await Task.CompletedTask;
        var formatter = new LoudFormatter();
        var evt = new LogEvent(new DateTime(2026, 10, 23, 20, 17, 35, 268, DateTimeKind.Utc), LogLevel.Info, "hello", null, null);
        var text = WriteDirect(formatter, evt);
        Assert.Equal("SUBCLASS hello" + Environment.NewLine, text);
        Assert.DoesNotContain("2026-10-23", text, StringComparison.Ordinal);
    }

    /// <summary>Writes one event on this thread and returns the file text.</summary>
    /// <param name="formatter">Formatter attached to the writer. Not null.</param>
    /// <param name="evt">Event to write. Not null.</param>
    /// <returns>The file contents after dispose.</returns>
    /// <remarks>
    /// Does not throw for a normal path. The flush interval is zero, so the line is in the file before dispose.
    /// Deletes its directory afterwards. Not thread-safe.
    /// </remarks>
    private static string WriteDirect(ILogFormatter formatter, LogEvent evt)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "direct.log");
            using (var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(evt);
                writer.Flush();
            }

            return File.ReadAllText(path);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Queues one event. The processor is started under the current culture.</summary>
    /// <param name="formatter">Formatter attached to the writer. Not null.</param>
    /// <param name="evt">Event to write. Not null.</param>
    /// <returns>The file contents after the queue disposes the writer.</returns>
    /// <remarks>
    /// Does not throw for a normal path. The idle interval is one hour. Dispose wakes the processor, so the clock does not move.
    /// The queue is constructed here, after the caller has set the culture. Not thread-safe.
    /// </remarks>
    private static string WriteQueued(ILogFormatter formatter, LogEvent evt)
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "queued.log");
            var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero);
            var queue = new BackgroundLogQueue(
                new ILogWriter[] { writer },
                capacity: 8,
                batchSize: 1,
                flushInterval: TimeSpan.FromHours(1),
                asyncEnabled: false);
            Assert.True(queue.TryEnqueue(evt));
            queue.Dispose();
            return File.ReadAllText(path);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Creates an empty directory under the temp path.</summary>
    /// <returns>The directory path. Not null.</returns>
    /// <remarks>Does not throw. The caller deletes it.</remarks>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-culture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a directory left by a test. A locked file is ignored.</summary>
    /// <param name="path">Directory to delete. Not null.</param>
    /// <remarks>Does not throw. A missing directory returns.</remarks>
    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Restores the current culture and UI culture when disposed.</summary>
    /// <remarks>Not thread-safe. Only the test thread constructs it. Dispose is idempotent.</remarks>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _ui;
        private int _disposed;

        /// <summary>Sets both current cultures to <paramref name="name"/>.</summary>
        /// <param name="name">Culture name, for example <c>fi-FI</c>. Not null.</param>
        /// <remarks>Does not throw when the culture is installed. The previous cultures are restored by <see cref="Dispose"/>.</remarks>
        internal CultureScope(string name)
        {
            _culture = CultureInfo.CurrentCulture;
            _ui = CultureInfo.CurrentUICulture;
            var next = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = next;
            CultureInfo.CurrentUICulture = next;
        }

        /// <summary>Puts the cultures back. A second call does nothing.</summary>
        /// <remarks>Does not throw. Safe to call from a finally block.</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _ui;
        }
    }

    /// <summary>Replaces the plain-text line so the file proves the subclass was used.</summary>
    /// <remarks>The base constructor still probes the culture. The file writer must not take the base fast path.</remarks>
    private sealed class LoudFormatter : PlainTextFormatter, ILogFormatter
    {
        /// <summary>Builds the UTC millisecond formatter the fast path knows.</summary>
        /// <remarks>Does not throw. Does not change the culture.</remarks>
        internal LoudFormatter()
            : base(Pattern)
        {
        }

        /// <summary>The line the file must contain.</summary>
        /// <param name="logEvent">Event being written. Not null.</param>
        /// <returns>A line that the base formatter would not produce.</returns>
        /// <remarks>Does not throw. Hides the base interface method for this instance.</remarks>
        string ILogFormatter.Format(LogEvent logEvent) => "SUBCLASS " + logEvent.Message;
    }
}
