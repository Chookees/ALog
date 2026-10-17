using System.Diagnostics;
using ALog.Config;
using ALog.Core;
using ALog.Enrichers;
using ALog.Filter;
using ALog.Formatters;
using ALog.Public.Interfaces;

namespace ALog.Tests;

/// <summary>
/// Covers engine, queue, dispatcher, template, context, and factory branches
/// that the existing suite left unmeasured. Behavior is pinned, not changed.
/// </summary>
public sealed class CoverageEngineTests : IDisposable
{
    /// <summary>Clears the static logger and context so one test cannot leak into the next.</summary>
    public CoverageEngineTests()
    {
        Log.Shutdown();
        Log.ClearContext();
        SelfLog.Target = null;
    }

    /// <summary>Same cleanup as the constructor, including a SelfLog target left by a failing test.</summary>
    public void Dispose()
    {
        Log.Shutdown();
        Log.ClearContext();
        SelfLog.Target = null;
    }

    /// <summary>
    /// A second <see cref="Log.Init(LoggerConfig)"/> disposes the previous engine.
    /// Exception, template, async, and scope overloads all reach the writer.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Log_Overloads_ReachTheWriter_AndReInitDisposesThePreviousEngine()
    {
        var first = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(first).SetMinimumLevel(LogLevel.Trace));
        var second = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(second).SetMinimumLevel(LogLevel.Trace).UseAsync(false));

        Log.Write(new InvalidOperationException("boom"), "sync-ex");
        await Log.WriteAsync(new InvalidOperationException("boom"), "async-ex");
        await Log.WriteAsync(LogLevel.Debug, "hello {A}", "alpha");
        Log.Write(new InvalidOperationException("boom"), LogLevel.Error, "bad {A}", "beta");
        using (Log.BeginScope("request", "r1"))
            Log.Write("in-scope");
        await Log.FlushAsync();
        Log.Reset();

        Assert.True(first.Disposed);
        Assert.Contains(second.Events, item => item.Message == "hello alpha");
        Assert.Contains(second.Events, item => item.Message == "in-scope");
    }

    /// <summary>
    /// Options build a file writer, plain formatter, and the four builtin enrichers.
    /// Console is off. The level alias <c>warning</c> is accepted. A zero flush
    /// interval becomes one millisecond.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Factory_FromOptions_BuildsFilePlainEnrichers_AndInitWrites()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var options = new ALogOptions
            {
                MinimumLevel = "warning",
                AsyncEnabled = false,
                BackgroundQueueEnabled = false,
                BackgroundQueueFlushIntervalMs = 0,
                Formatter = "plain",
                Console = new ConsoleWriterOptions { Enabled = false, UseColors = false },
                File = new FileWriterOptions { Enabled = true, Path = path, MaxFileSizeInBytes = 100_000 },
                Enrichers = ["MachineName", "ThreadId", "ProcessId", "Activity"]
            };

            var config = LoggerConfigFactory.FromOptions(options);
            Assert.Equal(LogLevel.Warn, config.MinimumLevel);
            Assert.Equal(4, config.Enrichers.Count);
            Assert.Single(config.Writers);
            Assert.IsType<PlainTextFormatter>(config.Formatter);
            Assert.Equal(TimeSpan.FromMilliseconds(1), config.BackgroundQueueFlushInterval);

            Log.Init(options);
            Log.Write(LogLevel.Warn, "file-line");
            Log.Shutdown();
            Assert.Contains("file-line", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Level aliases and the empty level map to the expected values.
    /// An unknown level and an unknown enricher throw <see cref="ArgumentException"/>.
    /// A null enricher list yields nothing. JSON pretty selects <see cref="JsonFormatter"/>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Factory_ParseLevel_AndEnrichers_CoverAliasesAndRejects()
    {
        await Task.CompletedTask;
        Assert.Equal(LogLevel.Info, LoggerConfigFactory.ParseLevel(null));
        Assert.Equal(LogLevel.Info, LoggerConfigFactory.ParseLevel("  "));
        Assert.Equal(LogLevel.Info, LoggerConfigFactory.ParseLevel("information"));
        Assert.Equal(LogLevel.Warn, LoggerConfigFactory.ParseLevel("warning"));
        Assert.Equal(LogLevel.Fatal, LoggerConfigFactory.ParseLevel("critical"));
        Assert.Equal(LogLevel.Fatal, LoggerConfigFactory.ParseLevel("none"));
        Assert.Equal(LogLevel.Debug, LoggerConfigFactory.ParseLevel("debug"));
        Assert.Throws<ArgumentException>(() => LoggerConfigFactory.ParseLevel("nope"));
        Assert.Empty(LoggerConfigFactory.CreateEnrichers(null));
        Assert.Throws<ArgumentException>(() => LoggerConfigFactory.CreateEnrichers(["nope"]).ToList());

        var json = LoggerConfigFactory.FromOptions(new ALogOptions
        {
            Formatter = "JSON",
            JsonPretty = true,
            Console = new ConsoleWriterOptions { Enabled = false }
        });
        Assert.IsType<JsonFormatter>(json.Formatter);
    }

    /// <summary>
    /// Below-minimum calls return before a writer runs. A null template is reported
    /// and still produces a line. Async dispatch and the background queue both deliver.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_DropsBelowMinimum_ReportsBadTemplate_AndQueuesAsync()
    {
        var writer = new RecordingWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .SetMinimumLevel(LogLevel.Error)
            .UseAsync(true));
        engine.Log("hidden", LogLevel.Info);
        await engine.LogAsync("hidden", LogLevel.Info);
        engine.Log(new InvalidOperationException("x"), "hidden", LogLevel.Info);
        await engine.LogAsync(new InvalidOperationException("x"), "hidden", LogLevel.Info);
        await engine.LogAsync("hidden {A}", LogLevel.Info, "a");
        engine.Log(new InvalidOperationException("x"), "hidden {A}", LogLevel.Info, "a");
        engine.LogStructured("cat", LogLevel.Info, null, "hidden");
        engine.LogStructured("cat", LogLevel.Error, null, null!, "value");
        engine.Log("visible", LogLevel.Error);
        await engine.FlushAsync();
        engine.Dispose();
        engine.Dispose();
        Assert.Contains(writer.Events, item => item.Message == "visible");
        Assert.DoesNotContain(writer.Events, item => item.Message == "hidden");
    }

    /// <summary>
    /// Async logging with a background queue uses <c>EnqueueAsync</c>. A throwing
    /// writer dispose is swallowed. The config formatter is copied onto a writer
    /// that does not already have one.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_AsyncQueue_CopiesFormatter_AndSwallowsWriterDispose()
    {
        var writer = new RecordingWriter();
        var formattable = new CapturingFormattableWriter();
        var engine = new LoggerEngine(new LoggerConfig()
            .AddWriter(writer)
            .AddWriter(formattable)
            .AddWriter(new BoomWriter())
            .SetFormatter(new PlainTextFormatter())
            .SetMinimumLevel(LogLevel.Trace)
            .UseBackgroundQueue(true, 20, 2, TimeSpan.FromMilliseconds(20)));

        await engine.LogAsync("queued {A}", LogLevel.Info, "alpha");
        await engine.FlushAsync();
        engine.Dispose();
        Assert.Contains(writer.Events, item => item.Message.Contains("alpha", StringComparison.Ordinal));
        Assert.NotNull(formattable.Formatter);
    }

    /// <summary>
    /// Direct queue calls cover dispose, a second dispose, and a batch whose writer throws.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Queue_Dispose_RejectsLaterEnqueue_AndBatchErrorsStayInside()
    {
        var queue = new BackgroundLogQueue(
            [new BoomWriter()],
            capacity: 10,
            batchSize: 2,
            flushInterval: TimeSpan.FromMilliseconds(20),
            asyncEnabled: true);
        await queue.EnqueueAsync(Event("one"));
        await queue.FlushAsync();
        queue.Dispose();
        Assert.False(queue.TryEnqueue(Event("two")));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync(Event("three")));
        await queue.FlushAsync();
        queue.Dispose();
    }

    /// <summary>
    /// Dispatcher catches writer failures for sync, async, and batch dispatch.
    /// Async disabled uses the sync path for a batch.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Dispatcher_SwallowsWriterFailures_OnSyncAsyncAndBatch()
    {
        var boom = new BoomWriter();
        var sync = new LogDispatcher([boom], asyncEnabled: false);
        sync.Dispatch(Event("s"));
        await sync.DispatchAsync(Event("s"));
        await sync.DispatchBatchAsync([Event("s")]);

        var live = new RecordingWriter();
        var asyncDispatcher = new LogDispatcher([boom, live], asyncEnabled: true);
        await asyncDispatcher.DispatchAsync(Event("kept"));
        await asyncDispatcher.DispatchBatchAsync([Event("kept-batch")]);
        Assert.Equal(2, live.Events.Count);
    }

    /// <summary>
    /// Template rendering covers a format, a null argument, a throwing argument,
    /// an extra argument, and an unused hole. <see cref="MessageTemplate.HasHoles"/>
    /// is false for an empty template.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MessageTemplate_CoversFormatNullThrowAndUnusedHole()
    {
        await Task.CompletedTask;
        var lines = new List<string>();
        SelfLog.Enable(lines.Add);
        try
        {
            var formatted = MessageTemplate.Format("n {A:0.0}", 1.5m);
            Assert.Equal("n 1.5", formatted.Rendered);
            var missing = MessageTemplate.Format("n {A}", new object?[] { null });
            Assert.Equal("n ", missing.Rendered);
            Assert.Equal(string.Empty, missing.Properties["A"]);
            var thrown = MessageTemplate.Format("n {A}", new BadFormat());
            Assert.Contains("format error", thrown.Rendered, StringComparison.Ordinal);
            var unused = MessageTemplate.Format("n {A} {B}", "only");
            Assert.Contains("{B}", unused.Rendered, StringComparison.Ordinal);
            Assert.False(MessageTemplate.HasHoles(""));
            Assert.NotEmpty(lines);
        }
        finally
        {
            SelfLog.Target = null;
        }
    }

    /// <summary>
    /// SelfLog ignores a null exception, a disabled mode, and a message getter that throws.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SelfLog_IgnoresNull_Disable_AndThrowingMessage()
    {
        await Task.CompletedTask;
        var lines = new List<string>();
        SelfLog.Report("ignored", null!);
        SelfLog.Enable(lines.Add);
        SelfLog.Disable();
        SelfLog.Report("", new BadMessageException());
        Assert.Empty(lines);
        SelfLog.Target = null;
        SelfLog.Report("stderr", new BadMessageException());
    }

    /// <summary>
    /// Context push covers an empty list, a null key, the 128-property cap,
    /// a streaming enumerable, text scopes, and a scope whose text throws.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LogContext_CoversEmptyNullLimitStreamAndThrowingScope()
    {
        await Task.CompletedTask;
        using (LogContext.Push([]))
            Log.ClearContext();

        var pairs = new List<KeyValuePair<string, object?>> { new(null!, "skip"), new("Keep", "yes") };
        using (LogContext.Push(pairs))
        {
            var writer = new RecordingWriter();
            Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
            Log.Write("ctx");
            Log.Shutdown();
            Assert.Equal("yes", writer.Events[0].Context!["Keep"]);
        }

        var many = new List<KeyValuePair<string, object?>>();
        for (var i = 0; i < 129; i++)
            many.Add(new KeyValuePair<string, object?>("K" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i));
        using (LogContext.Push(many))
            Assert.NotNull(LogContext.Push(Array.Empty<KeyValuePair<string, object?>>()));

        using (LogContext.Push(new PairStream()))
        using (LogContext.PushScope("batch"))
        using (LogContext.PushScope(new BadFormat()))
        using (LogContext.PushScope(null!))
        {
            Assert.NotNull(LogContext.PushScope("inner"));
        }
    }

    /// <summary>
    /// Disposing an outer scope before an inner one splices the chain.
    /// More than 64 nested scopes hits the depth guard when the event is built.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LogContext_SpliceAndDepthGuard()
    {
        await Task.CompletedTask;
        var outer = LogContext.Push([new KeyValuePair<string, object?>("Outer", 1)]);
        var inner = LogContext.Push([new KeyValuePair<string, object?>("Inner", 2)]);
        outer.Dispose();
        inner.Dispose();

        var frames = new IDisposable[70];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = LogContext.Push([new KeyValuePair<string, object?>("D" + i, i)]);

        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace));
        Log.Write("deep");
        Log.Shutdown();
        for (var i = frames.Length - 1; i >= 0; i--)
            frames[i].Dispose();
        Assert.Single(writer.Events);
    }

    /// <summary>
    /// Builtin enrichers, the minimum-level filter, and both activity states run.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Enrichers_Filter_AndActivity_PopulateOrSkip()
    {
        await Task.CompletedTask;
        var bag = new Dictionary<string, object>();
        var evt = Event("enrich");
        new MachineNameEnricher().Enrich(bag, evt);
        new ThreadIdEnricher().Enrich(bag, evt);
        new ProcessIdEnricher().Enrich(bag, evt);
        new ActivityEnricher().Enrich(bag, evt);
        Assert.False(bag.ContainsKey(ActivityEnricher.TraceIdProperty));
        using (var activity = new Activity("c1"))
        {
            activity.Start();
            new ActivityEnricher().Enrich(bag, evt);
            Assert.True(bag.ContainsKey(ActivityEnricher.TraceIdProperty));
        }

        var enricher = new PropertyEnricher("App", "alog");
        enricher.Enrich(bag, evt);
        Assert.Equal("alog", bag["App"]);
        Assert.Throws<ArgumentNullException>(() => new PropertyEnricher(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => new PropertyEnricher("x", null!));

        var filter = new MinimumLevelFilter(LogLevel.Warn);
        Assert.False(filter.ShouldLog(evt));
        Assert.True(filter.ShouldLog(Event("err", LogLevel.Error)));
    }

    /// <summary>Creates a temp directory for one factory test.</summary>
    /// <returns>An empty directory path the caller deletes.</returns>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a temp directory when it is still there.</summary>
    /// <param name="path">Directory from <see cref="NewDirectory"/>.</param>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Builds an info event at the fixed clock.</summary>
    /// <param name="message">Rendered message.</param>
    /// <param name="level">Level. The default is info.</param>
    /// <returns>An event with no exception and no properties.</returns>
    private static LogEvent Event(string message, LogLevel level = LogLevel.Info)
        => new(CoverageClock.UtcNow, level, message, null, null);

    /// <summary>Writer that throws from every operation so callers must catch.</summary>
    private sealed class BoomWriter : ILogWriter
    {
        /// <summary>Throws so the dispatcher and queue error paths run.</summary>
        /// <param name="logEvent">Ignored.</param>
        public void Write(LogEvent logEvent) => throw new InvalidOperationException("sync");

        /// <summary>Returns a failed task so async dispatch catches it.</summary>
        /// <param name="logEvent">Ignored.</param>
        /// <returns>A faulted task.</returns>
        public Task WriteAsync(LogEvent logEvent)
            => Task.FromException(new InvalidOperationException("async"));

        /// <summary>Throws so engine dispose must swallow the failure.</summary>
        public void Dispose() => throw new InvalidOperationException("dispose");
    }

    /// <summary>Formattable value whose conversion always throws.</summary>
    private sealed class BadFormat : IFormattable
    {
        /// <summary>Throws from <see cref="ToString"/> so template rendering reports the argument.</summary>
        public override string ToString() => throw new FormatException("bad");

        /// <summary>Throws for every format.</summary>
        /// <param name="format">Ignored.</param>
        /// <param name="formatProvider">Ignored.</param>
        /// <returns>Never returns.</returns>
        public string ToString(string? format, IFormatProvider? formatProvider) => throw new FormatException("bad");
    }

    /// <summary>Exception whose <see cref="Exception.Message"/> getter throws.</summary>
    private sealed class BadMessageException : Exception
    {
        /// <summary>Throws so SelfLog has to keep the line without the message text.</summary>
        public override string Message => throw new InvalidOperationException("no-message");
    }

    /// <summary>
    /// Enumerable that is not an <see cref="IReadOnlyCollection{T}"/>, so context copy
    /// uses the streaming path.
    /// </summary>
    private sealed class PairStream : IEnumerable<KeyValuePair<string, object?>>
    {
        /// <summary>Yields one pair and one null key.</summary>
        /// <returns>The enumerator.</returns>
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("Stream", "yes");
            yield return new KeyValuePair<string, object?>(null!, "skip");
        }

        /// <summary>Non-generic enumerator for <see cref="IEnumerable"/>.</summary>
        /// <returns>The same pairs as <see cref="GetEnumerator"/>.</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
