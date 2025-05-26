namespace ALog.Core;

using System;
using System.Threading.Tasks;
using ALog.Config;
using ALog.Public.Interfaces;

public class LoggerEngine : ILogger
{
    private readonly ILogConfiguration _config;
    private readonly LogDispatcher _dispatcher;

    public LoggerEngine(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dispatcher = new LogDispatcher(config.Writers);
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        _dispatcher.Dispatch(formatted);
    }

    public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        await _dispatcher.DispatchAsync(formatted);
    }

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        _dispatcher.Dispatch(formatted);
    }

    public async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        await _dispatcher.DispatchAsync(formatted);
    }

    private LogEvent CreateLogEvent(string message, Exception? exception, LogLevel level)
    {
        var context = Internal.ContextManager.GetContext();

        return new LogEvent(
            Timestamp: DateTime.UtcNow,
            Level: level,
            Message: message,
            Exception: exception,
            Context: context
        );
    }

    private LogEvent Format(LogEvent logEvent)
    {
        if (_config.Formatter is null)
            return logEvent;

        var formatted = _config.Formatter.Format(logEvent);
        return logEvent with { Message = formatted };
    }

    private bool ShouldLog(LogEvent logEvent)
    {
        if (logEvent.Level < _config.MinimumLevel)
            return false;

        if (_config.Filter is not null && !_config.Filter.ShouldLog(logEvent))
            return false;

        return true;
    }
}
