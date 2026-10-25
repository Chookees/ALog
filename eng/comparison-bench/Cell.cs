// One measured cell in a fresh process (BEN-06): warm-up (not timed), then N events, Close (flush + dispose)
// inside the timed span, then the BEN-04 file check. Prints one line "RESULT <json>" on stdout.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AlogComparison;

/// <summary>Runs one library × scenario cell.</summary>
public static class Cell
{
    /// <summary>
    /// Measures one library and scenario, then checks the file.
    /// </summary>
    /// <param name="libId">Harness id such as <c>serilog</c>. Not null.</param>
    /// <param name="scenarioId">T1 through T7. Not null.</param>
    /// <param name="workDir">Directory for the output file. Not null. Created if missing.</param>
    /// <returns>0 after the result line is written. A failed file check is still 0; <c>valid</c> is false in the JSON.</returns>
    /// <remarks>
    /// Does not throw for a normal cell. Structured warm-up and the measured run share one user-id sequence,
    /// starting at 0, so <see cref="Verify"/> can see a gap. One process runs one cell. Not thread-safe.
    /// </remarks>
    public static int Run(string libId, string scenarioId, string workDir)
    {
        var lib = Libs.Create(libId);
        var sc = Scenario.Get(scenarioId);
        var res = new Dictionary<string, object?>
        {
            ["lib"] = lib.Id,
            ["framework"] = lib.Framework,
            ["mode"] = lib.Mode,
            ["version"] = Version(lib),
            ["scenario"] = sc.Id,
            ["n"] = sc.N,
            ["warmup"] = sc.Warmup,
            ["threads"] = sc.Threads,
            ["format"] = sc.Format.ToString(),
            ["notes"] = lib.Notes,
            ["ranked"] = lib.Ranked,
        };
        var na = lib.NotSupported(sc.Id);
        if (na is not null)
        {
            res["valid"] = "n/a";
            res["reason"] = na;
            Emit(res);
            return 0;
        }

        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, $"{lib.Id}-{sc.Id}.log");
        if (File.Exists(path))
            File.Delete(path); // every cell starts from an empty file (BEN-03)
        var config = new LibConfig(path, sc.Format, sc.Filtered);
        var mutantDrop = Environment.GetEnvironmentVariable("BENCH_MUTANT_DROP") == "1";
        var proc = Process.GetCurrentProcess();

        if (sc.Id != "T6")
        {
            lib.Init(config);
            Body(lib, sc, sc.Warmup, warmup: true, mutantDrop: false, indexBase: 0);
            lib.Drain();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        if (sc.Id == "T6")
            lib.Init(config);
        Body(lib, sc, sc.N, warmup: false, mutantDrop, indexBase: sc.Warmup);
        if (sc.Filtered)
            lib.Sentinel();
        var writeMs = sw.Elapsed.TotalMilliseconds;
        lib.Close();
        sw.Stop();

        proc.Refresh();
        var totalMs = sw.Elapsed.TotalMilliseconds;
        var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
        var peakWs = proc.PeakWorkingSet64;
        var vmHwm = ReadVmHwm();

        var v = Verify.Run(path, sc);
        res["valid"] = v.Ok ? "true" : "false";
        res["reason"] = v.Ok ? null : string.Join("; ", v.Failures);
        res["expected_records"] = sc.ExpectedRecords;
        res["records"] = v.Records;
        res["token_records"] = v.TokenRecords;
        res["file_bytes"] = v.FileBytes;
        res["total_ms"] = Math.Round(totalMs, 3);
        res["write_ms"] = Math.Round(writeMs, 3);
        res["cpu_ms"] = Math.Round(cpuMs, 3);
        res["cpu_pct"] = Math.Round(100.0 * cpuMs / totalMs, 2);
        res["alloc_bytes"] = alloc;
        res["alloc_bytes_per_op"] = Math.Round((double)alloc / sc.N, 2);
        res["mean_ns_per_op"] = Math.Round(totalMs * 1e6 / sc.N, 2);
        res["events_per_s"] = Math.Round(sc.N / (totalMs / 1000.0), 1);
        res["peak_ws_bytes"] = peakWs;
        res["peak_ws_mb"] = Math.Round(peakWs / 1048576.0, 2);
        res["vmhwm_bytes"] = vmHwm;
        res["gc0"] = GC.CollectionCount(0) - g0;
        res["gc1"] = GC.CollectionCount(1) - g1;
        res["gc2"] = GC.CollectionCount(2) - g2;
        res["mutant_drop"] = mutantDrop;
        if (Environment.GetEnvironmentVariable("BENCH_KEEP_OUTPUT") != "1")
            File.Delete(path);
        Emit(res);
        return 0;
    }

    private static string Version(ILib lib) => Libs.PackageVersion(lib.Framework);

    /// <summary>
    /// Writes <paramref name="count"/> calls of the scenario's shape.
    /// Structured calls number from <paramref name="indexBase"/> so warm-up and the measured run are one sequence.
    /// </summary>
    /// <param name="lib">Library under test. Not null. Already initialised.</param>
    /// <param name="sc">Scenario. Not null.</param>
    /// <param name="count">How many calls. The loop stops at this count.</param>
    /// <param name="warmup">True for the untimed warm-up. A multi-thread scenario still warms up on one thread.</param>
    /// <param name="mutantDrop">True when one call in the middle is skipped, so the file is short on purpose.</param>
    /// <param name="indexBase">First structured index. Warm-up uses 0. The measured run uses the warm-up count.</param>
    /// <remarks>
    /// Does not throw for a library call. The loops are bounded by <paramref name="count"/> and by the thread count.
    /// Only the threads this method starts call the library. T4 does not share one index across threads.
    /// </remarks>
    private static void Body(ILib lib, Scenario sc, long count, bool warmup, bool mutantDrop, long indexBase)
    {
        var skip = mutantDrop ? count / 2 : -1;
        if (sc.Threads > 1 && !warmup)
        {
            var per = count / sc.Threads;
            skip = mutantDrop ? per / 2 : -1;
            var threads = new Thread[sc.Threads];
            using var go = new ManualResetEventSlim(false);
            for (var t = 0; t < sc.Threads; t++)
            {
                var first = t == 0;
                threads[t] = new Thread(() =>
                {
                    go.Wait();
                    for (long i = 0; i < per; i++)
                    {
                        if (first && i == skip)
                            continue;
                        lib.Simple();
                    }
                });
                threads[t].Start();
            }

            go.Set();
            foreach (var t in threads)
                t.Join();
            return;
        }

        for (long i = 0; i < count; i++)
        {
            if (i == skip)
                continue;
            switch (sc.Call)
            {
                case CallKind.Simple:
                    lib.Simple();
                    break;
                case CallKind.Structured:
                    lib.Structured((int)((indexBase + i) % int.MaxValue));
                    break;
                default:
                    lib.Error(Texts.ThrowChain());
                    break;
            }
        }
    }

    /// <summary>VmHWM from /proc/self/status in bytes, or -1 where that file does not exist.</summary>
    private static long ReadVmHwm()
    {
        const string status = "/proc/self/status";
        if (!File.Exists(status))
            return -1;
        foreach (var line in File.ReadLines(status))
        {
            if (!line.StartsWith("VmHWM:", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                return kb * 1024;
        }

        return -1;
    }

    private static void Emit(Dictionary<string, object?> res)
        => Console.Out.WriteLine("RESULT " + JsonSerializer.Serialize(res));
}
