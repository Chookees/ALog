// MEL bridge. The message text is the formatter's. Property names come from state keys.
// {OriginalFormat} is the template and is not a property. The scan stops at MelScopeAdapter.MaxProperties.
// Next: MelScopeAdapter.cs for scopes, LoggerEngine.LogCaptured for the write.
namespace ALog.Extensions.Logging;

using System.Collections.Concurrent;
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

    public void Log<TState>(
        MelLogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        var alogLevel = LogLevelMapper.ToALog(logLevel);

        // The level gate already returned. Factory scopes, including the
        // activity scope, are copied only for this emit and then removed.
        using var external = MelScopeAdapter.AttachExternal(_scopes());
        Write(state, exception, message, alogLevel);
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
    /// Writes one MEL call. Named state goes to <see cref="LoggerEngine.LogCaptured"/> with the formatter text.
    /// Anything else is logged as the formatter text alone, because there is no template to keep.
    /// </summary>
    /// <typeparam name="TState">State type MEL passed. A key/value list is read; other types are not.</typeparam>
    /// <param name="state">MEL state. Null is logged as plain text.</param>
    /// <param name="exception">Exception from MEL. Null when the call had none.</param>
    /// <param name="message">Text the formatter returned. This is the event text when a template is present.</param>
    /// <param name="alogLevel">ALog level already mapped from MEL. The engine drops it when it is below the minimum.</param>
    /// <remarks>
    /// Allocates the property bag only when a non-empty <c>{OriginalFormat}</c> is inside the scan window.
    /// Does not render the template. Safe to call from the logging thread; the engine is the same one <c>LogStructured</c> uses.
    /// </remarks>
    private void Write<TState>(TState state, Exception? exception, string message, ALogLevel alogLevel)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            var captured = TryBind(values, message, alogLevel, exception);
            if (captured.HasValue)
            {
                _engine.LogCaptured(captured.Value);
                return;
            }
        }

        _engine.LogStructured(_category, alogLevel, exception, message);
    }

    /// <summary>
    /// Binds MEL pairs by key for one event. The first <c>{OriginalFormat}</c> is the template.
    /// Every other pair becomes a property named by <see cref="MessageTemplate.ReadPropertyName"/>.
    /// </summary>
    /// <param name="values">State pairs in MEL's order. The list is not reordered. Null is rejected by the caller.</param>
    /// <param name="message">Formatter text stored as the event message, including null at runtime.</param>
    /// <param name="level">ALog level copied onto the captured event.</param>
    /// <param name="exception">Exception copied onto the captured event. Null is allowed.</param>
    /// <returns>
    /// The captured event, or null when the first template is missing or empty.
    /// A null return tells <see cref="Write{TState}"/> to keep the formatter text without named properties.
    /// </returns>
    /// <remarks>
    /// Reads at most <see cref="MelScopeAdapter.MaxProperties"/> pairs so a huge state cannot grow without a bound.
    /// The first of two equal names wins (ordinal). A null value is stored. Does not throw for a bad key.
    /// Allocates the bag. Not safe to share the returned bag across threads before the engine copies it.
    /// </remarks>
    private LoggerEngine.CapturedLog? TryBind(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        string message,
        ALogLevel level,
        Exception? exception)
    {
        var limit = values.Count;
        if (limit > MelScopeAdapter.MaxProperties)
            limit = MelScopeAdapter.MaxProperties;

        var sawFormat = false;
        string? template = null;
        Dictionary<string, object>? props = null;
        for (var i = 0; i < limit; i++)
        {
            var pair = values[i];
            if (string.Equals(pair.Key, OriginalFormat, StringComparison.Ordinal))
            {
                // The first template wins. A later one must not replace it or become a property.
                if (!sawFormat)
                {
                    sawFormat = true;
                    template = pair.Value?.ToString();
                }

                continue;
            }

            var name = MessageTemplate.ReadPropertyName(pair.Key);
            if (name is null)
                continue;

            props ??= new Dictionary<string, object>(StringComparer.Ordinal);
            if (props.ContainsKey(name))
                continue;

            // Dictionary<string, object> stores a null value. The annotation is the only lie.
            props.Add(name, pair.Value!);
        }

        if (!sawFormat || string.IsNullOrEmpty(template))
            return null;

        return new LoggerEngine.CapturedLog
        {
            Category = _category,
            Level = level,
            Exception = exception,
            Message = message,
            MessageTemplate = template,
            Properties = props
        };
    }
}
