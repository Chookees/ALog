using System.Globalization;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// MEL message values are bound by name (arc_spec C3-01, C3-02, C3-04).
/// The rendered text is whatever the caller passed as the MEL formatter.
/// Property names come from the state keys, not from the order of the values.
/// </summary>
public sealed class MelKeyBindingTests
{
    /// <summary>
    /// A template <c>{A} {B}</c> whose pairs arrive as B, then the format, then A
    /// keeps each value under its own name and the formatter's text (C3-01).
    /// </summary>
    /// <remarks>
    /// No I/O. The provider writes one event on the calling thread.
    /// </remarks>
    [Fact]
    public void ReorderedPairs_BindByName_AndKeepFormatterText()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("B", "beta"),
            new("{OriginalFormat}", "{A} {B}"),
            new("A", "alpha")
        };

        var evt = Emit(state, "{A} {B}", "alpha beta");

        AssertProperty(evt, "A", "alpha");
        AssertProperty(evt, "B", "beta");
        Assert.Equal(2, evt.Properties!.Count);
    }

    /// <summary>
    /// A key <c>{Elapsed:0.00}</c> is stored as <c>Elapsed</c>. The format suffix is not part of the name (C3-02).
    /// </summary>
    /// <remarks>
    /// No I/O. The numeric value is kept as the original object, not as the formatted text.
    /// </remarks>
    [Fact]
    public void FormatSuffix_IsStrippedFromThePropertyName()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("{Elapsed:0.00}", 1.5d),
            new("{OriginalFormat}", "took {Elapsed:0.00}")
        };

        var evt = Emit(state, "took {Elapsed:0.00}", "took 1.50");

        AssertProperty(evt, "Elapsed", 1.5d);
        Assert.False(evt.Properties!.ContainsKey("{Elapsed:0.00}"));
        Assert.False(evt.Properties.ContainsKey("Elapsed:0.00"));
    }

    /// <summary>
    /// ALog's own template parser still does not treat <c>@</c> as an operator.
    /// The MEL bridge stores <c>{@Order}</c> as the property <c>Order</c> (C3b-02).
    /// </summary>
    /// <remarks>
    /// No I/O. The parser assertions are the original C3 check and are unchanged.
    /// A later parser change that starts stripping <c>@</c> fails this test.
    /// </remarks>
    [Fact]
    public void AtSign_ParserKeepsTheMarker_BridgeStoresOrder()
    {
        var order = new AtOrder("box");
        var parsed = MessageTemplate.Format("{@Order}", order);
        Assert.False(MessageTemplate.HasHoles("{@Order}"));
        Assert.False(parsed.Properties.ContainsKey("Order"));
        Assert.False(parsed.Properties.ContainsKey("@Order"));

        var state = new List<KeyValuePair<string, object?>>
        {
            new("{@Order}", order),
            new("{OriginalFormat}", "shipped {@Order}")
        };

        var evt = Emit(state, "shipped {@Order}", "shipped box");

        AssertProperty(evt, "Order", order);
        Assert.False(evt.Properties!.ContainsKey("@Order"));
    }

    /// <summary>
    /// The same key twice keeps the first value. Later keys are not shifted into it (C3-02).
    /// </summary>
    /// <remarks>
    /// No I/O. Comparison is ordinal, so this does not fold case.
    /// </remarks>
    [Fact]
    public void DuplicateKey_FirstValueWins_AndLaterNamesStay()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("A", "first"),
            new("A", "second"),
            new("B", "bee"),
            new("{OriginalFormat}", "{A} {B}")
        };

        var evt = Emit(state, "{A} {B}", "first bee");

        AssertProperty(evt, "A", "first");
        AssertProperty(evt, "B", "bee");
        Assert.Equal(2, evt.Properties!.Count);
    }

    /// <summary>
    /// Positional holes are named <c>0</c> and <c>1</c>, which is the key MEL stores, even when the pairs are reversed (C3-02).
    /// </summary>
    /// <remarks>
    /// No I/O. The braces in the key are not part of the property name.
    /// </remarks>
    [Fact]
    public void PositionalHoles_AreNamedByIndex_NotByListOrder()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("{1}", "right"),
            new("{OriginalFormat}", "{0} {1}"),
            new("{0}", "left")
        };

        var evt = Emit(state, "{0} {1}", "left right");

        AssertProperty(evt, "0", "left");
        AssertProperty(evt, "1", "right");
        Assert.Equal(2, evt.Properties!.Count);
    }

    /// <summary>
    /// A hole with no matching key adds no property. The formatter text is kept as written (C3-02).
    /// </summary>
    /// <remarks>
    /// No I/O. Nothing is borrowed from another pair to fill the gap.
    /// </remarks>
    [Fact]
    public void MissingKey_OmitsThatProperty_AndKeepsFormatterText()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("A", "alpha"),
            new("{OriginalFormat}", "{A} {B}")
        };

        var evt = Emit(state, "{A} {B}", "alpha (no b)");

        AssertProperty(evt, "A", "alpha");
        Assert.False(evt.Properties!.ContainsKey("B"));
    }

    /// <summary>
    /// A key that the template never mentions is still a property (C3-02).
    /// </summary>
    /// <remarks>
    /// No I/O. The extra pair is not dropped and is not written into <c>A</c>.
    /// </remarks>
    [Fact]
    public void ExtraKey_BecomesAProperty_WithoutTakingAnothersValue()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Extra", "extra"),
            new("{OriginalFormat}", "{A}"),
            new("A", "alpha")
        };

        var evt = Emit(state, "{A}", "alpha");

        AssertProperty(evt, "A", "alpha");
        AssertProperty(evt, "Extra", "extra");
    }

    /// <summary>
    /// A null value stays a present property, a null or empty key is skipped, and entries past the
    /// 128-item window are dropped. The first <c>{OriginalFormat}</c> wins when it appears twice.
    /// </summary>
    /// <remarks>
    /// No I/O. The window is the same 128 cap as <see cref="MelScopeAdapter"/>.
    /// The 128th data key sits just outside that window when the format key occupies the first slot.
    /// </remarks>
    [Fact]
    public void Binding_HonorsNulls_EmptyKeys_FirstTemplate_AndTheCap()
    {
        var state = new List<KeyValuePair<string, object?>>(132)
        {
            new("{OriginalFormat}", "kept {A}"),
            new("{OriginalFormat}", "ignored {A}"),
            new(null!, "skip-null-key"),
            new("", "skip-empty-key"),
            new("{}", "skip-empty-name"),
            new("N", null)
        };
        for (var i = 0; i < 130; i++)
            state.Add(new KeyValuePair<string, object?>("P" + i.ToString(CultureInfo.InvariantCulture), i));

        var evt = Emit(state, "kept {A}", "formatter");

        Assert.Equal(128, MelScopeAdapter.MaxProperties);
        Assert.Equal("kept {A}", evt.MessageTemplate);
        AssertProperty(evt, "N", null);
        Assert.False(evt.Properties!.ContainsKey(""));
        Assert.False(evt.Properties.ContainsKey("OriginalFormat"));
        // Six leading entries, then P0. A 128-entry window ends at P121.
        Assert.True(evt.Properties.ContainsKey("P0"));
        Assert.True(evt.Properties.ContainsKey("P121"));
        Assert.False(evt.Properties.ContainsKey("P122"));
        Assert.Equal(123, evt.Properties.Count);
    }

    /// <summary>
    /// No <c>{OriginalFormat}</c>, or an empty one, keeps the formatter text and the named pairs.
    /// The template stays empty. The bridge must not copy the formatter text into the template (C3b-03).
    /// </summary>
    /// <remarks>
    /// No I/O. Covers a missing key and an empty value. A null value is covered by the C3b tests.
    /// </remarks>
    [Fact]
    public void MissingOrEmptyOriginalFormat_KeepsFormatterText_AndNamedProperties()
    {
        var missing = EmitPlain(
            new List<KeyValuePair<string, object?>> { new("A", "alpha") },
            "plain");
        Assert.Equal("plain", missing.Message);
        Assert.Equal(string.Empty, missing.MessageTemplate);
        Assert.Equal("alpha", missing.Properties!["A"]);

        var empty = EmitPlain(
            new List<KeyValuePair<string, object?>>
            {
                new("{OriginalFormat}", string.Empty),
                new("A", "alpha")
            },
            "plain");
        Assert.Equal("plain", empty.Message);
        Assert.Equal(string.Empty, empty.MessageTemplate);
        Assert.Equal("alpha", empty.Properties!["A"]);
    }

    /// <summary>
    /// <see cref="LoggerEngine.LogCaptured"/> drops a level below the minimum and stores a null message as empty.
    /// The provider's own gate would hide the level check, so this calls the engine directly.
    /// </summary>
    /// <remarks>
    /// No I/O. Disposes both engines. Safe to run next to the other unit tests.
    /// </remarks>
    [Fact]
    public void LogCaptured_DropsEventsBelowTheMinimum_AndStoresANullMessageAsEmpty()
    {
        var hidden = new RecordingWriter();
        using (var quiet = new LoggerEngine(
            new LoggerConfig().AddWriter(hidden).SetMinimumLevel(ALog.LogLevel.Error).UseAsync(false)))
        {
            quiet.LogCaptured(new LoggerEngine.CapturedLog
            {
                Category = "C3",
                Level = ALog.LogLevel.Info,
                Message = "hidden",
                MessageTemplate = "hidden {A}",
                Properties = new Dictionary<string, object> { ["A"] = "x" }
            });
        }

        Assert.Empty(hidden.Events);

        var writer = new RecordingWriter();
        using var engine = new LoggerEngine(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace).UseAsync(false));
        engine.LogCaptured(new LoggerEngine.CapturedLog
        {
            Category = "C3",
            Level = ALog.LogLevel.Info,
            Message = null,
            MessageTemplate = "t"
        });

        var evt = Assert.Single(writer.Events);
        Assert.Equal(string.Empty, evt.Message);
        Assert.Equal("t", evt.MessageTemplate);
        Assert.Equal("C3", evt.Category);
    }

    /// <summary>
    /// A scope whose pairs are not in template order keeps each name. <c>{OriginalFormat}</c> is not a scope property (C3-04).
    /// </summary>
    /// <remarks>
    /// No I/O. Uses the logger directly so <c>BeginScope</c> goes through the scope adapter, not the host.
    /// </remarks>
    [Fact]
    public void BeginScope_ReorderedKeys_KeepTheirNames()
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace).UseAsync(false));
        var logger = provider.CreateLogger("C3.Scope");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("B", "beta"),
            new("{OriginalFormat}", "{A} {B}"),
            new("A", "alpha")
        };

        using (logger.BeginScope(state))
            logger.LogInformation("inside-scope");

        var evt = Assert.Single(writer.Events);
        Assert.Equal("inside-scope", evt.Message);
        Assert.NotNull(evt.Context);
        Assert.Equal("alpha", evt.Context["A"]);
        Assert.Equal("beta", evt.Context["B"]);
        Assert.False(evt.Context.ContainsKey("{OriginalFormat}"));
        Assert.False(evt.Context.ContainsKey("OriginalFormat"));
    }

    /// <summary>
    /// Writes one information event through the MEL bridge and checks the formatter text and template.
    /// </summary>
    /// <param name="state">Pairs in the order the test wants. Must contain <c>{OriginalFormat}</c>.</param>
    /// <param name="template">Expected <see cref="LogEvent.MessageTemplate"/>.</param>
    /// <param name="rendered">Text the MEL formatter returns. This is the expected message.</param>
    /// <returns>The single event the recording writer stored.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the bridge writes a different message, template, or count.</exception>
    /// <remarks>
    /// No I/O. The writer is synchronous. Safe to call from any thread; it does not use <c>Log</c>.
    /// </remarks>
    private static LogEvent Emit(
        List<KeyValuePair<string, object?>> state,
        string template,
        string rendered)
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace).UseAsync(false));
        var logger = provider.CreateLogger("C3.Mel");
        logger.Log(MelLogLevel.Information, new EventId(3, "C3"), state, null, (_, _) => rendered);

        var evt = Assert.Single(writer.Events);
        Assert.Equal(rendered, evt.Message);
        Assert.Equal(template, evt.MessageTemplate);
        return evt;
    }

    /// <summary>
    /// Writes one information event and returns it without requiring a stored template.
    /// Used when the state has no usable <c>{OriginalFormat}</c> and the bridge falls back.
    /// </summary>
    /// <param name="state">Pairs in the order the test wants. May omit the template key.</param>
    /// <param name="rendered">Text the MEL formatter returns. This is the expected message.</param>
    /// <returns>The single event the recording writer stored.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the bridge writes a different message or more than one event.</exception>
    /// <remarks>
    /// No I/O. The writer is synchronous.
    /// </remarks>
    private static LogEvent EmitPlain(List<KeyValuePair<string, object?>> state, string rendered)
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace).UseAsync(false));
        var logger = provider.CreateLogger("C3.Mel");
        logger.Log(MelLogLevel.Information, new EventId(3, "C3"), state, null, (_, _) => rendered);

        var evt = Assert.Single(writer.Events);
        Assert.Equal(rendered, evt.Message);
        return evt;
    }

    /// <summary>
    /// Asserts that one property exists and holds the expected value, including null.
    /// </summary>
    /// <param name="evt">Event written by <see cref="Emit"/>.</param>
    /// <param name="name">Property name. Compared with ordinal rules by the bag.</param>
    /// <param name="expected">Value stored for <paramref name="name"/>. Null is a present value, not a missing key.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the key is missing or the value differs.</exception>
    /// <remarks>
    /// No I/O. Does not copy the bag.
    /// </remarks>
    private static void AssertProperty(LogEvent evt, string name, object? expected)
    {
        Assert.NotNull(evt.Properties);
        Assert.True(evt.Properties!.ContainsKey(name), "Missing property " + name + ".");
        Assert.Equal(expected, evt.Properties[name]);
    }

    /// <summary>
    /// A small object used so the <c>@</c> test can see that the same instance was stored.
    /// </summary>
    /// <remarks>
    /// No behavior. Equality is reference equality via <see cref="Assert.Equal{T}(T, T)"/> on the instance.
    /// </remarks>
    private sealed class AtOrder
    {
        /// <summary>
        /// Stores the label that <see cref="ToString"/> returns.
        /// </summary>
        /// <param name="label">Text MEL would render. Null is rejected.</param>
        public AtOrder(string label)
        {
            Label = label;
        }

        /// <summary>Text returned by <see cref="ToString"/>.</summary>
        public string Label { get; }

        /// <summary>
        /// Returns <see cref="Label"/> so a re-render would show that text instead of the type name.
        /// </summary>
        /// <returns>The label passed to the constructor.</returns>
        public override string ToString() => Label;
    }
}
