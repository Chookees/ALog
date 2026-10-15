namespace ALog.Internal;

/// <summary>
/// One immutable scope on an async flow: properties plus the previous frame.
/// Frames are never mutated. <see cref="Relink"/> returns a new node so a
/// sibling flow that still holds the old chain is left alone.
/// </summary>
internal sealed class ScopeFrame
{
    public ScopeFrame(object id, KeyValuePair<string, object?>[] properties, ScopeFrame? parent)
    {
        Id = id;
        Properties = properties;
        Parent = parent;
    }

    public object Id { get; }

    /// <summary>Owned by this frame. Callers must not mutate the array.</summary>
    public KeyValuePair<string, object?>[] Properties { get; }

    public ScopeFrame? Parent { get; }

    public ScopeFrame Relink(ScopeFrame? parent) => new(Id, Properties, parent);
}
