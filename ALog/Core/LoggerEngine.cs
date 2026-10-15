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
        => Emit(CreateLogEvent(message, null, level, null, null, null));

    public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
        => await EmitAsync(CreateLogEvent(message, null, level, null, null, null));

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
        => Emit(CreateLogEvent(message, exception, level, null, null, null));

    public async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
        => await EmitAsync(CreateLogEvent(message, exception, level, null, null, null));

    public void Log(string messageTemplate, LogLevel level, params object?[] propertyValues)
        => Emit(CreateFromTemplate(messageTemplate, null, level, propertyValues, null));

    public async Task LogAsync(string messageTemplate, LogLevel level, params object?[] propertyValues)
        => await EmitAsync(CreateFromTemplate(messageTemplate, null, level, propertyValues, null));

    public void Log(Exception? exception, string messageTemplate, LogLevel level, params object?[] propertyValues)
        => Emit(CreateFromTemplate(messageTemplate, exception, level, propertyValues, null));

    /// <summary>Used by MEL adapter to pass category + template properties.</summary>
    public void LogStructured(
        string? category,
        LogLevel level,
        Exception? exception,
        string messageTemplate,
        params object?[] propertyValues)
        => Emit(CreateFromTemplate(messageTemplate, exception, level, propertyValues, category));

    private LogEvent CreateFromTemplate(
        string messageTemplate,
        Exception? exception,
        LogLevel level,
        object?[]? propertyValues,
        string? category)
    {
        try
        {
            var values = propertyValues ?? Array.Empty<object?>();
            var (rendered, props) = MessageTemplate.Format(messageTemplate, values);
            return CreateLogEvent(rendered, exception, level, props, messageTemplate, category);
        }
        catch (Exception ex)
        {
            // Per-argument failures are handled inside Format. This is the backstop
            // for a bad template so the call still produces a line.
            Internal.SelfLog.Report("MessageTemplate", ex);
            var fallback = messageTemplate ?? string.Empty;
            return CreateLogEvent(fallback, exception, level, null, messageTemplate, category);
        }
    }

    private void Emit(LogEvent logEvent)
    {
        if (!ShouldLog(logEvent)) return;

        if (_backgroundQueue != null)
            _backgroundQueue.TryEnqueue(logEvent);
        else
            _dispatcher.Dispatch(logEvent);
    }

    private async Task EmitAsync(LogEvent logEvent)
    {
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

    private LogEvent CreateLogEvent(
        string message,
        Exception? exception,
        LogLevel level,
        IReadOnlyDictionary<string, object>? properties,
        string? messageTemplate,
        string? category)
    {
        var context = Internal.ContextManager.GetContext();
        var props = properties is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(properties, StringComparer.Ordinal);

        var preliminary = new LogEvent(
            Timestamp: DateTime.UtcNow,
            Level: level,
            Message: message,
            Exception: exception,
            Context: context,
            Properties: props,
            MessageTemplate: messageTemplate,
            Category: category);

        ApplyEnrichers(_config.Enrichers, props, preliminary);
        return preliminary with { Properties = props };
    }

    private static void ApplyEnrichers(
        IReadOnlyList<ILogEnricher> enrichers,
        IDictionary<string, object> properties,
        LogEvent preliminary)
    {
        for (var i = 0; i < enrichers.Count; i++)
        {
            var enricher = enrichers[i];
            if (enricher is null)
                continue;

            try
            {
                enricher.Enrich(properties, preliminary);
            }
            catch (Exception ex)
            {
                // Skip this enricher only. Later enrichers and the event still run.
                Internal.SelfLog.Report("Enricher " + enricher.GetType().Name, ex);
            }
        }
    }

    private bool ShouldLog(LogEvent logEvent)
    {
        if (logEvent.Level < _config.MinimumLevel)
            return false;

        var filter = _config.Filter;
        if (filter is null)
            return true;

        try
        {
            return filter.ShouldLog(logEvent);
        }
        catch (Exception ex)
        {
            // Fail open: a throw is not a decision to drop the line. A filter
            // that returns false still suppresses the event.
            Internal.SelfLog.Report("Filter " + filter.GetType().Name, ex);
            return true;
        }
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
                System.Console.Error.WriteLine($"[LoggerEngine] Writer dispose failed: {ex}");
            }
        }
    }
}
