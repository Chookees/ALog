namespace ALog.Core;

using ALog.Config;
using ALog.Public.Interfaces;

public class LoggerEngine : ILogger
{
    private readonly ILogConfiguration _config;

    public LoggerEngine(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        Process(logEvent);
    }

    public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        await ProcessAsync(logEvent);
    }

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        Process(logEvent);
    }

    public async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        await ProcessAsync(logEvent);
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

    private void Process(LogEvent logEvent)
    {
        if (!ShouldLog(logEvent))
            return;

        foreach (var writer in _config.Writers)
        {
            try
            {
                if (_config.Formatter is not null)
                {
                    var formatted = _config.Formatter.Format(logEvent);
                    var formattedEvent = logEvent with { Message = formatted };
                    writer.Write(formattedEvent);
                }
                else
                {
                    writer.Write(logEvent);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LoggerEngine] Write failed: {ex}");
            }
        }
    }

    private async Task ProcessAsync(LogEvent logEvent)
    {
        if (!ShouldLog(logEvent))
            return;

        foreach (var writer in _config.Writers)
        {
            try
            {
                if (_config.Formatter is not null)
                {
                    var formatted = _config.Formatter.Format(logEvent);
                    var formattedEvent = logEvent with { Message = formatted };
                    await writer.WriteAsync(formattedEvent);
                }
                else
                {
                    await writer.WriteAsync(logEvent);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LoggerEngine] Async write failed: {ex}");
            }
        }
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
