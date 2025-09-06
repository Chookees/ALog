namespace ALog.Config;

public interface ILogConfiguration
{
    IReadOnlyList<ILogWriter> Writers { get; }
    LogLevel MinimumLevel { get; }

    ILogFilter? Filter { get; }
    ILogFormatter? Formatter { get; }

    bool AsyncEnabled { get; }
    bool UseBackgroundQueue { get; }
    int BackgroundQueueCapacity { get; }
    int BackgroundQueueBatchSize { get; }
    TimeSpan BackgroundQueueFlushInterval { get; }
}