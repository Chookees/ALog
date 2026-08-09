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
    private bool _disposed;

    public LoggerEngine(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        ApplyDefaultFormatter(config);

        _dispatcher = new LogDispatcher(config.Writers, config.AsyncEnabled);

        if (_config.BackgroundQueueEnabled)
        {
            _backgroundQueue = new BackgroundLogQueue(
                config.Writers,
                config.BackgroundQueueCapacity,
                config.BackgroundQueueBatchSize,
                config.BackgroundQueueFlushInterval,
                config.AsyncEnabled);
        }
    }

    private static void ApplyDefaultFormatter(ILogConfiguration config)
    {
        if (config.Formatter is null)
            return;

        foreach (var writer in config.Writers)
        {
            if (writer is IFormattableLogWriter formattable && formattable.Formatter is null)
            {
                formattable.Formatter = config.Formatter;
            }
        }
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        if (_backgroundQueue != null)
        {
            _backgroundQueue.TryEnqueue(logEvent);
        }
        else
        {
            _dispatcher.Dispatch(logEvent);
        }
    }

    public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        var logEvent = CreateLogEvent(message, null, level);
        if (!ShouldLog(logEvent)) return;

        if (_backgroundQueue != null)
        {
            await _backgroundQueue.EnqueueAsync(logEvent);
        }
        else if (!_config.AsyncEnabled)
        {
            _dispatcher.Dispatch(logEvent);
        }
        else
        {
            await _dispatcher.DispatchAsync(logEvent);
        }
    }

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        if (_backgroundQueue != null)
        {
            _backgroundQueue.TryEnqueue(logEvent);
        }
        else
        {
            _dispatcher.Dispatch(logEvent);
        }
    }

    public async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        var logEvent = CreateLogEvent(message, exception, level);
        if (!ShouldLog(logEvent)) return;

        if (_backgroundQueue != null)
        {
            await _backgroundQueue.EnqueueAsync(logEvent);
        }
        else if (!_config.AsyncEnabled)
        {
            _dispatcher.Dispatch(logEvent);
        }
        else
        {
            await _dispatcher.DispatchAsync(logEvent);
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
        if (_disposed)
            return;

        _disposed = true;
        _backgroundQueue?.Dispose();

        foreach (var writer in _config.Writers)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LoggerEngine] Writer dispose failed: {ex}");
            }
        }
    }
}
