// Owns one logger: filters, enrichers, and either direct writers or the single background queue.
// With a queue, writer disposal belongs to the queue so a call still inside Write is not disposed.
// Next file: ALog/Core/BackgroundLogQueue.cs.

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
    private readonly TimeProvider _clock;
    private readonly ManualResetEventSlim _disposeDone = new(false);
    private int _disposeOnce;

    /// <summary>
    /// Builds the engine on <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <param name="config">Filters, writers, and the queue switch. Not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <remarks>
    /// Does not throw for a writer failure. The queue, when enabled, starts before this returns.
    /// Safe to construct on any thread. Log after this returns.
    /// </remarks>
    public LoggerEngine(ILogConfiguration config)
        : this(config, TimeProvider.System)
    {
    }

    /// <summary>
    /// Builds the engine on <paramref name="clock"/>. Tests pass a clock they can move.
    /// Production uses <see cref="TimeProvider.System"/> through the public constructor.
    /// </summary>
    /// <param name="config">Filters, writers, and the queue switch. Not null.</param>
    /// <param name="clock">Clock for the queue shutdown, flush, and cutoff, and for a second <see cref="Dispose"/>. Not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> or <paramref name="clock"/> is null.</exception>
    /// <remarks>
    /// Does not throw for a writer failure. The clock cannot be changed after this returns.
    /// A frozen test clock does not move the queue waits until the test advances it.
    /// </remarks>
    internal LoggerEngine(ILogConfiguration config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        _config = config;
        _clock = clock;
        _levels = new MinimumLevelCache(config);

        ApplyDefaultFormatter(config);

        _dispatcher = new LogDispatcher(config.Writers, config.AsyncEnabled);

        if (_config.BackgroundQueueEnabled)
        {
            _backgroundQueue = new BackgroundLogQueue(
                config.Writers,
                new BackgroundLogQueue.QueueSettings(
                    config.BackgroundQueueCapacity,
                    config.BackgroundQueueBatchSize,
                    config.BackgroundQueueFlushInterval,
                    config.AsyncEnabled,
                    clock));
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

    /// <summary>
    /// Writes one event whose text is already final.
    /// The MEL bridge uses this so ASP.NET and EF templates are stored, not rendered again.
    /// </summary>
    /// <param name="captured">
    /// Category, level, exception, formatter text, template, and named properties.
    /// A null message is stored as empty. A null property bag means no template properties.
    /// </param>
    /// <remarks>
    /// Drops the event when the level is below the configured minimum. Does not throw for that case.
    /// Allocates the event. Safe to call from the logging thread; the engine is the same one <see cref="LogStructured"/> uses.
    /// </remarks>
    internal void LogCaptured(in CapturedLog captured)
    {
        if (!_levels.Allows(captured.Level))
            return;

        var message = captured.Message ?? string.Empty;
        WriteSync(Enrich(CreatePlain(new Seed
        {
            Message = message,
            Exception = captured.Exception,
            Level = captured.Level,
            Properties = captured.Properties,
            MessageTemplate = captured.MessageTemplate,
            Category = captured.Category
        })));
    }

    /// <summary>
    /// One MEL event whose text and names are already decided.
    /// Passed as one value so <see cref="LogCaptured"/> stays within the parameter limit.
    /// </summary>
    internal readonly struct CapturedLog
    {
        /// <summary>Logger category. Null is stored as null.</summary>
        public string? Category { get; init; }

        /// <summary>ALog level. Compared with the configured minimum.</summary>
        public LogLevel Level { get; init; }

        /// <summary>Exception attached to the event. Null when the caller had none.</summary>
        public Exception? Exception { get; init; }

        /// <summary>Text the MEL formatter returned. Null is stored as empty.</summary>
        public string? Message { get; init; }

        /// <summary>The <c>{OriginalFormat}</c> template, unchanged. Null when the caller has none.</summary>
        public string? MessageTemplate { get; init; }

        /// <summary>
        /// Properties keyed by name. Null means none. A null entry is a present value.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Properties { get; init; }
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

    /// <summary>
    /// The background queue when one was configured. Null when the queue is off.
    /// Tests read the accept and drop counts. Production callers use <see cref="FlushAsync"/> and <see cref="Dispose"/>.
    /// </summary>
    internal BackgroundLogQueue? Queue => _backgroundQueue;

    public async Task FlushAsync()
    {
        if (_backgroundQueue != null)
        {
            await _backgroundQueue.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the background queue. The queue disposes idle writers at its deadline and a writer whose call
    /// is still running only after that call returns. Without a queue, writers are disposed here.
    /// A second call waits for the first.
    /// </summary>
    /// <remarks>
    /// Does not throw. Writer dispose failures are written to the error stream.
    /// Without a queue, writers are disposed immediately, as they were before the queue deadline existed.
    /// The wait for a second call is <see cref="BackgroundLogQueue.ShutdownDeadline"/> on the engine clock.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
        {
            // Same bound as the queue. The timer is the only timeout, so a frozen test clock stays blocked.
            BackgroundLogQueue.WaitUntilSignaledOrDeadline(_clock, _disposeDone);
            return;
        }

        try
        {
            if (_backgroundQueue is not null)
            {
                _backgroundQueue.Dispose();
                return;
            }

            DisposeWriters();
        }
        finally
        {
            _disposeDone.Set();
        }
    }

    /// <summary>
    /// Disposes every configured writer. A writer that throws does not stop the others.
    /// </summary>
    /// <remarks>
    /// Does not throw. Called only when the engine has no background queue. The queue disposes its own writers.
    /// The loop is bounded by the writer list, which does not grow after the engine is built.
    /// </remarks>
    private void DisposeWriters()
    {
        var writers = _config.Writers;
        for (var i = 0; i < writers.Count; i++)
        {
            var writer = writers[i];
            if (writer is null)
                continue;

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
