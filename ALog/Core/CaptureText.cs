namespace ALog.Core;

using System.Globalization;

/// <summary>
/// Turns one logged value into text for a plain line.
/// A throw becomes <c>[capture failed: ExceptionType]</c> and does not escape.
/// </summary>
internal static class CaptureText
{
    /// <summary>
    /// Reads one property or scope value.
    /// An <see cref="IFormattable"/> value uses <see cref="IFormattable.ToString(string?, IFormatProvider?)"/>
    /// with a null format and <see cref="CultureInfo.CurrentCulture"/>. Any other value uses <see cref="object.ToString"/>.
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
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.CurrentCulture) ?? string.Empty;

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
    /// <returns>The list. Empty when the bag is empty. Every pair is included.</returns>
    /// <remarks>
    /// A throwing value uses <see cref="Read"/>. Does not call SelfLog.
    /// The loop is bounded by <paramref name="values"/> count.
    /// Allocates one string per pair.
    /// </remarks>
    internal static string Join(IReadOnlyDictionary<string, object> values)
    {
        var rendered = new List<string>(values.Count);
        foreach (var pair in values)
            rendered.Add(pair.Key + "=" + Read(pair.Value));

        return string.Join(", ", rendered);
    }
}
