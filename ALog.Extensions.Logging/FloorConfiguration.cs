namespace ALog.Extensions.Logging;

using ALog.Config;
using ALog.Public.Interfaces;

/// <summary>
/// Presents the lowest enabled level to the engine.
/// The user's global minimum stays on the inner configuration. A category rule
/// below that global is folded into <see cref="MinimumLevel"/> at init and reload
/// so the engine's gate does not drop it.
/// </summary>
internal sealed class FloorConfiguration : ILogConfiguration
{
    private readonly ILogConfiguration _inner;

    // Lowest level any category rule allows. int.MaxValue means no rule opens
    // the gate below the live global, so raising SetMinimumLevel still sticks.
    private volatile int _ruleFloor = int.MaxValue;

    public FloorConfiguration(ILogConfiguration inner)
    {
        _inner = inner;
    }

    public LogLevel Global => _inner.MinimumLevel;

    public LogLevel MinimumLevel
    {
        get
        {
            // Lower of the rule floor and the live global. A later SetMinimumLevel
            // is honored in both directions. A category rule below the global stays
            // visible so the engine does not drop it.
            var global = (int)_inner.MinimumLevel;
            var rules = _ruleFloor;
            return (LogLevel)(rules < global ? rules : global);
        }
    }

    public void SetRuleFloor(int ruleFloor) => _ruleFloor = ruleFloor;

    public IReadOnlyList<ILogWriter> Writers => _inner.Writers;
    public IReadOnlyList<ILogEnricher> Enrichers => _inner.Enrichers;
    public ILogFilter? Filter => _inner.Filter;
    public ILogFormatter? Formatter => _inner.Formatter;
    public bool AsyncEnabled => _inner.AsyncEnabled;
    public bool BackgroundQueueEnabled => _inner.BackgroundQueueEnabled;
    public int BackgroundQueueCapacity => _inner.BackgroundQueueCapacity;
    public int BackgroundQueueBatchSize => _inner.BackgroundQueueBatchSize;
    public TimeSpan BackgroundQueueFlushInterval => _inner.BackgroundQueueFlushInterval;

    /// <summary>The inner queue mode. A default on this type would hide the value the caller set.</summary>
    public QueueFullMode QueueFullMode => _inner.QueueFullMode;

    /// <summary>The inner file buffer size.</summary>
    public int FileBufferSize => _inner.FileBufferSize;

    /// <summary>The inner file flush interval.</summary>
    public TimeSpan FileFlushInterval => _inner.FileFlushInterval;

    /// <summary>True when the inner configuration named a file buffer size.</summary>
    public bool HasFileBufferSize => _inner.HasFileBufferSize;

    /// <summary>True when the inner configuration named a file flush interval.</summary>
    public bool HasFileFlushInterval => _inner.HasFileFlushInterval;

    /// <summary>The inner flush timeout.</summary>
    public TimeSpan FlushTimeout => _inner.FlushTimeout;
}
