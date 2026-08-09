namespace ALog.Public.Interfaces;

public interface ILogWriter : IDisposable
{
    void Write(LogEvent logEvent);
    Task WriteAsync(LogEvent logEvent);
}
