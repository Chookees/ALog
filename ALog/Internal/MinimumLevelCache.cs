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

    public bool Allows(LogLevel level) => (int)level >= Floor;

    /// <summary>
    /// The configured minimum as an int. A log call compares against this and returns before any other work.
    /// </summary>
    /// <remarks>
    /// Reads the configuration each call so a level change is visible on the next event.
    /// The cached copy is updated when the configuration changed. Does not throw.
    /// </remarks>
    internal int Floor
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            var configured = (int)_config.MinimumLevel;
            if (configured != _cached)
                _cached = configured;
            return configured;
        }
    }
}
