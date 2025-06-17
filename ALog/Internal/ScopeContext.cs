namespace ALog.Internal;

internal  class ScopeContext : ILogScope
{
    private readonly KeyValuePair<string, object> _entry;

    public ScopeContext(string key, object value)
    {
        _entry = new KeyValuePair<string, object>(key, value);
        ContextManager.Push(_entry);
    }

    public void Dispose()
    {
        ContextManager.Pop(_entry);
    }
}
