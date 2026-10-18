using ALog.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// Covers both directions of the Microsoft.Extensions.Logging level map.
/// The reverse map is internal. Tests see it through <c>InternalsVisibleTo</c>.
/// Next file: <c>CoverageMelTests.cs</c> for the provider.
/// </summary>
public sealed class LevelMapperCoverageTests
{
    /// <summary>
    /// Every Microsoft level, including None and an unknown value, has an ALog level.
    /// </summary>
    /// <remarks>
    /// <see cref="Microsoft.Extensions.Logging.ILogger.IsEnabled"/> returns before the map
    /// when the level is None, so this test calls the map directly.
    /// </remarks>
    /// <returns>A task that finishes after the asserts.</returns>
    [Fact(Timeout = 30000)]
    public async Task ToALog_MapsEveryLevelIncludingNoneAndUnknown()
    {
        await Task.CompletedTask;
        Assert.Equal(ALog.LogLevel.Trace, LogLevelMapper.ToALog(MelLogLevel.Trace));
        Assert.Equal(ALog.LogLevel.Debug, LogLevelMapper.ToALog(MelLogLevel.Debug));
        Assert.Equal(ALog.LogLevel.Info, LogLevelMapper.ToALog(MelLogLevel.Information));
        Assert.Equal(ALog.LogLevel.Warn, LogLevelMapper.ToALog(MelLogLevel.Warning));
        Assert.Equal(ALog.LogLevel.Error, LogLevelMapper.ToALog(MelLogLevel.Error));
        Assert.Equal(ALog.LogLevel.Fatal, LogLevelMapper.ToALog(MelLogLevel.Critical));
        Assert.Equal(ALog.LogLevel.Fatal, LogLevelMapper.ToALog(MelLogLevel.None));
        Assert.Equal(ALog.LogLevel.Info, LogLevelMapper.ToALog((MelLogLevel)99));
    }

    /// <summary>
    /// Every ALog level, including an unknown value, has a Microsoft level.
    /// </summary>
    /// <remarks>
    /// The reverse map is not on the log path today. The arms still ship in the assembly,
    /// so a broken arm fails this test.
    /// </remarks>
    /// <returns>A task that finishes after the asserts.</returns>
    [Fact(Timeout = 30000)]
    public async Task ToMel_MapsEveryLevelIncludingUnknown()
    {
        await Task.CompletedTask;
        Assert.Equal(MelLogLevel.Trace, LogLevelMapper.ToMel(ALog.LogLevel.Trace));
        Assert.Equal(MelLogLevel.Debug, LogLevelMapper.ToMel(ALog.LogLevel.Debug));
        Assert.Equal(MelLogLevel.Information, LogLevelMapper.ToMel(ALog.LogLevel.Info));
        Assert.Equal(MelLogLevel.Warning, LogLevelMapper.ToMel(ALog.LogLevel.Warn));
        Assert.Equal(MelLogLevel.Error, LogLevelMapper.ToMel(ALog.LogLevel.Error));
        Assert.Equal(MelLogLevel.Critical, LogLevelMapper.ToMel(ALog.LogLevel.Fatal));
        Assert.Equal(MelLogLevel.Information, LogLevelMapper.ToMel((ALog.LogLevel)99));
    }
}
