using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ALog;
using ALog.Config;
using ALog.Writers.Console;

BenchmarkRunner.Run<ALogBenchmarks>();

[MemoryDiagnoser]
public class ALogBenchmarks
{
    private readonly NullWriter _null = new();

    [GlobalSetup]
    public void Setup()
    {
        Log.Shutdown();
        Log.Init(new LoggerConfig()
            .AddWriter(_null)
            .AddEnricher(new ALog.Enrichers.MachineNameEnricher())
            .SetMinimumLevel(LogLevel.Trace));
    }

    [GlobalCleanup]
    public void Cleanup() => Log.Shutdown();

    [Benchmark]
    public void SyncWrite_Plain() => Log.Write("hello");

    [Benchmark]
    public void SyncWrite_Template() => Log.Write(LogLevel.Info, "User {UserId} action {Action}", 42, "login");

    [Benchmark]
    public async Task AsyncWrite_Plain() => await Log.WriteAsync("hello-async");
}

internal sealed class NullWriter : ALog.Public.Interfaces.ILogWriter
{
    public void Write(ALog.Core.LogEvent logEvent) { }
    public Task WriteAsync(ALog.Core.LogEvent logEvent) => Task.CompletedTask;
    public void Dispose() { }
}
