// BEN-04 self-check: the file holds exactly the expected number of events, and the first, a middle and the last
// event carry every field from BEN-03. Any mismatch makes the cell invalid.
using System.Globalization;
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

    [GeneratedRegex("User \"?(?<id>\\d+)\"? logged in from", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdText();

    /// <summary>
    /// Verifies <paramref name="path"/> against <paramref name="sc"/>.
    /// Every record start is parsed as a UTC timestamp. A single-thread run must not go backwards.
    /// A single-thread structured run must number users 0, 1, 2, … through warm-up and the measured calls.
    /// </summary>
    /// <param name="path">Output file. A missing file is a failure.</param>
    /// <param name="sc">Scenario. Not null. Multi-thread runs skip the clock order.</param>
    /// <returns>Counts and the failure list. The list stops at 32 stored problems.</returns>
    /// <remarks>
    /// Does not throw. Writes one <c>Verify:</c> line to standard error: <c>ok</c> or the joined problems.
    /// Only the cell thread calls this. The file is read once.
    /// </remarks>
    public static VerifyResult Run(string path, Scenario sc)
    {
        var failures = new List<string>();
        if (!File.Exists(path))
        {
            failures.Add("output file missing");
            Console.Error.WriteLine("Verify: output file missing");
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
        var dropped = 0;
        var wanted = new SortedSet<long> { 0, Math.Max(0, (expected - 1) / 2), Math.Max(0, expected - 1) };
        var samples = new Dictionary<long, StringBuilder>();
        StringBuilder? current = null;
        DateTimeOffset? previous = null;
        var checkOrder = sc.Threads == 1;
        var checkSequence = sc.Call == CallKind.Structured && sc.Threads == 1 && !sc.Filtered;
        long expectUser = 0;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var start = sc.Format == Fmt.Json ? line.Length > 0 && line[0] == '{' : PlainStart().IsMatch(line);
            if (start)
            {
                var index = records++;
                if (line.Contains(token, StringComparison.Ordinal))
                    tokenRecords++;
                if (checkOrder || checkSequence)
                    NoteStart(line, sc, index, checkOrder, checkSequence, ref previous, ref expectUser, failures, ref dropped);
                current = null;
                if (wanted.Contains(index))
                {
                    current = new StringBuilder(line);
                    samples[index] = current;
                }
            }
            else if (line.Length > 0 && sc.Format == Fmt.Json)
            {
                AddFailure(failures, ref dropped, "JSON line that does not start a record");
            }
            else if (current is not null && line.Length > 0)
            {
                current.Append('\n').Append(line);
            }
        }

        if (records != expected)
            AddFailure(failures, ref dropped, $"records {records} != expected {expected}");
        if (tokenRecords != expected)
            AddFailure(failures, ref dropped, $"records with message text {tokenRecords} != expected {expected}");
        if (checkSequence && records == expected && expectUser != expected)
            AddFailure(failures, ref dropped, $"sequence ended at {expectUser}, expected {expected}");
        foreach (var (index, text) in samples)
        {
            var isSentinel = sc.Filtered;
            foreach (var f in CheckFields(text.ToString(), sc, isSentinel))
                AddFailure(failures, ref dropped, $"record {index}: {f}");
        }

        if (dropped > 0)
            failures.Add("further failures omitted: " + dropped.ToString(CultureInfo.InvariantCulture));
        var reason = failures.Count == 0 ? "ok" : string.Join("; ", failures);
        Console.Error.WriteLine("Verify: " + reason);
        return new VerifyResult(records, tokenRecords, bytes, failures);
    }

    /// <summary>
    /// Checks one record start for a real UTC timestamp and, on a single-thread structured run, the next user id.
    /// </summary>
    /// <param name="line">The record's first line. Not null.</param>
    /// <param name="sc">Scenario that decided plain or JSON. Not null.</param>
    /// <param name="index">Zero-based record number in the file.</param>
    /// <param name="checkOrder">True when this scenario has one writer thread. Multi-thread runs skip the clock order.</param>
    /// <param name="checkSequence">True when user ids must be 0, 1, 2, … across warm-up and the measured run.</param>
    /// <param name="previous">The previous UTC stamp, or null for the first record.</param>
    /// <param name="expectUser">The next user id. Advanced when the id matches.</param>
    /// <param name="failures">Where a problem is stored. Not null. Capped by <see cref="AddFailure"/>.</param>
    /// <param name="dropped">How many problems were not stored.</param>
    /// <remarks>
    /// Does not throw. A JSON line is parsed once. A plain stamp is the text between the first brackets.
    /// The offset must be zero. An equal stamp is in order. Only the verifying thread calls this.
    /// </remarks>
    private static void NoteStart(
        string line,
        Scenario sc,
        long index,
        bool checkOrder,
        bool checkSequence,
        ref DateTimeOffset? previous,
        ref long expectUser,
        List<string> failures,
        ref int dropped)
    {
        if (!TryStart(line, sc, out var stamp, out var hasUser, out var userId, out var problem))
        {
            AddFailure(failures, ref dropped, "record " + index.ToString(CultureInfo.InvariantCulture) + ": " + problem);
            return;
        }

        if (checkOrder)
        {
            if (previous is DateTimeOffset prior && stamp < prior)
            {
                AddFailure(
                    failures,
                    ref dropped,
                    "record " + index.ToString(CultureInfo.InvariantCulture) + ": timestamp went backwards");
            }

            previous = stamp;
        }

        if (!checkSequence)
            return;

        if (!hasUser || userId != expectUser)
        {
            AddFailure(
                failures,
                ref dropped,
                "record " + index.ToString(CultureInfo.InvariantCulture) + ": sequence " + (hasUser ? userId.ToString(CultureInfo.InvariantCulture) : "missing") + " != " + expectUser.ToString(CultureInfo.InvariantCulture));
            return;
        }

        expectUser++;
    }

    /// <summary>Reads the UTC stamp and, when the line is structured, the user id.</summary>
    /// <param name="line">Record start. Not null.</param>
    /// <param name="sc">Plain or JSON. Not null.</param>
    /// <param name="stamp">The UTC stamp when the result is true.</param>
    /// <param name="hasUser">True when a user id was present.</param>
    /// <param name="userId">The user id when <paramref name="hasUser"/> is true.</param>
    /// <param name="problem">Why the line cannot be used. Empty when the result is true.</param>
    /// <returns>False when the stamp is missing, not a real timestamp, or not UTC.</returns>
    /// <remarks>Does not throw. JSON uses <see cref="JsonDocument"/>. Plain text does not. Safe for one verifying thread.</remarks>
    private static bool TryStart(string line, Scenario sc, out DateTimeOffset stamp, out bool hasUser, out long userId, out string problem)
    {
        stamp = default;
        hasUser = false;
        userId = 0;
        problem = "";
        if (sc.Format == Fmt.Json)
            return TryJsonStart(line, out stamp, out hasUser, out userId, out problem);

        if (!TryPlainStamp(line, out stamp))
        {
            problem = "timestamp missing or not UTC";
            return false;
        }

        var match = UserIdText().Match(line);
        if (match.Success && long.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
            hasUser = true;
        return true;
    }

    /// <summary>The text between the first <c>[</c> and <c>]</c> must be a UTC timestamp.</summary>
    /// <param name="line">Plain record start. Not null.</param>
    /// <param name="stamp">The stamp when the result is true.</param>
    /// <returns>False when the brackets or the timestamp are missing, or the offset is not zero.</returns>
    /// <remarks>Does not throw. Does not read the rest of the line. Safe for one verifying thread.</remarks>
    private static bool TryPlainStamp(string line, out DateTimeOffset stamp)
    {
        stamp = default;
        if (line.Length < 2 || line[0] != '[')
            return false;
        var end = line.IndexOf(']');
        if (end <= 1)
            return false;
        return TryUtc(line.Substring(1, end - 1), out stamp);
    }

    /// <summary>Parses one JSON record for its timestamp and user id.</summary>
    /// <param name="line">One JSON object. Not null.</param>
    /// <param name="stamp">The UTC stamp when the result is true.</param>
    /// <param name="hasUser">True when the rendered message contains a user id.</param>
    /// <param name="userId">That id.</param>
    /// <param name="problem">Why the line was rejected. Empty on success.</param>
    /// <returns>False when the JSON or the timestamp cannot be used.</returns>
    /// <remarks>Does not throw. The document is disposed before this returns. Safe for one verifying thread.</remarks>
    private static bool TryJsonStart(string line, out DateTimeOffset stamp, out bool hasUser, out long userId, out string problem)
    {
        stamp = default;
        hasUser = false;
        userId = 0;
        problem = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var ts = FindString(doc.RootElement, "timestamp");
            if (ts is null || !TryUtc(ts, out stamp))
            {
                problem = "timestamp missing or not UTC: " + (ts ?? "<none>");
                return false;
            }

            var msg = FindString(doc.RootElement, "message") ?? FindString(doc.RootElement, "renderedMessage");
            if (msg is not null)
            {
                var match = UserIdText().Match(msg);
                if (match.Success && long.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
                    hasUser = true;
            }

            return true;
        }
        catch (JsonException ex)
        {
            problem = "invalid JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>True when <paramref name="text"/> is a real timestamp whose offset is zero.</summary>
    /// <param name="text">Timestamp text. Not null.</param>
    /// <param name="stamp">The value when the result is true.</param>
    /// <returns>False for a local offset, an unparsable string, or an empty string.</returns>
    /// <remarks>Does not throw. Uses the invariant culture and the round-trip style. Safe for any thread.</remarks>
    private static bool TryUtc(string text, out DateTimeOffset stamp)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out stamp))
            return false;
        return stamp.Offset == TimeSpan.Zero;
    }

    /// <summary>Stores one problem until 32 are stored, then counts the rest.</summary>
    /// <param name="failures">The stored problems. Not null.</param>
    /// <param name="dropped">Incremented once the list is full.</param>
    /// <param name="text">The problem. Not null.</param>
    /// <remarks>Does not throw. The cap keeps a bad file from growing the result without a bound. Only the verifying thread calls this.</remarks>
    private static void AddFailure(List<string> failures, ref int dropped, string text)
    {
        if (failures.Count < 32)
            failures.Add(text);
        else
            dropped++;
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

                // ALog and NLog call it "message". Serilog's ExpressionTemplate writes both "message" and "renderedMessage".
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
