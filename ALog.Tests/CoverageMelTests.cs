using System.Collections;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MelLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Options;

namespace ALog.Tests;

/// <summary>
/// Drives the MEL provider through its public registration API.
/// Extension internals are not visible to this assembly, so every path here
/// starts at <see cref="ILogger"/>, <see cref="ILoggingBuilder"/>, or <see cref="ALogLoggerProvider"/>.
/// </summary>
public sealed class CoverageMelTests : IDisposable
{
    /// <summary>Clears static ALog state before a provider test.</summary>
    public CoverageMelTests()
    {
        Log.Shutdown();
        Log.ClearContext();
    }

    /// <summary>Clears static ALog state after a provider test.</summary>
    public void Dispose()
    {
        Log.Shutdown();
        Log.ClearContext();
    }

    /// <summary>
    /// Null builder, config, configuration, and configure callbacks throw.
    /// Binding and the options action both register a provider.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddALog_RejectsNulls_AndBindsConfiguration()
    {
        await Task.CompletedTask;
        ILoggingBuilder? missing = null;
        Assert.Throws<ArgumentNullException>(() => missing!.AddALog(new LoggerConfig()));
        var services = new ServiceCollection();
        ILoggingBuilder? builder = null;
        services.AddLogging(logging => builder = logging);
        Assert.NotNull(builder);
        Assert.Throws<ArgumentNullException>(() => builder.AddALog((LoggerConfig)null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddALog((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => builder.AddALog((Action<ALogOptions>)null!));
        Assert.Throws<ArgumentNullException>(() => ((IConfiguration)null!).GetALogOptions());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:MinimumLevel"] = "Debug",
            ["ALog:Formatter"] = "plain",
            ["ALog:Console:Enabled"] = "false"
        }).Build();
        var bound = configuration.GetALogOptions();
        Assert.Equal("Debug", bound.MinimumLevel);
        Assert.Equal("plain", bound.Formatter);

        var directory = Path.Combine(Path.GetTempPath(), "alog-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var provider = new ServiceCollection()
                .AddLogging(logging => logging.AddALog(options =>
                {
                    options.Console = new ConsoleWriterOptions { Enabled = false };
                    options.File = new FileWriterOptions
                    {
                        Enabled = true,
                        Path = Path.Combine(directory, "mel.log")
                    };
                    options.MinimumLevel = "Information";
                }))
                .BuildServiceProvider();
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Bound").LogInformation("from-options");
            Assert.True(File.Exists(Path.Combine(directory, "mel.log")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Category rules, a provider-specific rule, a wildcard, two wildcards, None,
    /// a custom filter, and another provider's rule are applied through the logging builder.
    /// A reload replaces the table.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FilterRules_SelectCategory_AndReload()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var monitor = new FilterMonitor();
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Orders.Child", MelLevel.Warning, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule("ALogLoggerProvider", "Orders", MelLevel.Debug, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(
            typeof(ALogLoggerProvider).FullName,
            "Exact",
            MelLevel.Trace,
            null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Sys*", MelLevel.Debug, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Quiet", MelLevel.None, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Custom", null, (_, _, _) => false));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule("OtherProvider", "Other", MelLevel.Trace, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, null, null, null));

        using var provider = Build(writer, monitor);
        var alog = provider.GetServices<ILoggerProvider>().OfType<ALogLoggerProvider>().Single();
        var orders = alog.CreateLogger("Orders.Child");
        Assert.True(orders.IsEnabled(MelLevel.Debug));
        Assert.False(orders.IsEnabled(MelLevel.Trace));
        orders.LogDebug("debug-order {A}", "alpha");
        alog.CreateLogger("Exact").LogTrace("trace-exact");
        alog.CreateLogger("System.Net").LogDebug("sys");
        alog.CreateLogger("Quiet").LogInformation("quiet");
        alog.CreateLogger("Custom").LogTrace("custom-trace");
        alog.CreateLogger("Other").LogTrace("other-provider");
        Assert.False(alog.CreateLogger("Orders").IsEnabled(MelLevel.None));

        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Reloaded", MelLevel.Trace, null));
        monitor.Raise();
        alog.CreateLogger("Reloaded").LogTrace("after-reload");

        Assert.Contains(writer.Events, item => item.Message.Contains("alpha", StringComparison.Ordinal));
        Assert.Contains(writer.Events, item => item.Message == "after-reload");
        Assert.Contains(writer.Events, item => item.Message == "custom-trace");
        Assert.DoesNotContain(writer.Events, item => item.Message == "quiet");
    }

    /// <summary>
    /// A category pattern with two wildcards is skipped by the level table.
    /// The logging factory rejects that pattern, so this test calls the ALog provider directly.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FilterRules_SkipTwoWildcards()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var monitor = new FilterMonitor();
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "A*B*C", MelLevel.Trace, null));
        using var provider = Build(writer, monitor);
        var alog = provider.GetServices<ILoggerProvider>().OfType<ALogLoggerProvider>().Single();
        alog.CreateLogger("AxByCz").LogTrace("two-stars");
        Assert.DoesNotContain(writer.Events, item => item.Message == "two-stars");
    }

    /// <summary>
    /// More than 256 rules keep the tail. The first rule is outside that window.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FilterRules_KeepTheLast256()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var monitor = new FilterMonitor();
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Dropped", MelLevel.Trace, null));
        for (var i = 0; i < 255; i++)
        {
            monitor.CurrentValue.Rules.Add(new LoggerFilterRule(
                null,
                "Filler" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MelLevel.None,
                null));
        }

        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "Kept", MelLevel.Trace, null));
        using var provider = Build(writer, monitor);
        var factory = provider.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Dropped").LogTrace("dropped-line");
        factory.CreateLogger("Kept").LogTrace("kept-line");
        Assert.DoesNotContain(writer.Events, item => item.Message == "dropped-line");
        Assert.Contains(writer.Events, item => item.Message == "kept-line");
    }

    /// <summary>
    /// A provider wrapped around an existing engine does not dispose that engine.
    /// BeginScope on the logger pushes a string scope and a key/value scope.
    /// An external scope provider is copied onto the event at emit.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Provider_DirectEngine_LocalScopesAndExternalScope()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace));
        var provider = new ALogLoggerProvider(engine, ownsEngine: false);
        var logger = provider.CreateLogger("Direct");
        using (logger.BeginScope("batch-7"))
        using (logger.BeginScope(new Dictionary<string, object> { ["User"] = "ada" }))
            logger.LogInformation("local {A}", "alpha");

        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var scoped = provider.CreateLogger("External");
        using (scoped.BeginScope("outer"))
        using (scoped.BeginScope(new[] { new KeyValuePair<string, object?>("Zone", "eu") }))
            scoped.LogWarning("external");

        provider.CreateLogger(null!).LogInformation("empty-category");
        provider.Dispose();
        Assert.False(writer.Disposed);
        engine.Dispose();
        Assert.Contains(writer.Events, item => item.Message.Contains("alpha", StringComparison.Ordinal));
        Assert.Contains(writer.Events, item => item.Message == "external");
    }

    /// <summary>
    /// Every MEL level is enabled or disabled against an open table, including an unknown value.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Provider_IsEnabled_CoversEveryMelLevel()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace));
        using var provider = new ALogLoggerProvider(engine, ownsEngine: true);
        var logger = provider.CreateLogger("Levels");
        Assert.False(logger.IsEnabled(MelLevel.None));
        Assert.True(logger.IsEnabled(MelLevel.Trace));
        Assert.True(logger.IsEnabled(MelLevel.Debug));
        Assert.True(logger.IsEnabled(MelLevel.Information));
        Assert.True(logger.IsEnabled(MelLevel.Warning));
        Assert.True(logger.IsEnabled(MelLevel.Error));
        Assert.True(logger.IsEnabled(MelLevel.Critical));
        Assert.True(logger.IsEnabled((MelLevel)99));
        logger.LogCritical("critical");
        logger.LogError(new InvalidOperationException("boom"), "error");
        Assert.Contains(writer.Events, item => item.Message == "critical");
    }

    /// <summary>
    /// Scope shapes that the earlier tests did not send: object-only pairs, a full text
    /// list, an empty string, a null scope, a throwing scope walk, and lists past the cap.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Scopes_CoverPairCopiesLimitsAndThrowingWalk()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Trace));
        using var provider = new ALogLoggerProvider(engine, ownsEngine: true);
        var logger = provider.CreateLogger("Scopes");
        using (logger.BeginScope(new ObjectPairStream(2)))
        using (logger.BeginScope(new ObjectPairStream(0, onlyFormat: true)))
        using (logger.BeginScope(NullablePairs(129)))
        using (logger.BeginScope(FormatOnlyNullable()))
            logger.LogInformation("local-scopes");

        var scopes = new ScriptedScopes();
        scopes.Items.Add(null);
        scopes.Items.Add(string.Empty);
        scopes.Items.Add(new object());
        for (var i = 0; i < 70; i++)
            scopes.Items.Add("text-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        scopes.Items.Add(NullablePairs(129));
        scopes.Items.Add(new NullablePairStream(1));
        provider.SetScopeProvider(scopes);
        logger.LogInformation("external-cap");

        scopes.Items.Clear();
        scopes.Items.Add(new NullablePairStream(129));
        scopes.Items.Add(new ObjectPairStream(129));
        logger.LogInformation("external-streams");

        scopes.Throw = true;
        logger.LogInformation("scopes-threw");
        Assert.Contains(writer.Events, item => item.Message == "scopes-threw");
    }

    /// <summary>
    /// A later generic rule loses to a provider rule, and a longer generic rule wins
    /// over a shorter one. A null category matches every logger.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FilterRules_ProviderBeatsGeneric_AndNullCategoryMatches()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var monitor = new FilterMonitor();
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "App", MelLevel.Warning, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "App.Service.Orders", MelLevel.Information, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, "App.Service", MelLevel.Trace, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule("ALogLoggerProvider", "App", MelLevel.Debug, null));
        monitor.CurrentValue.Rules.Add(new LoggerFilterRule(null, null, MelLevel.Error, null));
        using var provider = Build(writer, monitor);
        var alog = provider.GetServices<ILoggerProvider>().OfType<ALogLoggerProvider>().Single();
        alog.CreateLogger("App.Service.Orders").LogDebug("beats");
        Assert.Contains(writer.Events, item => item.Message == "beats");
    }

    /// <summary>
    /// Binding <see cref="IConfiguration"/> and a background queue read the floor
    /// configuration's queue properties.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddALog_BindsConfiguration_AndReadsQueueFloors()
    {
        await Task.CompletedTask;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:MinimumLevel"] = "Error",
            ["ALog:Console:Enabled"] = "false"
        }).Build();
        using (var unused = new ServiceCollection().AddLogging(logging => logging.AddALog(configuration)).BuildServiceProvider())
        {
            unused.GetRequiredService<ILoggerFactory>().CreateLogger("Cfg").LogError("cfg");
        }

        var writer = new RecordingWriter();
        var config = new LoggerConfig()
            .AddWriter(writer)
            .SetMinimumLevel(ALog.LogLevel.Trace)
            .UseBackgroundQueue(true, 4, 1, TimeSpan.FromMilliseconds(20));
        using (var provider = new ServiceCollection().AddLogging(logging => logging.AddALog(config)).BuildServiceProvider())
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Queue").LogInformation("queued-floor");
        }

        Assert.Contains(writer.Events, item => item.Message == "queued-floor");
    }

    /// <summary>Builds a nullable pair list of the requested size.</summary>
    /// <param name="count">How many pairs to store.</param>
    /// <returns>A list the scope adapter treats as a known collection.</returns>
    private static List<KeyValuePair<string, object?>> NullablePairs(int count)
    {
        var pairs = new List<KeyValuePair<string, object?>>(count);
        for (var i = 0; i < count; i++)
            pairs.Add(new KeyValuePair<string, object?>("N" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i));
        return pairs;
    }

    /// <summary>Builds a nullable list whose keys the adapter drops.</summary>
    /// <returns>A list that copies to an empty property set.</returns>
    private static List<KeyValuePair<string, object?>> FormatOnlyNullable()
    {
        return new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", "x"),
            new(null!, "y")
        };
    }

    /// <summary>
    /// Registers ALog with a recording writer and the supplied filter monitor.
    /// The monitor is registered after logging so the provider receives it.
    /// </summary>
    /// <param name="writer">Destination for log events.</param>
    /// <param name="monitor">Filter rules visible to the provider.</param>
    /// <returns>A service provider the caller disposes.</returns>
    private static ServiceProvider Build(RecordingWriter writer, FilterMonitor monitor)
    {
        var config = new LoggerConfig().AddWriter(writer).SetMinimumLevel(ALog.LogLevel.Error).UseAsync(false);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddALog(config));
        services.AddSingleton<IOptionsMonitor<LoggerFilterOptions>>(monitor);
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Filter monitor whose <see cref="Raise"/> method runs the provider reload callback.
/// </summary>
internal sealed class FilterMonitor : IOptionsMonitor<LoggerFilterOptions>
{
    private Action<LoggerFilterOptions, string?>? changed;

    /// <summary>Rules the provider reads at construction and again on <see cref="Raise"/>.</summary>
    public LoggerFilterOptions CurrentValue { get; set; } = new();

    /// <summary>Returns <see cref="CurrentValue"/> for every name.</summary>
    /// <param name="name">Ignored.</param>
    /// <returns>The current options.</returns>
    public LoggerFilterOptions Get(string? name) => CurrentValue;

    /// <summary>Stores the provider callback and returns a handle that removes it.</summary>
    /// <param name="listener">Called from <see cref="Raise"/>.</param>
    /// <returns>A disposable that detaches <paramref name="listener"/>.</returns>
    public IDisposable OnChange(Action<LoggerFilterOptions, string?> listener)
    {
        changed += listener;
        return new Subscription(() => changed -= listener);
    }

    /// <summary>Invokes the stored callback with <see cref="CurrentValue"/>.</summary>
    public void Raise() => changed?.Invoke(CurrentValue, null);

    private sealed class Subscription : IDisposable
    {
        private readonly Action dispose;

        public Subscription(Action dispose) => this.dispose = dispose;

        /// <summary>Detaches the listener.</summary>
        public void Dispose() => dispose();
    }
}

/// <summary>
/// Streams pairs. Nullable annotations are erased at runtime, so this is the
/// same sequence as <see cref="NullablePairStream"/> and the adapter copies it
/// on the nullable path.
/// </summary>
internal sealed class ObjectPairStream : IEnumerable<KeyValuePair<string, object>>
{
    private readonly int count;
    private readonly bool onlyFormat;

    /// <summary>Stores how many real pairs to yield after the format key.</summary>
    /// <param name="count">Real pairs. Zero with <paramref name="onlyFormat"/> yields the format key only.</param>
    /// <param name="onlyFormat">When true, the format key is the only item.</param>
    public ObjectPairStream(int count, bool onlyFormat = false)
    {
        this.count = count;
        this.onlyFormat = onlyFormat;
    }

    /// <summary>Yields the format key, then the requested real pairs.</summary>
    /// <returns>The pair sequence.</returns>
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        yield return new KeyValuePair<string, object>("{OriginalFormat}", "x");
        if (onlyFormat)
            yield break;

        for (var i = 0; i < count; i++)
        {
            yield return new KeyValuePair<string, object>(
                "K" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i);
        }
    }

    /// <summary>Returns the typed enumerator.</summary>
    /// <returns>The pair sequence.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Streams nullable pairs without implementing a list, so the adapter scans
/// them one at a time instead of indexing a known collection.
/// </summary>
internal sealed class NullablePairStream : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly int count;

    /// <summary>Stores how many real pairs to yield after the skipped keys.</summary>
    /// <param name="count">Real pairs.</param>
    public NullablePairStream(int count) => this.count = count;

    /// <summary>Yields a format key, a null key, then the real pairs.</summary>
    /// <returns>The pair sequence.</returns>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return new KeyValuePair<string, object?>("{OriginalFormat}", "fmt");
        yield return new KeyValuePair<string, object?>(null!, "skip");
        for (var i = 0; i < count; i++)
        {
            yield return new KeyValuePair<string, object?>(
                "P" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i);
        }
    }

    /// <summary>Returns the typed enumerator.</summary>
    /// <returns>The pair sequence.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// External scope provider whose items and failure flag the test sets directly.
/// </summary>
internal sealed class ScriptedScopes : IExternalScopeProvider
{
    private bool throwWalk;

    /// <summary>Scopes returned by <see cref="ForEachScope{TState}"/>, in order.</summary>
    public List<object?> Items { get; } = new();

    /// <summary>When true, the next walk throws before any item is visited.</summary>
    public bool Throw
    {
        get => throwWalk;
        set => throwWalk = value;
    }

    /// <summary>Visits <see cref="Items"/>, or throws when <see cref="Throw"/> is set.</summary>
    /// <typeparam name="TState">State the caller passes through.</typeparam>
    /// <param name="callback">Invoked once per item.</param>
    /// <param name="state">Value passed to <paramref name="callback"/>.</param>
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        if (throwWalk)
            throw new InvalidOperationException("scope walk failed");

        for (var i = 0; i < Items.Count; i++)
            callback(Items[i], state);
    }

    /// <summary>Appends <paramref name="state"/> and returns a handle that does nothing.</summary>
    /// <param name="state">Scope to remember.</param>
    /// <returns>A disposable the caller can ignore.</returns>
    public IDisposable Push(object? state)
    {
        Items.Add(state);
        return new NoOp();
    }

    private sealed class NoOp : IDisposable
    {
        /// <summary>Does nothing. The test drops scopes by clearing <see cref="Items"/>.</summary>
        public void Dispose()
        {
        }
    }
}
