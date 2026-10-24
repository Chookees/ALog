// Full-N ranked speed checks. One fresh comparison process per cell.
// ALog async median total_ms of 3 runs must be <= the fastest valid competitor, with a 5% tie.
// An invalid competitor fails the scenario and does not produce a rank.
// The cells run only when ALOG_SPEED=1. Required CI builds this project and does not run them.
// Next file: eng/comparison-bench/Cell.cs (the RESULT line these tests parse).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AlogComparison.SpeedTests;

/// <summary>
/// AC-1 through AC-7, plus the unranked alog-sync neighbour.
/// Cells run one at a time so the measured processes do not share the disk or the CPU.
/// </summary>
public sealed class RankedSpeedTests
{
    /// <summary>How many timed runs make one median. The acceptance rule uses this count.</summary>
    public const int Runs = 3;

    /// <summary>ALog may be this factor times the fastest competitor and still tie.</summary>
    public const double TieFactor = 1.05;

    private static readonly TimeSpan CellTimeout = TimeSpan.FromSeconds(1800);

    /// <summary>One cell at a time. The next cell waits until this one has exited.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>AC-1. Plain 1,000,000 Info lines, including log4net.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T1_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T1", includeLog4Net: true);
    }

    /// <summary>AC-2. Structured 1,000,000 Info lines. log4net is not a competitor.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T2_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T2", includeLog4Net: false);
    }

    /// <summary>AC-3. 100,000 errors with a three-level exception chain.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T3_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T3", includeLog4Net: true);
    }

    /// <summary>AC-4. Eight threads writing 125,000 plain Info lines each.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T4_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T4", includeLog4Net: true);
    }

    /// <summary>AC-5. 10,000,000 calls below Warn, then one Warn sentinel.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T5_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T5", includeLog4Net: true);
    }

    /// <summary>AC-6. Cold process: init, one event, flush, dispose.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T6_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T6", includeLog4Net: true);
    }

    /// <summary>AC-7. Structured 1,000,000 lines as JSON. log4net is not a competitor.</summary>
    /// <returns>A task that finishes when the three runs of each library have been ranked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is eight hours. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 28_800_000)]
    public async Task T7_AlogAsync_TotalMs_AtMostFastest()
    {
        SkipUnlessSpeed();
        await AssertRank("T7", includeLog4Net: false);
    }

    /// <summary>AC-8 neighbour. One alog-sync T1 cell must stay valid. It has no speed rank.</summary>
    /// <returns>A task that finishes when that one cell has been checked.</returns>
    /// <remarks>Skips unless <c>ALOG_SPEED=1</c>. The wait bound is one hour. Not thread-safe.</remarks>
    [SkippableFact(Timeout = 3_600_000)]
    public async Task Neighbor_AlogSync_T1_Valid()
    {
        SkipUnlessSpeed();
        var root = RepoRoot();
        var dll = ComparisonDll(root);
        var row = await RunCell(root, dll, "alog-sync", "T1", 1);
        Assert.True(
            row.Valid == "true" && row.N == ExpectedN("T1"),
            "alog-sync T1 must be valid at full N. " + row.Describe());
    }

    /// <summary>The middle of three totals. The rank uses this, not the mean.</summary>
    /// <param name="a">One run. Any finite total.</param>
    /// <param name="b">One run. Any finite total.</param>
    /// <param name="c">One run. Any finite total.</param>
    /// <returns>The middle value.</returns>
    /// <remarks>Does not throw. No allocation. Safe for any thread.</remarks>
    public static double Median(double a, double b, double c)
    {
        if (a > b)
            (a, b) = (b, a);
        if (b > c)
            (b, c) = (c, b);
        if (a > b)
            (a, b) = (b, a);
        return b;
    }

    /// <summary>
    /// Why the median fails the 5% tie, or null when ALog is within the tie.
    /// </summary>
    /// <param name="alogMedian">Median <c>total_ms</c> of the ALog async runs.</param>
    /// <param name="fastestMedian">Median <c>total_ms</c> of the fastest valid competitor.</param>
    /// <param name="fastestLib">That competitor's id. Not null.</param>
    /// <returns>Null when <paramref name="alogMedian"/> is at most <see cref="TieFactor"/> times the fastest median.</returns>
    /// <remarks>Does not throw. Does not rank an invalid file; the caller must not call this then. Safe for any thread.</remarks>
    public static string? RankProblem(double alogMedian, double fastestMedian, string fastestLib)
    {
        var limit = fastestMedian * TieFactor;
        if (alogMedian <= limit)
            return null;
        return FormattableString.Invariant(
            $"alog-async median {alogMedian:0.000} ms > fastest {fastestLib} median {fastestMedian:0.000} ms * {TieFactor:0.00} = {limit:0.000} ms");
    }

    /// <summary>The sentence printed for a computed rank, including a pass inside the 5% tie.</summary>
    /// <param name="alogMedian">Median <c>total_ms</c> of the ALog async runs.</param>
    /// <param name="fastestMedian">Median <c>total_ms</c> of the fastest valid competitor.</param>
    /// <param name="fastestLib">That competitor's id. Not null.</param>
    /// <returns>One sentence. It names both medians, the limit, and why the comparison passed or failed.</returns>
    /// <remarks>Does not throw. Safe for any thread.</remarks>
    public static string RankReason(double alogMedian, double fastestMedian, string fastestLib)
    {
        var limit = fastestMedian * TieFactor;
        var how = alogMedian > limit
            ? "slower than the 5% tie"
            : alogMedian <= fastestMedian
                ? "at least as fast"
                : "within the 5% tie";
        return FormattableString.Invariant(
            $"alog-async median {alogMedian:0.000} ms, fastest {fastestLib} median {fastestMedian:0.000} ms, limit {limit:0.000} ms (5% tie), {how}");
    }

    /// <summary>Skips the test unless the operator set <c>ALOG_SPEED=1</c>.</summary>
    /// <remarks>
    /// Does not return when the variable is anything else. Required CI does not set it, so these cells stay skipped.
    /// Not thread-safe. Call it before any cell starts.
    /// </remarks>
    private static void SkipUnlessSpeed()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("ALOG_SPEED"), "1", StringComparison.Ordinal),
            "Set ALOG_SPEED=1 to run the ranked speed checks. They are not part of required CI.");
    }

    /// <summary>
    /// Median of three <c>total_ms</c> values. An equal median passes. A median inside 5% passes and says so.
    /// A competitor whose <c>valid</c> is not true fails the scenario. The rank is not computed in that case.
    /// </summary>
    /// <param name="scenario">T1 through T7. Not null.</param>
    /// <param name="includeLog4Net">True when log4net supports the scenario.</param>
    /// <returns>A task that finishes after every cell of the scenario.</returns>
    /// <remarks>Does not throw for a slow cell. The assertion carries the reason. One scenario at a time. Not thread-safe.</remarks>
    private static async Task AssertRank(string scenario, bool includeLog4Net)
    {
        var root = RepoRoot();
        var dll = ComparisonDll(root);
        var expected = ExpectedN(scenario);
        var libs = new List<string> { "alog-async", "nlog", "serilog" };
        if (includeLog4Net)
            libs.Add("log4net");

        var rows = new List<CellRow>(libs.Count * Runs);
        foreach (var lib in libs)
        {
            for (var run = 1; run <= Runs; run++)
                rows.Add(await RunCell(root, dll, lib, scenario, run));
        }

        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (row.N != expected)
            {
                problems.Add(
                    row.Lib + " run " + row.Run.ToString(CultureInfo.InvariantCulture)
                    + " n=" + row.N.ToString(CultureInfo.InvariantCulture)
                    + " expected " + expected.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.Equals(row.Valid, "true", StringComparison.Ordinal))
            {
                problems.Add(
                    row.Lib + " run " + row.Run.ToString(CultureInfo.InvariantCulture)
                    + " valid=" + row.Valid + " reason=" + (row.Reason ?? "") + "; rank not computed");
            }
        }

        string verdict;
        if (problems.Count > 0)
        {
            verdict = "rank not computed";
        }
        else
        {
            var alogMedian = MedianOf(rows, "alog-async");
            var fastestLib = "";
            var fastest = double.MaxValue;
            for (var i = 0; i < libs.Count; i++)
            {
                if (libs[i] == "alog-async")
                    continue;
                var median = MedianOf(rows, libs[i]);
                if (median < fastest)
                {
                    fastest = median;
                    fastestLib = libs[i];
                }
            }

            verdict = RankReason(alogMedian, fastest, fastestLib);
            var problem = RankProblem(alogMedian, fastest, fastestLib);
            if (problem is not null)
                problems.Add(problem);
        }

        Note(scenario + " " + verdict);
        var detail = string.Join("; ", rows.Select(r => r.Describe())) + " || " + verdict;
        Assert.True(problems.Count == 0, scenario + " " + string.Join(" | ", problems) + " || " + detail);
    }

    /// <summary>The middle <c>total_ms</c> of the three runs of <paramref name="lib"/>.</summary>
    /// <param name="rows">Every run of the scenario. Not null. Contains three rows for the library.</param>
    /// <param name="lib">Library id. Not null.</param>
    /// <returns>The median.</returns>
    /// <remarks>Does not throw when the three rows are present. Only the ranking thread calls this.</remarks>
    private static double MedianOf(List<CellRow> rows, string lib)
    {
        double a = 0, b = 0, c = 0;
        var seen = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Lib, lib, StringComparison.Ordinal))
                continue;
            if (seen == 0)
                a = rows[i].TotalMs;
            else if (seen == 1)
                b = rows[i].TotalMs;
            else if (seen == 2)
                c = rows[i].TotalMs;
            seen++;
        }

        if (seen != Runs)
            throw new InvalidOperationException(lib + " has " + seen.ToString(CultureInfo.InvariantCulture) + " runs");
        return Median(a, b, c);
    }

    /// <summary>Full scenario size when BENCH_N and BENCH_N_SCALE are unset. Matches <c>Scenario.Get</c>.</summary>
    /// <param name="scenario">T1 through T7. Not null.</param>
    /// <returns>The measured call count.</returns>
    /// <remarks>Does not throw for a known scenario. Safe for any thread.</remarks>
    private static long ExpectedN(string scenario) => scenario switch
    {
        "T1" or "T2" or "T4" or "T7" => 1_000_000,
        "T3" => 100_000,
        "T5" => 10_000_000,
        "T6" => 1,
        _ => throw new ArgumentException("unknown scenario " + scenario, nameof(scenario)),
    };

    /// <summary>Walks up from the test output to the repo root.</summary>
    /// <returns>The directory that contains <c>global.json</c> and the bench project. Not null.</returns>
    /// <remarks>Throws when the root is not found. Not thread-safe.</remarks>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))
                && File.Exists(Path.Combine(dir.FullName, "eng", "comparison-bench", "ALog.Comparison.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    /// <summary>The Release harness. The host is <c>DOTNET_ROOT</c> or <c>PATH</c>.</summary>
    /// <param name="root">Repo root. Not null.</param>
    /// <returns>The harness dll path. Not null.</returns>
    /// <remarks>Fails the test when the host file or the dll is missing. Does not use a hard-coded user path.</remarks>
    private static string ComparisonDll(string root)
    {
        var host = DotnetHost();
        var dll = Path.Combine(root, "eng", "comparison-bench", "bin", "Release", "net10.0", "ALog.Comparison.dll");
        Assert.True(File.Exists(host), "SDK host missing: " + host);
        Assert.True(File.Exists(dll), "Release comparison build missing: " + dll);
        return dll;
    }

    /// <summary>The dotnet host on <c>DOTNET_ROOT</c> or <c>PATH</c>.</summary>
    /// <returns>A path that exists, or the name <c>dotnet</c> when neither location has it. Not null.</returns>
    /// <remarks>Does not throw. Does not look in a hard-coded user directory. Safe for the test thread.</remarks>
    private static string DotnetHost()
    {
        var file = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var underRoot = Path.Combine(root, file);
            if (File.Exists(underRoot))
                return underRoot;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(parts[i], file);
            if (File.Exists(candidate))
                return candidate;
        }

        return file;
    }

    /// <summary>One cell, under the gate so two cells never run together.</summary>
    /// <param name="root">Repo root. Not null.</param>
    /// <param name="dll">Harness dll. Not null.</param>
    /// <param name="lib">Library id. Not null.</param>
    /// <param name="scenario">Scenario id. Not null.</param>
    /// <param name="run">1-based run number.</param>
    /// <returns>The parsed result. A timeout is <c>valid=false</c> with a reason.</returns>
    /// <remarks>Does not throw for a failed cell. Deletes the work directory. Not thread-safe.</remarks>
    private static async Task<CellRow> RunCell(string root, string dll, string lib, string scenario, int run)
    {
        await Gate.WaitAsync();
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "alog-speed", scenario, lib + "-" + run.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var row = await Spawn(root, dll, lib, scenario, run, dir);
                Note(scenario + " " + row.Describe());
                return row;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // The cell already deleted its log. A leftover directory does not change the result.
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Starts one cell. The child inherits the process time zone. Serilog stamps are UTC without forcing <c>TZ</c>.
    /// <c>BENCH_N</c> and <c>BENCH_N_SCALE</c> are removed so the child uses the full scenario size.
    /// </summary>
    /// <param name="root">Working directory. Not null.</param>
    /// <param name="dll">Harness dll. Not null.</param>
    /// <param name="lib">Library id. Not null.</param>
    /// <param name="scenario">Scenario id. Not null.</param>
    /// <param name="run">1-based run number.</param>
    /// <param name="dir">Empty work directory. Not null.</param>
    /// <returns>The parsed result, or a false row with the reason the process did not produce one.</returns>
    /// <remarks>
    /// Does not throw for a timeout or a non-zero exit. The host is <c>DOTNET_ROOT</c> or <c>PATH</c>.
    /// The wait is 1800 seconds. Not thread-safe.
    /// </remarks>
    private static async Task<CellRow> Spawn(string root, string dll, string lib, string scenario, int run, string dir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("cell");
        psi.ArgumentList.Add(lib);
        psi.ArgumentList.Add(scenario);
        psi.ArgumentList.Add(dir);
        psi.Environment.Remove("BENCH_N");
        psi.Environment.Remove("BENCH_N_SCALE");
        psi.Environment.Remove("BENCH_MUTANT_DROP");
        psi.Environment.Remove("BENCH_MUTANT_FULL_MODE");
        psi.Environment.Remove("BENCH_KEEP_OUTPUT");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return new CellRow(lib, run, "false", 0, 0, "process did not start");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CellTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            try
            {
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            await Drain(stdoutTask, stderrTask);
            return new CellRow(lib, run, "false", 0, 0, "timeout after " + CellTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (process.ExitCode != 0 || line is null)
        {
            return new CellRow(
                lib,
                run,
                "false",
                0,
                0,
                "exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " stderr: " + Trim(stderr));
        }

        return Parse(lib, run, line["RESULT ".Length..], stderr);
    }

    /// <summary>Reads both streams after a killed process so the pipes do not stay open.</summary>
    /// <param name="stdout">The stdout read. Not null.</param>
    /// <param name="stderr">The stderr read. Not null.</param>
    /// <returns>A task that finishes when both reads have ended or failed.</returns>
    /// <remarks>Does not throw. A closed pipe is ignored. Only the cell thread calls this.</remarks>
    private static async Task Drain(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await stdout;
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }

        try
        {
            await stderr;
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }
    }

    /// <summary>Reads <c>valid</c>, <c>total_ms</c>, <c>n</c>, and <c>reason</c> from one RESULT object.</summary>
    /// <param name="lib">Library id. Not null.</param>
    /// <param name="run">1-based run number.</param>
    /// <param name="json">The object after the RESULT prefix. Not null.</param>
    /// <param name="stderr">Verifier text. Not null. Appended to the reason when it is not empty.</param>
    /// <returns>The row. <c>valid</c> is the text the cell printed.</returns>
    /// <remarks>Does not throw for a missing field. Throws when <paramref name="json"/> is not JSON. Only the cell thread calls this.</remarks>
    private static CellRow Parse(string lib, int run, string json, string stderr)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var valid = ReadValid(root);
        var total = root.TryGetProperty("total_ms", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? ms.GetDouble()
            : 0;
        var n = root.TryGetProperty("n", out var count) && count.ValueKind == JsonValueKind.Number
            ? count.GetInt64()
            : 0;
        string? reason = null;
        if (root.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String)
            reason = why.GetString();
        if (!string.IsNullOrWhiteSpace(stderr))
            reason = (reason ?? "") + " stderr: " + Trim(stderr);
        return new CellRow(lib, run, valid, total, n, reason);
    }

    /// <summary>The <c>valid</c> field as text. A JSON boolean becomes <c>true</c> or <c>false</c>.</summary>
    /// <param name="root">The RESULT object. Not null.</param>
    /// <returns>The text, or empty when the field is absent.</returns>
    /// <remarks>Does not throw. Safe for the cell thread.</remarks>
    private static string ReadValid(JsonElement root)
    {
        if (!root.TryGetProperty("valid", out var valid))
            return "";
        return valid.ValueKind switch
        {
            JsonValueKind.String => valid.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => valid.GetRawText(),
        };
    }

    /// <summary>Writes one progress line to the console and to a temp log.</summary>
    /// <param name="line">The line. Not null.</param>
    /// <remarks>Does not throw. A locked log file is ignored. The console write is the operator's reason. Not thread-safe.</remarks>
    private static void Note(string line)
    {
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "alog-speed-progress.log"),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Progress is for the operator. A locked file does not change the assertion.
        }
    }

    /// <summary>One line, at most 400 characters, with the line breaks flattened.</summary>
    /// <param name="text">The process error stream. Not null.</param>
    /// <returns>The flattened text. Not null.</returns>
    /// <remarks>Does not throw. Safe for the cell thread.</remarks>
    private static string Trim(string text)
    {
        var flat = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return flat.Length <= 400 ? flat : flat[..400];
    }

    /// <summary>One cell result. <see cref="Valid"/> is the text the harness printed.</summary>
    /// <param name="lib">Library id.</param>
    /// <param name="run">1-based run number.</param>
    /// <param name="valid"><c>true</c>, <c>false</c>, or <c>n/a</c>.</param>
    /// <param name="totalMs"><c>total_ms</c> from the result, or 0 when the cell failed.</param>
    /// <param name="n">Measured call count.</param>
    /// <param name="reason">Why the cell is not valid, when it is not.</param>
    private readonly struct CellRow(string lib, int run, string valid, double totalMs, long n, string? reason)
    {
        /// <summary>Library id.</summary>
        public string Lib { get; } = lib;

        /// <summary>1-based run number.</summary>
        public int Run { get; } = run;

        /// <summary><c>true</c> when the file check passed.</summary>
        public string Valid { get; } = valid;

        /// <summary>Timed total in milliseconds.</summary>
        public double TotalMs { get; } = totalMs;

        /// <summary>Measured call count.</summary>
        public long N { get; } = n;

        /// <summary>Why the cell is not valid. Null when the cell did not say.</summary>
        public string? Reason { get; } = reason;

        /// <summary>One short description for the assertion.</summary>
        /// <returns>Library, run, validity, total, and count.</returns>
        /// <remarks>Does not throw. Safe for the ranking thread.</remarks>
        public string Describe()
            => FormattableString.Invariant($"{Lib}#{Run} valid={Valid} total_ms={TotalMs:0.000} n={N}");
    }
}
