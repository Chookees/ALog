// Owns one logger: filters, enrichers, and either direct writers or the single background queue.
// With a queue, writer disposal belongs to the queue so a call still inside Write is not disposed.
// Next file: ALog/Core/BackgroundLogQueue.cs.

namespace ALog.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ALog.Config;
using ALog.Internal;
using ALog.Public.Interfaces;

public class LoggerEngine : ILogger, IDisposable
{
    private readonly ILogConfiguration _config;
    private readonly LoggerConfig? _fastLevels;
    private readonly LogDispatcher _dispatcher;
    private readonly BackgroundLogQueue? _backgroundQueue;
    private readonly MinimumLevelCache _levels;
    private int _disposeOnce;

    /// <summary>
    /// Builds the engine on <see cref="ProductionClock.Default"/>.
    /// </summary>
    /// <param name="config">Filters, writers, and the queue switch. Not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <remarks>
    /// Does not throw for a writer failure. The queue, when enabled, starts before this returns.
    /// Safe to construct on any thread. Log after this returns.
    /// </remarks>
    public LoggerEngine(ILogConfiguration config)
        : this(config, ProductionClock.Default)
    {
    }

    /// <summary>
    /// Builds the engine on <paramref name="clock"/>. Tests pass a clock they can move.
    /// Production uses <see cref="ProductionClock.Default"/> through the public constructor.
    /// </summary>
    /// <param name="config">Filters, writers, and the queue switch. Not null.</param>
    /// <param name="clock">Clock for the queue shutdown, flush, and cutoff. Not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> or <paramref name="clock"/> is null.</exception>
    /// <remarks>
    /// Does not throw for a writer failure. The clock cannot be changed after this returns.
    /// A frozen test clock does not move the queue waits until the test advances it.
    /// </remarks>
    internal LoggerEngine(ILogConfiguration config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        LogOptionRules.Validate(config);
        if (config is LoggerConfig concrete)
            concrete.Seal();

        _config = config;
        _fastLevels = config as LoggerConfig;
        _levels = new MinimumLevelCache(config);

        ApplyDefaultFormatter(config);
        ApplyFileStartup(config, clock);

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
                    clock)
                {
                    FullMode = config.QueueFullMode,
                    FlushTimeout = config.FlushTimeout
                });
        }
    }

    /// <summary>
    /// Gives each file writer this engine's clock, and the configuration buffer or interval when that option was set.
    /// </summary>
    /// <param name="config">The engine configuration. Not null.</param>
    /// <param name="clock">The engine clock. Not null.</param>
    /// <remarks>
    /// Does not throw. A writer that is not a <see cref="FileLogWriter"/> is skipped. The loop is bounded by the writer list.
    /// A set <see cref="ILogConfiguration.HasFileBufferSize"/> or <see cref="ILogConfiguration.HasFileFlushInterval"/>
    /// replaces the writer's constructor value. An option that was not set leaves the constructor value,
    /// including an explicit zero interval or an explicit 4096-byte buffer. The clock is always the engine clock.
    /// </remarks>
    private static void ApplyFileStartup(ILogConfiguration config, TimeProvider clock)
    {
        var writers = config.Writers;
        int? buffer = config.HasFileBufferSize ? config.FileBufferSize : null;
        TimeSpan? interval = config.HasFileFlushInterval ? config.FileFlushInterval : null;
        for (var i = 0; i < writers.Count; i++)
        {
            if (writers[i] is FileLogWriter file)
                file.ApplyStartup(buffer, interval, clock);
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

    /// <summary>
    /// Writes one structured event. The level check is first so a filtered call does no other work.
    /// </summary>
    /// <param name="category">Logger name stored on the event. Null stays null.</param>
    /// <param name="level">Compared with the configured minimum. Below it, the call returns.</param>
    /// <param name="exception">Attached exception. Null when the caller has none.</param>
    /// <param name="messageTemplate">Template text. Null is stored as an empty message.</param>
    /// <param name="propertyValues">Hole values in order. None skips template rendering.</param>
    /// <remarks>
    /// Does not throw for a filtered level. A template failure still queues a line.
    /// The level compare reads <see cref="LoggerConfig.MinimumLevelValue"/> when the configuration is a
    /// <see cref="LoggerConfig"/>, so a filtered call does not go through the interface.
    /// Safe to call from several threads. The queue, when enabled, does the file write.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LogStructured(
        string? category,
        LogLevel level,
        Exception? exception,
        string messageTemplate,
        params object?[] propertyValues)
    {
        // LoggerConfig stores the minimum as a volatile int. The interface read walks a property
        // and is too slow for a filtered call. A test double keeps the interface path.
        var floor = _fastLevels is null ? _levels.Floor : _fastLevels.MinimumLevelValue;
        if ((int)level < floor)
            return;

        LogStructuredCore(category, level, exception, messageTemplate, propertyValues);
    }

    /// <summary>Builds and writes one event that already passed the level gate.</summary>
    /// <remarks>Not inlined, so a filtered call does not have to compile this body.</remarks>
    private void LogStructuredCore(
        string? category,
        LogLevel level,
        Exception? exception,
        string messageTemplate,
        object?[]? propertyValues)
        => WriteSync(Enrich(CreateFromTemplate(messageTemplate, exception, level, propertyValues, category)));

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
            // No holes to fill. Skip the template parser and the property dictionary.
            if (propertyValues is null || propertyValues.Length == 0)
            {
                return CreatePlain(new Seed
                {
                    Message = messageTemplate ?? string.Empty,
                    Exception = exception,
                    Level = level,
                    MessageTemplate = messageTemplate,
                    Category = category
                });
            }

            var (rendered, props) = MessageTemplate.Format(messageTemplate, propertyValues);
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

    /// <summary>
    /// Shared empty bag. It is not a <see cref="Dictionary{TKey, TValue}"/>, so a caller cannot add a key to it.
    /// Enrich copies before an enricher runs because this bag is not a dictionary.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, object> NoProperties = EmptyPropertyBag.Instance;

    private static LogEvent CreatePlain(in Seed seed)
    {
        var context = ContextManager.GetContext();
        IReadOnlyDictionary<string, object> props;
        if (seed.Properties is null || seed.Properties.Count == 0)
            props = NoProperties;
        else if (seed.Properties is Dictionary<string, object> ready)
            props = ready;
        else
            props = new Dictionary<string, object>(seed.Properties, StringComparer.Ordinal);

        return new LogEvent(
            Timestamp: EventTimestamp(),
            Level: seed.Level,
            Message: seed.Message,
            Exception: seed.Exception,
            Context: context,
            Properties: props,
            MessageTemplate: seed.MessageTemplate,
            Category: seed.Category);
    }

    /// <summary>
    /// The time stored on the event. Not a shutdown or flush deadline.
    /// </summary>
    /// <returns>The current UTC time.</returns>
    /// <remarks>Does not throw. Called once per event that passes the level gate.</remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "Event timestamp is wall-clock time by design; it is log data, not a deadline.")]
    private static DateTime EventTimestamp()
        => DateTime.UtcNow;

    /// <summary>
    /// Runs enrichers. A dictionary already on the event is updated in place.
    /// The shared empty bag is copied first, because it is not a dictionary and must stay empty for every event.
    /// </summary>
    /// <param name="preliminary">Event before enrichers. Not null.</param>
    /// <returns>The same event when there are no enrichers. Otherwise the event whose property bag was updated.</returns>
    /// <remarks>
    /// Does not throw for an empty enricher list. An enricher that throws is reported by <see cref="ApplyEnrichers"/> and skipped.
    /// Safe to call from several threads when each thread has its own event. The shared empty bag is never written.
    /// </remarks>
    private LogEvent Enrich(LogEvent preliminary)
    {
        var enrichers = _config.Enrichers;
        if (enrichers.Count == 0)
            return preliminary;

        Dictionary<string, object> props;
        // The shared empty bag is not a Dictionary, so this branch never mutates it.
        if (preliminary.Properties is Dictionary<string, object> existing)
        {
            props = existing;
        }
        else
        {
            props = preliminary.Properties is null || preliminary.Properties.Count == 0
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(preliminary.Properties, StringComparer.Ordinal);
            preliminary = preliminary with { Properties = props };
        }

        ApplyEnrichers(enrichers, props, preliminary);
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

    /// <summary>
    /// Waits until the background queue is idle, then asks every writer to push bytes it is still holding.
    /// Without a queue, only the writers are flushed.
    /// </summary>
    /// <returns>A task that finishes when those flushes have returned.</returns>
    /// <remarks>
    /// Does not throw. A writer that throws is written to the error stream and the next writer still runs.
    /// The queue flush does not report ALOG0071. That line stays on dispose and shutdown.
    /// An idle queue flushes writers on this thread and does not build the async state machine.
    /// </remarks>
    public Task FlushAsync()
    {
        if (_backgroundQueue is null)
        {
            FlushWriters();
            return Task.CompletedTask;
        }

        var pending = _backgroundQueue.FlushAsync();
        if (pending.IsCompletedSuccessfully)
        {
            FlushWriters();
            return Task.CompletedTask;
        }

        return FinishFlushAsync(pending);
    }

    /// <summary>Flushes writers after the queue task. Used only when the queue was not already idle.</summary>
    /// <param name="pending">The queue flush. Not null. Not already completed.</param>
    /// <returns>A task that finishes when the writers have been asked to push their buffers.</returns>
    /// <remarks>Does not throw. A writer failure is reported inside <see cref="FlushWriters"/> and does not stop the next writer.</remarks>
    private async Task FinishFlushAsync(Task pending)
    {
        await pending.ConfigureAwait(false);
        FlushWriters();
    }

    /// <summary>
    /// Asks every configured writer to push buffered bytes. A writer that throws does not stop the others.
    /// </summary>
    /// <remarks>Does not throw. The loop is bounded by the writer list, which does not grow after the engine is built.</remarks>
    private void FlushWriters()
    {
        var writers = _config.Writers;
        for (var i = 0; i < writers.Count; i++)
        {
            var writer = writers[i];
            if (writer is null)
                continue;

            try
            {
                writer.Flush();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LoggerEngine] Writer flush failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Closes the background queue. The queue disposes idle writers at its deadline and a writer whose call
    /// is still running only after that call returns. Without a queue, writers are disposed here.
    /// A second call returns immediately and does not report again.
    /// </summary>
    /// <remarks>
    /// Does not throw. Writer dispose failures are written to the error stream.
    /// Without a queue, writers are disposed immediately, as they were before the queue deadline existed.
    /// A second call does not wait and does not emit another ALOG0071.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

        if (_backgroundQueue is not null)
        {
            _backgroundQueue.Dispose();
            return;
        }

        DisposeWriters();
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
