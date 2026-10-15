namespace ALog.Internal;

/// <summary>
/// Ambient log scope for the current async flow.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AsyncLocal{T}"/> holds one immutable <see cref="ScopeFrame"/>,
/// not a shared stack. A forked task inherits the reference. When that task
/// begins a scope it stores a new frame only on its own flow, so parallel
/// branches cannot push or pop each other's keys.
/// </para>
/// <para>
/// Dispose restores that frame's own parent when the frame is still the head.
/// Disposing out of order splices the frame out of this flow: frames above it
/// are copied onto the disposed frame's parent (same ids, new links). Inner
/// keys stay; the disposed keys disappear. Existing frames are not mutated, so
/// a sibling flow is never rewritten. If the id is not on this flow, dispose
/// does nothing.
/// </para>
/// <para>
/// Duplicate keys: the innermost frame wins. Inside one frame the last pair
/// wins. The dictionary is materialized at emit time as
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> of <c>string</c> to <c>object</c>
/// (the <see cref="Core.LogEvent.Context"/> contract; no public type change).
/// A scope with no keys returns <see cref="NoOpLogScope.Instance"/> and allocates nothing.
/// Walks stop at <see cref="MaxScopeDepth"/>. Trace and activity fields are not
/// stored here; that work is 0.3.0.
/// </para>
/// </remarks>
internal static class ContextManager
{
    internal const int MaxScopeDepth = 64;

    private static readonly AsyncLocal<ScopeFrame?> CurrentFrame = new();

    internal static ScopeFrame? Current
    {
        get => CurrentFrame.Value;
        set => CurrentFrame.Value = value;
    }

    public static IReadOnlyDictionary<string, object> GetContext()
    {
        var frame = CurrentFrame.Value;
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        var depth = 0;
        while (frame is not null && depth < MaxScopeDepth)
        {
            CopyNewKeys(dict, frame.Properties);
            frame = frame.Parent;
            depth++;
        }

        if (frame is not null)
            ReportDepth("Scope chain exceeded " + MaxScopeDepth + ".");

        return dict;
    }

    public static void Remove(object scopeId)
    {
        if (scopeId is null)
            return;

        var head = CurrentFrame.Value;
        if (head is null)
            return;

        // Common path: dispose the innermost scope. No copy, no sibling write.
        if (ReferenceEquals(head.Id, scopeId))
        {
            CurrentFrame.Value = head.Parent;
            return;
        }

        TrySplice(head, scopeId);
    }

    public static void Clear() => CurrentFrame.Value = null;

    internal static int Depth(ScopeFrame? frame)
    {
        var depth = 0;
        while (frame is not null && depth < MaxScopeDepth)
        {
            depth++;
            frame = frame.Parent;
        }

        return depth;
    }

    private static void CopyNewKeys(
        Dictionary<string, object> dict,
        KeyValuePair<string, object?>[] properties)
    {
        // Reverse order: the last pair in this frame wins. Keys already taken
        // by an inner frame are left alone, so the inner scope wins overall.
        for (var i = properties.Length - 1; i >= 0; i--)
        {
            var property = properties[i];
            if (property.Key is null || dict.ContainsKey(property.Key))
                continue;

            dict[property.Key] = property.Value!;
        }
    }

    private static void TrySplice(ScopeFrame head, object scopeId)
    {
        var above = new ScopeFrame[MaxScopeDepth];
        var count = 0;
        var cursor = head;

        while (cursor is not null && count < MaxScopeDepth)
        {
            if (ReferenceEquals(cursor.Id, scopeId))
            {
                RelinkAbove(above, count, cursor.Parent);
                return;
            }

            above[count] = cursor;
            count++;
            cursor = cursor.Parent;
        }

        if (cursor is not null)
            ReportDepth("Out-of-order dispose stopped at depth " + MaxScopeDepth + ".");
    }

    private static void RelinkAbove(ScopeFrame[] above, int count, ScopeFrame? parent)
    {
        for (var i = count - 1; i >= 0; i--)
            parent = above[i].Relink(parent);

        CurrentFrame.Value = parent;
    }

    private static void ReportDepth(string message)
    {
        SelfLog.Report("ContextManager", new InvalidOperationException(message));
    }
}
