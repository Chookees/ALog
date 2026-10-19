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

/// <summary>
/// Options for the console writer created by <see cref="LoggerConfigFactory.FromOptions"/>.
/// An explicit <c>AddWriter(new ConsoleLogWriter(...))</c> does not read this type.
/// </summary>
/// <remarks>Read once at startup. Changes while the app runs have no effect; restart the app.</remarks>
public sealed class ConsoleWriterOptions
{
    /// <summary>
    /// When true, <see cref="LoggerConfigFactory.FromOptions"/> adds one console writer.
    /// The default is false, so a missing <c>ALog:Console</c> section does not print.
    /// Set <c>ALog:Console:Enabled</c> to true to opt in (DC-AB2, A2a-61).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true, the console writer colors the line by level.
    /// The default is true. It is used only when <see cref="Enabled"/> is true.
    /// </summary>
    public bool UseColors { get; set; } = true;
}

/// <summary>
/// Options for the file writer created by <see cref="LoggerConfigFactory.FromOptions"/>.
/// An explicit <c>AddWriter(new FileLogWriter(...))</c> does not read this type.
/// </summary>
/// <remarks>Read once at startup. Changes while the app runs have no effect; restart the app.</remarks>
public sealed class FileWriterOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "logs/app.log";
    public long? MaxFileSizeInBytes { get; set; }
}
