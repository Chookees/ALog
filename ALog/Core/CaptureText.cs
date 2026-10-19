namespace ALog.Core;

/// <summary>
/// Turns one logged value into text for a plain line.
/// A throw becomes <c>[capture failed: ExceptionType]</c> and does not escape.
/// </summary>
internal static class CaptureText
{
    // shortcut: a plain line keeps at most 1024 pairs so the loop has a fixed bound.
    internal const int MaxValues = 1024;

    /// <summary>
    /// Reads one property or scope value.
    /// Null becomes empty. A throw becomes <c>[capture failed: ExceptionType]</c> using the short type name.
    /// </summary>
    /// <param name="value">Value from the event or from context. Null is an empty value.</param>
    /// <returns>Text safe to place after <c>=</c>. Never null. The exception message is not included.</returns>
    /// <remarks>Does not call SelfLog. Does not throw. Does not replace the object on the event.</remarks>
    internal static string Read(object? value)
    {
        if (value is null)
            return string.Empty;

        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return "[capture failed: " + ex.GetType().Name + "]";
        }
    }

    /// <summary>
    /// Renders pairs as <c>name=text</c>, separated by a comma and a space.
    /// </summary>
    /// <param name="values">Properties or context. Not null. Not modified.</param>
    /// <returns>The list. Empty when the bag is empty. At most <see cref="MaxValues"/> pairs.</returns>
    /// <remarks>
    /// A throwing value uses <see cref="Read"/>. Does not call SelfLog.
    /// Allocates one string per rendered pair.
    /// </remarks>
    internal static string Join(IReadOnlyDictionary<string, object> values)
    {
        var capacity = values.Count < MaxValues ? values.Count : MaxValues;
        var rendered = new List<string>(capacity);
        foreach (var pair in values)
        {
            if (rendered.Count >= MaxValues)
                break;

            rendered.Add(pair.Key + "=" + Read(pair.Value));
        }

        return string.Join(", ", rendered);
    }
}
