namespace ALog;

using System.Globalization;
using ALog.Internal;

/// <summary>
/// Ambient log scope for the current async flow.
/// Key/value scopes become context properties (the inner value wins).
/// Anything else is rendered into the <c>Scope</c> context array, outer to inner.
/// </summary>
public static class LogContext
{
    private const int MaxProperties = 128;

    /// <summary>
    /// Pushes key/value pairs onto the current async flow.
    /// Dispose removes this scope only. An empty list returns a shared no-op handle.
    /// </summary>
    /// <param name="properties">Pairs to add. Null keys are skipped. The caller may reuse the collection.</param>
    /// <returns>A handle that restores the previous scope.</returns>
    public static IDisposable Push(IEnumerable<KeyValuePair<string, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var copy = Copy(properties);
        if (copy.Length == 0)
            return NoOpLogScope.Instance;

        return ScopeContext.BeginOwned(copy, text: null);
    }

    /// <summary>
    /// Pushes a scope that is not a key/value list, for example <c>BeginScope("Batch 7")</c>.
    /// The value is rendered once, here, into the <c>Scope</c> array. Dispose removes it.
    /// A null state returns a shared no-op handle.
    /// </summary>
    /// <param name="state">Scope value. Strings are stored as themselves.</param>
    /// <returns>A handle that restores the previous scope.</returns>
    public static IDisposable PushScope(object state)
    {
        if (state is null)
            return NoOpLogScope.Instance;

        if (state is string text)
            return ScopeContext.BeginOwned(ScopeContext.EmptyProperties, text);

        return ScopeContext.BeginOwned(ScopeContext.EmptyProperties, Render(state));
    }

    private static KeyValuePair<string, object?>[] Copy(
        IEnumerable<KeyValuePair<string, object?>> properties)
    {
        if (properties is IReadOnlyCollection<KeyValuePair<string, object?>> collection)
            return CopyKnown(collection, collection.Count);

        return CopyUnknown(properties);
    }

    private static KeyValuePair<string, object?>[] CopyKnown(
        IEnumerable<KeyValuePair<string, object?>> properties,
        int count)
    {
        if (count <= 0)
            return ScopeContext.EmptyProperties;

        if (count > MaxProperties)
            count = MaxProperties;

        var copy = new KeyValuePair<string, object?>[count];
        var used = 0;
        foreach (var pair in properties)
        {
            if (pair.Key is null)
                continue;

            if (used == count)
            {
                ReportLimit();
                break;
            }

            copy[used++] = pair;
        }

        return Trim(copy, used);
    }

    private static KeyValuePair<string, object?>[] CopyUnknown(
        IEnumerable<KeyValuePair<string, object?>> properties)
    {
        var buffer = new KeyValuePair<string, object?>[MaxProperties];
        var used = 0;
        foreach (var pair in properties)
        {
            if (pair.Key is null)
                continue;

            if (used == MaxProperties)
            {
                ReportLimit();
                break;
            }

            buffer[used++] = pair;
        }

        return Trim(buffer, used);
    }

    private static KeyValuePair<string, object?>[] Trim(
        KeyValuePair<string, object?>[] buffer,
        int used)
    {
        if (used == 0)
            return ScopeContext.EmptyProperties;

        if (used == buffer.Length)
            return buffer;

        var trimmed = new KeyValuePair<string, object?>[used];
        Array.Copy(buffer, trimmed, used);
        return trimmed;
    }

    private static string Render(object state)
    {
        try
        {
            return Convert.ToString(state, CultureInfo.InvariantCulture) ?? state.GetType().Name;
        }
        catch (Exception ex)
        {
            // ToString belongs to the caller. Keep the scope, without their exception text.
            SelfLog.Report("Scope", ex);
            return state.GetType().Name;
        }
    }

    private static void ReportLimit()
    {
        SelfLog.Report(
            "LogContext",
            new InvalidOperationException(
                "Scope property limit of " + MaxProperties + " exceeded; extra keys were dropped."));
    }
}
