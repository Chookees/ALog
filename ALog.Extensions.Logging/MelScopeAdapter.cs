namespace ALog.Extensions.Logging;

using ALog;
using Microsoft.Extensions.Logging;

/// <summary>
/// MEL scopes.
/// <see cref="BeginLocal{TState}"/> is for a logger used without a factory.
/// Once <see cref="ISupportExternalScope"/> is set, the factory keeps every
/// BeginScope, including the activity scope, on its own provider. Those are
/// read in <see cref="AttachExternal"/> at emit time, after the level gate,
/// and pushed onto <see cref="LogContext"/> only for that call.
/// </summary>
internal static class MelScopeAdapter
{
    private const string OriginalFormat = "{OriginalFormat}";

    /// <summary>
    /// Most entries read from one MEL scope or one MEL log state.
    /// The log bridge uses the same window so a large state cannot grow without a bound.
    /// </summary>
    internal const int MaxProperties = 128;
    private const int MaxTexts = 64;

    // Static so the log path does not allocate a closure per call.
    private static readonly Action<object?, ExternalScopeState> OnScope = OnExternalScope;

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

    /// <summary>
    /// Copies the factory's scopes onto <see cref="LogContext"/> for one emit.
    /// Call this only after the level gate. Dispose removes just these frames.
    /// </summary>
    internal static IDisposable AttachExternal(IExternalScopeProvider? provider)
    {
        if (provider is null)
            return EmptyDispose.Instance;

        var state = new ExternalScopeState
        {
            Properties = new List<KeyValuePair<string, object?>>(4),
            Texts = new List<object>(4)
        };

        try
        {
            provider.ForEachScope(OnScope, state);
        }
        catch (Exception)
        {
            // The bridge cannot see the internal reporter. Keep the scopes already
            // copied; a failure here must not escape into the application.
        }

        return PushCollected(state);
    }

    private static void OnExternalScope(object? scope, ExternalScopeState state)
    {
        if (scope is null)
            return;

        // Key/value scopes, including the activity scope, become properties.
        // Anything else is one string in the Scope array.
        if (TryAddPairs(scope, state))
            return;

        if (state.Texts.Count >= MaxTexts)
            return;

        // Strings are stored as themselves. Other values go through PushScope,
        // which catches a throwing ToString.
        if (scope is string text && text.Length == 0)
            return;

        state.Texts.Add(scope);
    }

    private static bool TryAddPairs(object scope, ExternalScopeState state)
    {
        if (scope is IReadOnlyList<KeyValuePair<string, object?>> list)
            return AddList(list, state);

        if (scope is IEnumerable<KeyValuePair<string, object?>> nullablePairs)
            return AddNullable(nullablePairs, state);

        if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            return AddPairs(pairs, state);

        return false;
    }

    private static bool AddList(
        IReadOnlyList<KeyValuePair<string, object?>> list,
        ExternalScopeState state)
    {
        var saw = false;
        var limit = list.Count;
        if (limit > MaxProperties)
            limit = MaxProperties;

        for (var i = 0; i < limit; i++)
        {
            if (!Take(list[i].Key, list[i].Value, state, ref saw))
                break;
        }

        return saw;
    }

    private static bool AddNullable(
        IEnumerable<KeyValuePair<string, object?>> pairs,
        ExternalScopeState state)
    {
        var saw = false;
        var scanned = 0;
        foreach (var pair in pairs)
        {
            if (scanned == MaxProperties)
                break;

            scanned++;
            if (!Take(pair.Key, pair.Value, state, ref saw))
                break;
        }

        return saw;
    }

    private static bool AddPairs(
        IEnumerable<KeyValuePair<string, object>> pairs,
        ExternalScopeState state)
    {
        var saw = false;
        var scanned = 0;
        foreach (var pair in pairs)
        {
            if (scanned == MaxProperties)
                break;

            scanned++;
            if (!Take(pair.Key, pair.Value, state, ref saw))
                break;
        }

        return saw;
    }

    private static bool Take(string? key, object? value, ExternalScopeState state, ref bool saw)
    {
        if (!Include(key))
            return true;

        saw = true;
        if (state.Properties.Count >= MaxProperties)
            return false;

        state.Properties.Add(new KeyValuePair<string, object?>(key!, value));
        return true;
    }

    private static IDisposable PushCollected(ExternalScopeState state)
    {
        // Properties go on first, then text outer to inner. LogContext frames
        // are already underneath, so they stay first in the Scope array.
        // The property frame is inner, so an external key wins over LogContext.
        IDisposable? properties = null;
        if (state.Properties.Count > 0)
            properties = LogContext.Push(state.Properties);

        var texts = state.Texts.Count;
        if (properties is null && texts == 0)
            return EmptyDispose.Instance;

        if (texts == 0)
            return properties!;

        var handles = new IDisposable[texts + (properties is null ? 0 : 1)];
        var used = 0;
        if (properties is not null)
            handles[used++] = properties;

        for (var i = 0; i < texts; i++)
            handles[used++] = LogContext.PushScope(state.Texts[i]);

        return used == 1 ? handles[0] : new ScopeGroup(handles, used);
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

    // Lists are reference fields so the callback can fill them. ForEachScope
    // passes the struct by value; a closure would allocate on every emit.
    private struct ExternalScopeState
    {
        public List<KeyValuePair<string, object?>> Properties;
        public List<object> Texts;
    }

    private sealed class ScopeGroup : IDisposable
    {
        private readonly IDisposable[] _handles;
        private readonly int _count;
        private int _disposed;

        public ScopeGroup(IDisposable[] handles, int count)
        {
            _handles = handles;
            _count = count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Reverse of push, so the innermost external frame leaves first.
            for (var i = _count - 1; i >= 0; i--)
                _handles[i].Dispose();
        }
    }

    private sealed class EmptyDispose : IDisposable
    {
        internal static readonly EmptyDispose Instance = new();

        private EmptyDispose()
        {
        }

        public void Dispose()
        {
        }
    }
}
