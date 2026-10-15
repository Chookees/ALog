namespace ALog.Extensions.Logging;

using ALog;

/// <summary>
/// Attaches one MEL scope to the ALog scope chain.
/// The factory calls <see cref="BeginLocal{TState}"/> when a scope begins.
/// Log calls do not copy the chain again: the frames are already there.
/// </summary>
internal static class MelScopeAdapter
{
    private const string OriginalFormat = "{OriginalFormat}";
    private const int MaxProperties = 128;

    internal static IDisposable BeginLocal<TState>(TState state) where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> nullablePairs)
        {
            var pairs = CopyNullable(nullablePairs);
            if (pairs.Length > 0)
                return LogContext.Push(pairs);
        }
        else if (state is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            var copied = CopyPairs(pairs);
            if (copied.Length > 0)
                return LogContext.Push(copied);
        }

        // Not a key/value list (BeginScope("Batch 7"), or a format value with
        // no properties left after {OriginalFormat} is removed).
        return LogContext.PushScope(state);
    }

    private static KeyValuePair<string, object?>[] CopyNullable(
        IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        var buffer = new List<KeyValuePair<string, object?>>(4);
        foreach (var pair in pairs)
        {
            if (!Include(pair.Key))
                continue;

            if (buffer.Count == MaxProperties)
                break;

            buffer.Add(pair);
        }

        return buffer.Count == 0 ? Array.Empty<KeyValuePair<string, object?>>() : buffer.ToArray();
    }

    private static KeyValuePair<string, object?>[] CopyPairs(
        IEnumerable<KeyValuePair<string, object>> pairs)
    {
        var buffer = new List<KeyValuePair<string, object?>>(4);
        foreach (var pair in pairs)
        {
            if (!Include(pair.Key))
                continue;

            if (buffer.Count == MaxProperties)
                break;

            buffer.Add(new KeyValuePair<string, object?>(pair.Key, pair.Value));
        }

        return buffer.Count == 0 ? Array.Empty<KeyValuePair<string, object?>>() : buffer.ToArray();
    }

    private static bool Include(string? key)
        => key is not null && !string.Equals(key, OriginalFormat, StringComparison.Ordinal);
}
