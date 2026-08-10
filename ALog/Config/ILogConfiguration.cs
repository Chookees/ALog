namespace ALog.Config;

using ALog.Public.Interfaces;

public interface ILogConfiguration
{
    IReadOnlyList<ILogWriter> Writers { get; }
    IReadOnlyList<ILogEnricher> Enrichers { get; }
    LogLevel MinimumLevel { get; }

    ILogFilter? Filter { get; }
    ILogFormatter? Formatter { get; }

    bool AsyncEnabled { get; }
    bool BackgroundQueueEnabled { get; }
    int BackgroundQueueCapacity { get; }
    int BackgroundQueueBatchSize { get; }
    TimeSpan BackgroundQueueFlushInterval { get; }
}
