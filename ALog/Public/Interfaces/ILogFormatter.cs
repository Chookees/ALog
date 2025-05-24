namespace ALog.Public.Interfaces;

public interface ILogFormatter
{
    string Format(LogEvent logEvent);
}