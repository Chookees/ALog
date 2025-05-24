namespace ALog.Internal;

internal static class ContextManager
{
    private static readonly AsyncLocal<Dictionary<string, object>?> _context
        = new();

    public static void Set(string key, object value)
    {
        _context.Value ??= new Dictionary<string, object>();
        _context.Value[key] = value;
    }

    public static IReadOnlyDictionary<string, object>? GetContext()
    {
        return _context.Value != null
            ? new Dictionary<string, object>(_context.Value)
            : null;
    }

    public static void Clear()
    {
        _context.Value = null;
    }
}
