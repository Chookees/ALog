namespace ALog.Internal;

internal static class ContextManager
{
    private static readonly AsyncLocal<Stack<KeyValuePair<string, object>>> _contextStack = new();

    public static void Push(KeyValuePair<string, object> entry)
    {
        _contextStack.Value ??= new();
        _contextStack.Value.Push(entry);
    }

    public static void Pop(KeyValuePair<string, object> expected)
    {
        if (_contextStack.Value?.Count > 0 && _contextStack.Value.Peek().Equals(expected))
        {
            _contextStack.Value.Pop();
        }
    }

    public static IReadOnlyDictionary<string, object> GetContext()
    {
        var dict = new Dictionary<string, object>();

        if (_contextStack.Value != null)
        {
            foreach (var entry in _contextStack.Value)
                dict[entry.Key] = entry.Value;
        }

        return dict;
    }

    public static void Clear() => _contextStack.Value?.Clear();
}
