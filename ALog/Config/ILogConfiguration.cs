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

    /// <summary>What a full background queue does. Callers that do not set it keep <see cref="QueueFullMode.Drop"/>.</summary>
    /// <remarks>A default keeps an older implementation compiling. The engine reads this once at startup.</remarks>
    ALog.Config.QueueFullMode QueueFullMode => ALog.Config.QueueFullMode.Drop;

    /// <summary>Bytes a file writer collects before it writes. The default is 64 KiB.</summary>
    /// <remarks>A default keeps an older implementation compiling. Valid startup values are 4096..16777216.</remarks>
    int FileBufferSize => LogOptionRules.DefaultBuffer;

    /// <summary>How often a file buffer is written when it is not full. The default is 1 second.</summary>
    /// <remarks>Zero writes every event. A default keeps an older implementation compiling.</remarks>
    TimeSpan FileFlushInterval => LogOptionRules.DefaultInterval;

    /// <summary>
    /// True when this configuration named a file buffer size.
    /// False leaves each <c>FileLogWriter</c> at the size it was constructed with.
    /// </summary>
    /// <remarks>A default keeps an older implementation compiling. The engine reads this once at startup.</remarks>
    bool HasFileBufferSize => false;

    /// <summary>
    /// True when this configuration named a file flush interval.
    /// False leaves each <c>FileLogWriter</c> at the interval it was constructed with.
    /// </summary>
    /// <remarks>A default keeps an older implementation compiling. The engine reads this once at startup.</remarks>
    bool HasFileFlushInterval => false;

    /// <summary>How long flush and shutdown wait. The default is 5 seconds.</summary>
    /// <remarks>Zero does not wait. A default keeps an older implementation compiling.</remarks>
    TimeSpan FlushTimeout => LogOptionRules.DefaultFlushTimeout;
}
