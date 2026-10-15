namespace ALog.Internal;

using ALog.Public.Interfaces;

/// <summary>
/// Pushes one immutable frame and, on dispose, removes that same frame.
/// </summary>
internal sealed class ScopeContext : ILogScope
{
    internal static readonly KeyValuePair<string, object?>[] EmptyProperties =
        Array.Empty<KeyValuePair<string, object?>>();

    private readonly object _id;
    private int _disposed;

    private ScopeContext(object id)
    {
        _id = id;
    }

    public static ILogScope Begin(KeyValuePair<string, object?>[]? properties)
    {
        if (properties is null || properties.Length == 0 || !HasUsableKey(properties))
            return NoOpLogScope.Instance;

        return BeginOwned(Copy(properties), text: null);
    }

    /// <summary>
    /// The arrays are owned by the new frame. Callers must not mutate them.
    /// </summary>
    internal static ILogScope BeginOwned(KeyValuePair<string, object?>[] properties, string? text)
    {
        var parent = ContextManager.Current;
        // Depth lives on the node, so this check does not walk the chain.
        if (parent is not null && parent.Depth >= ContextManager.MaxScopeDepth)
        {
            SelfLog.Report(
                "ContextManager",
                new InvalidOperationException(
                    "Scope depth limit of " + ContextManager.MaxScopeDepth + " exceeded; scope was not pushed."));
            return NoOpLogScope.Instance;
        }

        var id = new object();
        ContextManager.Current = new ScopeFrame(id, properties, text, parent);
        return new ScopeContext(id);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ContextManager.Remove(_id);
    }

    private static bool HasUsableKey(KeyValuePair<string, object?>[] properties)
    {
        for (var i = 0; i < properties.Length; i++)
        {
            if (properties[i].Key is not null)
                return true;
        }

        return false;
    }

    private static KeyValuePair<string, object?>[] Copy(KeyValuePair<string, object?>[] properties)
    {
        // The caller may reuse its buffer. The frame must keep a private copy.
        var copy = new KeyValuePair<string, object?>[properties.Length];
        Array.Copy(properties, copy, properties.Length);
        return copy;
    }
}

/// <summary>
/// Shared dispose handle for a scope that pushed no keys.
/// Dispose is a no-op, so one instance is safe and allocates nothing.
/// </summary>
internal sealed class NoOpLogScope : ILogScope
{
    internal static readonly NoOpLogScope Instance = new();

    private NoOpLogScope()
    {
    }

    public void Dispose()
    {
    }
}
