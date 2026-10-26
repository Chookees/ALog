using System.Globalization;
using System.Text;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>
/// The UTF-8 fast path for both stamp patterns must match <see cref="PlainTextFormatter.Format"/>
/// under the listed cultures, or stay off when the culture cannot use the hand-written stamp.
/// </summary>
[Collection("Culture")]
public sealed class PlainTextFastStampParityTests
{
    private const string UtcMillisPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string LocalSecondsPattern = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Cultures that must allow the fast path on Linux/ICU (Artur's list plus invariant).</summary>
    public static TheoryData<string> FastCultures
    {
        get
        {
            var data = new TheoryData<string>
            {
                "hi-IN",
                "en-IN",
                "ja-JP",
                "zh-CN",
                "en-US",
                "en-GB",
                "de-DE",
                "",
            };
            return data;
        }
    }

    /// <summary>Extra cultures with the expected <see cref="PlainTextFormatter.AllowsFastStamp"/> result.</summary>
    public static TheoryData<string, bool> ConditionalCultures
    {
        get
        {
            var data = new TheoryData<string, bool>
            {
                { "fr-FR", true },
                { "fi-FI", false },
                { "sv-SE", false },
            };
            return data;
        }
    }

    /// <summary>
    /// Under each required fast culture the default pattern takes the fast path and matches Format.
    /// </summary>
    /// <param name="cultureName">Culture name, or empty for invariant.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(FastCultures))]
    public async Task DefaultPattern_TryWriteUtf8_MatchesFormat(string cultureName)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        Assert.True(PlainTextFormatter.AllowsFastStamp(scope.Culture), ProbeReport(scope.Culture));
        AssertParityTaken(new PlainTextFormatter(), SampleEvents());
        AssertParityTaken(new PlainTextFormatter(false), SampleEvents());
        AssertParityTaken(new PlainTextFormatter(LocalSecondsPattern), SampleEvents());
    }

    /// <summary>
    /// Under each required fast culture the UTC-millis pattern takes the fast path and matches Format.
    /// </summary>
    /// <param name="cultureName">Culture name, or empty for invariant.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(FastCultures))]
    public async Task UtcMillisPattern_TryWriteUtf8_MatchesFormat(string cultureName)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        Assert.True(PlainTextFormatter.AllowsFastStamp(scope.Culture), ProbeReport(scope.Culture));
        AssertParityTaken(new PlainTextFormatter(UtcMillisPattern), SampleEvents());
    }

    /// <summary>
    /// fr-FR, fi-FI, and sv-SE: take the fast path only when the expected probe result allows it.
    /// </summary>
    /// <param name="cultureName">Culture under test.</param>
    /// <param name="expectedAllowed">Expected <see cref="PlainTextFormatter.AllowsFastStamp"/> result.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(ConditionalCultures))]
    public async Task DefaultPattern_ConditionalCulture_MatchesFormat(string cultureName, bool expectedAllowed)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        Assert.Equal(expectedAllowed, PlainTextFormatter.AllowsFastStamp(scope.Culture));
        AssertConditional(new PlainTextFormatter(), SampleEvents(), expectedAllowed);
    }

    /// <summary>
    /// fr-FR, fi-FI, and sv-SE for the UTC-millis pattern.
    /// </summary>
    /// <param name="cultureName">Culture under test.</param>
    /// <param name="expectedAllowed">Expected <see cref="PlainTextFormatter.AllowsFastStamp"/> result.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(ConditionalCultures))]
    public async Task UtcMillisPattern_ConditionalCulture_MatchesFormat(string cultureName, bool expectedAllowed)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        Assert.Equal(expectedAllowed, PlainTextFormatter.AllowsFastStamp(scope.Culture));
        AssertConditional(new PlainTextFormatter(UtcMillisPattern), SampleEvents(), expectedAllowed);
    }

    /// <summary>
    /// A ja-JP clone whose <see cref="DateTimeFormatInfo.Calendar"/> is Japanese stays off the fast path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task JapaneseDtfiCalendar_AllowsStampIsFalse()
    {
        await Task.CompletedTask;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
        culture.DateTimeFormat.Calendar = new JapaneseCalendar();
        using var scope = new CultureScope(culture);
        Assert.False(PlainTextFormatter.AllowsFastStamp(culture), ProbeReport(culture));
        AssertParityNotTaken(new PlainTextFormatter(), SampleEvents()[0]);
        AssertParityNotTaken(new PlainTextFormatter(UtcMillisPattern), SampleEvents()[0]);
        AssertFileMatchesFormat(new PlainTextFormatter(), SampleEvents()[0]);
    }

    /// <summary>
    /// A culture whose <see cref="DateTimeFormatInfo.Calendar"/> is Hijri stays off the fast path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HijriDtfiCalendar_AllowsStampIsFalse()
    {
        await Task.CompletedTask;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        // Pin sign and separator so only the non-Gregorian DTFI calendar can reject the fast path.
        culture.NumberFormat.NegativeSign = "-";
        culture.DateTimeFormat.TimeSeparator = ":";
        culture.DateTimeFormat.Calendar = new HijriCalendar();
        using var scope = new CultureScope(culture);
        Assert.False(PlainTextFormatter.AllowsFastStamp(culture), ProbeReport(culture));
        AssertParityNotTaken(new PlainTextFormatter(), SampleEvents()[0]);
        AssertParityNotTaken(new PlainTextFormatter(UtcMillisPattern), SampleEvents()[0]);
        AssertFileMatchesFormat(new PlainTextFormatter(), SampleEvents()[0]);
    }

    /// <summary>
    /// A culture whose <see cref="DateTimeFormatInfo.Calendar"/> is UmAlQura stays off the fast path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UmAlQuraDtfiCalendar_AllowsStampIsFalse()
    {
        await Task.CompletedTask;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        culture.NumberFormat.NegativeSign = "-";
        culture.DateTimeFormat.TimeSeparator = ":";
        culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        using var scope = new CultureScope(culture);
        Assert.False(PlainTextFormatter.AllowsFastStamp(culture), ProbeReport(culture));
        AssertParityNotTaken(new PlainTextFormatter(), SampleEvents()[0]);
        AssertFileMatchesFormat(new PlainTextFormatter(), SampleEvents()[0]);
    }

    /// <summary>
    /// th-TH defaults to ThaiBuddhistCalendar, so the fast stamp stays off and file bytes match Format.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThaiBuddhistCalendar_AllowsStampIsFalse()
    {
        await Task.CompletedTask;
        using var scope = new CultureScope("th-TH");
        Assert.IsType<ThaiBuddhistCalendar>(scope.Culture.DateTimeFormat.Calendar);
        Assert.False(PlainTextFormatter.AllowsFastStamp(scope.Culture), ProbeReport(scope.Culture));
        AssertParityNotTaken(new PlainTextFormatter(), SampleEvents()[0]);
        AssertParityNotTaken(new PlainTextFormatter(UtcMillisPattern), SampleEvents()[0]);
        AssertFileMatchesFormat(new PlainTextFormatter(), SampleEvents()[0]);
    }

    /// <summary>A pattern that is neither default nor UTC-millis never takes the UTF-8 fast path.</summary>
    [Fact(Timeout = 30000)]
    public async Task OtherPattern_TryWriteUtf8_ReturnsFalse()
    {
        await Task.CompletedTask;
        using var scope = new CultureScope("en-US");
        var formatter = new PlainTextFormatter("yyyy/MM/dd HH:mm:ss");
        var buffer = new byte[256];
        Assert.False(formatter.TryWriteUtf8(SampleEvents()[0], buffer, out var written));
        Assert.Equal(0, written);
    }

    /// <summary>A buffer shorter than the 19-byte local stamp makes TryWriteUtf8 return false.</summary>
    [Fact(Timeout = 30000)]
    public async Task DefaultPattern_BufferTooSmallForStamp_ReturnsFalse()
    {
        await Task.CompletedTask;
        using var scope = new CultureScope("en-US");
        var formatter = new PlainTextFormatter();
        Assert.False(formatter.TryWriteUtf8(SampleEvents()[0], new byte[1 + 18], out _));
    }

    /// <summary>Builds events that exercise category, message, props, and edge timestamps.</summary>
    private static LogEvent[] SampleEvents()
    {
        return
        [
            Event(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null, "hello", null),
            Event(new DateTime(2026, 10, 23, 20, 17, 35, DateTimeKind.Utc), "app", "hello", null),
            Event(
                new DateTime(2026, 1, 2, 3, 4, 5, 268, DateTimeKind.Utc),
                "app",
                "count",
                new Dictionary<string, object> { ["n"] = -5, ["label"] = "ok" }),
            Event(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, "midnight", null),
            Event(new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, "year-one", null),
            Event(new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc), "edge", "year-max", null),
            Event(new DateTime(2026, 9, 8, 7, 6, 5, DateTimeKind.Utc), null, "single-digits", null),
        ];
    }

    /// <summary>One-line probe report for diagnostics.</summary>
    private static string ProbeReport(CultureInfo culture)
        => culture.Name
            + " DTFI.Calendar="
            + culture.DateTimeFormat.Calendar.GetType().Name
            + " TimeSeparator='"
            + culture.DateTimeFormat.TimeSeparator
            + "' NegativeSign='"
            + culture.NumberFormat.NegativeSign
            + "'";

    /// <summary>Asserts fast path taken and byte-identical to Format.</summary>
    private static void AssertParityTaken(PlainTextFormatter formatter, IEnumerable<LogEvent> events)
    {
        var buffer = new byte[1024];
        foreach (var evt in events)
        {
            Assert.True(formatter.TryWriteUtf8(evt, buffer, out var written), Describe(evt));
            var expected = Encoding.UTF8.GetBytes(formatter.Format(evt));
            Assert.Equal(expected.Length, written);
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(0, written)), Describe(evt));
        }
    }

    /// <summary>Asserts fast path not taken.</summary>
    private static void AssertParityNotTaken(PlainTextFormatter formatter, LogEvent evt)
    {
        var buffer = new byte[1024];
        Assert.False(formatter.TryWriteUtf8(evt, buffer, out var written));
        Assert.Equal(0, written);
    }

    /// <summary>When allowed, fast path; otherwise TryWriteUtf8 false and Format bytes are the source of truth.</summary>
    private static void AssertConditional(PlainTextFormatter formatter, IEnumerable<LogEvent> events, bool allowed)
    {
        if (allowed)
            AssertParityTaken(formatter, events);
        else
        {
            foreach (var evt in events)
            {
                AssertParityNotTaken(formatter, evt);
                AssertFileMatchesFormat(formatter, evt);
            }
        }
    }

    /// <summary>File bytes for one event equal Format plus the platform newline.</summary>
    private static void AssertFileMatchesFormat(PlainTextFormatter formatter, LogEvent evt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "alog-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "line.log");
            using (var writer = new FileLogWriter(path, formatter, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(evt);
                writer.Flush();
            }

            Assert.Equal(formatter.Format(evt) + Environment.NewLine, File.ReadAllText(path));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>One info event with optional category, message, and properties.</summary>
    private static LogEvent Event(
        DateTime when,
        string? category,
        string message,
        Dictionary<string, object>? properties)
        => new(when, LogLevel.Info, message, null, null, properties, null, category);

    /// <summary>Short label for assertion messages.</summary>
    private static string Describe(LogEvent evt)
        => evt.Timestamp.ToString("o", CultureInfo.InvariantCulture) + "/" + (evt.Message ?? "");

    /// <summary>Sets CurrentCulture and restores it on dispose.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous;

        /// <summary>Activates a named culture, or invariant when the name is empty.</summary>
        /// <param name="cultureName">Culture name, or empty for invariant.</param>
        public CultureScope(string cultureName)
        {
            _previous = CultureInfo.CurrentCulture;
            Culture = cultureName.Length == 0
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = Culture;
        }

        /// <summary>Activates an already-built culture instance.</summary>
        /// <param name="culture">Culture to install. Not null.</param>
        public CultureScope(CultureInfo culture)
        {
            _previous = CultureInfo.CurrentCulture;
            Culture = culture;
            CultureInfo.CurrentCulture = culture;
        }

        /// <summary>The culture installed for this scope.</summary>
        public CultureInfo Culture { get; }

        /// <summary>Restores the previous culture.</summary>
        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
