namespace ALog.Config;

public interface ILogConfiguration
{
    IReadOnlyList<ILogWriter> Writers { get; }
    LogLevel MinimumLevel { get; }

    ILogFilter? Filter { get; }
    ILogFormatter? Formatter { get; }

    bool AsyncEnabled { get; }
}