namespace ALog.Extensions.Logging;

using System.Collections.Concurrent;
using ALog.Config;
using ALog.Core;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

/// <summary>
/// Bridges Microsoft.Extensions.Logging to a shared <see cref="LoggerEngine"/>.
/// </summary>
public sealed class ALogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly LoggerEngine _engine;
    private readonly ConcurrentDictionary<string, ALogLogger> _loggers = new(StringComparer.Ordinal);
    private IExternalScopeProvider? _scopeProvider;
    private readonly bool _ownsEngine;

    public ALogLoggerProvider(LoggerConfig config, bool ownsEngine = true)
        : this(new LoggerEngine(config), ownsEngine)
    {
    }

    public ALogLoggerProvider(LoggerEngine engine, bool ownsEngine = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new ALogLogger(name, _engine, () => _scopeProvider));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
        if (_ownsEngine)
            _engine.Dispose();
    }
}

internal sealed class ALogLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly string _category;
    private readonly LoggerEngine _engine;
    private readonly Func<IExternalScopeProvider?> _scopeProvider;

    public ALogLogger(string category, LoggerEngine engine, Func<IExternalScopeProvider?> scopeProvider)
    {
        _category = category;
        _engine = engine;
        _scopeProvider = scopeProvider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _scopeProvider()?.Push(state) ?? NullScope.Instance;

    public bool IsEnabled(MelLogLevel logLevel) => logLevel != MelLogLevel.None;

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

        // Prefer original format string when available (MEL formatted log values).
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            string? original = null;
            var props = new List<object?>();
            foreach (var pair in values)
            {
                if (pair.Key == "{OriginalFormat}")
                {
                    original = pair.Value?.ToString();
                    continue;
                }

                props.Add(pair.Value);
            }

            if (!string.IsNullOrEmpty(original) && MessageTemplate.HasHoles(original))
            {
                _engine.LogStructured(_category, alogLevel, exception, original, props.ToArray());
                return;
            }
        }

        _engine.LogStructured(_category, alogLevel, exception, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
