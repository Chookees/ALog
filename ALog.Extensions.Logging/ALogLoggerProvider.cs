// MEL bridge. When formatting succeeds, the event text is the formatter's text.
// {OriginalFormat} is the template. It is searched across the whole list, not only the first 128 entries.
// A null or throwing formatter, or a throwing state access, does not escape into the application:
// the event is still written and ALOG0022 is reported once per category and exception type, up to 256 pairs.
// A property name drops one leading '@' or '$'. ALog's own template parser is unchanged.
// Next: MelScopeAdapter.cs for scopes, LoggerEngine.LogCaptured for the write.
namespace ALog.Extensions.Logging;

using System.Collections.Concurrent;
using ALog;
using ALog.Config;
using ALog.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ALogLevel = ALog.LogLevel;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>
/// Bridges Microsoft.Extensions.Logging to a shared <see cref="LoggerEngine"/>.
/// </summary>
public sealed class ALogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly LoggerEngine _engine;
    private readonly bool _ownsEngine;
    private readonly FloorConfiguration? _floorConfig;
    private readonly ConcurrentDictionary<string, ALogLogger> _loggers = new(StringComparer.Ordinal);
    private readonly IDisposable? _reload;
    private volatile LevelTable _table;
    private volatile IExternalScopeProvider? _scopes;
    private int _generation;

    public ALogLoggerProvider(LoggerConfig config, bool ownsEngine = true)
        : this(config, ownsEngine, monitor: null)
    {
    }

    public ALogLoggerProvider(LoggerEngine engine, bool ownsEngine = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
        // The shared engine enforces its own minimum. This provider does not
        // know that level, so IsEnabled stays open and Log still hits the gate.
        _table = LevelTable.Open();
    }

    internal ALogLoggerProvider(
        LoggerConfig config,
        bool ownsEngine,
        IOptionsMonitor<LoggerFilterOptions>? monitor)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Seal();
        _floorConfig = new FloorConfiguration(config);
        _table = LevelTable.Capture(monitor?.CurrentValue, generation: 0);
        _floorConfig.SetRuleFloor(_table.RuleFloor);
        _engine = new LoggerEngine(_floorConfig);
        _ownsEngine = ownsEngine;
        if (monitor is not null)
            _reload = monitor.OnChange(OnReload);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName ?? string.Empty, Create);

    /// <summary>
    /// The factory calls this when the provider is registered. Later BeginScope
    /// calls on a factory logger go to that provider, not to <see cref="ALogLogger"/>.
    /// </summary>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopes = scopeProvider;

    public void Dispose()
    {
        _reload?.Dispose();
        if (_ownsEngine)
            _engine.Dispose();
    }

    private ALogLogger Create(string category)
    {
        return new ALogLogger(category, _engine, _floorConfig, CurrentTable, CurrentScopes);
    }

    private LevelTable CurrentTable() => _table;

    private IExternalScopeProvider? CurrentScopes() => _scopes;

    private void OnReload(LoggerFilterOptions options)
    {
        try
        {
            var generation = Interlocked.Increment(ref _generation);
            var table = LevelTable.Capture(options, generation);
            _floorConfig?.SetRuleFloor(table.RuleFloor);
            _table = table;
        }
        catch (Exception)
        {
            // A bad filter reload must not tear down the host. The previous floor stays.
        }
    }

}

internal sealed class ALogLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly string _category;
    private readonly LoggerEngine _engine;
    private readonly FloorConfiguration? _levels;
    private readonly Func<LevelTable> _current;
    private readonly Func<IExternalScopeProvider?> _scopes;
    private volatile int _categoryMin = LevelTable.UseGlobal;
    private volatile int _seen = -1;

    // Same spelling MEL uses. Compared ordinally so a similar key stays a property.
    private const string OriginalFormat = "{OriginalFormat}";

    // The message on the synthetic failure used when the formatter delegate is null.
    private const string NullFormatterMessage = "The formatter is null.";

    /// <summary>
    /// Fixed ceiling for a state that is only an enumerable.
    /// A list is bounded by its <c>Count</c> instead, so a template at any index is still found.
    /// </summary>
    /// <remarks>
    /// shortcut: non-list enumerables stop at 1024 entries. Raise this when a real MEL state
    /// that is not a list is longer than 1024 and its template sits past that index.
    /// </remarks>
    internal const int MaxEnumeratedEntries = 1024;

    public ALogLogger(
        string category,
        LoggerEngine engine,
        FloorConfiguration? levels,
        Func<LevelTable> current,
        Func<IExternalScopeProvider?> scopes)
    {
        _category = category;
        _engine = engine;
        _levels = levels;
        _current = current;
        _scopes = scopes;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        // The factory does not call this after SetScopeProvider. Direct use of
        // this logger must push onto that same provider, or ForEachScope misses it.
        var external = _scopes();
        if (external is not null)
            return external.Push(state);

        return MelScopeAdapter.BeginLocal(state);
    }

    public bool IsEnabled(MelLogLevel logLevel)
    {
        if (logLevel == MelLogLevel.None)
            return false;

        EnsureCurrent();
        var level = (int)LogLevelMapper.ToALog(logLevel);
        // Floor is the volatile minimum shared with the engine, including a
        // category rule below the global level. The category check is next.
        if (level < Floor())
            return false;

        var categoryMin = _categoryMin;
        if (categoryMin == LevelTable.UseGlobal)
            categoryMin = Global();

        return level >= categoryMin;
    }

    private int Floor()
        => _levels is null ? (int)ALogLevel.Trace : (int)_levels.MinimumLevel;

    private int Global()
        => _levels is null ? (int)ALogLevel.Trace : (int)_levels.Global;

    /// <summary>
    /// Writes one MEL call. A null formatter, a throwing formatter, or a throwing state
    /// does not escape: the event is still written and ALOG0022 is reported once per pair.
    /// </summary>
    /// <typeparam name="TState">State type MEL passed. A key/value list is read. Other types are plain text.</typeparam>
    /// <param name="logLevel">MEL level. <see cref="MelLogLevel.None"/> and levels below the floor return before any work.</param>
    /// <param name="eventId">MEL event id. Stored by MEL on the call. This bridge does not copy it onto the event.</param>
    /// <param name="state">MEL state. Null is logged as plain text when the formatter succeeds.</param>
    /// <param name="exception">Exception from the caller. Null when the call had none. Kept on the event even when formatting fails.</param>
    /// <param name="formatter">Renders the state. Null is reported as <c>ArgumentNullException</c> and is not called.</param>
    /// <remarks>
    /// Does not throw for a formatter or state failure. Allocates the property bag only when a pair is kept.
    /// Factory scopes are attached for this call only. Safe to call from several logging threads; the report set is locked.
    /// </remarks>
    public void Log<TState>(
        MelLogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var alogLevel = LogLevelMapper.ToALog(logLevel);
        var formatFailure = ReadFormatter(state, exception, formatter, out var message);

        // The level gate already returned. Factory scopes, including the
        // activity scope, are copied only for this emit and then removed.
        using var external = MelScopeAdapter.AttachExternal(_scopes());
        var bound = BindState(state);
        var failure = formatFailure ?? bound.Failure;
        if (failure is not null)
            MelFormatGuard.Report(_category, failure);

        if (failure is null && !bound.Recognized)
        {
            _engine.LogStructured(_category, alogLevel, exception, message ?? string.Empty);
            return;
        }

        var template = UsableTemplate(bound.Template);
        var text = failure is null ? message ?? string.Empty : template ?? string.Empty;
        _engine.LogCaptured(new LoggerEngine.CapturedLog
        {
            Category = _category,
            Level = alogLevel,
            Exception = exception,
            Message = text,
            MessageTemplate = template ?? string.Empty,
            Properties = bound.Properties
        });
    }

    private void EnsureCurrent()
    {
        var table = _current();
        if (_seen == table.Generation)
            return;

        _categoryMin = table.Threshold(_category);
        _seen = table.Generation;
    }

    /// <summary>
    /// Calls the formatter. A null delegate becomes an <see cref="ArgumentNullException"/>
    /// whose message is <c>The formatter is null.</c> A throw is returned, not rethrown.
    /// </summary>
    /// <typeparam name="TState">State type the formatter expects.</typeparam>
    /// <param name="state">State passed to the formatter. Not inspected here.</param>
    /// <param name="exception">Caller exception passed to the formatter. Null when the call had none.</param>
    /// <param name="formatter">MEL formatter. Null is not called.</param>
    /// <param name="message">Formatter text when the call succeeded. Null when it failed or returned null.</param>
    /// <returns>The failure, or null when the formatter returned.</returns>
    /// <remarks>
    /// Does not throw. Does not allocate except for the synthetic null-formatter exception.
    /// Safe to call from the logging thread.
    /// </remarks>
    private static Exception? ReadFormatter<TState>(
        TState state,
        Exception? exception,
        Func<TState, Exception?, string>? formatter,
        out string? message)
    {
        message = null;
        if (formatter is null)
            return new ArgumentNullException(null, NullFormatterMessage);

        try
        {
            message = formatter(state, exception);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Reads a list or a pair sequence into a <see cref="Bound"/>. Anything else is not recognized.
    /// </summary>
    /// <typeparam name="TState">State type MEL passed.</typeparam>
    /// <param name="state">MEL state. Null is not a pair list.</param>
    /// <returns>The pairs read before the first failure. <see cref="Bound.Recognized"/> is false for other types.</returns>
    /// <remarks>
    /// Does not throw. A list is bounded by <c>Count</c>. A pure enumerable stops at <see cref="MaxEnumeratedEntries"/>.
    /// Safe to call from the logging thread. The state is not modified.
    /// </remarks>
    private static Bound BindState<TState>(TState state)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            return BindList(list);

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            return BindEnumerable(pairs);

        return default;
    }

    /// <summary>
    /// Reads <paramref name="list"/> by index. <c>Count</c> or the indexer may throw; the failure is stored.
    /// </summary>
    /// <param name="list">State list. Not null. Not modified.</param>
    /// <returns>A recognized binding. Properties stop after the first 128 entries. The template search uses the whole count.</returns>
    /// <remarks>
    /// Does not throw. Allocates the property bag only when a property is kept.
    /// The loop is bounded by the count read once at the start.
    /// </remarks>
    private static Bound BindList(IReadOnlyList<KeyValuePair<string, object?>> list)
    {
        var bound = new Bound { Recognized = true };
        int count;
        try
        {
            count = list.Count;
        }
        catch (Exception ex)
        {
            bound.Failure = ex;
            return bound;
        }

        for (var i = 0; i < count; i++)
        {
            try
            {
                var pair = list[i];
                Accept(ref bound, pair.Key, pair.Value, i);
            }
            catch (Exception ex)
            {
                bound.Failure = ex;
                break;
            }
        }

        return bound;
    }

    /// <summary>
    /// Enumerates <paramref name="pairs"/> up to <see cref="MaxEnumeratedEntries"/>.
    /// A throwing enumerator is stored as the failure. Pairs already accepted stay.
    /// </summary>
    /// <param name="pairs">Pair sequence that is not read as a list. Not null. Not modified.</param>
    /// <returns>A recognized binding. The template search stops at the fixed ceiling.</returns>
    /// <remarks>
    /// Does not throw. Disposes the enumerator. Allocates the property bag only when a property is kept.
    /// </remarks>
    private static Bound BindEnumerable(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var bound = new Bound { Recognized = true };
        try
        {
            using var enumerator = pairs.GetEnumerator();
            for (var index = 0; index < MaxEnumeratedEntries; index++)
            {
                if (!enumerator.MoveNext())
                    return bound;

                var pair = enumerator.Current;
                Accept(ref bound, pair.Key, pair.Value, index);
            }
        }
        catch (Exception ex)
        {
            bound.Failure = ex;
        }

        return bound;
    }

    /// <summary>
    /// Stores one pair. The first <c>{OriginalFormat}</c> is the template. Other pairs inside the
    /// 128-entry window become properties. A leading <c>@</c> or <c>$</c> is removed from the name.
    /// </summary>
    /// <param name="bound">Binding being filled. The template flag and the bag are updated.</param>
    /// <param name="key">State key. Null and empty are skipped. <c>{OriginalFormat}</c> is the template.</param>
    /// <param name="value">State value. Stored as-is, including null. Not destructured.</param>
    /// <param name="index">Zero-based position. Properties at index 128 or later are skipped. The template is not.</param>
    /// <remarks>
    /// Throws only when the template value's <c>ToString</c> throws. The caller catches that.
    /// The first of two equal names wins (ordinal). Allocates the bag on the first kept property.
    /// </remarks>
    private static void Accept(ref Bound bound, string? key, object? value, int index)
    {
        if (string.Equals(key, OriginalFormat, StringComparison.Ordinal))
        {
            // The first template wins, even when it is null or empty. A later one is not a property.
            if (!bound.SawTemplate)
            {
                var text = value?.ToString();
                bound.SawTemplate = true;
                bound.Template = text;
            }

            return;
        }

        if (index >= MelScopeAdapter.MaxProperties)
            return;

        var name = PropertyName(key);
        if (name is null)
            return;

        bound.Properties ??= new Dictionary<string, object>(StringComparer.Ordinal);
        if (bound.Properties.ContainsKey(name))
            return;

        // Dictionary<string, object> stores a null value. The annotation is the only lie.
        bound.Properties.Add(name, value!);
    }

    /// <summary>
    /// Turns a MEL key into a property name. Braces and a format suffix are removed first.
    /// One leading <c>@</c> or <c>$</c> is then removed, unless that is the whole name.
    /// </summary>
    /// <param name="key">State key, for example <c>{@Order}</c>, <c>$Order</c>, or <c>{Elapsed:0.00}</c>. Null returns null.</param>
    /// <returns>The name, or null when the key has no name. <c>{@}</c> returns <c>@</c>.</returns>
    /// <remarks>
    /// Does not throw. Does not destructure the value. Safe to call from any thread.
    /// ALog's template parser is not used here and still does not treat <c>@</c> as an operator.
    /// </remarks>
    private static string? PropertyName(string? key)
    {
        var name = MessageTemplate.ReadPropertyName(key);
        if (string.IsNullOrEmpty(name))
            return null;

        if (name.Length > 1 && (name[0] == '@' || name[0] == '$'))
            return name.Substring(1);

        return name;
    }

    /// <summary>
    /// Returns the template when it has text, otherwise null so the event template stays empty.
    /// </summary>
    /// <param name="template">First <c>{OriginalFormat}</c> value. Null and empty mean there is no template.</param>
    /// <returns>The same string, or null when it is null or empty.</returns>
    /// <remarks>Does not throw. Does not trim. A string of spaces is a template.</remarks>
    private static string? UsableTemplate(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return null;

        return template;
    }

    /// <summary>
    /// Pairs read from one MEL state before the first failure.
    /// </summary>
    private struct Bound
    {
        /// <summary>First template text, or null when that value was null. Empty is not a usable template.</summary>
        public string? Template;

        /// <summary>True after the first <c>{OriginalFormat}</c> entry, including a null or empty one.</summary>
        public bool SawTemplate;

        /// <summary>Properties kept so far. Null when none were kept. A null entry is a present value.</summary>
        public Dictionary<string, object>? Properties;

        /// <summary>First state-access failure. Null when the read finished.</summary>
        public Exception? Failure;

        /// <summary>True when the state was a pair list or a pair sequence.</summary>
        public bool Recognized;
    }
}

/// <summary>
/// Remembers which category and exception type already produced ALOG0022.
/// The set stops at 256 pairs so it cannot grow without a bound.
/// </summary>
internal static class MelFormatGuard
{
    /// <summary>Most pairs that will ever be reported. The 257th new pair is silent.</summary>
    internal const int MaxPairs = 256;

    /// <summary>
    /// Most characters of an exception message copied into ALOG0022, after trailing whitespace and periods are removed together.
    /// A longer message is cut and marked <c> [truncated]</c>. Nothing left becomes <c>(no message)</c>.
    /// </summary>
    internal const int MaxExceptionMessageLength = 500;

    private static readonly object _gate = new();
    private static readonly HashSet<string> _seen = new(MaxPairs);

    /// <summary>
    /// Writes ALOG0022 for <paramref name="category"/> and <paramref name="error"/> the first time that pair is seen.
    /// A repeated pair, or a new pair after <see cref="MaxPairs"/>, writes nothing.
    /// </summary>
    /// <param name="category">Logger category. Null is stored as empty. Compared ordinally.</param>
    /// <param name="error">Failure that prevented formatting. Null writes nothing. The type, not the message, identifies the pair.</param>
    /// <remarks>
    /// Does not throw. The set is locked. The line is written after the lock is released so a slow target cannot deadlock a second report.
    /// Safe to call from several logging threads. Allocates the line and, once per new pair, a set entry.
    /// </remarks>
    internal static void Report(string? category, Exception? error)
    {
        if (error is null)
            return;

        var name = category ?? string.Empty;
        var key = name + "\0" + error.GetType().AssemblyQualifiedName;
        lock (_gate)
        {
            if (_seen.Contains(key) || _seen.Count >= MaxPairs)
                return;

            _seen.Add(key);
        }

        SelfLog.ReportLine(Describe(name, error));
    }

    /// <summary>
    /// Drops the remembered pairs. Tests call this so one case does not consume the 256-pair ceiling.
    /// </summary>
    /// <remarks>
    /// Does not throw. Takes the same lock as <see cref="Report"/>. Not used on the logging path.
    /// </remarks>
    internal static void Reset()
    {
        lock (_gate)
            _seen.Clear();
    }

    /// <summary>
    /// Builds the ALOG0022 sentence with the category, the exception type name, and the exception message filled in.
    /// </summary>
    /// <param name="category">Logger category. Not null. Inserted as written. Not trimmed or shortened.</param>
    /// <param name="error">Failure whose type name and message fill the sentence. Not null. The type name is not shortened.</param>
    /// <returns>
    /// One line. Trailing whitespace and periods are removed from the message first, so the sentence contributes
    /// exactly one period before <c>ALog wrote the event</c>. A message longer than <see cref="MaxExceptionMessageLength"/>
    /// is cut and ends with <c> [truncated]</c>. A message that is only whitespace and periods becomes <c>(no message)</c>.
    /// </returns>
    /// <remarks>
    /// Does not throw. Newlines in the message become spaces before the trim, so the report stays one physical line.
    /// Allocates the line. The original exception message is not retained past this call.
    /// </remarks>
    private static string Describe(string category, Exception error)
    {
        var detail = FitDetail(SafeDetail(error));
        return "A log message from category '" + category + "' could not be formatted: "
            + error.GetType().Name + ": " + detail
            + ". ALog wrote the event with its message template only. Check the state object passed to ILogger.Log.";
    }

    /// <summary>
    /// Prepares an exception message for the ALOG0022 slot.
    /// Newlines become spaces, then trailing whitespace and periods are removed together.
    /// What remains is cut to <see cref="MaxExceptionMessageLength"/> characters when it is longer,
    /// without splitting a surrogate pair, and <c> [truncated]</c> is appended.
    /// Nothing left becomes <c>(no message)</c>.
    /// </summary>
    /// <param name="detail">Exception message. Not null. May contain newlines. May be millions of characters.</param>
    /// <returns>
    /// The text that sits between the exception type and the sentence period.
    /// At most <see cref="MaxExceptionMessageLength"/> characters of the message, plus <c> [truncated]</c> when cut.
    /// <c>(no message)</c> when only whitespace and periods remain.
    /// </returns>
    /// <remarks>
    /// Does not throw. <c>Replace</c> copies the whole flattened message. The trim then walks that copy from the end,
    /// and only the kept prefix is cut to 500 characters. Safe to call from the logging thread.
    /// </remarks>
    private static string FitDetail(string detail)
    {
        var flat = detail.Replace('\r', ' ').Replace('\n', ' ');
        var end = flat.Length;
        for (var i = 0; i < flat.Length && end > 0; i++)
        {
            var ch = flat[end - 1];
            if (ch != '.' && !char.IsWhiteSpace(ch))
                break;

            end--;
        }

        if (end == 0)
            return "(no message)";

        if (end <= MaxExceptionMessageLength)
            return flat.Substring(0, end);

        var cut = MaxExceptionMessageLength;
        // A high surrogate at the cut would leave its pair on the discarded side.
        if (char.IsHighSurrogate(flat[cut - 1]))
            cut--;

        return string.Concat(flat.AsSpan(0, cut), " [truncated]".AsSpan());
    }

    /// <summary>
    /// Reads <see cref="Exception.Message"/> without letting a broken getter escape.
    /// </summary>
    /// <param name="error">Exception whose message is needed. Not null.</param>
    /// <returns>The message, or empty when the getter returns null or throws.</returns>
    /// <remarks>Does not throw.</remarks>
    private static string SafeDetail(Exception error)
    {
        try
        {
            return error.Message ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
