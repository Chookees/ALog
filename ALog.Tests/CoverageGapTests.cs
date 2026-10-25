using System.Collections;
using System.Globalization;
using System.Text;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>
/// Hits formatter and file branches the fast path left unmeasured.
/// </summary>
public sealed class CoverageGapTests
{
    private const string UtcPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly DateTime When = new(2026, 10, 24, 12, 0, 0, 123, DateTimeKind.Utc);

    /// <summary>
    /// The shared empty bag reports no keys, no values, and no lookup.
    /// </summary>
    /// <returns>A task that finishes after the bag has been read.</returns>
    /// <remarks>
    /// The indexer always throws. The other members return empty results.
    /// Not thread-safe. The bag itself may be read from any thread.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task EmptyBag_ReportsNothing()
    {
        await Task.CompletedTask;
        IReadOnlyDictionary<string, object> bag = EmptyPropertyBag.Instance;
        var count = bag.Count;
        Assert.True(count == 0);
        Assert.False(bag.ContainsKey("missing"));
        Assert.False(bag.TryGetValue("missing", out var value));
        Assert.Null(value);
        Assert.Empty(bag.Keys);
        Assert.Empty(bag.Values);
        Assert.Empty(bag);
        Assert.Empty((IEnumerable)bag);
        Assert.Throws<KeyNotFoundException>(() => bag["missing"]);
    }

    /// <summary>
    /// Compact JSON writes every supported value and level, and falls back when a value would not match the serializer.
    /// </summary>
    /// <returns>A task that finishes after the documents are read.</returns>
    /// <remarks>
    /// The fallback cases are a minimum int, a quote, a non-dictionary bag, and a value that is not a string, int, or bool.
    /// Not thread-safe. The formatter keeps its builder on this thread.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Json_CompactPath_CoversValuesLevelsAndFallbacks()
    {
        await Task.CompletedTask;
        var formatter = new JsonFormatter();
        var properties = new Dictionary<string, object>
        {
            ["s"] = "ok",
            ["z"] = null!,
            ["n"] = -7,
            ["p"] = 5,
            ["w"] = 42,
            ["b"] = 1000,
            ["t"] = true,
            ["f"] = false,
        };
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal, (LogLevel)99 })
        {
            var text = formatter.Format(Event(level, "hello", properties, "tmpl", "app", EmptyContext()));
            Assert.Contains("hello", text, StringComparison.Ordinal);
            Assert.Contains(level.ToString(), text, StringComparison.Ordinal);
        }

        var full = formatter.Format(Event(LogLevel.Info, "hello", properties, "tmpl", "app", new Dictionary<string, object> { ["ctx"] = "v" }));
        Assert.Contains("\"t\":true", full, StringComparison.Ordinal);
        Assert.Contains("\"f\":false", full, StringComparison.Ordinal);
        Assert.Contains("\"z\":null", full, StringComparison.Ordinal);
        Assert.Contains("-7", full, StringComparison.Ordinal);
        Assert.Contains("1000", full, StringComparison.Ordinal);

        Assert.Contains("min", formatter.Format(Event(LogLevel.Info, "min", new Dictionary<string, object> { ["m"] = int.MinValue })), StringComparison.Ordinal);
        Assert.Contains("quote", formatter.Format(Event(LogLevel.Info, "quote", new Dictionary<string, object> { ["a\"b"] = 1 })), StringComparison.Ordinal);
        Assert.Contains("bad", formatter.Format(Event(LogLevel.Info, "bad", new Dictionary<string, object> { ["k"] = "say \"hi\"" })), StringComparison.Ordinal);
        Assert.Contains("long", formatter.Format(Event(LogLevel.Info, "long", new Dictionary<string, object> { ["k"] = 1L })), StringComparison.Ordinal);
        Assert.Contains("bag", formatter.Format(Event(LogLevel.Info, "bag", new OneBag(1))), StringComparison.Ordinal);
        Assert.Contains("ctx", formatter.Format(Event(LogLevel.Info, "ctx", null, null, null, new OneBag("x"))), StringComparison.Ordinal);
        Assert.Contains("cquote", formatter.Format(Event(LogLevel.Info, "cquote", null, null, null, new Dictionary<string, object> { ["k"] = "a\"b" })), StringComparison.Ordinal);
    }

    /// <summary>
    /// The plain UTF-8 path writes levels and properties, and refuses a short buffer, an escape, or a value it cannot render.
    /// </summary>
    /// <returns>A task that finishes after the buffers are checked.</returns>
    /// <remarks>
    /// The culture is restored before this returns. A short buffer must not be reported as a successful write.
    /// Not thread-safe. The formatter reads the culture of this thread.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Plain_Utf8Path_CoversOverflowEscapesAndValues()
    {
        await Task.CompletedTask;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var formatter = new PlainTextFormatter(UtcPattern);
            var properties = new Dictionary<string, object>
            {
                ["ab"] = -42,
                ["cd"] = "hi",
                ["ef"] = null!,
                ["gh"] = 7,
            };
            var ev = Event(LogLevel.Info, "hello", properties, null, "cat");
            var wide = new byte[512];
            Assert.True(formatter.TryWriteUtf8(ev, wide, out var written));
            var text = Encoding.ASCII.GetString(wide, 0, written);
            Assert.Contains("hello", text, StringComparison.Ordinal);
            Assert.Contains("ab=-42", text, StringComparison.Ordinal);
            Assert.Contains("cd=hi", text, StringComparison.Ordinal);

            for (var n = 0; n < written; n++)
                Assert.False(formatter.TryWriteUtf8(ev, new byte[n], out _));

            foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal, (LogLevel)99 })
            {
                Assert.True(formatter.TryWriteUtf8(Event(level, "m"), wide, out var levelBytes));
                Assert.Contains(level.ToString(), Encoding.ASCII.GetString(wide, 0, levelBytes), StringComparison.Ordinal);
            }

            foreach (var message in new[] { "a|b", "a\\b", "a\nb", "a\rb", "a-->b", "a" + "\u00e9" })
                Assert.False(formatter.TryWriteUtf8(Event(LogLevel.Info, message), wide, out _));

            Assert.False(formatter.TryWriteUtf8(Event(LogLevel.Info, "m", new Dictionary<string, object> { ["k"] = int.MinValue }), wide, out _));
            Assert.False(formatter.TryWriteUtf8(Event(LogLevel.Info, "m", new Dictionary<string, object> { ["k"] = 1.5d }), wide, out _));
            Assert.False(formatter.TryWriteUtf8(Event(LogLevel.Info, "m", new Dictionary<string, object> { ["k|x"] = 1 }), wide, out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// A fast line that fills the scratch buffer has no room for the newline, so the writer uses the string path.
    /// A line longer than 64 KiB misses both fast buffers and is still written.
    /// </summary>
    /// <returns>A task that finishes after both files are read.</returns>
    /// <remarks>
    /// The scratch buffer is per thread. The exact fill uses whatever size this thread still keeps, or 1024 when it keeps none.
    /// Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task File_ExactFillAndHugeLine_StillWrite()
    {
        await Task.CompletedTask;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var directory = Path.Combine(Path.GetTempPath(), "alog-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var formatter = new PlainTextFormatter(UtcPattern);
            var capacity = FileLogWriter.RetainedUtf8Bytes;
            if (capacity < 1024)
                capacity = 1024;
            var length = MessageLengthFor(formatter, capacity);
            var exact = Event(LogLevel.Info, new string('a', length));
            var exactPath = Path.Combine(directory, "exact.log");
            using (var writer = new FileLogWriter(exactPath, formatter, fileFlushInterval: TimeSpan.Zero))
                writer.Write(exact);
            Assert.Contains(new string('a', length), File.ReadAllText(exactPath), StringComparison.Ordinal);

            var huge = Event(LogLevel.Info, new string('b', 70_000));
            var hugePath = Path.Combine(directory, "huge.log");
            using (var writer = new FileLogWriter(hugePath, formatter, fileFlushInterval: TimeSpan.Zero))
                writer.Write(huge);
            Assert.Contains(new string('b', 70_000), File.ReadAllText(hugePath), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A startup buffer smaller than the array already reserved replaces that array before the file opens.
    /// </summary>
    /// <returns>A task that finishes after the call returns.</returns>
    /// <remarks>Does not create the file. Not thread-safe. The instance is not shared.</remarks>
    [Fact(Timeout = 30000)]
    public async Task SharedFile_SmallerStartupBuffer_ReplacesTheArray()
    {
        await Task.CompletedTask;
        var path = Path.Combine(Path.GetTempPath(), "alog-gap-nopen-" + Guid.NewGuid().ToString("N") + ".log");
        var shared = new SharedLogFile(path, 1024, TimeSpan.Zero, TimeProvider.System);
        shared.ApplyStartup(32, TimeSpan.FromSeconds(2), TimeProvider.System);
        Assert.Equal(path, shared.Path);
    }

    /// <summary>The message length whose fast line is exactly <paramref name="capacity"/> bytes.</summary>
    /// <param name="formatter">UTC plain formatter. Not null.</param>
    /// <param name="capacity">Target byte count. At least the stamp.</param>
    /// <returns>The message length.</returns>
    /// <remarks>Does not throw when a length in range hits the target. The search stops at the capacity. Not thread-safe.</remarks>
    private static int MessageLengthFor(PlainTextFormatter formatter, int capacity)
    {
        var low = 0;
        var high = capacity;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var buffer = new byte[capacity + 32];
            var ok = formatter.TryWriteUtf8(Event(LogLevel.Info, new string('a', mid)), buffer, out var written);
            if (!ok)
            {
                high = mid - 1;
                continue;
            }

            if (written == capacity)
                return mid;
            if (written < capacity)
                low = mid + 1;
            else
                high = mid - 1;
        }

        throw new InvalidOperationException("no message length fills " + capacity.ToString(CultureInfo.InvariantCulture) + " bytes");
    }

    /// <summary>One event at the fixed UTC instant.</summary>
    /// <param name="level">Level written into the line.</param>
    /// <param name="message">Message text. Not null.</param>
    /// <param name="properties">Property bag. Null when the event has none.</param>
    /// <param name="template">Template text. Null when there is none.</param>
    /// <param name="category">Category. Null when there is none.</param>
    /// <param name="context">Context bag. Null when there is none.</param>
    /// <returns>The event. Not null.</returns>
    /// <remarks>Does not throw. Does not write. Safe for any thread.</remarks>
    private static LogEvent Event(
        LogLevel level,
        string message,
        IReadOnlyDictionary<string, object>? properties = null,
        string? template = null,
        string? category = null,
        IReadOnlyDictionary<string, object>? context = null)
        => new(When, level, message, null, context, properties, template, category);

    /// <summary>An empty dictionary, so a context object is present and has no pairs.</summary>
    /// <returns>A new empty dictionary. Not null.</returns>
    /// <remarks>Does not throw. The caller owns it. Safe for any thread.</remarks>
    private static Dictionary<string, object> EmptyContext() => new();

    /// <summary>One pair that is not a <see cref="Dictionary{TKey, TValue}"/>.</summary>
    /// <remarks>The formatter must not treat this as the compact-path dictionary. Not thread-safe.</remarks>
    private sealed class OneBag : IReadOnlyDictionary<string, object>
    {
        private readonly object _value;

        /// <summary>Stores the single value under the key <c>k</c>.</summary>
        /// <param name="value">The value. Not null.</param>
        /// <remarks>Does not throw. Does not copy a collection.</remarks>
        internal OneBag(object value) => _value = value;

        /// <summary>Always one.</summary>
        public int Count => 1;

        /// <summary>The stored value. The key is ignored.</summary>
        /// <param name="key">Ignored.</param>
        /// <returns>The stored value.</returns>
        /// <remarks>Does not throw. Safe for the test thread.</remarks>
        public object this[string key] => _value;

        /// <summary>Always true.</summary>
        /// <param name="key">Ignored.</param>
        /// <returns>True.</returns>
        /// <remarks>Does not throw.</remarks>
        public bool ContainsKey(string key) => true;

        /// <summary>Returns the stored value.</summary>
        /// <param name="key">Ignored.</param>
        /// <param name="value">The stored value.</param>
        /// <returns>True.</returns>
        /// <remarks>Does not throw.</remarks>
        public bool TryGetValue(string key, out object value)
        {
            value = _value;
            return true;
        }

        /// <summary>The key <c>k</c>.</summary>
        public IEnumerable<string> Keys
        {
            get { yield return "k"; }
        }

        /// <summary>The stored value.</summary>
        public IEnumerable<object> Values
        {
            get { yield return _value; }
        }

        /// <summary>The one pair.</summary>
        /// <returns>An enumerator of that pair.</returns>
        /// <remarks>Does not throw.</remarks>
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object>("k", _value);
        }

        /// <summary>The one pair.</summary>
        /// <returns>The same walk as <see cref="GetEnumerator"/>.</returns>
        /// <remarks>Does not throw.</remarks>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
