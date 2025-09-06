namespace ALog.Core;

using System;
using System.Threading.Tasks;
using ALog.Config;
using ALog.Public.Interfaces;

public class LoggerEngine : ILogger, IDisposable
{
    private readonly ILogConfiguration _config;
    private readonly LogDispatcher _dispatcher;
    private readonly BackgroundLogQueue? _backgroundQueue;

    public LoggerEngine(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dispatcher = new LogDispatcher(config.Writers);
        
        if (_config.UseBackgroundQueue)
        {
            _backgroundQueue = new BackgroundLogQueue(
                config.Writers,
                config.BackgroundQueueCapacity,
                config.BackgroundQueueBatchSize,
                config.BackgroundQueueFlushInterval);
        }
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        
        if (_backgroundQueue != null)
        {
            _backgroundQueue.TryEnqueue(formatted);
        }
        else
        {
            _dispatcher.Dispatch(formatted);
        }
    }

    public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        
        if (_backgroundQueue != null)
        {
            await _backgroundQueue.EnqueueAsync(formatted);
        }
        else
        {
            await _dispatcher.DispatchAsync(formatted);
        }
    }

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        
        if (_backgroundQueue != null)
        {
            _backgroundQueue.TryEnqueue(formatted);
        }
        else
        {
            _dispatcher.Dispatch(formatted);
        }
    }

    public async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        var formatted = Format(logEvent);
        
        if (_backgroundQueue != null)
        {
            await _backgroundQueue.EnqueueAsync(formatted);
        }
        else
        {
            await _dispatcher.DispatchAsync(formatted);
        }
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

    public async Task FlushAsync()
    {
        if (_backgroundQueue != null)
        {
            await _backgroundQueue.FlushAsync();
        }
    }

    public void Dispose()
    {
        _backgroundQueue?.Dispose();
    }
}
