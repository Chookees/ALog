namespace ALog.Config;

using System.Globalization;

/// <summary>
/// Checks the queue and file options and builds the startup error sentences.
/// Invalid values stop startup. They are never replaced with a silent default.
/// </summary>
internal static class LogOptionRules
{
    /// <summary>Default bytes collected before <c>FileLogWriter</c> writes. 64 KiB.</summary>
    internal const int DefaultBuffer = 65536;

    /// <summary>Smallest accepted <see cref="DefaultBuffer"/>. 4 KiB.</summary>
    internal const int MinBuffer = 4096;

    /// <summary>Largest accepted <see cref="DefaultBuffer"/>. 16 MiB.</summary>
    internal const int MaxBuffer = 16 * 1024 * 1024;

    /// <summary>Smallest accepted background-queue capacity.</summary>
    internal const int MinCapacity = 1;

    /// <summary>Largest accepted background-queue capacity.</summary>
    internal const int MaxCapacity = 100_000;

    /// <summary>How many characters of a bad value are copied into the error.</summary>
    internal const int ShownLimit = 64;

    /// <summary>Text after "Allowed range:" for <see cref="DefaultBuffer"/>.</summary>
    internal const string BufferRange = "4096 to 16777216";

    /// <summary>Text after "Allowed range:" for a flush interval or a flush timeout.</summary>
    internal const string DurationRange = "00:00:00 to 00:01:00";

    /// <summary>Text after "Allowed range:" for the background-queue capacity.</summary>
    internal const string CapacityRange = "1 to 100000";

    /// <summary>Default <c>FileFlushInterval</c>. One second.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>Default <c>FlushTimeout</c>. Five seconds.</summary>
    internal static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longest accepted interval or timeout. Sixty seconds.</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>Shortest timeout that keeps a 100 ms gap before the shutdown deadline. Below this the cutoff is half.</summary>
    private static readonly TimeSpan CutoffSplit = TimeSpan.FromMilliseconds(200);

    /// <summary>Gap between the new-write cutoff and the shutdown deadline when the timeout is at least <see cref="CutoffSplit"/>.</summary>
    private static readonly TimeSpan CutoffGap = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Checks every option the engine reads.
    /// </summary>
    /// <param name="config">Configuration the engine is about to start. Not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">An option is outside its allowed values. The message names the path and the range.</exception>
    /// <remarks>
    /// Does not write a log line. Capacity is checked only when the background queue is enabled.
    /// Called once from the engine constructor. A later change is ignored by <see cref="LoggerConfig.Seal"/>.
    /// </remarks>
    internal static void Validate(ILogConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _ = RequireMode(config.QueueFullMode);
        RequireBuffer(config.FileBufferSize, PathOf("FileBufferSize"));
        RequireDuration(config.FileFlushInterval, PathOf("FileFlushInterval"));
        RequireDuration(config.FlushTimeout, PathOf("FlushTimeout"));
        if (config.BackgroundQueueEnabled)
            RequireCapacity(config.BackgroundQueueCapacity, PathOf("BackgroundQueueCapacity"));
    }

    /// <summary>
    /// Returns <paramref name="mode"/> when it is <see cref="QueueFullMode.Drop"/> or <see cref="QueueFullMode.Block"/>.
    /// </summary>
    /// <param name="mode">The value to check. A cast from an unknown integer is rejected.</param>
    /// <returns>The same value when it is one of the two named modes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="mode"/> is not Drop or Block.</exception>
    /// <remarks>Does not write a log line. The path in the message is <c>ALog:QueueFullMode</c>.</remarks>
    internal static QueueFullMode RequireMode(QueueFullMode mode)
    {
        if (mode == QueueFullMode.Drop || mode == QueueFullMode.Block)
            return mode;

        var text = ((int)mode).ToString(CultureInfo.InvariantCulture);
        throw new InvalidOperationException(NotAllowed(PathOf("QueueFullMode"), text));
    }

    /// <summary>
    /// Rejects a buffer size outside 4096..16777216.
    /// </summary>
    /// <param name="value">The size in bytes.</param>
    /// <param name="path">Configuration path copied into the message. Not null.</param>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is outside the range.</exception>
    /// <remarks>Does not write a log line. The allowed range text is <see cref="BufferRange"/>.</remarks>
    internal static void RequireBuffer(int value, string path)
    {
        if (value >= MinBuffer && value <= MaxBuffer)
            return;

        throw new InvalidOperationException(Outside(path, value.ToString(CultureInfo.InvariantCulture), BufferRange));
    }

    /// <summary>
    /// Rejects a duration below zero or above 60 seconds, including <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    /// <param name="value">The interval or timeout.</param>
    /// <param name="path">Configuration path copied into the message. Not null.</param>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is outside 0..60 seconds.</exception>
    /// <remarks>Does not write a log line. Zero is valid and means "do not wait" or "write every event".</remarks>
    internal static void RequireDuration(TimeSpan value, string path)
    {
        if (value >= TimeSpan.Zero && value <= MaxDuration)
            return;

        throw new InvalidOperationException(Outside(path, value.ToString("c", CultureInfo.InvariantCulture), DurationRange));
    }

    /// <summary>
    /// Rejects a capacity outside 1..100000.
    /// </summary>
    /// <param name="value">The capacity.</param>
    /// <param name="path">Configuration path copied into the message. Not null.</param>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is outside the range.</exception>
    /// <remarks>Does not write a log line. The allowed range text is <see cref="CapacityRange"/>.</remarks>
    internal static void RequireCapacity(int value, string path)
    {
        if (value >= MinCapacity && value <= MaxCapacity)
            return;

        throw new InvalidOperationException(Outside(path, value.ToString(CultureInfo.InvariantCulture), CapacityRange));
    }

    /// <summary>
    /// How long new writes may continue after shutdown starts.
    /// </summary>
    /// <param name="timeout">The flush timeout. Negative values are not expected; they take the half path.</param>
    /// <returns>
    /// Zero when <paramref name="timeout"/> is zero or one tick.
    /// Half of <paramref name="timeout"/> when it is below 200 ms.
    /// Otherwise <paramref name="timeout"/> minus 100 ms. Five seconds becomes 4900 ms.
    /// </returns>
    /// <remarks>Does not throw. Does not read a clock. The result is never longer than <paramref name="timeout"/>.</remarks>
    internal static TimeSpan DeriveCutoff(TimeSpan timeout)
    {
        if (timeout < CutoffSplit)
            return TimeSpan.FromTicks(timeout.Ticks / 2);

        return timeout - CutoffGap;
    }

    /// <summary>The sentence for a value that is not Drop or Block.</summary>
    /// <param name="path">Configuration path. Not null.</param>
    /// <param name="value">The raw text. Null is shown as empty. Newlines are removed and the text stops at 64 characters.</param>
    /// <returns>The full startup sentence.</returns>
    /// <remarks>Does not throw. Does not read configuration.</remarks>
    internal static string NotAllowed(string path, string? value)
        => "Invalid ALog configuration at '" + path + "': '" + Shown(value)
            + "' is not an allowed value. Allowed values: Drop, Block.";

    /// <summary>The sentence for a value that parsed and is outside the range.</summary>
    /// <param name="path">Configuration path. Not null.</param>
    /// <param name="value">The raw text. Null is shown as empty.</param>
    /// <param name="range">The allowed range, without a trailing period.</param>
    /// <returns>The full startup sentence.</returns>
    /// <remarks>Does not throw. Does not read configuration.</remarks>
    internal static string Outside(string path, string? value, string range)
        => "Invalid ALog configuration at '" + path + "': '" + Shown(value)
            + "' is outside the allowed range. Allowed range: " + range + ".";

    /// <summary>The sentence for a value that cannot be parsed.</summary>
    /// <param name="path">Configuration path. Not null.</param>
    /// <param name="value">The raw text. Null is shown as empty.</param>
    /// <param name="range">The allowed range, without a trailing period.</param>
    /// <returns>The full startup sentence.</returns>
    /// <remarks>Does not throw. Does not read configuration.</remarks>
    internal static string Unreadable(string path, string? value, string range)
        => "Invalid ALog configuration at '" + path + "': '" + Shown(value)
            + "' is not readable. Allowed range: " + range + ".";

    /// <summary>
    /// Copies at most 64 characters and turns CR and LF into spaces so the error stays one line.
    /// </summary>
    /// <param name="value">Raw configuration text. Null becomes empty.</param>
    /// <returns>The text to put between quotes. Never longer than 64 characters.</returns>
    /// <remarks>Does not throw. Does not trim. A space that was already there is kept.</remarks>
    internal static string Shown(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var limit = value.Length < ShownLimit ? value.Length : ShownLimit;
        var buffer = new char[limit];
        for (var i = 0; i < limit; i++)
        {
            var ch = value[i];
            buffer[i] = ch == '\r' || ch == '\n' ? ' ' : ch;
        }

        return new string(buffer);
    }

    /// <summary>
    /// Accepts Drop and Block after trim, in any letter case. Numbers and any other text are rejected.
    /// </summary>
    /// <param name="raw">The stored text. Not null. May be empty.</param>
    /// <param name="mode">The mode when this returns true. Otherwise <see cref="QueueFullMode.Drop"/>.</param>
    /// <returns>True when <paramref name="raw"/> is a mode name.</returns>
    /// <remarks>Does not throw. Does not accept "0" or "1".</remarks>
    internal static bool TryParseMode(string raw, out QueueFullMode mode)
    {
        var text = raw.Trim();
        if (text.Equals("Drop", StringComparison.OrdinalIgnoreCase))
        {
            mode = QueueFullMode.Drop;
            return true;
        }

        if (text.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            mode = QueueFullMode.Block;
            return true;
        }

        mode = QueueFullMode.Drop;
        return false;
    }

    /// <summary>Parses a base-10 integer after trim. Leading or trailing text is rejected.</summary>
    /// <param name="raw">The stored text. Not null.</param>
    /// <param name="value">The integer when this returns true.</param>
    /// <returns>True when the whole trimmed text is an integer.</returns>
    /// <remarks>Does not throw. Does not check the allowed range.</remarks>
    internal static bool TryParseInt(string raw, out int value)
        => int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a duration with the invariant culture after trim.</summary>
    /// <param name="raw">The stored text. Not null. <c>00:00:05</c> is five seconds.</param>
    /// <param name="value">The duration when this returns true.</param>
    /// <returns>True when <see cref="TimeSpan.TryParse(string, IFormatProvider, out TimeSpan)"/> accepts the trimmed text.</returns>
    /// <remarks>Does not throw. Does not check the allowed range. A bare number such as <c>5</c> is not a duration.</remarks>
    internal static bool TryParseDuration(string raw, out TimeSpan value)
        => TimeSpan.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out value);

    /// <summary>Builds <c>ALog:</c> plus <paramref name="name"/>.</summary>
    /// <param name="name">The key under the ALog section. Not null.</param>
    /// <returns>The path used in startup errors.</returns>
    /// <remarks>Does not throw. Does not read configuration.</remarks>
    internal static string PathOf(string name)
        => ALogOptions.SectionName + ":" + name;
}
