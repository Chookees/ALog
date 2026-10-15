namespace ALog.Core;

using System.Globalization;
using System.Text.RegularExpressions;
using ALog.Internal;

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
}
