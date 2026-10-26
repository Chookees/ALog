using System.Globalization;
using System.Text;
using ALog.Core;
using ALog.Formatters;

namespace ALog.Tests;

/// <summary>
/// The UTF-8 fast path for both stamp patterns must match <see cref="PlainTextFormatter.Format"/>
/// under the listed cultures, and <see cref="PlainTextFormatter.TryWriteUtf8"/> must actually succeed.
/// </summary>
[Collection("Culture")]
public sealed class PlainTextFastStampParityTests
{
    private const string UtcMillisPattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string LocalSecondsPattern = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Cultures Artur required, plus the invariant culture.</summary>
    public static TheoryData<string> Cultures
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

    /// <summary>
    /// Under each listed culture the default pattern takes the fast path and matches <see cref="PlainTextFormatter.Format"/>.
    /// </summary>
    /// <param name="cultureName">Culture name, or empty for <see cref="CultureInfo.InvariantCulture"/>.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(Cultures))]
    public async Task DefaultPattern_TryWriteUtf8_MatchesFormat(string cultureName)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        AssertCultureAllowsFastStamp(scope.Culture);
        AssertParity(new PlainTextFormatter(), SampleEvents());
        AssertParity(new PlainTextFormatter(false), SampleEvents());
        AssertParity(new PlainTextFormatter(LocalSecondsPattern), SampleEvents());
    }

    /// <summary>
    /// Under each listed culture the UTC-millis pattern takes the fast path and matches <see cref="PlainTextFormatter.Format"/>.
    /// </summary>
    /// <param name="cultureName">Culture name, or empty for <see cref="CultureInfo.InvariantCulture"/>.</param>
    [Theory(Timeout = 60000)]
    [MemberData(nameof(Cultures))]
    public async Task UtcMillisPattern_TryWriteUtf8_MatchesFormat(string cultureName)
    {
        await Task.CompletedTask;
        using var scope = new CultureScope(cultureName);
        AssertCultureAllowsFastStamp(scope.Culture);
        var formatter = new PlainTextFormatter(UtcMillisPattern);
        AssertParity(formatter, SampleEvents());
    }

    /// <summary>
    /// Builds events that exercise category, message, int (including negative), string props, and edge timestamps.
    /// </summary>
    /// <returns>A fixed list of events. Not null.</returns>
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

    /// <summary>
    /// Asserts every event's fast path returns true and equals UTF-8 of <see cref="PlainTextFormatter.Format"/>.
    /// </summary>
    /// <param name="formatter">Formatter under the current culture. Not null.</param>
    /// <param name="events">Events to compare. Not null.</param>
    private static void AssertParity(PlainTextFormatter formatter, IEnumerable<LogEvent> events)
    {
        var buffer = new byte[1024];
        foreach (var evt in events)
        {
            Assert.True(
                formatter.TryWriteUtf8(evt, buffer, out var written),
                "TryWriteUtf8 must take the fast path for " + Describe(evt));
            var expected = Encoding.UTF8.GetBytes(formatter.Format(evt));
            Assert.Equal(expected.Length, written);
            Assert.True(
                expected.AsSpan().SequenceEqual(buffer.AsSpan(0, written)),
                "fast bytes must equal Encoding.UTF8.GetBytes(Format(e)) for " + Describe(evt));
        }
    }

    /// <summary>
    /// Fails with the calendar, time separator, and negative sign when the culture cannot use the hand-written stamp.
    /// </summary>
    /// <param name="culture">Current culture. Not null.</param>
    private static void AssertCultureAllowsFastStamp(CultureInfo culture)
    {
        var calendarOk = culture.Calendar is GregorianCalendar;
        var separatorOk = culture.DateTimeFormat.TimeSeparator == ":";
        var signOk = culture.NumberFormat.NegativeSign == "-";
        if (calendarOk && separatorOk && signOk)
            return;

        Assert.Fail(
            "AllowsFastStamp failed for "
            + culture.Name
            + ": calendar="
            + culture.Calendar.GetType().Name
            + ", TimeSeparator='"
            + culture.DateTimeFormat.TimeSeparator
            + "', NegativeSign='"
            + culture.NumberFormat.NegativeSign
            + "'");
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

    /// <summary>Sets <see cref="CultureInfo.CurrentCulture"/> and restores it on dispose.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous;

        /// <summary>Activates <paramref name="cultureName"/>, or the invariant culture when the name is empty.</summary>
        /// <param name="cultureName">Culture name, or empty for invariant.</param>
        public CultureScope(string cultureName)
        {
            _previous = CultureInfo.CurrentCulture;
            Culture = cultureName.Length == 0
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = Culture;
        }

        /// <summary>The culture that was installed for this scope.</summary>
        public CultureInfo Culture { get; }

        /// <summary>Restores the previous <see cref="CultureInfo.CurrentCulture"/>.</summary>
        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
