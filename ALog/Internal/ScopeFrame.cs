namespace ALog.Internal;

/// <summary>
/// One immutable scope on an async flow: properties or one text value, plus the previous frame.
/// Frames are never mutated. <see cref="Relink"/> returns a new node so a
/// sibling flow that still holds the old chain is left alone.
/// <see cref="Depth"/> is <c>parent.Depth + 1</c> so the depth limit does not walk the chain.
/// </summary>
internal sealed class ScopeFrame
{
    public ScopeFrame(object id, KeyValuePair<string, object?>[] properties, string? text, ScopeFrame? parent)
    {
        Id = id;
        Properties = properties;
        Text = text;
        Parent = parent;
        Depth = parent is null ? 1 : parent.Depth + 1;
    }

    public object Id { get; }

    /// <summary>Owned by this frame. Callers must not mutate the array.</summary>
    public KeyValuePair<string, object?>[] Properties { get; }

    /// <summary>Set for a non-KVP scope. Null for a key/value scope.</summary>
    public string? Text { get; }

    public ScopeFrame? Parent { get; }

    public int Depth { get; }

    public ScopeFrame Relink(ScopeFrame? parent) => new(Id, Properties, Text, parent);
}
