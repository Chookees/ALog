using System.Collections.ObjectModel;

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
/// wins. A scope that is not key/value pairs is stored as text and emitted as
/// the <c>Scope</c> context entry: a string array, outer to inner.
/// With no scopes, <see cref="GetContext"/> returns one shared empty dictionary.
/// Each frame stores <see cref="ScopeFrame.Depth"/> so the push limit is O(1).
/// Walks stop at <see cref="MaxScopeDepth"/>. Activity fields are not stored on
/// this chain. The MEL bridge copies the factory activity scope at emit time.
/// </para>
/// </remarks>
internal static class ContextManager
{
    internal const int MaxScopeDepth = 64;
    internal const string ScopePropertyName = "Scope";

    private static readonly AsyncLocal<ScopeFrame?> CurrentFrame = new();

    // One shared instance. ReadOnlyDictionary rejects mutation, and JSON treats it as an object.
    private static readonly IReadOnlyDictionary<string, object> EmptyContext =
        new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(0));

    internal static ScopeFrame? Current
    {
        get => CurrentFrame.Value;
        set => CurrentFrame.Value = value;
    }

    public static IReadOnlyDictionary<string, object> GetContext()
    {
        var head = CurrentFrame.Value;
        if (head is null)
            return EmptyContext;

        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        Collect(dict, head);
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

    private static void Collect(Dictionary<string, object> dict, ScopeFrame head)
    {
        var textCount = 0;
        var guard = 0;
        var frame = head;

        while (frame is not null && guard < MaxScopeDepth)
        {
            CopyNewKeys(dict, frame.Properties);
            if (frame.Text is not null)
                textCount++;

            frame = frame.Parent;
            guard++;
        }

        if (frame is not null)
            ReportDepth("Scope chain exceeded " + MaxScopeDepth + ".");

        if (textCount == 0)
            return;

        // The array is the carrier for non-KVP scopes, so it keeps this name
        // even when a key/value scope also used "Scope".
        dict[ScopePropertyName] = TextsOuterToInner(head, textCount);
    }

    private static string[] TextsOuterToInner(ScopeFrame head, int textCount)
    {
        // The walk is inner to outer. Fill from the end so index 0 is the outer scope.
        // A reference array cannot live on the stack, so this is the one allocation.
        var texts = new string[textCount];
        var index = textCount;
        var guard = 0;
        var frame = head;

        while (frame is not null && guard < MaxScopeDepth && index > 0)
        {
            if (frame.Text is not null)
                texts[--index] = frame.Text;

            frame = frame.Parent;
            guard++;
        }

        return texts;
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
