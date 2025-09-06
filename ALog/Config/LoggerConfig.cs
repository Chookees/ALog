namespace ALog.Config;

public class LoggerConfig : ILogConfiguration
{
    private readonly List<ILogWriter> _writers = new();

    public IReadOnlyList<ILogWriter> Writers => _writers.AsReadOnly();
    public LogLevel MinimumLevel { get; private set; } = LogLevel.Info;

    public ILogFilter? Filter { get; private set; }
    public ILogFormatter? Formatter { get; private set; }

    public bool AsyncEnabled { get; private set; } = true;
    public bool UseBackgroundQueue { get; private set; } = false;
    public int BackgroundQueueCapacity { get; private set; } = 1000;
    public int BackgroundQueueBatchSize { get; private set; } = 10;
    public TimeSpan BackgroundQueueFlushInterval { get; private set; } = TimeSpan.FromMilliseconds(100);

    public LoggerConfig AddWriter(ILogWriter writer)
    {
        _writers.Add(writer);
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

    public LoggerConfig UseBackgroundQueue(bool enabled, int capacity = 1000, int batchSize = 10, TimeSpan? flushInterval = null)
    {
        UseBackgroundQueue = enabled;
        BackgroundQueueCapacity = capacity;
        BackgroundQueueBatchSize = batchSize;
        BackgroundQueueFlushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        return this;
    }
}
