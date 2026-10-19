using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ALog;
using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using ALog.Writers.Cloud;
using ALog.Writers.File;
using ALog.Writers.Http;
using ALog.Writers.Sql;

namespace ALog.Tests;

public class GapRegressionTests : IDisposable
{
    public GapRegressionTests()
    {
        Log.Shutdown();
    }

    public void Dispose()
    {
        Log.Shutdown();
    }

    [Fact]
    public void G_E01_UseBackgroundQueue_Method_And_BackgroundQueueEnabled_Property()
    {
        var config = new LoggerConfig()
            .UseBackgroundQueue(enabled: true, capacity: 50, batchSize: 5);

        Assert.True(config.BackgroundQueueEnabled);
        Assert.Equal(50, config.BackgroundQueueCapacity);
        Assert.Equal(5, config.BackgroundQueueBatchSize);
    }

    [Fact]
    public async Task G_H09_FileLogWriter_ConcurrentWrites_PreserveLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alog-file-{Guid.NewGuid():N}.log");
        try
        {
            var writer = new FileLogWriter(path);
            var tasks = Enumerable.Range(0, 50)
                .Select(i => Task.Run(() => writer.Write(new LogEvent(
                    DateTime.UtcNow, LogLevel.Info, $"line-{i}", null, null))))
                .ToArray();

            await Task.WhenAll(tasks);
            writer.Dispose();

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(50, lines.Length);
            Assert.All(lines, l => Assert.Contains("line-", l));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void G_H10_Formatter_Not_DoubleWrapped()
    {
        var writerFormatter = new TrackingFormatter("W");
        var configFormatter = new TrackingFormatter("CFG");
        var capturing = new CapturingFormattableWriter { Formatter = writerFormatter };

        var config = new LoggerConfig()
            .AddWriter(capturing)
            .SetFormatter(configFormatter);

        Log.Init(config);
        Log.Write("hello");
        Log.Shutdown();

        Assert.Single(capturing.Lines);
        Assert.Equal("W(hello)", capturing.Lines[0]);
        Assert.Equal(1, writerFormatter.CallCount);
        Assert.Equal(0, configFormatter.CallCount);
    }

    [Fact]
    public void G_H05_SqlTableName_RejectsInjection_AcceptsValid()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SqlLogWriter(
                "Server=localhost;Database=Logs;Trusted_Connection=True;TrustServerCertificate=True;",
                tableName: "logs]; DROP TABLE",
                autoCreateTable: false));

        Assert.Equal("tableName", ex.ParamName);
        Assert.Contains("^[A-Za-z_][A-Za-z0-9_]*$", ex.Message);

        using var ok = new SqlLogWriter(
            "Server=localhost;Database=Logs;Trusted_Connection=True;TrustServerCertificate=True;",
            tableName: "Logs",
            autoCreateTable: false);
        Assert.NotNull(ok);

        using var ok2 = new SqlLogWriter(
            "Server=localhost;Database=Logs;Trusted_Connection=True;TrustServerCertificate=True;",
            tableName: "ApplicationLogs",
            autoCreateTable: false);
        Assert.NotNull(ok2);
    }

    [Fact]
    public async Task G_H06_AwsCloudWatchWriter_Puts_Via_Transport()
    {
        var transport = new RecordingAwsTransport();
        using var aws = new AwsCloudWatchWriter("/aws/app", "stream-1", transport);

        var evt = new LogEvent(DateTime.UtcNow, LogLevel.Info, "cloud-msg", null, null);
        aws.Write(evt);
        await aws.WriteAsync(evt);

        Assert.Equal(2, transport.Calls.Count);
        Assert.All(transport.Calls, c =>
        {
            Assert.Equal("/aws/app", c.LogGroup);
            Assert.Equal("stream-1", c.LogStream);
            Assert.Equal("cloud-msg", c.Message);
        });
    }

    [Fact]
    public async Task G_H07_AzureLogWriter_Posts_Correct_Track_Payload()
    {
        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var azure = new AzureLogWriter(
            instrumentationKey: "11111111-1111-1111-1111-111111111111",
            httpClient: client);

        var evt = new LogEvent(DateTime.UtcNow, LogLevel.Error, "ai-msg", new InvalidOperationException("boom"),
            new Dictionary<string, object> { ["user"] = "a" });

        azure.Write(evt);
        await azure.WriteAsync(evt);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal(AzureLogWriter.DefaultEndpoint, r.Uri.ToString());
            Assert.False(r.Headers.ContainsKey("Content-Encoding") ||
                         r.ContentHeaders.ContainsKey("Content-Encoding"));
            Assert.Contains("application/json", r.ContentType);
            Assert.Contains("\"iKey\":\"11111111-1111-1111-1111-111111111111\"", r.Body);
            Assert.Contains("\"baseType\":\"MessageData\"", r.Body);
            Assert.Contains("ai-msg", r.Body);
            Assert.DoesNotContain("X-API-Key", r.Headers.Keys);
        });

        // Injected client must not be disposed by writer
        azure.Dispose();
        Assert.NotNull(client);
    }

    [Fact]
    public void G_M04_HttpWriter_DoesNotDispose_InjectedClient()
    {
        var handler = new TrackingHttpMessageHandler();
        var client = new DisposableHttpClient(handler);
        var writer = new HttpLogWriter("https://example.com/logs", httpClient: client);
        writer.Dispose();
        Assert.False(client.WasDisposed);
        client.Dispose();
    }

    [Fact]
    public void G_M06_Shutdown_Allows_ReInit()
    {
        var w1 = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(w1));
        Log.Write("first");
        Log.Shutdown();

        Assert.Throws<InvalidOperationException>(() => Log.Write("should fail"));

        var w2 = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(w2));
        Log.Write("second");
        Log.Shutdown();

        Assert.Single(w1.Events);
        Assert.Single(w2.Events);
        Assert.True(w1.Disposed);
    }

    [Fact]
    public async Task G_M03_Flush_Drains_Queued_Events()
    {
        var writer = new RecordingWriter();
        var config = new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(enabled: true, capacity: 100, batchSize: 10, flushInterval: TimeSpan.FromMilliseconds(200));

        Log.Init(config);
        for (var i = 0; i < 25; i++)
            Log.Write($"q-{i}");

        await Log.FlushAsync();
        Log.Shutdown();

        Assert.Equal(25, writer.Events.Count);
    }

    [Fact]
    public void G_H08_EngineDispose_Disposes_Writers()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig().AddWriter(writer));
        Log.Shutdown();
        Assert.True(writer.Disposed);
    }

    [Fact]
    public async Task G_M05_AsyncEnabled_False_Uses_Sync_Path()
    {
        var writer = new RecordingWriter();
        var config = new LoggerConfig()
            .AddWriter(writer)
            .UseAsync(false);

        Log.Init(config);
        await Log.WriteAsync("sync-preferred");
        Log.Shutdown();

        Assert.Single(writer.Events);
        Assert.Equal(1, writer.SyncWriteCount);
        Assert.Equal(0, writer.AsyncWriteCount);
    }

    [Fact]
    public void A11y_Uninitialized_Write_Mentions_Init()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Log.Write("nope"));
        Assert.Contains("Log.Init", ex.Message);
    }

    /// <summary>
    /// A full queue drops the overflow on the caller.
    /// The producer returns while the blocked writer has finished nothing, and the drop counter equals the overflow.
    /// </summary>
    /// <returns>A task that finishes after the reported drop lines are summed.</returns>
    /// <remarks>
    /// The 300 ms wait was a test bound in this method, not a product timeout.
    /// <see cref="BackgroundLogQueue.TryEnqueue"/> returns as soon as it drops.
    /// Completion, an empty writer, and <see cref="BackgroundLogQueue.FullDropCount"/> decide that.
    /// The 30 s waits are hang guards: one for the processor to enter the writer, one for the producer, and one for the ALOG0070 line.
    /// Not thread-safe. The finally block releases the writer and restores SelfLog.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task F_QA_001_SyncEnqueue_DropsWhenQueueFull_AndReports()
    {
        var writer = new BlockingWriter();
        const int capacity = 5;
        const int total = 20;
        var lines = new List<string>();
        var dropReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
            if (line.Contains("events dropped: the background queue is full", StringComparison.Ordinal))
                dropReported.TrySetResult();
        });

        var config = new LoggerConfig()
            .AddWriter(writer)
            .UseBackgroundQueue(
                enabled: true,
                capacity: capacity,
                batchSize: 1,
                flushInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            Log.Init(config);
            Log.Write("evt-0");

            var entered = Stopwatch.StartNew();
            while (writer.EnteredCount == 0 && entered.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(10);

            Assert.True(writer.EnteredCount > 0, "processor never entered writer");

            var producer = Task.Run(() =>
            {
                for (var i = 1; i < total; i++)
                    Log.Write($"evt-{i}");
            });

            // Dropping returns. A producer still inside TryEnqueue after the hang guard blocked instead.
            var finished = await Task.WhenAny(producer, Task.Delay(TimeSpan.FromSeconds(30))) == producer;
            Assert.True(finished, "sync producers blocked instead of dropping");
            await producer;

            Assert.Empty(writer.Events);
            var queue = CurrentQueue();
            Assert.Equal((long)capacity, queue.AcceptedCount);
            Assert.Equal((long)(total - capacity), queue.FullDropCount);

            var reported = await Task.WhenAny(dropReported.Task, Task.Delay(TimeSpan.FromSeconds(30))) == dropReported.Task;
            Assert.True(reported, "ALOG0070 was not written");
            writer.Release();
            await Log.FlushAsync();
            Log.Shutdown();

            int dropped;
            lock (lines)
                dropped = SumReportedDrops(lines);

            Assert.Equal(total, writer.Events.Count + dropped);
        }
        finally
        {
            writer.Release();
            SelfLog.Target = previous;
            Log.Shutdown();
        }
    }

    [Fact]
    public void F_QA_002_HttpWriter_DoesNotMutate_InjectedClient()
    {
        var handler = new TrackingHttpMessageHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        var originalTimeout = client.Timeout;

        var headers = new Dictionary<string, string> { ["X-Test"] = "1" };
        using var writer = new HttpLogWriter(
            "https://example.com/logs",
            httpClient: client,
            timeout: TimeSpan.FromSeconds(7),
            headers: headers);

        Assert.Equal(originalTimeout, client.Timeout);
        Assert.False(client.DefaultRequestHeaders.Contains("X-Test"));
    }

    /// <summary>
    /// A wake from the idle wait must not run the writer on the thread that queued the event.
    /// TryEnqueue returns while the writer has finished nothing.
    /// </summary>
    /// <returns>A task that finishes after the blocked write is released.</returns>
    /// <remarks>
    /// The processor runs on the constructing thread until <c>WaitForNextTickAsync</c> inside
    /// <c>BackgroundLogQueue.WaitForWorkAsync</c>. The test reads that suspension and the wake source.
    /// After enqueue, <c>_wakeCts</c> must be a new instance, which only the wake path replaces.
    /// The 30 s waits are hang guards. Not thread-safe. The delegate releases the writer.
    /// A thread-id compare is not used. The pool may reuse the producer thread after the hop.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task F_QA_003_WakeCancel_DoesNot_Block_Enqueue_On_WriterThread()
    {
        // Run off xUnit sync context so Cancel continuations stay on the producer (repro path).
        await Task.Run(async () =>
        {
            var writer = new BlockingWriter();
            using var queue = new BackgroundLogQueue(
                new ILogWriter[] { writer },
                capacity: 100,
                batchSize: 1,
                flushInterval: TimeSpan.FromSeconds(30),
                asyncEnabled: false);

            AssertProcessorIsParkedInIdleWait(queue);
            var parkedWake = WakeSource(queue);
            Assert.Equal(0, writer.EnteredCount);

            var enqueueDone = new ManualResetEventSlim(false);
            var producer = Task.Run(() =>
            {
                var ok = queue.TryEnqueue(new LogEvent(
                    DateTime.UtcNow, LogLevel.Info, "wake-test", null, null));
                Assert.True(ok);
                enqueueDone.Set();
            });

            // Hang contract: TryEnqueue returns while the writer remains blocked.
            var returned = enqueueDone.Wait(TimeSpan.FromSeconds(30));
            Assert.True(returned, "TryEnqueue hung — wake Cancel likely continued into blocking Write on producer");
            Assert.Empty(writer.Events);

            var entered = Stopwatch.StartNew();
            while (writer.EnteredCount == 0 && entered.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(10).ConfigureAwait(false);

            Assert.True(writer.EnteredCount > 0, "processor never entered blocking writer");
            AssertWakeSourceReplaced(queue, parkedWake);
            Assert.Empty(writer.Events);

            writer.Release();
            await producer.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await queue.FlushAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Instance fields, including the compiler-generated state machine fields.</summary>
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Roslyn's suspension number for the first await in <c>WaitForWorkAsync</c>.
    /// That await is <c>PeriodicTimer.WaitForNextTickAsync</c>. The next number is the pool hop after a wake.
    /// </summary>
    private const int IdleWaitState = 0;

    /// <summary>Most state-machine fields this test will scan. The generated machines are smaller than this.</summary>
    private const int MaxStateMachineFields = 32;

    /// <summary>
    /// Fails unless the processor is suspended inside the idle wait and no writer call is running.
    /// </summary>
    /// <param name="queue">Queue just constructed, before any enqueue. Not null.</param>
    /// <remarks>
    /// A task that merely exists and is not completed is not enough: that is also true before the processor starts.
    /// The proof is the live state machine: <c>ProcessLogsAsync</c> is awaiting <c>WaitForWorkAsync</c>,
    /// and that method is on its first await, the timer wait. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static void AssertProcessorIsParkedInIdleWait(BackgroundLogQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var task = ProcessorTask(queue);
        Assert.False(task.IsCompleted, "processor left before the idle wait");
        var processor = StateMachineOf(task);
        var awaited = AwaitedTask(processor);
        object? waiting = null;
        if (awaited is not null && !awaited.IsCompleted && awaited.GetType().GetField("StateMachine", InstanceMembers) is not null)
            waiting = StateMachineOf(awaited);

        var parked = processor.GetType().Name.Contains("ProcessLogsAsync", StringComparison.Ordinal)
            && waiting is not null
            && waiting.GetType().Name.Contains("WaitForWorkAsync", StringComparison.Ordinal)
            && AsyncSuspensionState(waiting) == IdleWaitState
            && StoresValueTaskAwaiter(waiting);
        Assert.True(parked, "processor has not entered the idle wait");
        Assert.Equal(0, Inflight(queue));
    }

    /// <summary>
    /// Reads the wake source the idle wait is registered on.
    /// </summary>
    /// <param name="queue">Open queue. Not null.</param>
    /// <returns>The current <c>_wakeCts</c>. Not null.</returns>
    /// <remarks>
    /// The object identity is the proof. The wake path replaces this instance. Not thread-safe. Only the test thread calls it.
    /// </remarks>
    private static object WakeSource(BackgroundLogQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var field = typeof(BackgroundLogQueue).GetField("_wakeCts", InstanceMembers);
        Assert.NotNull(field);
        var source = field.GetValue(queue);
        Assert.NotNull(source);
        return source;
    }

    /// <summary>
    /// Fails unless the wake path has replaced the source captured before enqueue.
    /// </summary>
    /// <param name="queue">Queue that has accepted one event. Not null.</param>
    /// <param name="parkedSource">The source from <see cref="WakeSource"/> before that enqueue. Not null.</param>
    /// <remarks>
    /// <c>ReplaceWakeCts</c> runs only after the idle wait is cancelled. A processor that had not started
    /// does not replace the source when it later finds the event already queued. Not thread-safe.
    /// </remarks>
    private static void AssertWakeSourceReplaced(BackgroundLogQueue queue, object parkedSource)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(parkedSource);
        Assert.False(ReferenceEquals(parkedSource, WakeSource(queue)), "wake path did not replace _wakeCts");
    }

    /// <summary>
    /// Reads the processor task.
    /// </summary>
    /// <param name="queue">Open queue. Not null.</param>
    /// <returns>The background task. Not null.</returns>
    /// <remarks>Does not wait. Not thread-safe. Only the test thread calls it.</remarks>
    private static Task ProcessorTask(BackgroundLogQueue queue)
    {
        var field = typeof(BackgroundLogQueue).GetField("_backgroundTask", InstanceMembers);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<Task>(field.GetValue(queue));
    }

    /// <summary>
    /// Reads how many writer calls are in flight.
    /// </summary>
    /// <param name="queue">Open queue. Not null.</param>
    /// <returns>The in-flight count. Zero when the processor is idle.</returns>
    /// <remarks>Does not wait. Not thread-safe. Only the test thread calls it.</remarks>
    private static int Inflight(BackgroundLogQueue queue)
    {
        var field = typeof(BackgroundLogQueue).GetField("_inflight", InstanceMembers);
        Assert.NotNull(field);
        return (int)field.GetValue(queue)!;
    }

    /// <summary>
    /// Reads the compiler state machine stored on an async task.
    /// </summary>
    /// <param name="task">Async task. Not null. Must be a state-machine box.</param>
    /// <returns>The boxed state machine. Not null.</returns>
    /// <remarks>A task that is not a state-machine box fails the test. Not thread-safe.</remarks>
    private static object StateMachineOf(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var field = task.GetType().GetField("StateMachine", InstanceMembers);
        Assert.NotNull(field);
        var machine = field.GetValue(task);
        Assert.NotNull(machine);
        return machine;
    }

    /// <summary>
    /// Reads the task the state machine is currently awaiting.
    /// </summary>
    /// <param name="machine">Compiler state machine. Not null.</param>
    /// <returns>The awaited task, or null when no awaiter holds one.</returns>
    /// <remarks>
    /// Skips the builder. Only awaiter fields are considered, and at most <see cref="MaxStateMachineFields"/> of them.
    /// Not thread-safe.
    /// </remarks>
    private static Task? AwaitedTask(object machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var fields = machine.GetType().GetFields(InstanceMembers);
        var limit = fields.Length;
        if (limit > MaxStateMachineFields)
            limit = MaxStateMachineFields;

        for (var i = 0; i < limit; i++)
        {
            if (!fields[i].FieldType.Name.Contains("Awaiter", StringComparison.Ordinal))
                continue;

            var awaiter = fields[i].GetValue(machine);
            if (awaiter is null)
                continue;

            var taskField = awaiter.GetType().GetField("m_task", InstanceMembers);
            if (taskField?.GetValue(awaiter) is Task awaited)
                return awaited;
        }

        return null;
    }

    /// <summary>
    /// Reads Roslyn's suspension number from a state machine.
    /// </summary>
    /// <param name="machine">Compiler state machine. Not null.</param>
    /// <returns>The <c>&lt;&gt;1__state</c> value. Zero is the first await.</returns>
    /// <remarks>A missing state field fails the test. Not thread-safe.</remarks>
    private static int AsyncSuspensionState(object machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var field = machine.GetType().GetField("<>1__state", InstanceMembers);
        Assert.NotNull(field);
        return (int)field.GetValue(machine)!;
    }

    /// <summary>
    /// True when the machine has the awaiter <c>WaitForNextTickAsync</c> stores.
    /// </summary>
    /// <param name="machine">Compiler state machine. Not null.</param>
    /// <returns>True when a field type name contains <c>ValueTaskAwaiter</c>.</returns>
    /// <remarks>
    /// Scans at most <see cref="MaxStateMachineFields"/> fields. The pool-hop awaiter is a different type and does not match.
    /// Not thread-safe.
    /// </remarks>
    private static bool StoresValueTaskAwaiter(object machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var fields = machine.GetType().GetFields(InstanceMembers);
        var limit = fields.Length;
        if (limit > MaxStateMachineFields)
            limit = MaxStateMachineFields;

        for (var i = 0; i < limit; i++)
        {
            if (fields[i].FieldType.Name.Contains("ValueTaskAwaiter", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The queue installed by <see cref="Log.Init"/>. The drop counter lives on that queue.
    /// </summary>
    /// <returns>The live queue. Not null.</returns>
    /// <remarks>
    /// The static logger field is private. This test reads it so the drop counter can be checked without a product change.
    /// Not thread-safe. Only the test thread calls it, after <see cref="Log.Init"/> and before <see cref="Log.Shutdown"/>.
    /// </remarks>
    private static BackgroundLogQueue CurrentQueue()
    {
        var field = typeof(Log).GetField("_logger", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var engine = Assert.IsType<LoggerEngine>(field.GetValue(null));
        var queue = engine.Queue;
        Assert.NotNull(queue);
        return queue!;
    }

    /// <summary>
    /// Adds the counts from every ALOG0070 line. A partial first line plus a later line still sums to the drops.
    /// </summary>
    /// <param name="lines">Captured SelfLog lines. Not null.</param>
    /// <returns>The sum of the counts. Zero when no drop line is present.</returns>
    private static int SumReportedDrops(List<string> lines)
    {
        const string marker = " events dropped: the background queue is full";
        var sum = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var at = lines[i].IndexOf(marker, StringComparison.Ordinal);
            if (at <= 0)
                continue;

            sum += int.Parse(lines[i].AsSpan(0, at), CultureInfo.InvariantCulture);
        }

        return sum;
    }
}

internal sealed class CapturingFormattableWriter : ILogWriter, IFormattableLogWriter
{
    public ILogFormatter? Formatter { get; set; }
    public List<string> Lines { get; } = new();

    public void Write(LogEvent logEvent)
    {
        Lines.Add(Formatter?.Format(logEvent) ?? logEvent.Message);
    }

    public Task WriteAsync(LogEvent logEvent)
    {
        Write(logEvent);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
