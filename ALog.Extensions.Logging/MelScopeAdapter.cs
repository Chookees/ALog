namespace ALog.Extensions.Logging;

using ALog.Internal;
using ALog.Public.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Puts MEL scope state on the ALog scope chain.
/// A factory logger pushes into <see cref="IExternalScopeProvider"/> and does not
/// call <see cref="ALogLogger.BeginScope"/>, so those states are applied for the
/// duration of one log call. Without a provider, <see cref="BeginLocal{TState}"/>
/// pushes onto the same chain directly.
/// </summary>
internal static class MelScopeAdapter
{
    private const string OriginalFormat = "{OriginalFormat}";

    internal static IDisposable BeginLocal<TState>(TState state) where TState : notnull
        => ScopeContext.Begin(CopyProperties(state));

    internal static IDisposable PushExternal(IExternalScopeProvider? provider)
    {
        if (provider is null)
            return NoOpLogScope.Instance;

        var collected = new StateList();
        provider.ForEachScope(static (state, list) => list.Add(state), collected);
        if (collected.DroppedOuter)
        {
            SelfLog.Report(
                "MelScope",
                new InvalidOperationException(
                    "MEL scope depth exceeded " + ContextManager.MaxScopeDepth + "."));
        }

        if (collected.Items.Count == 0)
            return NoOpLogScope.Instance;

        return PushStates(collected.Items);
    }

    private static IDisposable PushStates(List<object?> states)
    {
        var scopes = new ILogScope[states.Count];
        var count = 0;
        for (var i = 0; i < states.Count; i++)
        {
            var begun = BeginObject(states[i]);
            if (!ReferenceEquals(begun, NoOpLogScope.Instance))
                scopes[count++] = begun;
        }

        if (count == 0)
            return NoOpLogScope.Instance;

        if (count == 1)
            return scopes[0];

        return new ScopeGroup(scopes, count);
    }

    private static ILogScope BeginObject(object? state)
    {
        if (state is null)
            return NoOpLogScope.Instance;

        return ScopeContext.Begin(CopyProperties(state));
    }

    private static KeyValuePair<string, object?>[] CopyProperties<TState>(TState state)
    {
        if (state is object boxed)
            return CopyProperties(boxed);

        return Array.Empty<KeyValuePair<string, object?>>();
    }

    private static KeyValuePair<string, object?>[] CopyProperties(object state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> nullablePairs)
            return CopyNullable(nullablePairs);

        if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            return CopyPairs(pairs);

        return Array.Empty<KeyValuePair<string, object?>>();
    }

    private static KeyValuePair<string, object?>[] CopyNullable(
        IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        List<KeyValuePair<string, object?>>? list = null;
        foreach (var pair in pairs)
        {
            if (!Include(pair.Key))
                continue;

            list ??= new List<KeyValuePair<string, object?>>(4);
            list.Add(pair);
        }

        return list is null ? Array.Empty<KeyValuePair<string, object?>>() : list.ToArray();
    }

    private static KeyValuePair<string, object?>[] CopyPairs(
        IEnumerable<KeyValuePair<string, object>> pairs)
    {
        List<KeyValuePair<string, object?>>? list = null;
        foreach (var pair in pairs)
        {
            if (!Include(pair.Key))
                continue;

            list ??= new List<KeyValuePair<string, object?>>(4);
            list.Add(new KeyValuePair<string, object?>(pair.Key, pair.Value));
        }

        return list is null ? Array.Empty<KeyValuePair<string, object?>>() : list.ToArray();
    }

    private static bool Include(string? key)
        => key is not null && !string.Equals(key, OriginalFormat, StringComparison.Ordinal);

    private sealed class StateList
    {
        public List<object?> Items { get; } = new(4);

        public bool DroppedOuter { get; private set; }

        public void Add(object? state)
        {
            if (Items.Count == ContextManager.MaxScopeDepth)
            {
                // Callback order is outer to inner. Drop the oldest so the
                // innermost scopes, which win on duplicate keys, are kept.
                Items.RemoveAt(0);
                DroppedOuter = true;
            }

            Items.Add(state);
        }
    }

    private sealed class ScopeGroup : IDisposable
    {
        private readonly ILogScope[] _scopes;
        private readonly int _count;
        private int _disposed;

        public ScopeGroup(ILogScope[] scopes, int count)
        {
            _scopes = scopes;
            _count = count;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (var i = _count - 1; i >= 0; i--)
                _scopes[i].Dispose();
        }
    }
}
