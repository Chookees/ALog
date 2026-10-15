using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using ALog.Internal;
using ALog.Public.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// Regressions for 0.2.1 follow-up: fail-closed filters, non-KVP scopes,
/// the level gate, and allocation of the disabled path.
/// </summary>
public sealed class Patch021RegressionTests : IDisposable
{
    // Measured on master f49d944 with GC.GetAllocatedBytesForCurrentThread,
    // minimum of 20 calls after warmup, pre-allocated object[].
    // Disabled template write allocated 1336 because the event was built first.
    // Enabled template write: 1368 bytes with no scope, 1512 bytes with one KVP scope.
    private const long MasterEnabledNoScopeBytes = 1368;
    private const long MasterEnabledWithScopeBytes = 1512;

    public Patch021RegressionTests()
    {
        Log.ClearContext();
        Log.Shutdown();
        SelfLog.Target = null;
    }

    public void Dispose()
    {
        SelfLog.Target = null;
        Log.ClearContext();
        Log.Shutdown();
    }

    [Fact]
    public void Category_Override_Below_Global_Level_Still_Writes()
    {
        var writer = new RecordingWriter();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddFilter("Chatty", MelLogLevel.Debug);
            builder.AddALog(new LoggerConfig()
                .AddWriter(writer)
                .SetMinimumLevel(LogLevel.Warn));
        });

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<ILoggerFactory>();
        var chatty = factory.CreateLogger("Chatty");
        var nested = factory.CreateLogger("Chatty.Worker");
        var quiet = factory.CreateLogger("Quiet");

        chatty.LogDebug("chatty-debug");
        nested.LogDebug("nested-debug");
        quiet.LogDebug("quiet-debug");
        quiet.LogWarning("quiet-warn");

        Assert.Contains(writer.Events, e => e.Message.Contains("chatty-debug", StringComparison.Ordinal));
        Assert.Contains(writer.Events, e => e.Message.Contains("nested-debug", StringComparison.Ordinal));
        Assert.Contains(writer.Events, e => e.Message.Contains("quiet-warn", StringComparison.Ordinal));
        Assert.DoesNotContain(writer.Events, e => e.Message.Contains("quiet-debug", StringComparison.Ordinal));
    }

    [Fact]
    public void Mel_IsEnabled_Is_False_Below_Cached_Minimum()
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Error));
        var mel = provider.CreateLogger("Tests.Gate");

        Assert.False(mel.IsEnabled(MelLogLevel.Warning));
        Assert.False(mel.IsEnabled(MelLogLevel.Information));
        Assert.True(mel.IsEnabled(MelLogLevel.Error));
        Assert.False(mel.IsEnabled(MelLogLevel.None));

        mel.LogInformation("below-gate");
        Assert.Empty(writer.Events);
    }

    [Fact]
    public void BeginScope_Text_Appears_In_Scope_Array()
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        var mel = provider.CreateLogger("Tests.ScopeText");

        using (mel.BeginScope(new Dictionary<string, object> { ["Job"] = "j-1" }))
        using (mel.BeginScope("Batch 7"))
        using (mel.BeginScope("row"))
        {
            mel.LogInformation("with-text");
        }

        mel.LogInformation("after-text");

        var during = ContextOf(writer, "with-text");
        Assert.Equal("j-1", during["Job"]);
        Assert.Equal(new[] { "Batch 7", "row" }, Assert.IsType<string[]>(during["Scope"]));

        var after = ContextOf(writer, "after-text");
        Assert.False(after.ContainsKey("Job"));
        Assert.False(after.ContainsKey("Scope"));
    }

    [Fact]
    public void Factory_BeginScope_Text_Appears_In_Scope_Array()
    {
        var writer = new RecordingWriter();
        var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(provider);
        });

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mel.Factory.Text");

        using (logger.BeginScope("outer"))
        using (logger.BeginScope(new Dictionary<string, object> { ["JobId"] = "job-7" }))
        using (logger.BeginScope("inner"))
        {
            logger.LogInformation("factory-text");
        }

        var during = ContextOf(writer, "factory-text");
        Assert.Equal("job-7", during["JobId"]);
        Assert.Equal(new[] { "outer", "inner" }, Assert.IsType<string[]>(during["Scope"]));
    }

    [Fact]
    public void Disabled_Level_Allocates_Nothing()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Error));
        object?[] values = ["x"];

        var allocated = MinAllocated(
            () => Log.Write(LogLevel.Debug, "cold {V}", values),
            warmup: 30,
            samples: 20);

        Assert.Equal(0, allocated);
        Assert.Empty(writer.Events);
    }

    [Fact]
    public void GetContext_With_No_Scopes_Allocates_Nothing()
    {
        Log.ClearContext();
        var first = ContextManager.GetContext();
        var allocated = MinAllocated(() => _ = ContextManager.GetContext(), warmup: 10, samples: 20);
        var second = ContextManager.GetContext();

        Assert.Equal(0, allocated);
        Assert.Same(first, second);
        Assert.Empty(second);
    }

    [Fact]
    public void LogContext_Push_And_PushScope_Dispose_Restores()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));

        using (LogContext.Push(new[] { new KeyValuePair<string, object?>("user", "outer") }))
        {
            using (LogContext.PushScope("Batch 7"))
            using (LogContext.Push(new[] { new KeyValuePair<string, object?>("user", "inner") }))
            {
                Log.Write("inside");
            }

            Log.Write("after-inner");
        }

        Log.Write("cleared");

        var inside = ContextOf(writer, "inside");
        Assert.Equal("inner", inside["user"]);
        Assert.Equal(new[] { "Batch 7" }, Assert.IsType<string[]>(inside["Scope"]));

        var after = ContextOf(writer, "after-inner");
        Assert.Equal("outer", after["user"]);
        Assert.False(after.ContainsKey("Scope"));

        var cleared = ContextOf(writer, "cleared");
        Assert.False(cleared.ContainsKey("user"));
        Assert.False(cleared.ContainsKey("Scope"));
    }

    [Fact]
    public void SelfLog_Enable_Receives_The_Filter_Failure_Line()
    {
        var lines = new List<string>();
        SelfLog.Enable(lines.Add);
        try
        {
            var writer = new RecordingWriter();
            Log.Init(new LoggerConfig()
                .AddWriter(writer)
                .SetFilter(new BoomFilter())
                .SetMinimumLevel(LogLevel.Trace));

            var ex = Record.Exception(() => Log.Write("secret"));
            Assert.Null(ex);
            Assert.Empty(writer.Events);
            Assert.Single(lines);
            Assert.Contains("filter-boom", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            SelfLog.Target = null;
        }
    }

    [Fact]
    public void SelfLog_Disable_Stops_Output()
    {
        var lines = new List<string>();
        SelfLog.Enable(lines.Add);
        SelfLog.Disable();
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            var writer = new RecordingWriter();
            Log.Init(new LoggerConfig()
                .AddWriter(writer)
                .SetFilter(new BoomFilter())
                .SetMinimumLevel(LogLevel.Trace));

            var ex = Record.Exception(() => Log.Write("secret"));
            Assert.Null(ex);
            Assert.Empty(writer.Events);
            Assert.Empty(lines);
            Assert.DoesNotContain("[ALog]", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
            SelfLog.Target = null;
        }
    }

    [Fact]
    public void Enabled_Log_Does_Not_Allocate_More_Than_Master()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        object?[] values = ["x"];

        var noScope = MinAllocated(
            () => Log.Write(LogLevel.Info, "on {V}", values),
            warmup: 40,
            samples: 20);

        long withScope;
        using (Log.BeginScope("user", "a"))
        {
            withScope = MinAllocated(
                () => Log.Write(LogLevel.Info, "scoped {V}", values),
                warmup: 40,
                samples: 20);
        }

        Assert.True(
            noScope <= MasterEnabledNoScopeBytes,
            "Enabled log allocated " + noScope + " bytes; master was " + MasterEnabledNoScopeBytes + ".");
        Assert.True(
            withScope <= MasterEnabledWithScopeBytes,
            "Enabled log with a scope allocated " + withScope + " bytes; master was " + MasterEnabledWithScopeBytes + ".");
    }

    private static long MinAllocated(Action action, int warmup, int samples)
    {
        for (var i = 0; i < warmup; i++)
            action();

        var min = long.MaxValue;
        for (var i = 0; i < samples; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            var used = GC.GetAllocatedBytesForCurrentThread() - before;
            if (used < min)
                min = used;
        }

        return min;
    }

    private static Dictionary<string, object> ContextOf(RecordingWriter writer, string message)
    {
        LogEvent? found = null;
        foreach (var evt in writer.Events)
        {
            if (evt.Message == message)
            {
                found = evt;
                break;
            }
        }

        Assert.NotNull(found);
        Assert.NotNull(found!.Context);
        return new Dictionary<string, object>(found.Context!, StringComparer.Ordinal);
    }

    private sealed class BoomFilter : ILogFilter
    {
        public bool ShouldLog(LogEvent logEvent) => throw new InvalidOperationException("filter-boom");
    }
}
