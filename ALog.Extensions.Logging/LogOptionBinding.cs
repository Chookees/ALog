namespace ALog.Extensions.Logging;

using ALog.Config;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Reads the queue and file scalars before the binder, and writes the parsed values back after it.
/// A bad enum must not stay at the binder's silent default.
/// </summary>
internal static class LogOptionBinding
{
    /// <summary>
    /// Throws when a present queue or file scalar is not allowed. A missing key keeps the default.
    /// </summary>
    /// <param name="section">The <c>ALog</c> section. Not null. Not modified.</param>
    /// <exception cref="InvalidOperationException">A present value is not a mode name, is not readable, or is outside its range.</exception>
    /// <remarks>Does not write a log line. Called after the writer-section check and before <c>Bind</c>.</remarks>
    internal static void RejectInvalid(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        RejectMode(section, "QueueFullMode");
        RejectInt(section, "FileBufferSize", LogOptionRules.MinBuffer, LogOptionRules.MaxBuffer, LogOptionRules.BufferRange);
        RejectDuration(section, "FileFlushInterval");
        RejectDuration(section, "FlushTimeout");
        RejectInt(section, "BackgroundQueueCapacity", LogOptionRules.MinCapacity, LogOptionRules.MaxCapacity, LogOptionRules.CapacityRange);
    }

    /// <summary>
    /// Copies each present, already accepted value onto <paramref name="options"/>.
    /// </summary>
    /// <param name="section">The <c>ALog</c> section. Not null.</param>
    /// <param name="options">The bound options. Not null. Updated in place.</param>
    /// <remarks>Does not throw. A missing key is left as the binder left it. Called only after <see cref="RejectInvalid"/>.</remarks>
    internal static void ApplyParsed(IConfiguration section, ALogOptions options)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(options);
        if (TryRaw(section, "QueueFullMode", out var modeText) && LogOptionRules.TryParseMode(modeText, out var mode))
            options.QueueFullMode = mode;

        if (TryRaw(section, "FileBufferSize", out var bufferText) && LogOptionRules.TryParseInt(bufferText, out var buffer))
            options.FileBufferSize = buffer;

        if (TryRaw(section, "FileFlushInterval", out var intervalText) && LogOptionRules.TryParseDuration(intervalText, out var interval))
            options.FileFlushInterval = interval;

        if (TryRaw(section, "FlushTimeout", out var timeoutText) && LogOptionRules.TryParseDuration(timeoutText, out var timeout))
            options.FlushTimeout = timeout;

        if (TryRaw(section, "BackgroundQueueCapacity", out var capacityText) && LogOptionRules.TryParseInt(capacityText, out var capacity))
            options.BackgroundQueueCapacity = capacity;
    }

    /// <summary>Rejects a mode that is not Drop or Block after trim.</summary>
    /// <param name="section">The <c>ALog</c> section. Not null.</param>
    /// <param name="name">The key. Not null.</param>
    /// <exception cref="InvalidOperationException">The key is present and is not a mode name. Numbers are rejected.</exception>
    /// <remarks>Does not throw when the key is missing. The echoed value is the raw text, capped by the rule helper.</remarks>
    private static void RejectMode(IConfiguration section, string name)
    {
        if (!TryRaw(section, name, out var raw))
            return;

        if (LogOptionRules.TryParseMode(raw, out _))
            return;

        throw new InvalidOperationException(LogOptionRules.NotAllowed(LogOptionRules.PathOf(name), raw));
    }

    /// <summary>Rejects an integer that cannot be parsed or is outside <paramref name="min"/>..<paramref name="max"/>.</summary>
    /// <param name="section">The <c>ALog</c> section. Not null.</param>
    /// <param name="name">The key. Not null.</param>
    /// <param name="min">Smallest allowed value.</param>
    /// <param name="max">Largest allowed value.</param>
    /// <param name="range">The range text for the sentence. Not null.</param>
    /// <exception cref="InvalidOperationException">The key is present and is unreadable or outside the range.</exception>
    /// <remarks>Does not throw when the key is missing.</remarks>
    private static void RejectInt(IConfiguration section, string name, int min, int max, string range)
    {
        if (!TryRaw(section, name, out var raw))
            return;

        var path = LogOptionRules.PathOf(name);
        if (!LogOptionRules.TryParseInt(raw, out var value))
            throw new InvalidOperationException(LogOptionRules.Unreadable(path, raw, range));

        if (value < min || value > max)
            throw new InvalidOperationException(LogOptionRules.Outside(path, raw, range));
    }

    /// <summary>Rejects a duration that cannot be parsed or is outside 0..60 seconds.</summary>
    /// <param name="section">The <c>ALog</c> section. Not null.</param>
    /// <param name="name">The key. Not null.</param>
    /// <exception cref="InvalidOperationException">The key is present and is unreadable or outside the range.</exception>
    /// <remarks>
    /// Does not throw when the key is missing.
    /// <see cref="Timeout.InfiniteTimeSpan"/> parses and is outside the range, so the sentence says outside, not unreadable.
    /// </remarks>
    private static void RejectDuration(IConfiguration section, string name)
    {
        if (!TryRaw(section, name, out var raw))
            return;

        var path = LogOptionRules.PathOf(name);
        if (!LogOptionRules.TryParseDuration(raw, out var value))
            throw new InvalidOperationException(LogOptionRules.Unreadable(path, raw, LogOptionRules.DurationRange));

        if (value < TimeSpan.Zero || value > LogOptionRules.MaxDuration)
            throw new InvalidOperationException(LogOptionRules.Outside(path, raw, LogOptionRules.DurationRange));
    }

    /// <summary>Reads a scalar. A missing key is not present. An empty string is present.</summary>
    /// <param name="section">The <c>ALog</c> section. Not null.</param>
    /// <param name="name">The key. Not null.</param>
    /// <param name="raw">The stored text when this returns true.</param>
    /// <returns>True when the key has a value, including empty.</returns>
    /// <remarks>Does not throw. Does not trim.</remarks>
    private static bool TryRaw(IConfiguration section, string name, out string raw)
    {
        var value = section[name];
        if (value is null)
        {
            raw = string.Empty;
            return false;
        }

        raw = value;
        return true;
    }
}
