namespace ALog.Core;

using System;
using System.Threading.Tasks;
using ALog.Config;
using ALog.Internal;
using ALog.Public.Interfaces;

public class LoggerEngine : ILogger, IDisposable
{
    private readonly ILogConfiguration _config;
    private readonly LogDispatcher _dispatcher;
    private readonly BackgroundLogQueue? _backgroundQueue;
    private readonly MinimumLevelCache _levels;
    private bool _disposed;

    public LoggerEngine(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _levels = new MinimumLevelCache(config);

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
        if (!_levels.Allows(level))
            return;

        WriteSync(Enrich(CreatePlain(Plain(message, null, level))));
    }

    public Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        if (!_levels.Allows(level))
            return Task.CompletedTask;

        return WriteAsync(Enrich(CreatePlain(Plain(message, null, level))));
    }

    public void Log(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        if (!_levels.Allows(level))
            return;

        WriteSync(Enrich(CreatePlain(Plain(message, exception, level))));
    }

    public Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        if (!_levels.Allows(level))
            return Task.CompletedTask;

        return WriteAsync(Enrich(CreatePlain(Plain(message, exception, level))));
    }

    public void Log(string messageTemplate, LogLevel level, params object?[] propertyValues)
    {
        if (!_levels.Allows(level))
            return;

        WriteSync(Enrich(CreateFromTemplate(messageTemplate, null, level, propertyValues, null)));
    }

    public Task LogAsync(string messageTemplate, LogLevel level, params object?[] propertyValues)
    {
        if (!_levels.Allows(level))
            return Task.CompletedTask;

        return WriteAsync(Enrich(CreateFromTemplate(messageTemplate, null, level, propertyValues, null)));
    }

    public void Log(Exception? exception, string messageTemplate, LogLevel level, params object?[] propertyValues)
    {
        if (!_levels.Allows(level))
            return;

        WriteSync(Enrich(CreateFromTemplate(messageTemplate, exception, level, propertyValues, null)));
    }

    /// <summary>Used by MEL adapter to pass category + template properties.</summary>
    public void LogStructured(
        string? category,
        LogLevel level,
        Exception? exception,
        string messageTemplate,
        params object?[] propertyValues)
    {
        if (!_levels.Allows(level))
            return;

        WriteSync(Enrich(CreateFromTemplate(messageTemplate, exception, level, propertyValues, category)));
    }

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
            return CreatePlain(new Seed
            {
                Message = rendered,
                Exception = exception,
                Level = level,
                Properties = props,
                MessageTemplate = messageTemplate,
                Category = category
            });
        }
        catch (Exception ex)
        {
            // Per-argument failures are handled inside Format. This is the backstop
            // for a bad template so the call still produces a line.
            SelfLog.Report("MessageTemplate", ex);
            var fallback = messageTemplate ?? string.Empty;
            return CreatePlain(new Seed
            {
                Message = fallback,
                Exception = exception,
                Level = level,
                MessageTemplate = messageTemplate,
                Category = category
            });
        }
    }

    private static Seed Plain(string message, Exception? exception, LogLevel level)
        => new()
        {
            Message = message,
            Exception = exception,
            Level = level
        };

    private void WriteSync(LogEvent logEvent)
    {
        if (!PassesFilter(logEvent))
            return;

        if (_backgroundQueue != null)
            _backgroundQueue.TryEnqueue(logEvent);
        else
            _dispatcher.Dispatch(logEvent);
    }

    private async Task WriteAsync(LogEvent logEvent)
    {
        if (!PassesFilter(logEvent))
            return;

        if (_backgroundQueue != null)
        {
            await _backgroundQueue.EnqueueAsync(logEvent).ConfigureAwait(false);
        }
        else if (!_config.AsyncEnabled)
        {
            _dispatcher.Dispatch(logEvent);
        }
        else
        {
            await _dispatcher.DispatchAsync(logEvent).ConfigureAwait(false);
        }
    }

    private static LogEvent CreatePlain(in Seed seed)
    {
        var context = ContextManager.GetContext();
        var props = seed.Properties is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            : new Dictionary<string, object>(seed.Properties, StringComparer.Ordinal);

        return new LogEvent(
            Timestamp: DateTime.UtcNow,
            Level: seed.Level,
            Message: seed.Message,
            Exception: seed.Exception,
            Context: context,
            Properties: props,
            MessageTemplate: seed.MessageTemplate,
            Category: seed.Category);
    }

    private LogEvent Enrich(LogEvent preliminary)
    {
        if (preliminary.Properties is not Dictionary<string, object> props)
        {
            props = preliminary.Properties is null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(preliminary.Properties, StringComparer.Ordinal);
            preliminary = preliminary with { Properties = props };
        }

        ApplyEnrichers(_config.Enrichers, props, preliminary);
        return preliminary;
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
                SelfLog.Report("Enricher " + enricher.GetType().Name, ex);
            }
        }
    }

    private bool PassesFilter(LogEvent logEvent)
    {
        var filter = _config.Filter;
        if (filter is null)
            return true;

        try
        {
            return filter.ShouldLog(logEvent);
        }
        catch (Exception ex)
        {
            // Fail closed. A broken filter may be the PII filter; logging the
            // event would leak the data the filter was meant to hold back.
            SelfLog.Report("Filter " + filter.GetType().Name, ex);
            return false;
        }
    }

    public async Task FlushAsync()
    {
        if (_backgroundQueue != null)
        {
            await _backgroundQueue.FlushAsync().ConfigureAwait(false);
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

    /// <summary>Arguments for one event. Kept together so log methods stay within the parameter limit.</summary>
    private readonly struct Seed
    {
        public string Message { get; init; }
        public Exception? Exception { get; init; }
        public LogLevel Level { get; init; }
        public IReadOnlyDictionary<string, object>? Properties { get; init; }
        public string? MessageTemplate { get; init; }
        public string? Category { get; init; }
    }
}
