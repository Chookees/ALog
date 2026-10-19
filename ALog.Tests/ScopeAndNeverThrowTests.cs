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
/// Regressions for scope isolation and for logging that must not throw into the caller.
/// </summary>
public sealed class ScopeAndNeverThrowTests : IDisposable
{
    public ScopeAndNeverThrowTests()
    {
        Log.ClearContext();
        Log.Shutdown();
    }

    public void Dispose()
    {
        Log.ClearContext();
        Log.Shutdown();
    }

    [Fact]
    public void Duplicate_Keys_Inner_Scope_Wins()
    {
        var writer = Init();

        using (Log.BeginScope("user", "outer"))
        using (Log.BeginScope("user", "middle"))
        using (Log.BeginScope("user", "inner"))
        using (Log.BeginScope("request", "r1"))
        {
            Log.Write("dup");
        }

        var context = ContextOf(writer, "dup");
        Assert.Equal("inner", context["user"]);
        Assert.Equal("r1", context["request"]);
    }

    [Fact]
    public void Dispose_Restores_Previous_Scope()
    {
        var writer = Init();

        using (Log.BeginScope("user", "outer"))
        {
            Log.Write("before");
            using (Log.BeginScope("user", "inner"))
            using (Log.BeginScope("feature", "login"))
            {
                Log.Write("inside");
            }

            Log.Write("after");
        }

        Log.Write("cleared");

        var before = ContextOf(writer, "before");
        Assert.Equal("outer", before["user"]);
        Assert.False(before.ContainsKey("feature"));

        var inside = ContextOf(writer, "inside");
        Assert.Equal("inner", inside["user"]);
        Assert.Equal("login", inside["feature"]);

        var after = ContextOf(writer, "after");
        Assert.Equal("outer", after["user"]);
        Assert.False(after.ContainsKey("feature"));

        var cleared = ContextOf(writer, "cleared");
        Assert.False(cleared.ContainsKey("user"));
        Assert.False(cleared.ContainsKey("feature"));
    }

    [Fact]
    public void Nested_OutOfOrder_Dispose_Removes_Only_That_Scope()
    {
        var writer = Init();
        var outer = Log.BeginScope("a", "A");
        var middle = Log.BeginScope("b", "B");
        var inner = Log.BeginScope("c", "C");
        try
        {
            Log.Write("three");

            middle.Dispose();
            Log.Write("no-b");

            outer.Dispose();
            Log.Write("only-c");

            inner.Dispose();
            Log.Write("none");

            // A second dispose must not pop a newer scope that reused nothing here.
            inner.Dispose();
            outer.Dispose();
            Log.Write("still-none");
        }
        finally
        {
            inner.Dispose();
            middle.Dispose();
            outer.Dispose();
            Log.ClearContext();
        }

        AssertScope(writer, "three", ("a", "A"), ("b", "B"), ("c", "C"));
        AssertScope(writer, "no-b", ("a", "A"), ("c", "C"));
        AssertAbsent(writer, "no-b", "b");
        AssertScope(writer, "only-c", ("c", "C"));
        AssertAbsent(writer, "only-c", "a", "b");
        AssertAbsent(writer, "none", "a", "b", "c");
        AssertAbsent(writer, "still-none", "a", "b", "c");
    }

    [Fact]
    public async Task Parallel_Tasks_See_Only_Their_Own_Scopes()
    {
        var writer = Init();
        const int iterations = 32;

        for (var i = 0; i < iterations; i++)
        {
            Log.ClearContext();
            await RunIsolatedIteration(writer, i);
        }
    }

    [Fact]
    public async Task Disposing_On_One_Flow_Does_Not_Change_Sibling_Flow()
    {
        var writer = Init();
        var childEntered = NewSignal();
        var releaseChild = NewSignal();

        var root = Log.BeginScope("root", "R");
        var child = Task.Run(() =>
        {
            using (Log.BeginScope("child", "C"))
            {
                childEntered.TrySetResult();
                Assert.True(releaseChild.Task.Wait(TimeSpan.FromSeconds(30)), "child was not released");
                Log.Write("sibling-child");
            }
        });

        try
        {
            await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            root.Dispose();
            Log.Write("sibling-parent");
            releaseChild.TrySetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseChild.TrySetResult();
            root.Dispose();
            Log.ClearContext();
        }

        var parent = ContextOf(writer, "sibling-parent");
        Assert.False(parent.ContainsKey("root"));
        Assert.False(parent.ContainsKey("child"));

        var childContext = ContextOf(writer, "sibling-child");
        Assert.Equal("R", childContext["root"]);
        Assert.Equal("C", childContext["child"]);
    }

    [Fact]
    public async Task ClearContext_Does_Not_Clear_A_Sibling_Flow()
    {
        var writer = Init();
        var entered = NewSignal();
        var release = NewSignal();

        using (Log.BeginScope("root", "R"))
        {
            var child = Task.Run(() =>
            {
                using (Log.BeginScope("child", "C"))
                {
                    entered.TrySetResult();
                    Assert.True(release.Task.Wait(TimeSpan.FromSeconds(30)), "child was not released");
                    Log.Write("child-after-clear");
                }
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Log.ClearContext();
            Log.Write("parent-after-clear");
            release.TrySetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var parent = ContextOf(writer, "parent-after-clear");
        Assert.False(parent.ContainsKey("root"));
        Assert.False(parent.ContainsKey("child"));

        var childContext = ContextOf(writer, "child-after-clear");
        Assert.Equal("R", childContext["root"]);
        Assert.Equal("C", childContext["child"]);
    }

    [Fact]
    public async Task Scope_Remains_Visible_After_Await()
    {
        var writer = Init();

        using (Log.BeginScope("req", "after-await"))
        {
            await Task.Yield();
            Log.Write("later");
        }

        Log.Write("after-dispose");

        Assert.Equal("after-await", ContextOf(writer, "later")["req"]);
        Assert.False(ContextOf(writer, "after-dispose").ContainsKey("req"));
    }

    [Fact]
    public void Mel_BeginScope_Dictionary_Is_On_Log_Context()
    {
        var writer = new RecordingWriter();
        using var provider = new ALogLoggerProvider(
            new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        var mel = provider.CreateLogger("Tests.Scope");

        using (mel.BeginScope(new Dictionary<string, object> { ["RequestId"] = "r-9" }))
        {
            mel.LogInformation("hello {Name}", "world");
        }

        mel.LogInformation("after");

        Assert.Equal("r-9", ContextOf(writer, "hello world")["RequestId"]);
        Assert.False(ContextOf(writer, "after").ContainsKey("RequestId"));
    }

    [Fact]
    public void Mel_Factory_BeginScope_Is_On_Log_Context()
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
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mel.Factory");

        using (logger.BeginScope(new Dictionary<string, object> { ["JobId"] = "job-7" }))
        using (logger.BeginScope("Work {WorkId}", "w-1"))
        {
            logger.LogInformation("factory");
        }

        logger.LogInformation("factory-after");

        var during = ContextOf(writer, "factory");
        Assert.Equal("job-7", during["JobId"]);
        Assert.Equal("w-1", during["WorkId"]);
        Assert.False(ContextOf(writer, "factory-after").ContainsKey("JobId"));
        Assert.False(ContextOf(writer, "factory-after").ContainsKey("WorkId"));
    }

    [Fact]
    public void Throwing_Enricher_Is_Skipped_And_Event_Is_Logged()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .AddEnricher(new ThrowingEnricher())
            .AddEnricher(new MarkerEnricher())
            .SetMinimumLevel(LogLevel.Trace));

        var stderr = CaptureStderr(() =>
        {
            var ex = Record.Exception(() => Log.Write("enriched"));
            Assert.Null(ex);
        });

        Assert.Contains("[ALog]", stderr, StringComparison.Ordinal);
        Assert.Contains("enrich-boom", stderr, StringComparison.Ordinal);
        Assert.Single(writer.Events);
        Assert.Equal("enriched", writer.Events[0].Message);
        Assert.Equal("ok", writer.Events[0].Properties!["Marker"]);
    }

    [Fact]
    public void Throwing_Filter_Does_Not_Throw_And_Event_Is_Dropped()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .SetFilter(new ThrowingFilter())
            .SetMinimumLevel(LogLevel.Trace));

        var stderr = CaptureStderr(() =>
        {
            var ex = Record.Exception(() => Log.Write("kept"));
            Assert.Null(ex);
        });

        Assert.Contains("[ALog]", stderr, StringComparison.Ordinal);
        Assert.Contains("filter-boom", stderr, StringComparison.Ordinal);
        // A broken filter can be a PII filter. Drop the event (NLog/Serilog).
        Assert.Empty(writer.Events);
    }

    [Fact]
    public void Filter_That_Returns_False_Still_Drops_The_Event()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .SetFilter(new DenyFilter())
            .SetMinimumLevel(LogLevel.Trace));

        var ex = Record.Exception(() => Log.Write("dropped"));
        Assert.Null(ex);
        Assert.Empty(writer.Events);
    }

    [Fact]
    public void Argument_ToString_That_Throws_Renders_Placeholder()
    {
        var writer = Init();
        var boom = new ThrowingToString();

        var stderr = CaptureStderr(() =>
        {
            var ex = Record.Exception(() =>
                Log.Write(LogLevel.Info, "User {Name} did {Action}", boom, "login"));
            Assert.Null(ex);
        });

        Assert.Contains("[ALog]", stderr, StringComparison.Ordinal);
        Assert.Contains("to-string-boom", stderr, StringComparison.Ordinal);

        Assert.Single(writer.Events);
        var evt = writer.Events[0];
        Assert.Equal("User {Name:<format error>} did login", evt.Message);
        Assert.IsType<ThrowingToString>(evt.Properties!["Name"]);
        Assert.Equal("login", evt.Properties["Action"]);
    }

    [Fact]
    public void Formattable_Argument_That_Throws_Renders_Placeholder()
    {
        var writer = Init();

        var stderr = CaptureStderr(() =>
        {
            var ex = Record.Exception(() =>
                Log.Write(LogLevel.Info, "At {When:O}", new ThrowingFormattable()));
            Assert.Null(ex);
        });

        Assert.Contains("[ALog]", stderr, StringComparison.Ordinal);
        Assert.Contains("format-boom", stderr, StringComparison.Ordinal);
        Assert.Equal("At {When:<format error>}", writer.Events[0].Message);
    }

    [Fact]
    public void SelfLog_Failure_Does_Not_Escape_The_Log_Call()
    {
        var writer = Init();
        Log.Shutdown();
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .AddEnricher(new ThrowingEnricher())
            .SetMinimumLevel(LogLevel.Trace));

        var previous = Console.Error;
        Console.SetError(new ThrowingTextWriter());
        try
        {
            var ex = Record.Exception(() => Log.Write("still-safe"));
            Assert.Null(ex);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Single(writer.Events);
        Assert.Equal("still-safe", writer.Events[0].Message);
    }

    [Fact]
    public void Scope_With_No_Keys_Reuses_Cached_NoOp()
    {
        var empty = Array.Empty<KeyValuePair<string, object?>>();
        _ = ScopeContext.Begin(empty);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = ScopeContext.Begin(empty);
        var second = ScopeContext.Begin(null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(NoOpLogScope.Instance, first);
        Assert.Same(first, second);
        Assert.Equal(0, allocated);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void SelfLog_Target_Receives_One_Line_And_Cannot_Throw()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Target = line =>
        {
            lines.Add(line);
            throw new InvalidOperationException("target-boom");
        };

        try
        {
            var writer = new RecordingWriter();
            Log.Init(new LoggerConfig()
                .AddWriter(writer)
                .AddEnricher(new ThrowingEnricher())
                .SetMinimumLevel(LogLevel.Trace));

            var ex = Record.Exception(() => Log.Write("via-target"));
            Assert.Null(ex);
            Assert.Single(lines);
            Assert.Contains("enrich-boom", lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("\n", lines[0], StringComparison.Ordinal);
            Assert.Single(writer.Events);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    [Fact]
    public void Scope_Depth_Is_Bounded_And_Reported()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Target = lines.Add;
        var scopes = new List<ILogScope>(ContextManager.MaxScopeDepth + 1);
        try
        {
            for (var i = 0; i < ContextManager.MaxScopeDepth; i++)
                scopes.Add(Log.BeginScope("k", i));

            var extra = Log.BeginScope("k", "too-deep");
            Assert.Same(NoOpLogScope.Instance, extra);
            Assert.Contains(lines, line => line.Contains("depth", StringComparison.OrdinalIgnoreCase));

            var writer = Init();
            Log.Write("deep");
            Assert.Equal(ContextManager.MaxScopeDepth - 1, Assert.IsType<int>(ContextOf(writer, "deep")["k"]));
        }
        finally
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
                scopes[i].Dispose();

            SelfLog.Target = previous;
            Log.ClearContext();
        }
    }

    private static async Task RunIsolatedIteration(RecordingWriter writer, int iteration)
    {
        var leftEntered = NewSignal();
        var rightEntered = NewSignal();
        var release = NewSignal();
        var leftMessage = $"left-{iteration}";
        var rightMessage = $"right-{iteration}";
        var parentMessage = $"parent-{iteration}";
        var afterMessage = $"after-{iteration}";

        using (Log.BeginScope("root", "shared"))
        {
            var left = Task.Run(() => WriteBranch("L", leftMessage, leftEntered, release));
            var right = Task.Run(() => WriteBranch("R", rightMessage, rightEntered, release));

            await leftEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await rightEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Log.Write(parentMessage);
            release.TrySetResult();
            await Task.WhenAll(left, right);
            Log.Write(afterMessage);
        }

        var parent = ContextOf(writer, parentMessage);
        Assert.Equal("shared", parent["root"]);
        Assert.False(parent.ContainsKey("branch"));

        var leftContext = ContextOf(writer, leftMessage);
        Assert.Equal("shared", leftContext["root"]);
        Assert.Equal("L", leftContext["branch"]);

        var rightContext = ContextOf(writer, rightMessage);
        Assert.Equal("shared", rightContext["root"]);
        Assert.Equal("R", rightContext["branch"]);

        var after = ContextOf(writer, afterMessage);
        Assert.Equal("shared", after["root"]);
        Assert.False(after.ContainsKey("branch"));
    }

    private static void WriteBranch(
        string branch,
        string message,
        TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        using (Log.BeginScope("branch", branch))
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(30)), "branch was not released");
            Log.Write(message);
        }
    }

    private static RecordingWriter Init()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        return writer;
    }

    private static Dictionary<string, object> ContextOf(RecordingWriter writer, string message)
    {
        var matches = new List<LogEvent>();
        foreach (var evt in writer.Events)
        {
            if (evt.Message == message)
                matches.Add(evt);
        }

        var found = Assert.Single(matches);
        Assert.NotNull(found.Context);
        return new Dictionary<string, object>(found.Context!, StringComparer.Ordinal);
    }

    private static void AssertScope(
        RecordingWriter writer,
        string message,
        params (string Key, string Value)[] expected)
    {
        var context = ContextOf(writer, message);
        Assert.Equal(expected.Length, context.Count);
        foreach (var pair in expected)
            Assert.Equal(pair.Value, context[pair.Key]);
    }

    private static void AssertAbsent(RecordingWriter writer, string message, params string[] keys)
    {
        var context = ContextOf(writer, message);
        foreach (var key in keys)
            Assert.False(context.ContainsKey(key));
    }

    private static string CaptureStderr(Action action)
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            action();
            return buffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ThrowingEnricher : ILogEnricher
    {
        public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
            => throw new InvalidOperationException("enrich-boom");
    }

    private sealed class MarkerEnricher : ILogEnricher
    {
        public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
            => properties["Marker"] = "ok";
    }

    private sealed class ThrowingFilter : ILogFilter
    {
        public bool ShouldLog(LogEvent logEvent) => throw new InvalidOperationException("filter-boom");
    }

    private sealed class DenyFilter : ILogFilter
    {
        public bool ShouldLog(LogEvent logEvent) => false;
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("to-string-boom");
    }

    private sealed class ThrowingFormattable : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider)
            => throw new FormatException("format-boom");

        public override string ToString() => throw new InvalidOperationException("plain-boom");
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        public override void Write(char value) => throw new IOException("stderr-down");

        public override void Write(string? value) => throw new IOException("stderr-down");

        public override void WriteLine(string? value) => throw new IOException("stderr-down");
    }
}
