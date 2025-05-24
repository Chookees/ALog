namespace ALog.Public.Interfaces;

public interface ILogger
{
    void Log(string message, LogLevel level = LogLevel.Info);
    Task LogAsync(string message, LogLevel level = LogLevel.Info);

    void Log(Exception exception, string message, LogLevel level = LogLevel.Error);
    Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error);
}
