namespace ALog.Internal;

using ALog.Config;

/// <summary>
/// Lowest level any current rule will accept.
/// The value is a volatile int on the configuration (global minimum, or a lower
/// category rule folded in at init and when filters reload). This cache copies
/// that int so the log path does not walk the rules again.
/// </summary>
internal sealed class MinimumLevelCache
{
    private readonly ILogConfiguration _config;
    private volatile int _cached;

    public MinimumLevelCache(ILogConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cached = (int)config.MinimumLevel;
    }

    public bool Allows(LogLevel level)
    {
        var configured = (int)_config.MinimumLevel;
        var cached = _cached;
        if (configured != cached)
        {
            _cached = configured;
            cached = configured;
        }

        return (int)level >= cached;
    }
}
