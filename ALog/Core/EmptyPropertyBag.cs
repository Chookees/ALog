namespace ALog.Core;

/// <summary>
/// The empty property bag shared by message templates and the engine.
/// </summary>
/// <remarks>
/// This is not a <see cref="Dictionary{TKey, TValue}"/>. A cast to that type throws, so a caller cannot
/// add a key and change every later event that reused the bag.
/// The instance is immutable. Any thread may read it. Nothing writes it.
/// </remarks>
internal sealed class EmptyPropertyBag : IReadOnlyDictionary<string, object>
{
    /// <summary>The only instance. Events with no properties share it.</summary>
    internal static readonly EmptyPropertyBag Instance = new();

    /// <summary>No one else constructs a bag. There is nothing to store.</summary>
    /// <remarks>Does not throw. Does not allocate.</remarks>
    private EmptyPropertyBag()
    {
    }

    /// <summary>Always zero.</summary>
    /// <remarks>Does not throw. Safe to read from any thread.</remarks>
    public int Count => 0;

    /// <summary>There are no keys, so every lookup fails.</summary>
    /// <param name="key">Ignored. May be null.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="KeyNotFoundException">Always. The bag is empty.</exception>
    /// <remarks>Does not throw any other exception. Safe to call from any thread.</remarks>
    public object this[string key] => throw new KeyNotFoundException();

    /// <summary>No key is present.</summary>
    /// <param name="key">Ignored. May be null.</param>
    /// <returns>False.</returns>
    /// <remarks>Does not throw. Safe to call from any thread.</remarks>
    public bool ContainsKey(string key) => false;

    /// <summary>Does not find a value.</summary>
    /// <param name="key">Ignored. May be null.</param>
    /// <param name="value">Set to null.</param>
    /// <returns>False.</returns>
    /// <remarks>Does not throw. Safe to call from any thread.</remarks>
    public bool TryGetValue(string key, out object value)
    {
        value = null!;
        return false;
    }

    /// <summary>No keys.</summary>
    /// <remarks>The sequence is empty and shared. It is not a list the caller can add to. Safe to read from any thread.</remarks>
    public IEnumerable<string> Keys => Array.Empty<string>();

    /// <summary>No values.</summary>
    /// <remarks>The sequence is empty and shared. Safe to read from any thread.</remarks>
    public IEnumerable<object> Values => Array.Empty<object>();

    /// <summary>An empty enumerator. No pairs are produced.</summary>
    /// <returns>The enumerator of an empty array.</returns>
    /// <remarks>Does not throw. Does not use an iterator, so nothing is left running after it returns. Safe to call from any thread.</remarks>
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object>>)Array.Empty<KeyValuePair<string, object>>()).GetEnumerator();

    /// <summary>An empty enumerator. No pairs are produced.</summary>
    /// <returns>The same empty walk as <see cref="GetEnumerator"/>.</returns>
    /// <remarks>Does not throw. Safe to call from any thread.</remarks>
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
