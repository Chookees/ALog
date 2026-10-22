// Process runner (BEN-06, BEN-07): every cell in its own fresh process, library order shuffled per run and
// scenario with a recorded seed, raw values as CSV with the fixed BEN-07 column list, details and environment.
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlogComparison;

/// <summary>Runs all cells R times and writes raw.csv, details.jsonl and env.json into the output directory.</summary>
public static class Runner
{
    /// <summary>Fixed CSV column list (arc BEN-07).</summary>
    public static readonly string[] Columns =
    [
        "runner", "run", "framework", "version", "scenario", "mode", "n", "total_ms", "cpu_ms", "cpu_pct",
        "peak_ws_mb", "alloc_bytes_per_op", "mean_ns_per_op", "events_per_s", "valid",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Entry for <c>ALog.Comparison run --runs R --agent A --out DIR [--seed S]</c>.</summary>
    public static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        var runs = int.Parse(opts.GetValueOrDefault("runs", "5"), CultureInfo.InvariantCulture);
        var agent = opts.GetValueOrDefault("agent", "unknown");
        var outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "results/" + agent));
        var seed = opts.TryGetValue("seed", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : Random.Shared.Next();
        var libs = (Environment.GetEnvironmentVariable("BENCH_LIBS") ?? string.Join(',', Libs.All)).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var scenarios = (Environment.GetEnvironmentVariable("BENCH_SCENARIOS") ?? string.Join(',', Scenario.All)).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var timeout = TimeSpan.FromSeconds(Scenario.EnvLong("BENCH_CELL_TIMEOUT_S") ?? (IsSmall() ? 30 : 1800));
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, "_work");

        var env = Env.Collect(agent, runs, seed);
        File.WriteAllText(Path.Combine(outDir, "env.json"), env.ToJsonString(Indented));

        var rng = new Random(seed);
        var csvPath = Path.Combine(outDir, "raw.csv");
        using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
        using var details = new StreamWriter(Path.Combine(outDir, "details.jsonl"), false, new UTF8Encoding(false)) { NewLine = "\n" };
        csv.WriteLine(string.Join(',', Columns));
        int total = 0, valid = 0, invalid = 0, na = 0;

        for (var run = 1; run <= runs; run++)
        {
            foreach (var scId in scenarios)
            {
                var order = libs.OrderBy(_ => rng.Next()).ToArray();
                for (var pos = 0; pos < order.Length; pos++)
                {
                    var lib = order[pos];
                    var cellDir = Path.Combine(work, $"r{run}-{scId}-{lib}");
                    var r = Spawn(lib, scId, cellDir, timeout);
                    TryDelete(cellDir);
                    r["runner"] = agent;
                    r["run"] = run;
                    r["order_position"] = pos;
                    r["order"] = string.Join(' ', order);
                    r["seed"] = seed;
                    csv.WriteLine(CsvRow(r));
                    csv.Flush();
                    details.WriteLine(JsonSerializer.Serialize(r));
                    details.Flush();
                    total++;
                    var v = r.GetValueOrDefault("valid")?.ToString();
                    if (v == "true") valid++;
                    else if (v == "n/a") na++;
                    else invalid++;
                    Console.Out.WriteLine($"run {run}/{runs} {scId,-3} {lib,-10} valid={v,-5} total_ms={Get(r, "total_ms"),-10} {Get(r, "reason")}");
                }
            }
        }

        TryDelete(work);
        env["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        env["loadavg_end"] = Env.LoadAvg();
        env["cells"] = new JsonObject { ["total"] = total, ["valid"] = valid, ["invalid"] = invalid, ["n_a"] = na };
        File.WriteAllText(Path.Combine(outDir, "env.json"), env.ToJsonString(Indented));
        Console.Out.WriteLine($"cells: {total}, valid: {valid}, invalid: {invalid}, n/a: {na}; results in {outDir}");
        return invalid == 0 ? 0 : 1;
    }

    private static bool IsSmall()
        => Scenario.EnvLong("BENCH_N") is long n ? n <= 10_000 : (Scenario.EnvDouble("BENCH_N_SCALE") ?? 1.0) < 0.05;

    private static string Get(Dictionary<string, object?> r, string key) => r.GetValueOrDefault(key)?.ToString() ?? string.Empty;

    private static Dictionary<string, object?> Spawn(string lib, string scenario, string cellDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var host = Path.GetFileNameWithoutExtension(psi.FileName);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Runner).Assembly.Location);
        psi.ArgumentList.Add("cell");
        psi.ArgumentList.Add(lib);
        psi.ArgumentList.Add(scenario);
        psi.ArgumentList.Add(cellDir);
        psi.Environment["TZ"] = "UTC";

        var fail = new Dictionary<string, object?> { ["lib"] = lib, ["scenario"] = scenario, ["valid"] = "false" };
        try
        {
            var meta = Libs.Create(lib);
            fail["framework"] = meta.Framework;
            fail["mode"] = meta.Mode;
            fail["n"] = Scenario.Get(scenario).N;
        }
        catch (ArgumentException e)
        {
            fail["reason"] = e.Message;
            return fail;
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            p.WaitForExit();
            fail["reason"] = $"timeout after {timeout.TotalSeconds:0} s (hang guard)";
            return fail;
        }

        p.WaitForExit();
        var err = stderr.GetAwaiter().GetResult();
        var line = stdout.GetAwaiter().GetResult().Split('\n').FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (p.ExitCode != 0 || line is null)
        {
            fail["reason"] = $"exit code {p.ExitCode}, no result. stderr: {Trim(err)}";
            return fail;
        }

        var res = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line["RESULT ".Length..])!;
        var dict = res.ToDictionary(kv => kv.Key, kv => (object?)Plain(kv.Value));
        if (err.Length > 0)
            dict["stderr"] = Trim(err);
        return dict;
    }

    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText(),
    };

    private static string Trim(string s) => s.Length <= 2000 ? s.Trim() : s[..2000].Trim() + " …";

    private static string CsvRow(Dictionary<string, object?> r)
    {
        var cells = new string[Columns.Length];
        for (var i = 0; i < Columns.Length; i++)
        {
            var v = r.GetValueOrDefault(Columns[i]);
            var text = v switch
            {
                null => string.Empty,
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? string.Empty,
            };
            cells[i] = text.Contains(',', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal)
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : text;
        }

        return string.Join(',', cells);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort; the run directory keeps it under _work
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("expected --name value, got " + args[i]);
            d[args[i][2..]] = args[i + 1];
        }

        return d;
    }
}

/// <summary>Environment block (BEN-07): machine, CPU, cores, RAM, OS, SDK, runtime, load, versions, ALog SHA.</summary>
public static class Env
{
    /// <summary>Collects the block. run.sh passes SDK, SHAs and the file system type through BENCH_* variables.</summary>
    public static JsonObject Collect(string agent, int runs, int seed)
    {
        var versions = new JsonObject();
        foreach (var (k, v) in Libs.Versions())
            versions[k] = v;
        var dotnetVars = new JsonObject();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key.ToString() ?? string.Empty;
            if (key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("BENCH_", StringComparison.Ordinal) || key == "TZ" || key == "AlogSrc")
                dotnetVars[key] = e.Value?.ToString();
        }

        return new JsonObject
        {
            ["schema"] = "alog-comparison/1",
            ["runner"] = agent,
            ["started_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["runs"] = runs,
            ["seed"] = seed,
            ["machine"] = Environment.MachineName,
            ["cpu_model"] = CpuModel(),
            ["logical_cores"] = Environment.ProcessorCount,
            ["cgroup_cpu_max"] = ReadFirstLine("/sys/fs/cgroup/cpu.max"),
            ["ram_total_mb"] = RamMb(),
            ["os"] = RuntimeInformation.OSDescription,
            ["os_pretty"] = OsPretty(),
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["kernel"] = ReadFirstLine("/proc/sys/kernel/osrelease"),
            ["dotnet_sdk"] = Environment.GetEnvironmentVariable("BENCH_SDK") ?? "unknown",
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["gc"] = GCSettings.IsServerGC ? "server" : "workstation",
            ["loadavg_start"] = LoadAvg(),
            ["work_fs_type"] = Environment.GetEnvironmentVariable("BENCH_FS_TYPE") ?? "unknown",
            ["alog_sha"] = Environment.GetEnvironmentVariable("BENCH_ALOG_SHA") ?? "unknown",
            ["alog_dirty"] = Environment.GetEnvironmentVariable("BENCH_ALOG_DIRTY") ?? "unknown",
            ["harness_sha"] = Environment.GetEnvironmentVariable("BENCH_HARNESS_SHA") ?? "unknown",
            ["n_scale"] = Environment.GetEnvironmentVariable("BENCH_N_SCALE") ?? "1",
            ["n_fixed"] = Environment.GetEnvironmentVariable("BENCH_N"),
            ["versions"] = versions,
            ["scenarios"] = ScenarioTable(),
            ["queue_capacity"] = Libs.QueueCapacity,
            ["alog_full_mode"] = AlogLib.FullMode,
            ["libraries"] = LibraryTable(),
            ["env"] = dotnetVars,
        };
    }

    /// <summary>/proc/loadavg first line, or null.</summary>
    public static string? LoadAvg() => ReadFirstLine("/proc/loadavg");

    private static JsonArray ScenarioTable()
    {
        var a = new JsonArray();
        foreach (var id in Scenario.All)
        {
            var s = Scenario.Get(id);
            a.Add(new JsonObject { ["id"] = s.Id, ["n"] = s.N, ["warmup"] = s.Warmup, ["threads"] = s.Threads, ["format"] = s.Format.ToString(), ["title"] = s.Title });
        }

        return a;
    }

    private static JsonArray LibraryTable()
    {
        var a = new JsonArray();
        foreach (var id in Libs.All)
        {
            var l = Libs.Create(id);
            var na = new JsonObject();
            foreach (var s in Scenario.All)
            {
                if (l.NotSupported(s) is string reason)
                    na[s] = reason;
            }

            a.Add(new JsonObject { ["id"] = l.Id, ["framework"] = l.Framework, ["mode"] = l.Mode, ["ranked"] = l.Ranked, ["config"] = l.Notes, ["n_a"] = na });
        }

        return a;
    }

    private static string? CpuModel()
    {
        const string cpuinfo = "/proc/cpuinfo";
        if (File.Exists(cpuinfo))
        {
            foreach (var line in File.ReadLines(cpuinfo))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
    }

    private static long RamMb()
    {
        const string meminfo = "/proc/meminfo";
        if (File.Exists(meminfo))
        {
            var first = ReadFirstLine(meminfo) ?? string.Empty;
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                return kb / 1024;
        }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;
    }

    private static string? OsPretty()
    {
        const string osRelease = "/etc/os-release";
        if (!File.Exists(osRelease))
            return null;
        foreach (var line in File.ReadLines(osRelease))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                return line["PRETTY_NAME=".Length..].Trim('"');
        }

        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
