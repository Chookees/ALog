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

    private void Write<TState>(TState state, Exception? exception, string message, ALogLevel alogLevel)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values
            && TryOriginalFormat(values, out var original, out var props)
            && MessageTemplate.HasHoles(original))
        {
            _engine.LogStructured(_category, alogLevel, exception, original, props);
            return;
        }

        _engine.LogStructured(_category, alogLevel, exception, message);
    }

    private static bool TryOriginalFormat(
        IReadOnlyList<KeyValuePair<string, object?>> values,
        out string original,
        out object?[] props)
    {
        original = string.Empty;
        var found = false;
        var buffer = new List<object?>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var pair = values[i];
            if (pair.Key == "{OriginalFormat}")
            {
                original = pair.Value?.ToString() ?? string.Empty;
                found = true;
                continue;
            }

            buffer.Add(pair.Value);
        }

        props = buffer.ToArray();
        return found && original.Length > 0;
    }
}
