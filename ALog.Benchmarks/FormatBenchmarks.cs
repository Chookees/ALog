using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using ALog;
using ALog.Core;
using ALog.Formatters;

/// <summary>
/// Formats one event. The no-exception cases are the gate against master.
/// The chain cases report the exception path. The thrown chain includes the stack walk.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FormatBenchmarks
{
    private readonly PlainTextFormatter _plain = new();
    private readonly JsonFormatter _json = new();
    private readonly DateTime _time = new(2026, 10, 21, 20, 55, 19, DateTimeKind.Utc);
    private readonly LogEvent _clean;
    private readonly LogEvent _chain;

    /// <summary>Builds the two events once, so a run does not time construction.</summary>
    public FormatBenchmarks()
    {
        var props = new Dictionary<string, object> { ["user"] = "ada", ["n"] = 7 };
        var ctx = new Dictionary<string, object> { ["orderId"] = 17 };
        _clean = new LogEvent(_time, LogLevel.Error, "Payment failed", null, ctx, props);
        var ex = new InvalidOperationException("a", new InvalidOperationException("b", new TimeoutException("c")));
        _chain = new LogEvent(_time, LogLevel.Error, "Payment failed", ex, ctx, props);
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

    /// <summary>Plain text for a three-level chain that was thrown and caught, including the stack walk.</summary>
    /// <returns>The formatted line.</returns>
    [Benchmark]
    public string Plain_ThrownChain3()
    {
        try
        {
            ThrowOuter();
        }
        catch (InvalidOperationException ex)
        {
            return _plain.Format(_clean with { Exception = ex });
        }

        return string.Empty;
    }

    /// <summary>JSON for a three-level chain that was thrown and caught, including the stack walk.</summary>
    /// <returns>The formatted document.</returns>
    [Benchmark]
    public string Json_ThrownChain3()
    {
        try
        {
            ThrowOuter();
        }
        catch (InvalidOperationException ex)
        {
            return _json.Format(_clean with { Exception = ex });
        }

        return string.Empty;
    }

    /// <summary>Throws the outer exception of the three-level chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOuter()
    {
        try
        {
            ThrowMiddle();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("a", ex);
        }
    }

    /// <summary>Throws the middle exception of the three-level chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMiddle()
    {
        try
        {
            ThrowInner();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("b", ex);
        }
    }

    /// <summary>Throws the innermost exception of the three-level chain.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner()
    {
        throw new TimeoutException("c");
    }
}
