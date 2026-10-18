namespace ALog.Config;

using ALog.Enrichers;
using ALog.Formatters;
using ALog.Public.Interfaces;
using ALog.Writers.Console;
using ALog.Writers.File;

/// <summary>
/// Builds a <see cref="LoggerConfig"/> from <see cref="ALogOptions"/> (JSON/env-friendly).
/// A console writer is added only when <see cref="ConsoleWriterOptions.Enabled"/> is true (A2a-61).
/// </summary>
public static class LoggerConfigFactory
{
    /// <summary>
    /// Builds writers, enrichers, and the minimum level from bound options.
    /// A missing console section does not add a console writer. File is added only when enabled.
    /// </summary>
    /// <param name="options">Bound options. Null is rejected. <see cref="ALogOptions.Console"/> may be null.</param>
    /// <returns>A configuration the caller owns. It has no console writer unless console options are enabled.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the level name or an enricher name is unknown.</exception>
    /// <remarks>
    /// Does not write a log line and does not touch an explicit <c>AddWriter</c> call.
    /// Allocates the configuration and any writers it adds. Safe to call from any thread; the result is not shared.
    /// </remarks>
    public static LoggerConfig FromOptions(ALogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new LoggerConfig()
            .SetMinimumLevel(ParseLevel(options.MinimumLevel))
            .UseAsync(options.AsyncEnabled)
            .UseBackgroundQueue(
                options.BackgroundQueueEnabled,
                options.BackgroundQueueCapacity,
                options.BackgroundQueueBatchSize,
                TimeSpan.FromMilliseconds(Math.Max(1, options.BackgroundQueueFlushIntervalMs)));

        foreach (var enricher in CreateEnrichers(options.Enrichers))
            config.AddEnricher(enricher);

        if (string.Equals(options.Formatter, "json", StringComparison.OrdinalIgnoreCase))
            config.SetFormatter(new JsonFormatter(options.JsonPretty));
        else if (string.Equals(options.Formatter, "plain", StringComparison.OrdinalIgnoreCase))
            config.SetFormatter(new PlainTextFormatter());

        // DC-AB2: null (section missing) and Enabled false both skip the console writer.
        var console = options.Console;
        if (console is not null && console.Enabled)
        {
            config.AddWriter(new ConsoleLogWriter(useColors: console.UseColors));
        }

        if (options.File is { Enabled: true })
        {
            config.AddWriter(new FileLogWriter(
                options.File.Path,
                maxFileSizeInBytes: options.File.MaxFileSizeInBytes));
        }

        return config;
    }

    public static IEnumerable<ILogEnricher> CreateEnrichers(IEnumerable<string>? names)
    {
        if (names is null)
            yield break;

        foreach (var name in names)
        {
            yield return name.Trim().ToLowerInvariant() switch
            {
                "machinename" => new MachineNameEnricher(),
                "threadid" => new ThreadIdEnricher(),
                "processid" => new ProcessIdEnricher(),
                "activity" => new ActivityEnricher(),
                _ => throw new ArgumentException($"Unknown enricher '{name}'. Use MachineName, ThreadId, ProcessId, Activity.")
            };
        }
    }

    public static LogLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return LogLevel.Info;

        if (Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed))
            return parsed;

        // MEL aliases
        return level.Trim().ToLowerInvariant() switch
        {
            "information" => LogLevel.Info,
            "warning" => LogLevel.Warn,
            "critical" => LogLevel.Fatal,
            "none" => LogLevel.Fatal,
            _ => throw new ArgumentException($"Unknown log level '{level}'.")
        };
    }
}
