// C3b: the MEL bridge must not throw into the application (arc_spec 7c2, uiux 4a3).
// These tests use a real LoggerFactory. Next: ALogLoggerProvider.cs.
using System.Collections;
using System.Globalization;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// The MEL bridge writes the event and reports ALOG0022 when formatting fails,
/// strips one <c>@</c> or <c>$</c> from a property name, and keeps properties
/// when <c>{OriginalFormat}</c> is missing (C3b-01, C3b-02, C3b-03).
/// </summary>
public sealed class MelNoThrowTests : IDisposable
{
    private const string Fragment = "could not be formatted";

    private const string SentenceTail =
        ". ALog wrote the event with its message template only. Check the state object passed to ILogger.Log.";

    /// <summary>
    /// Clears process-wide diagnostic state so one test's reported pairs do not hide another's.
    /// </summary>
    public MelNoThrowTests()
    {
        MelFormatGuard.Reset();
    }

    /// <summary>
    /// Clears the reported pairs and restores stderr as the diagnostic sink.
    /// </summary>
    public void Dispose()
    {
        MelFormatGuard.Reset();
        SelfLog.Target = null;
    }

    /// <summary>
    /// Tester's throwing indexer (practice repro <c>ThrowingList</c>) does not escape
    /// <see cref="ILogger.Log{TState}"/>, including as <see cref="AggregateException"/>.
    /// The event text is empty because no template could be read. ALOG0022 names
    /// <c>InvalidOperationException</c> and <c>indexer</c>. The other provider still
    /// sees the original state and the formatter text <c>throwing list</c>.
    /// </summary>
    /// <remarks>
    /// No I/O. One factory, one call. Safe beside the other tests; the suite is not parallel.
    /// </remarks>
    [Fact]
    public void ThrowingIndexer_Repro_DoesNotThrow_WritesEmptyText_AndReportsAlog0022()
    {
        var state = new ThrowingIndexer();
        var lines = new List<string>();
        using var harness = new Harness("QA.ThrowIndex", lines);

        harness.Log(state, null, (_, _) => "throwing list");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal(string.Empty, evt.Message);
        Assert.Equal(string.Empty, evt.MessageTemplate);
        Assert.Empty(evt.Properties!);
        var report = Assert.Single(Reports(lines));
        Assert.Equal(Expected("QA.ThrowIndex", "InvalidOperationException", "indexer"), report);
        Assert.Contains(Fragment, report, StringComparison.Ordinal);
        var call = Assert.Single(harness.Witness.Calls);
        Assert.Same(state, call.State);
        Assert.Equal("throwing list", call.Text);
        Assert.False(call.FormatterWasNull);
        Assert.Null(call.FormatError);
    }

    /// <summary>
    /// A state whose <c>Count</c> throws still produces one empty event and one ALOG0022 line.
    /// Nothing was read, so there are no properties and no template.
    /// </summary>
    /// <remarks>
    /// No I/O. The second provider formats without touching <c>Count</c>.
    /// </remarks>
    [Fact]
    public void ThrowingCount_DoesNotThrow_WritesEmptyText_AndReportsAlog0022()
    {
        var state = new ThrowingCount();
        var lines = new List<string>();
        using var harness = new Harness("QA.ThrowCount", lines);

        harness.Log(state, null, (_, _) => "count-ok");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal(string.Empty, evt.Message);
        Assert.Equal(string.Empty, evt.MessageTemplate);
        Assert.Empty(evt.Properties!);
        Assert.Equal(Expected("QA.ThrowCount", "InvalidOperationException", "count"), Assert.Single(Reports(lines)));
        var call = Assert.Single(harness.Witness.Calls);
        Assert.Same(state, call.State);
        Assert.Equal("count-ok", call.Text);
    }

    /// <summary>
    /// A non-list sequence whose enumerator throws still produces one empty event and one ALOG0022 line.
    /// </summary>
    /// <remarks>
    /// No I/O. The type is not an <see cref="IReadOnlyList{T}"/>, so the bridge has to enumerate it.
    /// </remarks>
    [Fact]
    public void ThrowingEnumerator_DoesNotThrow_WritesEmptyText_AndReportsAlog0022()
    {
        var state = new ThrowingEnumerable();
        var lines = new List<string>();
        using var harness = new Harness("QA.ThrowEnum", lines);

        harness.Log(state, null, (_, _) => "enum-ok");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal(string.Empty, evt.Message);
        Assert.Equal(string.Empty, evt.MessageTemplate);
        Assert.Empty(evt.Properties!);
        Assert.Equal(Expected("QA.ThrowEnum", "InvalidOperationException", "enum"), Assert.Single(Reports(lines)));
        Assert.Same(state, Assert.Single(harness.Witness.Calls).State);
    }

    /// <summary>
    /// A throwing formatter does not escape. The event text is the template when the state
    /// could be read, and empty when the state is not a property list. The caller's exception
    /// stays on the event. The other provider receives the same state and the same formatter failure.
    /// </summary>
    /// <remarks>
    /// No I/O. Two calls, two categories, so each failure is its own ALOG0022 pair.
    /// </remarks>
    [Fact]
    public void ThrowingFormatter_DoesNotThrow_WritesTemplateOrEmpty_AndReportsAlog0022()
    {
        var lines = new List<string>();
        var app = new InvalidOperationException("app");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Id", 7),
            new("{OriginalFormat}", "ship {Id}")
        };
        using (var harness = new Harness("QA.ThrowFmt", lines))
        {
            harness.Log(state, app, (_, _) => throw new FormatException("boom"));

            AssertNoCallerError(harness);
            var evt = Assert.Single(harness.Writer.Events);
            Assert.Equal("ship {Id}", evt.Message);
            Assert.Equal("ship {Id}", evt.MessageTemplate);
            Assert.Equal(7, evt.Properties!["Id"]);
            Assert.Same(app, evt.Exception);
            var call = Assert.Single(harness.Witness.Calls);
            Assert.Same(state, call.State);
            Assert.IsType<FormatException>(call.FormatError);
            Assert.Equal("boom", call.FormatError!.Message);
        }

        using (var plain = new Harness("QA.ThrowFmtPlain", lines))
        {
            const string text = "plain string state";
            plain.Log(text, null, (_, _) => throw new FormatException("plain"));

            AssertNoCallerError(plain);
            var evt = Assert.Single(plain.Writer.Events);
            Assert.Equal(string.Empty, evt.Message);
            Assert.Equal(string.Empty, evt.MessageTemplate);
            Assert.Empty(evt.Properties!);
            Assert.Same(text, Assert.Single(plain.Witness.Calls).State);
        }

        var reports = Reports(lines);
        Assert.Equal(2, reports.Count);
        Assert.Equal(Expected("QA.ThrowFmt", "FormatException", "boom"), reports[0]);
        Assert.Equal(Expected("QA.ThrowFmtPlain", "FormatException", "plain"), reports[1]);
    }

    /// <summary>
    /// A state that is not a pair list keeps the formatter text as both the message and the template.
    /// A formatter that returns null writes an empty message and an empty template. Neither case reports ALOG0022.
    /// </summary>
    /// <remarks>
    /// No I/O. Two factories. The second provider sees the original string state.
    /// </remarks>
    [Fact]
    public void PlainState_SuccessfulFormatter_WritesTheFormatterText()
    {
        var lines = new List<string>();
        using (var harness = new Harness("QA.Plain", lines))
        {
            const string state = "state";
            harness.Log(state, null, (_, _) => "hello");

            var evt = Only(harness, "hello", "hello");
            Assert.Empty(evt.Properties!);
            var call = Assert.Single(harness.Witness.Calls);
            Assert.Same(state, call.State);
            Assert.Equal("hello", call.Text);
            Assert.False(call.FormatterWasNull);
        }

        using (var harness = new Harness("QA.PlainNull", lines))
        {
            harness.Log("state", null, (_, _) => null!);
            var evt = Only(harness, string.Empty, string.Empty);
            Assert.Empty(evt.Properties!);
        }

        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// When the formatter throws and the state indexer throws afterwards, ALOG0022 names the
    /// formatter exception. The event is still written, with empty text, because no template was read.
    /// </summary>
    /// <remarks>
    /// No I/O. One call. The other provider sees the same throwing list and the same formatter failure.
    /// </remarks>
    [Fact]
    public void FormatterThrow_IsReportedInsteadOfALaterStateThrow()
    {
        var lines = new List<string>();
        var state = new ThrowingIndexer();
        using var harness = new Harness("QA.Both", lines);

        harness.Log(state, null, (_, _) => throw new FormatException("fmt"));

        var evt = Only(harness, string.Empty, string.Empty);
        Assert.Empty(evt.Properties!);
        Assert.Equal(Expected("QA.Both", "FormatException", "fmt"), Assert.Single(Reports(lines)));
        var call = Assert.Single(harness.Witness.Calls);
        Assert.Same(state, call.State);
        Assert.IsType<FormatException>(call.FormatError);
        Assert.Equal("fmt", call.FormatError!.Message);
    }

    /// <summary>
    /// An indexer that throws after the template and one property keeps that property and
    /// uses the template as the event text, not the formatter text.
    /// </summary>
    /// <remarks>
    /// No I/O. The formatter itself succeeds, so the failure is the state access.
    /// </remarks>
    [Fact]
    public void IndexerThrow_AfterATemplate_KeepsEarlierProperties()
    {
        var state = new ThrowAtIndex(
            2,
            new("{OriginalFormat}", "kept {A}"),
            new("A", "alpha"),
            new("B", "beta"));
        var lines = new List<string>();
        using var harness = new Harness("QA.Partial", lines);

        harness.Log(state, null, (_, _) => "formatted-by-mel");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal("kept {A}", evt.Message);
        Assert.Equal("kept {A}", evt.MessageTemplate);
        Assert.Equal("alpha", evt.Properties!["A"]);
        Assert.False(evt.Properties.ContainsKey("B"));
        Assert.Equal(Expected("QA.Partial", "InvalidOperationException", "indexer"), Assert.Single(Reports(lines)));
        Assert.Equal("formatted-by-mel", Assert.Single(harness.Witness.Calls).Text);
    }

    /// <summary>
    /// A null formatter does not throw. ALOG0022 uses <c>ArgumentNullException</c> and
    /// <c>The formatter is null.</c> That pair is not reported again for a second null
    /// formatter or for a formatter that throws <see cref="ArgumentNullException"/>.
    /// With a template, the event text is the template. Without one, the text is empty
    /// and the other keys are still properties.
    /// </summary>
    /// <remarks>
    /// No I/O. Three calls, one category. The other provider sees a null formatter.
    /// </remarks>
    [Fact]
    public void NullFormatter_ReportsArgumentNullException_AndDoesNotThrow()
    {
        var lines = new List<string>();
        var withTemplate = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", "tmpl {A}"),
            new("A", "alpha")
        };
        var without = new List<KeyValuePair<string, object?>>
        {
            new("A", "alpha")
        };
        using var harness = new Harness("QA.NullFmt", lines);

        harness.Log(withTemplate, null, null);
        harness.Log(without, null, null);
        harness.Log(withTemplate, null, (_, _) => throw new ArgumentNullException(null, "other"));

        Assert.Null(harness.LastError);
        Assert.Equal(3, harness.Writer.Events.Count);
        Assert.Equal("tmpl {A}", harness.Writer.Events[0].Message);
        Assert.Equal("tmpl {A}", harness.Writer.Events[0].MessageTemplate);
        Assert.Equal("alpha", harness.Writer.Events[0].Properties!["A"]);
        Assert.Equal(string.Empty, harness.Writer.Events[1].Message);
        Assert.Equal(string.Empty, harness.Writer.Events[1].MessageTemplate);
        Assert.Equal("alpha", harness.Writer.Events[1].Properties!["A"]);
        var report = Assert.Single(Reports(lines));
        Assert.Equal(
            "A log message from category 'QA.NullFmt' could not be formatted: ArgumentNullException: The formatter is null"
                + SentenceTail,
            report);
        Assert.DoesNotContain("null..", report, StringComparison.Ordinal);
        Assert.Contains(Fragment, report, StringComparison.Ordinal);
        Assert.Equal(3, harness.Witness.Calls.Count);
        Assert.True(harness.Witness.Calls[0].FormatterWasNull);
        Assert.Same(withTemplate, harness.Witness.Calls[0].State);
        Assert.True(harness.Witness.Calls[1].FormatterWasNull);
        Assert.IsType<ArgumentNullException>(harness.Witness.Calls[2].FormatError);
    }

    /// <summary>
    /// A formatter message that already ends with a period is written with one period,
    /// the period that closes the ALOG0022 sentence.
    /// </summary>
    /// <remarks>No I/O. One call. The literal line is the contract, not a helper that adds the period again.</remarks>
    [Fact]
    public void MessageEndingWithPeriod_IsReportedWithOnePeriod()
    {
        var lines = new List<string>();
        using var harness = new Harness("QA.Dot", lines);

        harness.Log("state", null, (_, _) => throw new FormatException("ends."));

        AssertNoCallerError(harness);
        Assert.Equal(
            "A log message from category 'QA.Dot' could not be formatted: FormatException: ends" + SentenceTail,
            Assert.Single(Reports(lines)));
    }

    /// <summary>
    /// A message that ends with a period and a space is trimmed before the sentence period,
    /// so the line reads <c>x. ALog wrote</c> and not <c>x. . ALog wrote</c>.
    /// </summary>
    /// <remarks>No I/O. U-C4-03. The space is whitespace, removed together with the period.</remarks>
    [Fact]
    public void MessageEndingWithPeriodAndSpace_IsOneSentence()
    {
        var lines = new List<string>();
        using var harness = new Harness("QA.Trail", lines);

        harness.Log("state", null, (_, _) => throw new InvalidOperationException("x. "));

        AssertNoCallerError(harness);
        Assert.Equal(
            "A log message from category 'QA.Trail' could not be formatted: InvalidOperationException: x" + SentenceTail,
            Assert.Single(Reports(lines)));
    }

    /// <summary>
    /// A message that ends with a period and a newline is trimmed the same way as a trailing space.
    /// The newline is whitespace, so <c>x.\n</c> also reads <c>x. ALog wrote</c>.
    /// </summary>
    /// <remarks>No I/O. U-C4-03. The newline is flattened and then removed with the period.</remarks>
    [Fact]
    public void MessageEndingWithPeriodAndNewline_IsOneSentence()
    {
        var lines = new List<string>();
        using var harness = new Harness("QA.Nl", lines);

        harness.Log("state", null, (_, _) => throw new InvalidOperationException("x.\n"));

        AssertNoCallerError(harness);
        Assert.Equal(
            "A log message from category 'QA.Nl' could not be formatted: InvalidOperationException: x" + SentenceTail,
            Assert.Single(Reports(lines)));
    }

    /// <summary>
    /// A message that is only periods and whitespace becomes <c>(no message)</c>
    /// so the sentence does not collapse into a bare colon.
    /// </summary>
    /// <remarks>No I/O. U-C4-03. Spaces, newlines, and periods are stripped together.</remarks>
    [Fact]
    public void MessageThatIsOnlyPeriodsAndWhitespace_UsesNoMessage()
    {
        var lines = new List<string>();
        using var harness = new Harness("QA.Empty", lines);

        harness.Log("state", null, (_, _) => throw new InvalidOperationException(" \n. \t."));

        AssertNoCallerError(harness);
        Assert.Equal(
            "A log message from category 'QA.Empty' could not be formatted: InvalidOperationException: (no message)"
                + SentenceTail,
            Assert.Single(Reports(lines)));
    }

    /// <summary>
    /// A two-million-character exception message is cut to 500 characters and marked
    /// <c> [truncated]</c>. The diagnostic line stays under 700 characters, and the event is still written.
    /// </summary>
    /// <remarks>
    /// No I/O. The message is one block of the letter x. The cap is the spec's 500, not a value read from the cut.
    /// </remarks>
    [Fact]
    public void LongExceptionMessage_IsCutTo500_AndMarkedTruncated()
    {
        const int cap = 500;
        const int limit = 700;
        Assert.Equal(cap, MelFormatGuard.MaxExceptionMessageLength);
        var lines = new List<string>();
        using var harness = new Harness("L", lines);

        harness.Log("state", null, (_, _) => throw new FixedMessageException(new string('x', 2_000_000)));

        AssertNoCallerError(harness);
        Assert.Single(harness.Writer.Events);
        var report = Assert.Single(Reports(lines));
        Assert.True(report.Length <= limit, "line length " + report.Length.ToString(CultureInfo.InvariantCulture));
        var head = "A log message from category 'L' could not be formatted: FixedMessageException: ";
        var mark = " [truncated]" + SentenceTail;
        Assert.Equal(head + new string('x', cap) + mark, report);
    }

    /// <summary>
    /// A surrogate pair that starts on the 500th character is kept whole by moving the cut back one character.
    /// The line does not contain either half of the pair.
    /// </summary>
    /// <remarks>
    /// No I/O. 499 letters, then U+D800 U+DC00, then more letters. The pair straddles the 500-character cut.
    /// </remarks>
    [Fact]
    public void SurrogatePairAtTheCut_IsNotSplit()
    {
        const int cap = 500;
        var lines = new List<string>();
        using var harness = new Harness("S", lines);
        var message = new string('a', cap - 1) + "\uD800\uDC00" + "tail";

        harness.Log("state", null, (_, _) => throw new FixedMessageException(message));

        AssertNoCallerError(harness);
        var report = Assert.Single(Reports(lines));
        var head = "A log message from category 'S' could not be formatted: FixedMessageException: ";
        Assert.Equal(head + new string('a', cap - 1) + " [truncated]" + SentenceTail, report);
        Assert.DoesNotContain("\uD800", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\uDC00", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two threads calling one thousand times with the same category and the same exception type
    /// produce exactly one ALOG0022 line and two thousand events, and no caller exception.
    /// </summary>
    /// <remarks>
    /// No I/O. The threads share one logger. The recording writer and the witness lock their lists.
    /// </remarks>
    [Fact]
    public void SamePair_IsReportedOnce_ForTwoThreadsTimesOneThousand()
    {
        var lines = new List<string>();
        using var harness = new Harness("C3b.Same", lines);
        var logger = harness.Logger;
        var state = new ThrowingIndexer();
        using var start = new Barrier(2);
        var errors = 0;
        var threads = new Thread[2];
        for (var slot = 0; slot < threads.Length; slot++)
        {
            threads[slot] = new Thread(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                {
                    Interlocked.Increment(ref errors);
                    return;
                }

                for (var i = 0; i < 1000; i++)
                {
                    try
                    {
                        logger.Log(MelLogLevel.Information, new EventId(22, "C3b"), state, null, (_, _) => "throwing list");
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            });
        }

        for (var slot = 0; slot < threads.Length; slot++)
            threads[slot].Start();

        for (var slot = 0; slot < threads.Length; slot++)
            Assert.True(threads[slot].Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, errors);
        Assert.Equal(2000, harness.Writer.Events.Count);
        Assert.Equal(2000, harness.Witness.Calls.Count);
        Assert.All(harness.Witness.Calls, call => Assert.Equal("throwing list", call.Text));
        Assert.Equal(Expected("C3b.Same", "InvalidOperationException", "indexer"), Assert.Single(Reports(lines)));
    }

    /// <summary>
    /// A second exception type on the same category produces a second ALOG0022 line.
    /// </summary>
    /// <remarks>
    /// No I/O. The first failure is the throwing indexer. The second is a throwing formatter.
    /// </remarks>
    [Fact]
    public void DifferentExceptionType_ReportsASecondLine()
    {
        var lines = new List<string>();
        using var harness = new Harness("QA.TwoTypes", lines);
        var state = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", "tmpl")
        };

        harness.Log(new ThrowingIndexer(), null, (_, _) => "throwing list");
        harness.Log(state, null, (_, _) => throw new FormatException("second"));

        AssertNoCallerError(harness);
        var reports = Reports(lines);
        Assert.Equal(2, reports.Count);
        Assert.Equal(Expected("QA.TwoTypes", "InvalidOperationException", "indexer"), reports[0]);
        Assert.Equal(Expected("QA.TwoTypes", "FormatException", "second"), reports[1]);
    }

    /// <summary>
    /// The 256th new pair is reported and the 257th is not. A repeat of a reported pair
    /// adds no line. Every call still writes an event.
    /// </summary>
    /// <remarks>
    /// No I/O. Sequential on one factory. The cap is the spec's 256, not a value read from production.
    /// </remarks>
    [Fact]
    public void ReportedPairs_StopAfter256()
    {
        const int cap = 256;
        var lines = new List<string>();
        using var harness = new Harness("QA.Cap", lines);
        for (var i = 0; i < cap; i++)
        {
            var category = "C3b.Cap." + i.ToString(CultureInfo.InvariantCulture);
            harness.Log(harness.Factory.CreateLogger(category), new ThrowingIndexer(), null, (_, _) => "throwing list");
        }

        Assert.Equal(256, MelFormatGuard.MaxPairs);
        Assert.Equal(cap, Reports(lines).Count);
        harness.Log(harness.Factory.CreateLogger("C3b.Cap.extra"), new ThrowingIndexer(), null, (_, _) => "throwing list");
        harness.Log(harness.Factory.CreateLogger("C3b.Cap.0"), new ThrowingIndexer(), null, (_, _) => "throwing list");

        Assert.Null(harness.LastError);
        Assert.Equal(cap, Reports(lines).Count);
        Assert.Equal(cap + 2, harness.Writer.Events.Count);
    }

    /// <summary>
    /// <c>{@Order}</c> and <c>{$Order}</c> are stored as <c>Order</c> with the same instance.
    /// <c>{Order}</c> then <c>{@Order}</c> keeps the first value. A key that is only <c>@</c> or <c>$</c> stays.
    /// The formatter text and the template are unchanged. Nothing is destructured.
    /// </summary>
    /// <remarks>
    /// No I/O. ALog's own template parser is not called here; the C3 test still checks that parser.
    /// </remarks>
    [Fact]
    public void Prefixes_StripAtAndDollar_KeepTheValue_FirstWins_LoneMarkerStays()
    {
        var order = new Box("box");
        var lines = new List<string>();

        using (var harness = new Harness("QA.At", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("{@Order}", order),
                new("{OriginalFormat}", "shipped {@Order}")
            };
            harness.Log(state, null, (_, _) => "shipped box");
            var evt = Only(harness, "shipped box", "shipped {@Order}");
            Assert.Same(order, evt.Properties!["Order"]);
            Assert.False(evt.Properties.ContainsKey("@Order"));
        }

        using (var harness = new Harness("QA.Dollar", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("{$Order}", order),
                new("{OriginalFormat}", "shipped {$Order}")
            };
            harness.Log(state, null, (_, _) => "shipped box");
            var evt = Only(harness, "shipped box", "shipped {$Order}");
            Assert.Same(order, evt.Properties!["Order"]);
            Assert.False(evt.Properties.ContainsKey("$Order"));
        }

        using (var harness = new Harness("QA.RawAt", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("@Order", order),
                new("{OriginalFormat}", "raw")
            };
            harness.Log(state, null, (_, _) => "raw");
            var evt = Only(harness, "raw", "raw");
            Assert.Same(order, evt.Properties!["Order"]);
        }

        using (var harness = new Harness("QA.First", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("{Order}", "first"),
                new("{@Order}", "second"),
                new("{OriginalFormat}", "{Order}")
            };
            harness.Log(state, null, (_, _) => "first");
            var evt = Only(harness, "first", "{Order}");
            var only = Assert.Single(evt.Properties!);
            Assert.Equal("Order", only.Key);
            Assert.Equal("first", only.Value);
        }

        using (var harness = new Harness("QA.DollarFirst", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("{$Order}", "first"),
                new("{Order}", "second"),
                new("{OriginalFormat}", "{Order}")
            };
            harness.Log(state, null, (_, _) => "first");
            var evt = Only(harness, "first", "{Order}");
            var only = Assert.Single(evt.Properties!);
            Assert.Equal("Order", only.Key);
            Assert.Equal("first", only.Value);
        }

        using (var harness = new Harness("QA.Lone", lines))
        {
            var state = new List<KeyValuePair<string, object?>>
            {
                new("{@}", "marker"),
                new("{$}", "dollar"),
                new("{OriginalFormat}", "lone")
            };
            harness.Log(state, null, (_, _) => "lone");
            var evt = Only(harness, "lone", "lone");
            Assert.Equal("marker", evt.Properties!["@"]);
            Assert.Equal("dollar", evt.Properties["$"]);
        }

        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// Missing, null, and empty <c>{OriginalFormat}</c> keep every other key, including a stripped
    /// <c>@</c> name. The event text is the formatter text and the template is empty.
    /// An empty template does not lose to a later non-empty one.
    /// </summary>
    /// <remarks>
    /// No I/O. Three states, three categories.
    /// </remarks>
    [Fact]
    public void MissingNullOrEmptyOriginalFormat_KeepsProperties_AndLeavesTemplateEmpty()
    {
        var order = new Box("box");
        var lines = new List<string>();
        var missing = new List<KeyValuePair<string, object?>>
        {
            new("A", "alpha"),
            new("{@Order}", order),
            new("B", "beta")
        };
        var nothing = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", null),
            new("A", "alpha"),
            new("{@Order}", order)
        };
        var empty = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", string.Empty),
            new("{OriginalFormat}", "should-not-win"),
            new("A", "alpha"),
            new("{@Order}", order)
        };

        AssertBare(missing, order, "QA.Missing", lines);
        AssertBare(nothing, order, "QA.NullTpl", lines);
        AssertBare(empty, order, "QA.EmptyTpl", lines);
        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// <c>{OriginalFormat}</c> at index 200, and again past 1024, is the event template.
    /// Properties still come only from the first 128 entries. <c>{@Order}</c> in that window is <c>Order</c>.
    /// </summary>
    /// <remarks>
    /// No I/O. The lists are longer than the property window on purpose.
    /// </remarks>
    [Fact]
    public void OriginalFormat_PastThePropertyWindow_SetsTheRealTemplate()
    {
        var order = new Box("box");
        var lines = new List<string>();
        AssertWindow(order, 200, lines, "QA.At200");
        AssertWindow(order, 1500, lines, "QA.At1500");
        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// A sequence that is not a list still binds by key, and a template at index 200 is kept.
    /// Entries at index 128 and later are not properties.
    /// </summary>
    /// <remarks>
    /// No I/O. The sequence yields pairs. It does not implement <see cref="IReadOnlyList{T}"/>.
    /// </remarks>
    [Fact]
    public void NonListEnumerable_BindsByKey_AndFindsTemplatePast128()
    {
        var order = new Box("box");
        var items = TemplateAt(200, "real {A}", order);
        var state = new PairSequence(items);
        var lines = new List<string>();
        using var harness = new Harness("QA.EnumOk", lines);

        harness.Log(state, null, (_, _) => "rendered");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal("rendered", evt.Message);
        Assert.Equal("real {A}", evt.MessageTemplate);
        Assert.Same(order, evt.Properties!["Order"]);
        Assert.Equal("alpha", evt.Properties["A"]);
        Assert.True(evt.Properties.ContainsKey("P127"));
        Assert.False(evt.Properties.ContainsKey("P128"));
        Assert.Equal(128, evt.Properties.Count);
    }

    /// <summary>
    /// A non-list sequence stops at 1024 entries, so a template at that index is not used.
    /// The event text stays the formatter text and the template stays empty. The first property remains.
    /// </summary>
    /// <remarks>
    /// No I/O. 1024 is the bridge's fixed enumerable ceiling. A list is not subject to it;
    /// <see cref="OriginalFormat_PastThePropertyWindow_SetsTheRealTemplate"/> covers that.
    /// </remarks>
    [Fact]
    public void NonListEnumerable_StopsAt1024_AndLeavesTheTemplateEmpty()
    {
        const int bound = 1024;
        Assert.Equal(bound, ALogLogger.MaxEnumeratedEntries);
        var items = new List<KeyValuePair<string, object?>>(bound + 1);
        items.Add(new("Early", "yes"));
        for (var i = 1; i < bound; i++)
            items.Add(new("N" + i.ToString(CultureInfo.InvariantCulture), i));
        items.Add(new("{OriginalFormat}", "too-late"));
        var state = new PairSequence(items);
        var lines = new List<string>();
        using var harness = new Harness("QA.EnumCap", lines);

        harness.Log(state, null, (_, _) => "formatter-text");

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal("formatter-text", evt.Message);
        Assert.Equal(string.Empty, evt.MessageTemplate);
        Assert.Equal("yes", evt.Properties!["Early"]);
        Assert.False(evt.Properties.ContainsKey("OriginalFormat"));
        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// A formatter that returns null still writes the event. The message is empty, the template is kept,
    /// and ALOG0022 is not reported because the formatter did not throw.
    /// </summary>
    /// <remarks>No I/O. One call.</remarks>
    [Fact]
    public void FormatterThatReturnsNull_WritesEmptyMessage_AndKeepsProperties()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("A", "alpha"),
            new("{OriginalFormat}", "tmpl")
        };
        var lines = new List<string>();
        using var harness = new Harness("QA.NullText", lines);

        harness.Log(state, null, (_, _) => null!);

        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal(string.Empty, evt.Message);
        Assert.Equal("tmpl", evt.MessageTemplate);
        Assert.Equal("alpha", evt.Properties!["A"]);
        Assert.Empty(Reports(lines));
    }

    /// <summary>
    /// A broken exception message still produces ALOG0022 and the event.
    /// A null message and a throwing message both become <c>(no message)</c> in the sentence.
    /// A null error is ignored. <see cref="SelfLog.ReportLine"/> ignores null and empty.
    /// A null category is written as an empty name.
    /// </summary>
    /// <remarks>No I/O. Uses the internal report guard for the null-error and null-category cases.</remarks>
    [Fact]
    public void BrokenExceptionMessage_StillReports_AndBlankLinesAreIgnored()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Target = line =>
        {
            lock (lines)
                lines.Add(line);
        };
        try
        {
            SelfLog.ReportLine(null!);
            SelfLog.ReportLine(string.Empty);
            MelFormatGuard.Report("QA.NullError", null);
            Assert.Empty(lines);

            MelFormatGuard.Report(null, new InvalidOperationException("named"));
            Assert.Equal(Expected(string.Empty, "InvalidOperationException", "named"), Assert.Single(Reports(lines)));

            using var harness = new Harness("QA.BadMessage", lines);
            harness.Log(new ThrowingMessageList(), null, (_, _) => "throwing list");
            Assert.Null(harness.LastError);
            harness.Log(
                harness.Factory.CreateLogger("QA.NullMessage"),
                new List<KeyValuePair<string, object?>> { new("{OriginalFormat}", "kept") },
                null,
                (_, _) => throw new NullMessageException());
            Assert.Null(harness.LastError);

            Assert.Equal(2, harness.Writer.Events.Count);
            Assert.Equal(string.Empty, harness.Writer.Events[0].Message);
            Assert.Equal("kept", harness.Writer.Events[1].Message);
            var reports = Reports(lines);
            Assert.Equal(3, reports.Count);
            Assert.Equal(Expected("QA.BadMessage", "ThrowingMessageException", "(no message)"), reports[1]);
            Assert.Equal(Expected("QA.NullMessage", "NullMessageException", "(no message)"), reports[2]);
            Assert.DoesNotContain("\n", reports[1], StringComparison.Ordinal);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Checks one no-template state: formatter text, empty template, and the prefix rule.
    /// </summary>
    /// <param name="state">Pairs. May contain an empty or null <c>{OriginalFormat}</c>.</param>
    /// <param name="order">Instance stored under <c>{@Order}</c>. Compared by reference.</param>
    /// <param name="category">Logger category for this state.</param>
    /// <param name="lines">Sink for unexpected ALOG0022 lines.</param>
    private static void AssertBare(
        List<KeyValuePair<string, object?>> state,
        Box order,
        string category,
        List<string> lines)
    {
        using var harness = new Harness(category, lines);
        harness.Log(state, null, (_, _) => "formatted");
        var evt = Only(harness, "formatted", string.Empty);
        Assert.Equal("alpha", evt.Properties!["A"]);
        Assert.Same(order, evt.Properties["Order"]);
        Assert.False(evt.Properties.ContainsKey("@Order"));
        Assert.False(evt.Properties.ContainsKey("OriginalFormat"));
    }

    /// <summary>
    /// Checks a list whose template sits at <paramref name="index"/>, past the 128-entry property window.
    /// </summary>
    /// <param name="order">Instance stored as <c>{@Order}</c> at index 0.</param>
    /// <param name="index">Zero-based index of <c>{OriginalFormat}</c>. Must be at least 128.</param>
    /// <param name="lines">Sink for unexpected ALOG0022 lines.</param>
    /// <param name="category">Logger category for this list.</param>
    private static void AssertWindow(Box order, int index, List<string> lines, string category)
    {
        var state = TemplateAt(index, "real {A}", order);
        Assert.Equal(index, state.FindIndex(pair => pair.Key == "{OriginalFormat}"));
        using var harness = new Harness(category, lines);
        harness.Log(state, null, (_, _) => "rendered");
        var evt = Only(harness, "rendered", "real {A}");
        Assert.Same(order, evt.Properties!["Order"]);
        Assert.Equal("alpha", evt.Properties["A"]);
        Assert.True(evt.Properties.ContainsKey("P127"));
        Assert.False(evt.Properties.ContainsKey("P128"));
        Assert.False(evt.Properties.ContainsKey("P" + index.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(128, evt.Properties.Count);
    }

    /// <summary>
    /// Builds a list of <paramref name="index"/> data pairs and then one template pair.
    /// Index 0 is <c>{@Order}</c>, index 1 is <c>A</c>, and the rest are <c>P{n}</c>.
    /// </summary>
    /// <param name="index">Index the template pair will occupy. Zero or more.</param>
    /// <param name="template">Template text stored as <c>{OriginalFormat}</c>.</param>
    /// <param name="order">Value for <c>{@Order}</c>. Not copied.</param>
    /// <returns>The list. Its count is <paramref name="index"/> plus one.</returns>
    private static List<KeyValuePair<string, object?>> TemplateAt(int index, string template, object? order)
    {
        var state = new List<KeyValuePair<string, object?>>(index + 1);
        for (var i = 0; i < index; i++)
        {
            if (i == 0)
                state.Add(new KeyValuePair<string, object?>("{@Order}", order));
            else if (i == 1)
                state.Add(new KeyValuePair<string, object?>("A", "alpha"));
            else
                state.Add(new KeyValuePair<string, object?>("P" + i.ToString(CultureInfo.InvariantCulture), i));
        }

        state.Add(new KeyValuePair<string, object?>("{OriginalFormat}", template));
        return state;
    }

    /// <summary>
    /// Returns the single event when the caller saw no exception and the text and template match.
    /// </summary>
    /// <param name="harness">Factory harness that just logged once.</param>
    /// <param name="message">Expected event text.</param>
    /// <param name="template">Expected template. Empty when the state had no usable <c>{OriginalFormat}</c>.</param>
    /// <returns>The event the recording writer stored.</returns>
    private static LogEvent Only(Harness harness, string message, string template)
    {
        AssertNoCallerError(harness);
        var evt = Assert.Single(harness.Writer.Events);
        Assert.Equal(message, evt.Message);
        Assert.Equal(template, evt.MessageTemplate);
        return evt;
    }

    /// <summary>
    /// Fails when the factory logger threw, including an <see cref="AggregateException"/>.
    /// </summary>
    /// <param name="harness">Harness whose last <c>Log</c> result is under test.</param>
    private static void AssertNoCallerError(Harness harness)
    {
        Assert.Null(harness.LastError);
    }

    /// <summary>
    /// Builds an ALOG0022 line for a short message that is not the truncation case.
    /// Trailing periods on <paramref name="exceptionMessage"/> are removed once, so the sentence's own period is the only one.
    /// </summary>
    /// <param name="category">Logger category, inserted as written.</param>
    /// <param name="exceptionType">Runtime type name, such as <c>InvalidOperationException</c>.</param>
    /// <param name="exceptionMessage">Exception message. A trailing period is dropped. Longer messages are asserted as literals, not here.</param>
    /// <returns>The full diagnostic line, including the fragment <c>could not be formatted</c>.</returns>
    private static string Expected(string category, string exceptionType, string exceptionMessage)
    {
        return "A log message from category '" + category + "' could not be formatted: "
            + exceptionType + ": " + exceptionMessage.TrimEnd('.')
            + SentenceTail;
    }

    /// <summary>
    /// Copies the lines that contain the ALOG0022 fragment, in arrival order.
    /// </summary>
    /// <param name="lines">Lines captured from <see cref="SelfLog.Target"/>. Not null.</param>
    /// <returns>A new list. Empty when nothing was reported.</returns>
    private static List<string> Reports(List<string> lines)
    {
        var found = new List<string>();
        lock (lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(Fragment, StringComparison.Ordinal))
                    found.Add(lines[i]);
            }
        }

        return found;
    }

    /// <summary>
    /// One factory with ALog registered first and a witness provider second.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly Action<string>? _previous;
        private readonly bool _restore;

        /// <summary>
        /// Builds the factory, the recording writer, and the witness. Installs <paramref name="lines"/>
        /// as the SelfLog target when it is not null.
        /// </summary>
        /// <param name="category">Category of <see cref="Logger"/>. Not null.</param>
        /// <param name="lines">Optional diagnostic sink. Null leaves SelfLog on stderr.</param>
        public Harness(string category, List<string>? lines = null)
        {
            Writer = new RecordingWriter();
            Witness = new WitnessProvider();
            var config = new LoggerConfig()
                .AddWriter(Writer)
                .SetMinimumLevel(ALog.LogLevel.Trace)
                .UseAsync(false);
            Factory = LoggerFactory.Create(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(MelLogLevel.Trace);
                logging.AddALog(config);
                logging.AddProvider(Witness);
            });
            Logger = Factory.CreateLogger(category);
            if (lines is null)
                return;

            _previous = SelfLog.Target;
            SelfLog.Target = line =>
            {
                lock (lines)
                    lines.Add(line);
            };
            _restore = true;
        }

        /// <summary>Writer attached to ALog. The test reads it after <c>Log</c> returns.</summary>
        public RecordingWriter Writer { get; }

        /// <summary>Second MEL provider. It records the call and does not throw.</summary>
        public WitnessProvider Witness { get; }

        /// <summary>Factory that owns both providers. Disposing it disposes the engine.</summary>
        public ILoggerFactory Factory { get; }

        /// <summary>Logger created for the harness category.</summary>
        public ILogger Logger { get; }

        /// <summary>Exception from the last <c>Log</c> call, or null when the caller was not thrown into.</summary>
        public Exception? LastError { get; private set; }

        /// <summary>
        /// Logs through the factory logger and stores any exception instead of letting it fail the test early.
        /// </summary>
        /// <typeparam name="TState">State type. A list, a throwing list, or a plain string.</typeparam>
        /// <param name="state">State passed through unchanged. Null is allowed for a reference type.</param>
        /// <param name="exception">Exception attached to the event. Null when the call has none.</param>
        /// <param name="formatter">Formatter MEL passes to every provider. Null is the null-formatter case.</param>
        public void Log<TState>(
            TState state,
            Exception? exception,
            Func<TState, Exception?, string>? formatter)
        {
            Log(Logger, state, exception, formatter);
        }

        /// <summary>
        /// Logs through <paramref name="logger"/> and stores any exception from that call.
        /// </summary>
        /// <typeparam name="TState">State type.</typeparam>
        /// <param name="logger">Factory logger. Not null.</param>
        /// <param name="state">State passed through unchanged.</param>
        /// <param name="exception">Exception attached to the event. Null when the call has none.</param>
        /// <param name="formatter">Formatter. Null is the null-formatter case.</param>
        public void Log<TState>(
            ILogger logger,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string>? formatter)
        {
            LastError = Record.Exception(() =>
                logger.Log(MelLogLevel.Information, new EventId(22, "C3b"), state, exception, formatter!));
        }

        /// <summary>
        /// Disposes the factory and puts SelfLog back the way this harness found it.
        /// </summary>
        public void Dispose()
        {
            Factory.Dispose();
            if (_restore)
                SelfLog.Target = _previous;
        }
    }

    /// <summary>
    /// MEL provider that records each log call, including a null or throwing formatter.
    /// </summary>
    private sealed class WitnessProvider : ILoggerProvider
    {
        private readonly object _gate = new();

        /// <summary>Calls in arrival order. The list is guarded by a lock.</summary>
        public List<WitnessCall> Calls { get; } = new();

        /// <summary>
        /// Returns a logger that records into this provider.
        /// </summary>
        /// <param name="categoryName">Category MEL asked for. Null is stored as empty.</param>
        /// <returns>A logger that does not throw.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return new Witness(this, categoryName ?? string.Empty);
        }

        /// <summary>No resources to release.</summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Appends one call. Safe to call from the logging threads.
        /// </summary>
        /// <param name="call">The recorded call. Copied by value.</param>
        public void Add(WitnessCall call)
        {
            lock (_gate)
                Calls.Add(call);
        }

        private sealed class Witness : ILogger
        {
            private readonly WitnessProvider _owner;
            private readonly string _category;

            /// <summary>
            /// Stores the provider and the category.
            /// </summary>
            /// <param name="owner">Provider that keeps the calls. Not null.</param>
            /// <param name="category">Category name. Not null.</param>
            public Witness(WitnessProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            /// <summary>
            /// BeginScope is not part of these tests. Returns null.
            /// </summary>
            /// <typeparam name="TState">Ignored.</typeparam>
            /// <param name="state">Ignored.</param>
            /// <returns>Null. There is no scope to dispose.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            /// <summary>
            /// Accepts every level so the factory always delivers the call.
            /// </summary>
            /// <param name="logLevel">Ignored.</param>
            /// <returns>True.</returns>
            public bool IsEnabled(MelLogLevel logLevel)
            {
                return true;
            }

            /// <summary>
            /// Records the state and the formatter result. A null or throwing formatter is recorded, not rethrown.
            /// </summary>
            /// <typeparam name="TState">State type MEL passed.</typeparam>
            /// <param name="logLevel">Ignored.</param>
            /// <param name="eventId">Ignored.</param>
            /// <param name="state">Original state. Stored by reference.</param>
            /// <param name="exception">Ignored. The formatter receives it.</param>
            /// <param name="formatter">The same delegate ALog received. Null is recorded as null.</param>
            public void Log<TState>(
                MelLogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string? text = null;
                Exception? error = null;
                var missing = formatter is null;
                if (formatter is not null)
                {
                    try
                    {
                        text = formatter(state, exception);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                }

                _owner.Add(new WitnessCall
                {
                    Category = _category,
                    State = state,
                    Text = text,
                    FormatError = error,
                    FormatterWasNull = missing
                });
            }
        }
    }

    /// <summary>One call observed by <see cref="WitnessProvider"/>.</summary>
    private struct WitnessCall
    {
        /// <summary>Logger category.</summary>
        public string Category;

        /// <summary>State reference MEL passed. Not copied.</summary>
        public object? State;

        /// <summary>Formatter text, or null when the formatter was null or threw.</summary>
        public string? Text;

        /// <summary>Exception the formatter threw, or null.</summary>
        public Exception? FormatError;

        /// <summary>True when the formatter delegate was null.</summary>
        public bool FormatterWasNull;
    }

    /// <summary>
    /// Tester's throwing list. <c>Count</c> is 3, the indexer throws <c>indexer</c>, and the enumerator throws <c>enum</c>.
    /// </summary>
    private sealed class ThrowingIndexer : IReadOnlyList<KeyValuePair<string, object?>>
    {
        /// <summary>Returns 3 without throwing, matching the practice repro.</summary>
        public int Count => 3;

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> with message <c>indexer</c>.
        /// </summary>
        /// <param name="index">Ignored.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="InvalidOperationException">Always. Message is <c>indexer</c>.</exception>
        public KeyValuePair<string, object?> this[int index] => throw new InvalidOperationException("indexer");

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> with message <c>enum</c>.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="InvalidOperationException">Always. Message is <c>enum</c>.</exception>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("enum");

        /// <summary>
        /// Throws the same way as <see cref="GetEnumerator"/>.
        /// </summary>
        /// <returns>Never returns.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>List whose <c>Count</c> throws before any pair can be read.</summary>
    private sealed class ThrowingCount : IReadOnlyList<KeyValuePair<string, object?>>
    {
        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> with message <c>count</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public int Count => throw new InvalidOperationException("count");

        /// <summary>
        /// Returns a pair. The bridge must not reach this when <c>Count</c> throws.
        /// </summary>
        /// <param name="index">Ignored.</param>
        /// <returns>A placeholder pair.</returns>
        public KeyValuePair<string, object?> this[int index] => new("A", "alpha");

        /// <summary>
        /// Yields one pair. The bridge must not enumerate when it already uses <c>Count</c>.
        /// </summary>
        /// <returns>An enumerator of one pair.</returns>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("A", "alpha");
        }

        /// <summary>Returns the typed enumerator.</summary>
        /// <returns>The typed enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Sequence that is not a list and whose enumerator throws immediately.</summary>
    private sealed class ThrowingEnumerable : IEnumerable<KeyValuePair<string, object?>>
    {
        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> with message <c>enum</c>.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("enum");

        /// <summary>Throws the same way as the typed enumerator.</summary>
        /// <returns>Never returns.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>List that returns pairs until <c>throwAt</c> and then throws from the indexer.</summary>
    private sealed class ThrowAtIndex : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _items;
        private readonly int _throwAt;

        /// <summary>
        /// Stores the pairs and the index that throws.
        /// </summary>
        /// <param name="throwAt">First index that throws. Zero throws on the first read.</param>
        /// <param name="items">Pairs returned for indexes below <paramref name="throwAt"/>.</param>
        public ThrowAtIndex(int throwAt, params KeyValuePair<string, object?>[] items)
        {
            _throwAt = throwAt;
            _items = items;
        }

        /// <summary>Number of stored pairs, including the ones that throw when read.</summary>
        public int Count => _items.Length;

        /// <summary>
        /// Returns the stored pair, or throws <c>indexer</c> at and after the configured index.
        /// </summary>
        /// <param name="index">Zero-based index. Negative is out of range for a real list; these tests do not pass one.</param>
        /// <returns>The stored pair when <paramref name="index"/> is below the throw point.</returns>
        /// <exception cref="InvalidOperationException">Message <c>indexer</c> when <paramref name="index"/> is at or past the throw point.</exception>
        public KeyValuePair<string, object?> this[int index]
        {
            get
            {
                if (index >= _throwAt)
                    throw new InvalidOperationException("indexer");

                return _items[index];
            }
        }

        /// <summary>
        /// Throws <c>enum</c>. The list path must use the indexer, not this.
        /// </summary>
        /// <returns>Never returns.</returns>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("enum");

        /// <summary>Throws the same way as the typed enumerator.</summary>
        /// <returns>Never returns.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Yields pairs without implementing a list, so the bridge has to enumerate.
    /// </summary>
    private sealed class PairSequence : IEnumerable<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _items;

        /// <summary>
        /// Stores the pairs to yield, in order.
        /// </summary>
        /// <param name="items">Source pairs. Not copied. Not null.</param>
        public PairSequence(IReadOnlyList<KeyValuePair<string, object?>> items)
        {
            _items = items;
        }

        /// <summary>
        /// Yields each stored pair. This type is not an <see cref="IReadOnlyList{T}"/>.
        /// </summary>
        /// <returns>The pair sequence.</returns>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < _items.Count; i++)
                yield return _items[i];
        }

        /// <summary>Returns the typed enumerator.</summary>
        /// <returns>The pair sequence.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Exception whose <see cref="Exception.Message"/> is a fixed string, including a multi-megabyte one.
    /// </summary>
    private sealed class FixedMessageException : Exception
    {
        private readonly string _text;

        /// <summary>
        /// Stores <paramref name="text"/> as the message. The base message is not used.
        /// </summary>
        /// <param name="text">Message text. Not null. Not copied.</param>
        public FixedMessageException(string text)
        {
            _text = text;
        }

        /// <summary>The text passed to the constructor.</summary>
        public override string Message => _text;
    }

    /// <summary>List whose indexer throws an exception with a throwing <see cref="Exception.Message"/>.</summary>
    private sealed class ThrowingMessageList : IReadOnlyList<KeyValuePair<string, object?>>
    {
        /// <summary>Returns 1 so the indexer is reached.</summary>
        public int Count => 1;

        /// <summary>
        /// Throws <see cref="ThrowingMessageException"/>.
        /// </summary>
        /// <param name="index">Ignored.</param>
        /// <returns>Never returns.</returns>
        public KeyValuePair<string, object?> this[int index] => throw new ThrowingMessageException();

        /// <summary>Not used. The list path reads by index.</summary>
        /// <returns>An empty enumerator.</returns>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield break;
        }

        /// <summary>Returns the typed enumerator.</summary>
        /// <returns>The typed enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Exception whose message getter throws.</summary>
    private sealed class ThrowingMessageException : Exception
    {
        /// <summary>
        /// Throws so the diagnostic line cannot use the message text.
        /// </summary>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public override string Message => throw new InvalidOperationException("message");
    }

    /// <summary>Exception whose message getter returns null.</summary>
    private sealed class NullMessageException : Exception
    {
        /// <summary>Returns null so the diagnostic line has an empty detail.</summary>
        public override string Message => null!;
    }

    /// <summary>A reference-type value so a prefix test can see that the instance was stored, not rendered.</summary>
    private sealed class Box
    {
        /// <summary>
        /// Stores the label <see cref="ToString"/> returns.
        /// </summary>
        /// <param name="label">Text a destructuring formatter might show. Not null.</param>
        public Box(string label)
        {
            Label = label;
        }

        /// <summary>Text returned by <see cref="ToString"/>.</summary>
        public string Label { get; }

        /// <summary>
        /// Returns <see cref="Label"/> so a destructuring bug would be visible if the bridge rendered the value.
        /// </summary>
        /// <returns>The label passed to the constructor.</returns>
        public override string ToString() => Label;
    }
}
