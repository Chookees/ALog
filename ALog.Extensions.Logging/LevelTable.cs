namespace ALog.Extensions.Logging;

using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;
using ALogLevel = ALog.LogLevel;

/// <summary>
/// Snapshot of the global minimum and category rules.
/// Built at provider init and when <see cref="LoggerFilterOptions"/> reloads.
/// The log path reads the cached ints; it does not walk rules.
/// </summary>
internal sealed class LevelTable
{
    internal const int Disabled = int.MaxValue;

    // No matching rule: the caller substitutes the live global minimum.
    internal const int UseGlobal = -1;

    private const int MaxRules = 256;

    private static readonly string ProviderFullName =
        typeof(ALogLoggerProvider).FullName ?? nameof(ALogLoggerProvider);

    private readonly Rule[] _rules;

    private LevelTable(int generation, int ruleFloor, Rule[] rules)
    {
        Generation = generation;
        RuleFloor = ruleFloor;
        _rules = rules;
    }

    public int Generation { get; }

    public int RuleFloor { get; }

    public static LevelTable Open()
        => new(0, Disabled, Array.Empty<Rule>());

    public static LevelTable Capture(LoggerFilterOptions? options, int generation)
    {
        if (options is null || options.Rules.Count == 0)
            return new LevelTable(generation, Disabled, Array.Empty<Rule>());

        return CaptureRules(options.Rules, generation);
    }

    public int Threshold(string category)
    {
        var found = false;
        var bestProvider = false;
        var bestLength = -1;
        var best = 0;

        for (var i = 0; i < _rules.Length; i++)
        {
            var rule = _rules[i];
            if (!rule.Matches(category))
                continue;

            if (!found || Beats(rule, bestProvider, bestLength))
            {
                found = true;
                bestProvider = rule.ProviderSpecific;
                bestLength = rule.CategoryLength;
                best = rule.Threshold;
            }
        }

        return found ? best : UseGlobal;
    }

    private static bool Beats(in Rule rule, bool bestProvider, int bestLength)
    {
        if (rule.ProviderSpecific && !bestProvider)
            return true;

        if (rule.ProviderSpecific != bestProvider)
            return false;

        return rule.CategoryLength >= bestLength;
    }

    private static LevelTable CaptureRules(IList<LoggerFilterRule> rules, int generation)
    {
        var count = rules.Count;
        var start = 0;
        if (count > MaxRules)
        {
            // Later rules win on a tie, so keep the tail if the list is huge.
            start = count - MaxRules;
            count = MaxRules;
        }

        var built = new Rule[count];
        var used = 0;
        var ruleFloor = Disabled;
        for (var i = 0; i < count; i++)
        {
            var source = rules[start + i];
            if (!TryMap(source, out var rule))
                continue;

            built[used++] = rule;
            if (rule.Threshold < ruleFloor)
                ruleFloor = rule.Threshold;
        }

        if (used == 0)
            return new LevelTable(generation, Disabled, Array.Empty<Rule>());

        if (used != built.Length)
        {
            var trimmed = new Rule[used];
            Array.Copy(built, trimmed, used);
            built = trimmed;
        }

        return new LevelTable(generation, ruleFloor, built);
    }

    private static bool TryMap(LoggerFilterRule source, out Rule rule)
    {
        rule = default;
        if (source.ProviderName is not null && !IsOurProvider(source.ProviderName))
            return false;

        // Two wildcards are not a pattern we can apply. Skip the rule entirely
        // so it cannot lower the floor for every category.
        if (source.CategoryName is { } name && HasTwoWildcards(name))
            return false;

        if (source.LogLevel is null && source.Filter is null)
            return false;

        var threshold = ThresholdOf(source);
        rule = new Rule(source.ProviderName, source.CategoryName, threshold);
        return true;
    }

    private static int ThresholdOf(LoggerFilterRule rule)
    {
        if (rule.LogLevel == MelLogLevel.None)
            return Disabled;

        if (rule.LogLevel is { } level)
            return (int)LogLevelMapper.ToALog(level);

        // A custom filter can accept any level. Do not drop it at the gate.
        return (int)ALogLevel.Trace;
    }

    private static bool IsOurProvider(string providerName)
        => string.Equals(providerName, ProviderFullName, StringComparison.Ordinal)
            || string.Equals(providerName, nameof(ALogLoggerProvider), StringComparison.Ordinal);

    private readonly struct Rule
    {
        public Rule(string? providerName, string? category, int threshold)
        {
            ProviderSpecific = providerName is not null;
            Category = category;
            Threshold = threshold;
        }

        public bool ProviderSpecific { get; }
        public string? Category { get; }
        public int Threshold { get; }
        public int CategoryLength => Category is null ? -1 : Category.Length;

        public bool Matches(string category)
        {
            if (Category is null)
                return true;

            return CategoryFits(Category, category);
        }
    }

    private static bool HasTwoWildcards(string pattern)
    {
        var star = pattern.IndexOf('*');
        return star >= 0 && pattern.IndexOf('*', star + 1) >= 0;
    }

    private static bool CategoryFits(string pattern, string category)
    {
        var star = pattern.IndexOf('*');
        if (star < 0)
            return category.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

        if (HasTwoWildcards(pattern))
            return false;

        var prefix = pattern.AsSpan(0, star);
        var suffix = pattern.AsSpan(star + 1);
        return category.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && category.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
