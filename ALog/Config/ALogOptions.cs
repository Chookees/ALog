namespace ALog.Config;

/// <summary>
/// Serializable options bound from JSON/env (<c>ALog</c> section).
/// Writer factories still require code or named presets in configuration extensions.
/// </summary>
public sealed class ALogOptions
{
    public const string SectionName = "ALog";

    public string MinimumLevel { get; set; } = "Info";
    public bool AsyncEnabled { get; set; } = true;
    public bool BackgroundQueueEnabled { get; set; }
    public int BackgroundQueueCapacity { get; set; } = 1000;
    public int BackgroundQueueBatchSize { get; set; } = 10;
    public int BackgroundQueueFlushIntervalMs { get; set; } = 100;

    /// <summary>Builtin enrichers: MachineName, ThreadId, ProcessId, Activity.</summary>
    public List<string> Enrichers { get; set; } = new();

    public ConsoleWriterOptions? Console { get; set; }
    public FileWriterOptions? File { get; set; }
    public string? Formatter { get; set; } // "plain" | "json"
    public bool JsonPretty { get; set; }
}

public sealed class ConsoleWriterOptions
{
    public bool Enabled { get; set; } = true;
    public bool UseColors { get; set; } = true;
}

public sealed class FileWriterOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "logs/app.log";
    public long? MaxFileSizeInBytes { get; set; }
}
