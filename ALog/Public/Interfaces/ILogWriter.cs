namespace ALog.Public.Interfaces;

public interface ILogWriter : IDisposable
{
    void Write(LogEvent logEvent);
    Task WriteAsync(LogEvent logEvent);

    /// <summary>Pushes bytes the writer is still holding. The default does nothing.</summary>
    /// <remarks>Does not throw. File writers write their buffer. Other writers ignore the call.</remarks>
    void Flush()
    {
    }
}
