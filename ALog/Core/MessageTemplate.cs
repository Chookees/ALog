// Message templates for ALog's own Log.Write path.
// Hole names are letters, digits, and underscores. A leading '@' is not an operator.
// ReadPropertyName only strips braces and a format suffix. The MEL bridge strips one '@' or '$' after that.
// Next: LoggerEngine.cs.
namespace ALog.Core;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Parses Serilog-style message templates: "User {UserId} logged in".
/// </summary>
public static class MessageTemplate
{
    private static readonly Regex HolePattern = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<format>:[^}]*)?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly ConcurrentDictionary<string, CompiledTemplate> TemplateCache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, object> EmptyProperties = new(0);

    [ThreadStatic]
    private static StringBuilder? _render;

    [ThreadStatic]
    private static int _rendering;

    public static (string Rendered, IReadOnlyDictionary<string, object> Properties) Format(
        string template,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (args.Length == 0)
            return (template, EmptyProperties);

        var compiled = TemplateCache.GetOrAdd(template, static text => Compile(text));
        if (compiled.Holes == 0)
            return (template, EmptyProperties);

        var properties = new Dictionary<string, object>(compiled.Holes, StringComparer.Ordinal);
        var nested = _rendering++ > 0;
        var builder = nested ? new StringBuilder(64) : _render ??= new StringBuilder(128);
        if (!nested)
            builder.Clear();
        try
        {
            var argIndex = 0;
            var pieces = compiled.Pieces;
            for (var i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                if (!piece.IsHole)
                {
                    builder.Append(piece.Literal);
                    continue;
                }

                if (argIndex >= args.Length)
                {
                    builder.Append(piece.Raw);
                    continue;
                }

                var value = args[argIndex];
                argIndex++;
                properties[piece.Name!] = value ?? string.Empty;
                builder.Append(RenderArgument(piece.Name!, value, piece.Format));
            }
        }
        finally
        {
            _rendering--;
        }

        // Positional leftovers (no holes left) are ignored; unused holes keep raw text.
        return (builder.ToString(), properties);
    }

    /// <summary>Parses <paramref name="template"/> once. The result is immutable and safe to share.</summary>
    private static CompiledTemplate Compile(string template)
    {
        var matches = HolePattern.Matches(template);
        var pieces = new Piece[matches.Count == 0 ? 1 : matches.Count * 2 + 1];
        var count = 0;
        var index = 0;
        var holes = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.Index > index)
                pieces[count++] = new Piece(template.Substring(index, match.Index - index), null, null, null);

            var format = match.Groups["format"].Success
                ? match.Groups["format"].Value.TrimStart(':')
                : null;
            pieces[count++] = new Piece(null, match.Groups["name"].Value, format, match.Value);
            holes++;
            index = match.Index + match.Length;
        }

        if (index < template.Length || count == 0)
            pieces[count++] = new Piece(index == 0 ? template : template.Substring(index), null, null, null);

        if (count != pieces.Length)
            Array.Resize(ref pieces, count);

        return new CompiledTemplate(pieces, holes);
    }

    private sealed class CompiledTemplate(Piece[] pieces, int holes)
    {
        public Piece[] Pieces { get; } = pieces;
        public int Holes { get; } = holes;
    }

    private readonly struct Piece(string? literal, string? name, string? format, string? raw)
    {
        public string? Literal { get; } = literal;
        public string? Name { get; } = name;
        public string? Format { get; } = format;
        public string? Raw { get; } = raw;
        public bool IsHole => Name is not null;
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
