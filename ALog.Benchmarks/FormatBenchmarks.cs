using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using ALog;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.File;

/// <summary>
/// Formats one event. The no-exception cases are the gate against master.
/// The chain cases report the exception path. The thrown chain includes the stack walk.
/// The file cell times the default <see cref="PlainTextFormatter"/> through <see cref="FileLogWriter"/>.
/// The runner uses BenchmarkDotNet's default job.
/// </summary>
[MemoryDiagnoser]
public class FormatBenchmarks
{
    private readonly PlainTextFormatter _plain = new();
    private readonly JsonFormatter _json = new();
    private readonly DateTime _time = new(2026, 10, 21, 20, 55, 19, DateTimeKind.Utc);
    private readonly LogEvent _clean;
    private readonly LogEvent _chain;
    private readonly LogEvent _plainLine;
    private string? _filePath;
    private FileLogWriter? _fileWriter;

    /// <summary>Builds the two events once, so a run does not time construction.</summary>
    public FormatBenchmarks()
    {
        var props = new Dictionary<string, object> { ["user"] = "ada", ["n"] = 7 };
        var ctx = new Dictionary<string, object> { ["orderId"] = 17 };
        _clean = new LogEvent(_time, LogLevel.Error, "Payment failed", null, ctx, props);
        var ex = new InvalidOperationException("a", new InvalidOperationException("b", new TimeoutException("c")));
        _chain = new LogEvent(_time, LogLevel.Error, "Payment failed", ex, ctx, props);
        _plainLine = new LogEvent(_time, LogLevel.Info, "hello", null, null, null, null, "app");
    }

    /// <summary>
    /// Opens a temp file with <c>new PlainTextFormatter()</c> and a zero flush interval so each write hits the OS path.
    /// </summary>
    /// <remarks>Does not throw for a normal temp directory. The file is removed in <see cref="FileCleanup"/>.</remarks>
    [GlobalSetup(Target = nameof(File_Plain_DefaultFormat))]
    public void FileSetup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "alog-fmt-bench-" + Guid.NewGuid().ToString("N") + ".log");
        _fileWriter = new FileLogWriter(_filePath, new PlainTextFormatter(), fileFlushInterval: TimeSpan.Zero);
        _fileWriter.Write(_plainLine);
    }

    /// <summary>Disposes the file writer and deletes the temp path from <see cref="FileSetup"/>.</summary>
    [GlobalCleanup(Target = nameof(File_Plain_DefaultFormat))]
    public void FileCleanup()
    {
        _fileWriter?.Dispose();
        _fileWriter = null;
        if (_filePath is not null && File.Exists(_filePath))
            File.Delete(_filePath);
        _filePath = null;
    }

    /// <summary>Plain text with no exception.</summary>
    /// <returns>The formatted line.</returns>
    [Benchmark]
    public string Plain_NoException() => _plain.Format(_clean);

    /// <summary>
    /// One plain line through <see cref="FileLogWriter"/> with the default <see cref="PlainTextFormatter"/> pattern.
    /// </summary>
    [Benchmark]
    public void File_Plain_DefaultFormat() => _fileWriter!.Write(_plainLine);

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
