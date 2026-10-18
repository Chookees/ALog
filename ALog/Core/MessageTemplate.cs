// Message templates for ALog's own Log.Write path.
// Hole names are letters, digits, and underscores. A leading '@' is not an operator.
// ReadPropertyName only strips braces and a format suffix. The MEL bridge strips one '@' or '$' after that.
// Next: LoggerEngine.cs.
namespace ALog.Core;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Parses Serilog-style message templates: "User {UserId} logged in".
/// </summary>
public static class MessageTemplate
{
    private static readonly Regex HolePattern = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<format>:[^}]*)?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (string Rendered, IReadOnlyDictionary<string, object> Properties) Format(
        string template,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(template);

        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        if (args.Length == 0)
            return (template, properties);

        var argIndex = 0;
        var rendered = HolePattern.Replace(template, match =>
        {
            if (argIndex >= args.Length)
                return match.Value;

            var name = match.Groups["name"].Value;
            var value = args[argIndex];
            argIndex++;
            properties[name] = value ?? string.Empty;

            var format = match.Groups["format"].Success
                ? match.Groups["format"].Value.TrimStart(':')
                : null;
            return RenderArgument(name, value, format);
        });

        // Positional leftovers (no holes left) are ignored; unused holes keep raw text.
        return (rendered, properties);
    }

    private static string RenderArgument(string name, object? value, string? format)
    {
        try
        {
            if (value is null)
                return string.Empty;

            if (!string.IsNullOrEmpty(format) && value is IFormattable formattable)
                return formattable.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Convert.ToString / IFormattable can throw. Keep the rest of the message.
            SelfLog.Report("Message template argument '" + name + "'", ex);
            return "{" + name + ":<format error>}";
        }
    }

    public static bool HasHoles(string template) =>
        !string.IsNullOrEmpty(template) && HolePattern.IsMatch(template);

    /// <summary>
    /// Reads the property name stored for one MEL state key.
    /// Braces and a <c>:format</c> suffix are removed. <c>@</c> stays, because <see cref="HolePattern"/>
    /// does not treat it as an operator: <c>{@Order}</c> is not a hole, so the name is <c>@Order</c>.
    /// </summary>
    /// <param name="key">State key, for example <c>Method</c>, <c>{Elapsed:0.00}</c>, or <c>{0}</c>. Null and empty return null.</param>
    /// <returns>The name, or null when the key is empty or only a format suffix.</returns>
    /// <remarks>
    /// No allocation beyond the returned string. Does not throw. Safe to call from any thread.
    /// </remarks>
    internal static string? ReadPropertyName(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var start = 0;
        var end = key.Length;
        if (end >= 2 && key[0] == '{' && key[end - 1] == '}')
        {
            start = 1;
            end -= 1;
        }

        // The hole regex's format group is ":...". The first colon ends the name.
        var colon = key.IndexOf(':', start, end - start);
        if (colon >= 0)
            end = colon;

        if (end <= start)
            return null;

        return key.Substring(start, end - start);
    }
}
