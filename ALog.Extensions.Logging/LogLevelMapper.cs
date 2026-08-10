namespace ALog.Extensions.Logging;

using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using ALogLevel = ALog.LogLevel;

internal static class LogLevelMapper
{
    public static ALogLevel ToALog(MelLogLevel level) => level switch
    {
        MelLogLevel.Trace => ALogLevel.Trace,
        MelLogLevel.Debug => ALogLevel.Debug,
        MelLogLevel.Information => ALogLevel.Info,
        MelLogLevel.Warning => ALogLevel.Warn,
        MelLogLevel.Error => ALogLevel.Error,
        MelLogLevel.Critical => ALogLevel.Fatal,
        MelLogLevel.None => ALogLevel.Fatal,
        _ => ALogLevel.Info
    };

    public static MelLogLevel ToMel(ALogLevel level) => level switch
    {
        ALogLevel.Trace => MelLogLevel.Trace,
        ALogLevel.Debug => MelLogLevel.Debug,
        ALogLevel.Info => MelLogLevel.Information,
        ALogLevel.Warn => MelLogLevel.Warning,
        ALogLevel.Error => MelLogLevel.Error,
        ALogLevel.Fatal => MelLogLevel.Critical,
        _ => MelLogLevel.Information
    };
}
