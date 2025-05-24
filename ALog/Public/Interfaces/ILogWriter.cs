namespace ALog.Public.Interfaces;

public interface ILogWriter
{
    void Write(LogEvent logEvent);
    Task WriteAsync(LogEvent logEvent);
}