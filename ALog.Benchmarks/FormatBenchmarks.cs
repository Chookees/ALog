using BenchmarkDotNet.Attributes;
using ALog;
using ALog.Core;
using ALog.Formatters;

/// <summary>
/// Formats one event. The no-exception cases are the gate against master.
/// The chain cases report the exception path.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FormatBenchmarks
{
    private readonly PlainTextFormatter _plain = new();
    private readonly JsonFormatter _json = new();
    private readonly LogEvent _clean;
    private readonly LogEvent _chain;

    /// <summary>Builds the two events once, so a run does not time construction.</summary>
    public FormatBenchmarks()
    {
        var time = new DateTime(2026, 10, 21, 20, 55, 19, DateTimeKind.Utc);
        var props = new Dictionary<string, object> { ["user"] = "ada", ["n"] = 7 };
        var ctx = new Dictionary<string, object> { ["orderId"] = 17 };
        _clean = new LogEvent(time, LogLevel.Error, "Payment failed", null, ctx, props);
        var ex = new InvalidOperationException("a", new InvalidOperationException("b", new TimeoutException("c")));
        _chain = new LogEvent(time, LogLevel.Error, "Payment failed", ex, ctx, props);
    }

    /// <summary>Plain text with no exception.</summary>
    /// <returns>The formatted line.</returns>
    [Benchmark]
    public string Plain_NoException() => _plain.Format(_clean);

    /// <summary>JSON with no exception.</summary>
    /// <returns>The formatted document.</returns>
    [Benchmark]
    public string Json_NoException() => _json.Format(_clean);

    /// <summary>Plain text for a three-level exception chain.</summary>
    /// <returns>The formatted line.</returns>
    [Benchmark]
    public string Plain_Chain3() => _plain.Format(_chain);

    /// <summary>JSON for a three-level exception chain.</summary>
    /// <returns>The formatted document.</returns>
    [Benchmark]
    public string Json_Chain3() => _json.Format(_chain);
}
