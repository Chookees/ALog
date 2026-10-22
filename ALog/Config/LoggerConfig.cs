namespace ALog.Config;

using ALog.Public.Interfaces;

public class LoggerConfig : ILogConfiguration
{
    private readonly List<ILogWriter> _writers = new();
    private readonly List<ILogEnricher> _enrichers = new();

    public IReadOnlyList<ILogWriter> Writers => _writers.AsReadOnly();
    public IReadOnlyList<ILogEnricher> Enrichers => _enrichers.AsReadOnly();

    // Volatile so a reload on another thread is visible to the level gate.
    private volatile int _minimumLevel = (int)LogLevel.Info;

    public LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        private set => _minimumLevel = (int)value;
    }

    public ILogFilter? Filter { get; private set; }
    public ILogFormatter? Formatter { get; private set; }

    public bool AsyncEnabled { get; private set; } = true;
    public bool BackgroundQueueEnabled { get; private set; } = false;
    public int BackgroundQueueCapacity { get; private set; } = 1000;
    public int BackgroundQueueBatchSize { get; private set; } = 10;
    public TimeSpan BackgroundQueueFlushInterval { get; private set; } = TimeSpan.FromMilliseconds(100);

    private QueueFullMode _queueFullMode = QueueFullMode.Drop;
    private int _fileBufferSize = LogOptionRules.DefaultBuffer;
    private TimeSpan _fileFlushInterval = LogOptionRules.DefaultInterval;
    private TimeSpan _flushTimeout = LogOptionRules.DefaultFlushTimeout;
    private int _sealed;
    private int _reportedMode;
    private int _reportedBuffer;
    private int _reportedInterval;
    private int _reportedTimeout;

    /// <summary>What a full background queue does. <see cref="QueueFullMode.Drop"/> until <see cref="SetQueueFullMode"/>.</summary>
    public QueueFullMode QueueFullMode => _queueFullMode;

    /// <summary>Bytes a file writer collects before it writes.</summary>
    public int FileBufferSize => _fileBufferSize;

    /// <summary>How often a file buffer is written when it is not full.</summary>
    public TimeSpan FileFlushInterval => _fileFlushInterval;

    /// <summary>How long flush and shutdown wait.</summary>
    public TimeSpan FlushTimeout => _flushTimeout;

    public LoggerConfig AddWriter(ILogWriter writer)
    {
        _writers.Add(writer);
        return this;
    }

    public LoggerConfig AddEnricher(ILogEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        _enrichers.Add(enricher);
        return this;
    }

    public LoggerConfig SetMinimumLevel(LogLevel level)
    {
        MinimumLevel = level;
        return this;
    }

    public LoggerConfig SetFilter(ILogFilter filter)
    {
        Filter = filter;
        return this;
    }

    public LoggerConfig SetFormatter(ILogFormatter formatter)
    {
        Formatter = formatter;
        return this;
    }

    public LoggerConfig UseAsync(bool enabled)
    {
        AsyncEnabled = enabled;
        return this;
    }

    /// <summary>
    /// Turns the background queue on or off and stores its capacity.
    /// </summary>
    /// <param name="enabled">True starts one queue when the engine is built.</param>
    /// <param name="capacity">Most events waiting. Must be 1..100000 even when <paramref name="enabled"/> is false.</param>
    /// <param name="batchSize">Most events taken per wake. Not range-checked here.</param>
    /// <param name="flushInterval">Idle wait. Null means 100 ms.</param>
    /// <returns>This configuration.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="capacity"/> is outside 1..100000.</exception>
    /// <remarks>
    /// Does not start the queue. A call after <see cref="Seal"/> still updates these fields.
    /// The four file and queue-mode setters do not.
    /// </remarks>
    public LoggerConfig UseBackgroundQueue(bool enabled, int capacity = 1000, int batchSize = 10, TimeSpan? flushInterval = null)
    {
        LogOptionRules.RequireCapacity(capacity, LogOptionRules.PathOf("BackgroundQueueCapacity"));
        BackgroundQueueEnabled = enabled;
        BackgroundQueueCapacity = capacity;
        BackgroundQueueBatchSize = batchSize;
        BackgroundQueueFlushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        return this;
    }

    /// <summary>Stores <paramref name="mode"/> when it is Drop or Block and the configuration is not sealed.</summary>
    /// <param name="mode">Drop or Block. Any other cast is rejected.</param>
    /// <returns>This configuration. The previous mode stays when the call is ignored.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="mode"/> is not Drop or Block and the configuration is not sealed.</exception>
    /// <remarks>After <see cref="Seal"/>, reports ALOG0105 once and does not change the stored mode.</remarks>
    public LoggerConfig SetQueueFullMode(QueueFullMode mode)
    {
        if (RejectedBySeal(ref _reportedMode, "QueueFullMode"))
            return this;

        _queueFullMode = LogOptionRules.RequireMode(mode);
        return this;
    }

    /// <summary>Stores a file buffer size inside 4096..16777216.</summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>This configuration. The previous size stays when the call is ignored.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="bytes"/> is outside the range and the configuration is not sealed.</exception>
    /// <remarks>After <see cref="Seal"/>, reports ALOG0105 once and does not change the stored size.</remarks>
    public LoggerConfig SetFileBufferSize(int bytes)
    {
        if (RejectedBySeal(ref _reportedBuffer, "FileBufferSize"))
            return this;

        LogOptionRules.RequireBuffer(bytes, LogOptionRules.PathOf("FileBufferSize"));
        _fileBufferSize = bytes;
        return this;
    }

    /// <summary>Stores a file flush interval inside 0..60 seconds.</summary>
    /// <param name="interval">The interval. Zero writes every event.</param>
    /// <returns>This configuration. The previous interval stays when the call is ignored.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="interval"/> is outside the range and the configuration is not sealed.</exception>
    /// <remarks>After <see cref="Seal"/>, reports ALOG0105 once and does not change the stored interval.</remarks>
    public LoggerConfig SetFileFlushInterval(TimeSpan interval)
    {
        if (RejectedBySeal(ref _reportedInterval, "FileFlushInterval"))
            return this;

        LogOptionRules.RequireDuration(interval, LogOptionRules.PathOf("FileFlushInterval"));
        _fileFlushInterval = interval;
        return this;
    }

    /// <summary>Stores a flush timeout inside 0..60 seconds.</summary>
    /// <param name="timeout">How long flush and shutdown wait. Zero does not wait.</param>
    /// <returns>This configuration. The previous timeout stays when the call is ignored.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="timeout"/> is outside the range and the configuration is not sealed.</exception>
    /// <remarks>After <see cref="Seal"/>, reports ALOG0105 once and does not change the stored timeout.</remarks>
    public LoggerConfig SetFlushTimeout(TimeSpan timeout)
    {
        if (RejectedBySeal(ref _reportedTimeout, "FlushTimeout"))
            return this;

        LogOptionRules.RequireDuration(timeout, LogOptionRules.PathOf("FlushTimeout"));
        _flushTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Stops later changes to the queue mode, the file buffer, the flush interval, and the flush timeout.
    /// </summary>
    /// <remarks>
    /// Does not throw. A second call does nothing. <see cref="UseBackgroundQueue"/> stays open.
    /// Called from the engine and from the Microsoft.Extensions.Logging provider before the first event.
    /// </remarks>
    internal void Seal()
    {
        Volatile.Write(ref _sealed, 1);
    }

    /// <summary>
    /// Reports ALOG0105 once per option after <see cref="Seal"/> and tells the setter to keep the old value.
    /// </summary>
    /// <param name="reported">Zero until the line for this option has been written.</param>
    /// <param name="option">QueueFullMode, FileBufferSize, FileFlushInterval, or FlushTimeout.</param>
    /// <returns>True when the configuration is sealed. False when the setter should store the value.</returns>
    /// <remarks>Does not throw. The line is one sentence. A second change of the same option does not write it again.</remarks>
    private bool RejectedBySeal(ref int reported, string option)
    {
        if (Volatile.Read(ref _sealed) == 0)
            return false;

        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            ALog.SelfLog.ReportLine(
                "ALOG0105: " + option + " is read only at startup. The new value was ignored. Restart the app to apply it.");
        }

        return true;
    }
}
