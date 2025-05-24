namespace ALog;

using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;

public static class Log
{
    private static ILogger? _logger;

    public static void Init(LoggerConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        _logger = new LoggerEngine(config);
    }

    // 🔹 Hauptmethode – Kurzform: Log("...")
    public static void Invoke(string message, LogLevel level = LogLevel.Info)
    {
        EnsureInitialized();
        _logger!.Log(message, level);
    }

    public static async Task LogAsync(string message, LogLevel level = LogLevel.Info)
    {
        EnsureInitialized();
        await _logger!.LogAsync(message, level);
    }

    public static void Invoke(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        EnsureInitialized();
        _logger!.Log(exception, message, level);
    }

    public static async Task LogAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        EnsureInitialized();
        await _logger!.LogAsync(exception, message, level);
    }

    // Alias: erlaubt einfaches "Write(...)"
    public static void Write(string message, LogLevel level = LogLevel.Info) => Invoke(message, level);
    public static void Write(Exception exception, string message, LogLevel level = LogLevel.Error) => Invoke(exception, message, level);

    // Sicherheit: init vergessen?
    private static void EnsureInitialized()
    {
        if (_logger == null)
            throw new InvalidOperationException("Log.Init(...) must be called before using Log.");
    }

    public static void WithContext(string key, object value)
    {
        Internal.ContextManager.Set(key, value);
    }

    public static void ClearContext()
    {
        Internal.ContextManager.Clear();
    }
}
