namespace ALog.Internal;

/// <summary>
/// Pushes one immutable frame and, on dispose, removes that same frame.
/// </summary>
internal sealed class ScopeContext : ILogScope
{
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

        if (ContextManager.Depth(ContextManager.Current) >= ContextManager.MaxScopeDepth)
        {
            SelfLog.Report(
                "ContextManager",
                new InvalidOperationException(
                    "Scope depth limit of " + ContextManager.MaxScopeDepth + " exceeded; scope was not pushed."));
            return NoOpLogScope.Instance;
        }

        var id = new object();
        var frame = new ScopeFrame(id, Copy(properties), ContextManager.Current);
        ContextManager.Current = frame;
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
