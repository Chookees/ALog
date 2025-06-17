
namespace ALog.Filter;

public class MinimumLevelFilter : ILogFilter
{
    private readonly LogLevel _minimumLevel;

    public MinimumLevelFilter(LogLevel minimumLevel)
    {
        _minimumLevel = minimumLevel;
    }

    public bool ShouldLog(LogEvent logEvent)
    {
        return logEvent.Level >= _minimumLevel;
    }
}
