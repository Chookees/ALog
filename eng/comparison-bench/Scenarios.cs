// Scenarios T1–T7 (arc §8c BEN-02). Same messages, same values and the same category for every library.
using System.Globalization;
using System.Runtime.CompilerServices;

namespace AlogComparison;

/// <summary>Output format of a scenario.</summary>
public enum Fmt
{
    Plain,
    Json,
}

/// <summary>What one call in a scenario writes.</summary>
public enum CallKind
{
    Simple,
    Structured,
    Error,
}

/// <summary>One scenario: id, event count N, warm-up count, format, call shape, threads, filtered.</summary>
/// <param name="Id">T1 … T7.</param>
/// <param name="N">Measured calls. For T5 these are filtered and write nothing.</param>
/// <param name="Warmup">Calls before the stopwatch starts (BEN-06: 10,000). Zero for T6.</param>
/// <param name="Format">Plain text or JSON.</param>
/// <param name="Call">Call shape.</param>
/// <param name="Threads">Writer threads (T4: 8).</param>
/// <param name="Filtered">True when the calls are below the minimum level (T5).</param>
/// <param name="Title">Short description for README and logs.</param>
public sealed record Scenario(string Id, long N, long Warmup, Fmt Format, CallKind Call, int Threads, bool Filtered, string Title)
{
    /// <summary>Records the output file must contain after the run.</summary>
    /// <remarks>T5 writes nothing during warm-up and measurement, plus one sentinel event above the level.</remarks>
    public long ExpectedRecords => Filtered ? 1 : N + Warmup;

    /// <summary>All scenarios in report order.</summary>
    public static readonly string[] All = ["T1", "T2", "T7", "T3", "T4", "T5", "T6"];

    /// <summary>Builds a scenario. N and warm-up honour BENCH_N (fixed N) and BENCH_N_SCALE (factor).</summary>
    public static Scenario Get(string id)
    {
        var fixedN = EnvLong("BENCH_N");
        var scale = EnvDouble("BENCH_N_SCALE") ?? 1.0;
        long Count(long full) => fixedN is long f ? Math.Max(1, f) : Math.Max(1, (long)Math.Round(full * scale));
        var warmup = fixedN is long w ? Math.Clamp(w, 1, 10_000) : Count(10_000);
        return id switch
        {
            "T1" => new("T1", Count(1_000_000), warmup, Fmt.Plain, CallKind.Simple, 1, false, "1M plain messages"),
            "T2" => new("T2", Count(1_000_000), warmup, Fmt.Plain, CallKind.Structured, 1, false, "1M structured messages, 2 properties"),
            "T7" => new("T7", Count(1_000_000), warmup, Fmt.Json, CallKind.Structured, 1, false, "T2 with the JSON formatter"),
            "T3" => new("T3", Count(100_000), Math.Min(warmup, Count(100_000)), Fmt.Plain, CallKind.Error, 1, false, "100k thrown and caught exceptions, 3-level chain"),
            "T4" => new("T4", Math.Max(8, Count(1_000_000) / 8 * 8), warmup, Fmt.Plain, CallKind.Simple, 8, false, "8 threads x 125k plain messages"),
            "T5" => new("T5", Count(10_000_000), warmup, Fmt.Plain, CallKind.Simple, 1, true, "10M calls below the minimum level"),
            "T6" => new("T6", 1, 0, Fmt.Plain, CallKind.Simple, 1, false, "init + first event + flush, cold process"),
            _ => throw new ArgumentException("unknown scenario " + id, nameof(id)),
        };
    }

    /// <summary>Reads a long from the environment, or null.</summary>
    public static long? EnvLong(string name)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Reads a double from the environment, or null.</summary>
    public static double? EnvDouble(string name)
        => double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>Message texts and the exception chain shared by all adapters.</summary>
public static class Texts
{
    /// <summary>Category (logger name) used by every library.</summary>
    public const string Category = "Bench";

    /// <summary>T1, T4, T5, T6 message.</summary>
    public const string Simple = "Application event processed";

    /// <summary>T2/T7 template (BEN-02).</summary>
    public const string Template = "User {UserId} logged in from {Ip}";

    /// <summary>The same template for log4net, which has positional format strings only.</summary>
    public const string PositionalTemplate = "User {0} logged in from {1}";

    /// <summary>Second property value; the first is the running index.</summary>
    public const string Ip = "192.168.1.10";

    /// <summary>T3 message.</summary>
    public const string Error = "Payment failed";

    /// <summary>T5 sentinel, written once at Warn after the filtered calls so an empty file cannot pass.</summary>
    public const string Sentinel = "Filtered run finished";

    /// <summary>Exception types in the T3 chain, outermost first.</summary>
    public static readonly string[] ChainTypes = ["InvalidOperationException", "ArgumentException", "IOException"];

    /// <summary>Throws and catches a 3-level chain (spec S4) and returns the outer exception, with stack traces.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Exception ThrowChain()
    {
        try
        {
            Level1();
        }
        catch (InvalidOperationException e)
        {
            return e;
        }

        throw new InvalidOperationException("unreachable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Level1()
    {
        try
        {
            Level2();
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("Order 4711 could not be completed", e);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Level2()
    {
        try
        {
            Level3();
        }
        catch (IOException e)
        {
            throw new ArgumentException("Card data rejected", e);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Level3() => throw new IOException("Card terminal not reachable");
}
