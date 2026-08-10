namespace ALog.Config;

using ALog.Enrichers;
using ALog.Formatters;
using ALog.Public.Interfaces;
using ALog.Writers.Console;
using ALog.Writers.File;

/// <summary>
/// Builds a <see cref="LoggerConfig"/> from <see cref="ALogOptions"/> (JSON/env-friendly).
/// </summary>
public static class LoggerConfigFactory
{
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

        if (options.Console is null || options.Console.Enabled)
        {
            config.AddWriter(new ConsoleLogWriter(useColors: options.Console?.UseColors ?? true));
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
