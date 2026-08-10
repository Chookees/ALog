namespace ALog.Core;

/// <summary>
/// Structured log event with optional message template and property bag.
/// </summary>
public record LogEvent(
    DateTime Timestamp,
    LogLevel Level,
    string Message,
    Exception? Exception = null,
    IReadOnlyDictionary<string, object>? Context = null,
    IReadOnlyDictionary<string, object>? Properties = null,
    string? MessageTemplate = null,
    string? Category = null);
