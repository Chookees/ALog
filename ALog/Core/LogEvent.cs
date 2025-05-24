namespace ALog.Core;

public record LogEvent(
    DateTime Timestamp,
    LogLevel Level,
    string Message,
    Exception? Exception = null,
    IReadOnlyDictionary<string, object>? Context = null
);
