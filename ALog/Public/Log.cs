namespace ALog;

using ALog.Config;
using ALog.Core;
using ALog.Internal;
using ALog.Public.Interfaces;

public static class Log
{
    private static ILogger? _logger;

    public static void Init(LoggerConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        if (_logger is IDisposable previous)
        {
            previous.Dispose();
        }

        _logger = new LoggerEngine(config);
    }

    public static void Init(ALogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Init(LoggerConfigFactory.FromOptions(options));
    }

    // Main – short: Log("...")
    public static void Invoke(string message, LogLevel level = LogLevel.Info)
    {
        EnsureInitialized();
        _logger!.Log(message, level);
    }

    public static async Task WriteAsync(string message, LogLevel level = LogLevel.Info)
    {
        EnsureInitialized();
        await _logger!.LogAsync(message, level);
    }

    public static void Invoke(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        EnsureInitialized();
        _logger!.Log(exception, message, level);
    }

    public static async Task WriteAsync(Exception exception, string message, LogLevel level = LogLevel.Error)
    {
        EnsureInitialized();
        await _logger!.LogAsync(exception, message, level);
    }

    public static void Write(string message, LogLevel level = LogLevel.Info) => Invoke(message, level);
    public static void Write(Exception exception, string message, LogLevel level = LogLevel.Error) => Invoke(exception, message, level);

    /// <summary>Structured template: <c>Log.Write(LogLevel.Info, "User {UserId} logged in", userId)</c>.</summary>
    public static void Write(LogLevel level, string messageTemplate, params object?[] propertyValues)
    {
        EnsureInitialized();
        _logger!.Log(messageTemplate, level, propertyValues);
    }

    public static async Task WriteAsync(LogLevel level, string messageTemplate, params object?[] propertyValues)
    {
        EnsureInitialized();
        await _logger!.LogAsync(messageTemplate, level, propertyValues);
    }

    public static void Write(Exception? exception, LogLevel level, string messageTemplate, params object?[] propertyValues)
    {
        EnsureInitialized();
        _logger!.Log(exception, messageTemplate, level, propertyValues);
    }

    private static void EnsureInitialized()
    {
        if (_logger == null)
            throw new InvalidOperationException("Log.Init(...) must be called before using Log.");
    }

    public static void ClearContext()
    {
        ContextManager.Clear();
    }

    public static ILogScope BeginScope(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        var properties = new KeyValuePair<string, object?>[]
        {
            new(key, value)
        };
        return ScopeContext.Begin(properties);
    }

    public static async Task FlushAsync()
    {
        EnsureInitialized();
        if (_logger is LoggerEngine engine)
        {
            await engine.FlushAsync();
        }
    }

    public static void Shutdown()
    {
        if (_logger is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _logger = null;
    }

    public static void Reset() => Shutdown();
}
