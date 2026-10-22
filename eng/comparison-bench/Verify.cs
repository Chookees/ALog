// BEN-04 self-check: the file holds exactly the expected number of events, and the first, a middle and the last
// event carry every field from BEN-03. Any mismatch makes the cell invalid.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlogComparison;

/// <summary>Result of verifying one output file.</summary>
public sealed record VerifyResult(long Records, long TokenRecords, long FileBytes, List<string> Failures)
{
    /// <summary>True when there is no failure.</summary>
    public bool Ok => Failures.Count == 0;
}

/// <summary>Counts and samples events in an output file.</summary>
public static partial class Verify
{
    // A plain record starts with "[<ISO-8601 UTC timestamp>]". Continuation lines (stack traces) never do.
    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|\+00:00)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PlainStart();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|\+00:00)$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoUtc();

    [GeneratedRegex("User \"?\\d+\"? logged in from \"?192\\.168\\.1\\.10\"?", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredText();

    /// <summary>Verifies <paramref name="path"/> against <paramref name="sc"/>.</summary>
    public static VerifyResult Run(string path, Scenario sc)
    {
        var failures = new List<string>();
        if (!File.Exists(path))
        {
            failures.Add("output file missing");
            return new VerifyResult(0, 0, 0, failures);
        }

        var bytes = new FileInfo(path).Length;
        var token = sc.Filtered ? Texts.Sentinel : sc.Call switch
        {
            CallKind.Simple => Texts.Simple,
            CallKind.Structured => " logged in from ",
            _ => Texts.Error,
        };
        var expected = sc.ExpectedRecords;
        long records = 0, tokenRecords = 0;
        var wanted = new SortedSet<long> { 0, Math.Max(0, (expected - 1) / 2), Math.Max(0, expected - 1) };
        var samples = new Dictionary<long, StringBuilder>();
        StringBuilder? current = null;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var start = sc.Format == Fmt.Json ? line.Length > 0 && line[0] == '{' : PlainStart().IsMatch(line);
            if (start)
            {
                var index = records++;
                if (line.Contains(token, StringComparison.Ordinal))
                    tokenRecords++;
                current = null;
                if (wanted.Contains(index))
                {
                    current = new StringBuilder(line);
                    samples[index] = current;
                }
            }
            else if (line.Length > 0 && sc.Format == Fmt.Json)
            {
                failures.Add("JSON line that does not start a record");
            }
            else if (current is not null && line.Length > 0)
            {
                current.Append('\n').Append(line);
            }
        }

        if (records != expected)
            failures.Add($"records {records} != expected {expected}");
        if (tokenRecords != expected)
            failures.Add($"records with message text {tokenRecords} != expected {expected}");
        foreach (var (index, text) in samples)
        {
            var isSentinel = sc.Filtered;
            foreach (var f in CheckFields(text.ToString(), sc, isSentinel))
                failures.Add($"record {index}: {f}");
        }

        if (failures.Count > 8)
            failures.RemoveRange(8, failures.Count - 8);
        return new VerifyResult(records, tokenRecords, bytes, failures);
    }

    private static IEnumerable<string> CheckFields(string record, Scenario sc, bool sentinel)
    {
        var level = sentinel ? "warn" : sc.Call == CallKind.Error ? "error" : "info";
        if (sc.Format == Fmt.Json)
        {
            JsonDocument doc;
            string? fail = null;
            try
            {
                doc = JsonDocument.Parse(record);
            }
            catch (JsonException e)
            {
                doc = null!;
                fail = "invalid JSON: " + e.Message;
            }

            if (fail is not null)
            {
                yield return fail;
                yield break;
            }

            using (doc)
            {
                var ts = FindString(doc.RootElement, "timestamp");
                if (ts is null || !IsoUtc().IsMatch(ts))
                    yield return "timestamp missing or not ISO-8601 UTC: " + (ts ?? "<none>");
                var lv = FindString(doc.RootElement, "level");
                if (lv is null || !lv.StartsWith(level, StringComparison.OrdinalIgnoreCase))
                    yield return "level missing: " + (lv ?? "<none>");

                // ALog and NLog call it "message", Serilog's JsonFormatter "RenderedMessage" (strings quoted).
                var msg = FindString(doc.RootElement, "message") ?? FindString(doc.RootElement, "renderedMessage");
                if (msg is null || !StructuredText().IsMatch(msg))
                    yield return "rendered message missing: " + (msg ?? "<none>");
            }

            if (!record.Contains("\"" + Texts.Category + "\"", StringComparison.Ordinal))
                yield return "category missing";
            if (!record.Contains(Texts.Ip, StringComparison.Ordinal))
                yield return "property Ip missing";
            yield break;
        }

        var head = record.Split('\n')[0];
        if (!PlainStart().IsMatch(head))
            yield return "timestamp missing";
        if (head.IndexOf(level, StringComparison.OrdinalIgnoreCase) < 0)
            yield return "level missing";
        if (!head.Contains("[" + Texts.Category + "]", StringComparison.Ordinal))
            yield return "category missing";
        if (sentinel)
        {
            if (!head.Contains(Texts.Sentinel, StringComparison.Ordinal))
                yield return "message missing";
            yield break;
        }

        switch (sc.Call)
        {
            case CallKind.Simple:
                if (!head.Contains(Texts.Simple, StringComparison.Ordinal))
                    yield return "message missing";
                break;
            case CallKind.Structured:
                if (!StructuredText().IsMatch(head))
                    yield return "rendered message with values missing";
                break;
            default:
                if (!head.Contains(Texts.Error, StringComparison.Ordinal))
                    yield return "message missing";
                // Every library writes its standard exception format (DC 2026-10-22). Required: all three types of
                // the chain. Stack frames are not required because ALog's standard format is one line.
                foreach (var t in Texts.ChainTypes)
                {
                    if (!record.Contains(t, StringComparison.Ordinal))
                        yield return "exception type missing: " + t;
                }

                break;
        }
    }

    /// <summary>First string property whose name equals <paramref name="name"/> ignoring case, searched depth-first.</summary>
    private static string? FindString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        }

        foreach (var p in e.EnumerateObject())
        {
            var inner = FindString(p.Value, name);
            if (inner is not null)
                return inner;
        }

        return null;
    }
}
