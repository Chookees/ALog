// Full-N ranked speed checks. One fresh comparison process per cell.
// ALog async mean total_ms of 3 runs must be <= the fastest valid competitor.
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
    private const string DotnetExe = @"C:\Users\artur\.dotnet\dotnet.exe";
    private const int Runs = 3;
    private static readonly TimeSpan CellTimeout = TimeSpan.FromSeconds(1800);
    private static readonly object Gate = new();

    /// <summary>AC-1. Plain 1,000,000 Info lines, including log4net.</summary>
    [Fact]
    public void T1_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T1", includeLog4Net: true);

    /// <summary>AC-2. Structured 1,000,000 Info lines. log4net is not a competitor.</summary>
    [Fact]
    public void T2_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T2", includeLog4Net: false);

    /// <summary>AC-3. 100,000 errors with a three-level exception chain.</summary>
    [Fact]
    public void T3_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T3", includeLog4Net: true);

    /// <summary>AC-4. Eight threads writing 125,000 plain Info lines each.</summary>
    [Fact]
    public void T4_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T4", includeLog4Net: true);

    /// <summary>AC-5. 10,000,000 calls below Warn, then one Warn sentinel.</summary>
    [Fact]
    public void T5_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T5", includeLog4Net: true);

    /// <summary>AC-6. Cold process: init, one event, flush, dispose.</summary>
    [Fact]
    public void T6_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T6", includeLog4Net: true);

    /// <summary>AC-7. Structured 1,000,000 lines as JSON. log4net is not a competitor.</summary>
    [Fact]
    public void T7_AlogAsync_TotalMs_AtMostFastest() => AssertRank("T7", includeLog4Net: false);

    /// <summary>AC-8 neighbour. One alog-sync T1 cell must stay valid. It has no speed rank.</summary>
    [Fact]
    public void Neighbor_AlogSync_T1_Valid()
    {
        var root = RepoRoot();
        var dll = ComparisonDll(root);
        var row = RunCell(root, dll, "alog-sync", "T1", 1);
        Assert.True(
            row.Valid == "true" && row.N == ExpectedN("T1"),
            "alog-sync T1 must be valid at full N. " + row.Describe());
    }

    /// <summary>
    /// Mean of three <c>total_ms</c> values. An equal mean passes.
    /// A competitor cell whose <c>valid</c> is not true is left out of that competitor's mean.
    /// A competitor with no valid cell is left out of the minimum. No valid competitor fails the scenario.
    /// </summary>
    private static void AssertRank(string scenario, bool includeLog4Net)
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
                rows.Add(RunCell(root, dll, lib, scenario, run));
        }

        var problems = new List<string>();
        foreach (var row in rows)
        {
            if (row.N != expected)
                problems.Add(row.Lib + " run " + row.Run + " n=" + row.N + " expected " + expected);
        }

        var alog = rows.Where(r => r.Lib == "alog-async").ToList();
        foreach (var row in alog)
        {
            if (row.Valid != "true")
                problems.Add("alog-async run " + row.Run + " valid=" + row.Valid + " reason=" + (row.Reason ?? ""));
        }

        double? alogMean = alog.Count == Runs && alog.All(r => r.Valid == "true")
            ? alog.Average(r => r.TotalMs)
            : null;

        var competitorMeans = new List<(string Lib, double Mean, int Used)>();
        foreach (var lib in libs)
        {
            if (lib == "alog-async")
                continue;
            var valid = rows.Where(r => r.Lib == lib && r.Valid == "true").ToList();
            if (valid.Count == 0)
                continue;
            competitorMeans.Add((lib, valid.Average(r => r.TotalMs), valid.Count));
        }

        if (competitorMeans.Count == 0)
            problems.Add("no valid competitor");
        else if (alogMean is double mean)
        {
            var fastest = competitorMeans[0];
            for (var i = 1; i < competitorMeans.Count; i++)
            {
                if (competitorMeans[i].Mean < fastest.Mean)
                    fastest = competitorMeans[i];
            }

            if (mean > fastest.Mean)
            {
                problems.Add(
                    FormattableString.Invariant(
                        $"alog-async mean {mean:0.000} ms > fastest {fastest.Lib} mean {fastest.Mean:0.000} ms"));
            }
        }

        var detail = string.Join(
            "; ",
            rows.Select(r => r.Describe()))
            + (alogMean is double shown
                ? FormattableString.Invariant($" || alog-async mean {shown:0.000}")
                : " || alog-async mean n/a")
            + " || competitors "
            + (competitorMeans.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    competitorMeans.Select(c => FormattableString.Invariant($"{c.Lib} mean {c.Mean:0.000} (n={c.Used})"))));

        Assert.True(problems.Count == 0, scenario + " " + string.Join(" | ", problems) + " || " + detail);
    }

    /// <summary>Full scenario size when BENCH_N and BENCH_N_SCALE are unset. Matches <c>Scenario.Get</c>.</summary>
    private static long ExpectedN(string scenario) => scenario switch
    {
        "T1" or "T2" or "T4" or "T7" => 1_000_000,
        "T3" => 100_000,
        "T5" => 10_000_000,
        "T6" => 1,
        _ => throw new ArgumentException("unknown scenario " + scenario, nameof(scenario)),
    };

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

    private static string ComparisonDll(string root)
    {
        var dll = Path.Combine(root, "eng", "comparison-bench", "bin", "Release", "net10.0", "ALog.Comparison.dll");
        Assert.True(File.Exists(DotnetExe), "SDK host missing: " + DotnetExe);
        Assert.True(File.Exists(dll), "Release comparison build missing: " + dll);
        return dll;
    }

    /// <summary>
    /// One cell. The host is the pinned SDK. TZ=UTC matches the harness runner so a Serilog JSON stamp can stay valid.
    /// BENCH_N and BENCH_N_SCALE are removed so the child uses the full scenario size.
    /// </summary>
    private static CellRow RunCell(string root, string dll, string lib, string scenario, int run)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Path.GetTempPath(), "alog-speed", scenario, lib + "-" + run + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var row = Spawn(root, dll, lib, scenario, run, dir);
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
    }

    private static CellRow Spawn(string root, string dll, string lib, string scenario, int run, string dir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotnetExe,
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
        psi.Environment["DOTNET_ROOT"] = @"C:\Users\artur\.dotnet";
        psi.Environment["PATH"] = @"C:\Users\artur\.dotnet;" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["TZ"] = "UTC";
        psi.Environment.Remove("BENCH_N");
        psi.Environment.Remove("BENCH_N_SCALE");
        psi.Environment.Remove("BENCH_MUTANT_DROP");
        psi.Environment.Remove("BENCH_MUTANT_FULL_MODE");
        psi.Environment.Remove("BENCH_KEEP_OUTPUT");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CellTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            process.WaitForExit();
            return new CellRow(lib, run, "false", 0, 0, "timeout after " + CellTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        var line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (process.ExitCode != 0 || line is null)
        {
            return new CellRow(
                lib,
                run,
                "false",
                0,
                0,
                "exit " + process.ExitCode + " stderr: " + Trim(stderr));
        }

        return Parse(lib, run, line["RESULT ".Length..], stderr);
    }

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

    private static string Trim(string text)
    {
        var flat = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return flat.Length <= 400 ? flat : flat[..400];
    }

    private readonly struct CellRow(string lib, int run, string valid, double totalMs, long n, string? reason)
    {
        public string Lib { get; } = lib;
        public int Run { get; } = run;
        public string Valid { get; } = valid;
        public double TotalMs { get; } = totalMs;
        public long N { get; } = n;
        public string? Reason { get; } = reason;

        public string Describe()
            => FormattableString.Invariant($"{Lib}#{Run} valid={Valid} total_ms={TotalMs:0.000} n={N}");
    }
}
