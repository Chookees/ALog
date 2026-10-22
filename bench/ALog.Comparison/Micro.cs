// BenchmarkDotNet part (BEN-05): Release, default job, MemoryDiagnoser + ThreadingDiagnoser. One benchmark per
// scenario T1–T5 and T7 (T6 only in the process runner). The benchmark measures the write call; init runs in
// IterationSetup, flush and dispose in IterationCleanup and are not measured (the process runner measures them).
// IterationSetup needs a fixed invocation count, so each class sets [InvocationCount]; the rest is the default job.
// Queue capacity is Libs.QueueCapacity (10,000) for every library. Invocations per iteration stay <= 10,000 events.
// ALog uses QueueFullMode.Block, so a full queue waits and an iteration does not drop an event.
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace AlogComparison;

/// <summary>Entry for <c>ALog.Comparison micro [BenchmarkDotNet args]</c>.</summary>
public static class Micro
{
    /// <summary>Runs the BenchmarkDotNet switcher.</summary>
    public static void Run(string[] args)
        => BenchmarkSwitcher.FromTypes(
            [typeof(T1Plain), typeof(T2Structured), typeof(T3Exception), typeof(T4Threads), typeof(T5Filtered), typeof(T7Json)])
            .Run(args);
}

/// <summary>Shared setup: fresh logger and file per iteration.</summary>
public abstract class MicroBase
{
    private string dir = string.Empty;

    /// <summary>Library id.</summary>
    [ParamsSource(nameof(Libraries))]
    public string Lib { get; set; } = "alog-async";

    /// <summary>The adapter of the current iteration.</summary>
    protected ILib Adapter { get; private set; } = null!;

    /// <summary>Pre-thrown exception chain for T3 (the write is measured, not the throw).</summary>
    protected Exception Chain { get; } = Texts.ThrowChain();

    /// <summary>Scenario id; decides format, level and n/a.</summary>
    protected abstract string ScenarioId { get; }

    /// <summary>Libraries that support the scenario natively.</summary>
    public IEnumerable<string> Libraries() => Libs.All.Where(l => Libs.Create(l).NotSupported(ScenarioId) is null);

    /// <summary>Creates a fresh logger writing to a fresh temp file.</summary>
    [IterationSetup]
    public void Setup()
    {
        var sc = Scenario.Get(ScenarioId);
        dir = Path.Combine(Path.GetTempPath(), "alog-comparison-bdn-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Adapter = Libs.Create(Lib);
        Adapter.Init(new LibConfig(Path.Combine(dir, "out.log"), sc.Format, sc.Filtered));
    }

    /// <summary>Flush, dispose and delete the file; not measured.</summary>
    [IterationCleanup]
    public void Cleanup()
    {
        Adapter.Close();
        Directory.Delete(dir, recursive: true);
    }
}

/// <summary>T1: plain message.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(10_000)]
public class T1Plain : MicroBase
{
    protected override string ScenarioId => "T1";


    /// <summary>One write.</summary>
    [Benchmark]
    public void Write() => Adapter.Simple();
}

/// <summary>T2: structured message, 2 properties.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(10_000)]
public class T2Structured : MicroBase
{
    private int i;

    protected override string ScenarioId => "T2";


    /// <summary>One write.</summary>
    [Benchmark]
    public void Write() => Adapter.Structured(i++);
}

/// <summary>T7: T2 with the JSON formatter.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(10_000)]
public class T7Json : MicroBase
{
    private int i;

    protected override string ScenarioId => "T7";


    /// <summary>One write.</summary>
    [Benchmark]
    public void Write() => Adapter.Structured(i++);
}

/// <summary>T3: error with a 3-level exception chain.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(5_000)]
public class T3Exception : MicroBase
{
    protected override string ScenarioId => "T3";


    /// <summary>One write.</summary>
    [Benchmark]
    public void Write() => Adapter.Error(Chain);
}

/// <summary>T4: 8 threads writing in parallel; reported per event.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(1)]
public class T4Threads : MicroBase
{
    private const int Threads = 8;
    private const int PerThread = 1_250; // 8 x 1250 = 10,000 events per iteration

    protected override string ScenarioId => "T4";


    /// <summary>8 × 1250 writes in parallel.</summary>
    [Benchmark(OperationsPerInvoke = Threads * PerThread)]
    public void Write()
        => Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (var k = 0; k < PerThread; k++)
                Adapter.Simple();
        });
}

/// <summary>T5: call below the minimum level.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(1_000_000)]
public class T5Filtered : MicroBase
{
    protected override string ScenarioId => "T5";


    /// <summary>One filtered call.</summary>
    [Benchmark]
    public void Write() => Adapter.Simple();
}
