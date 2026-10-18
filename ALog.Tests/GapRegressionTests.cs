using System.Globalization;
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

    [Fact]
    public async Task F_QA_001_SyncEnqueue_DropsWhenQueueFull_AndReports()
    {
        var writer = new BlockingWriter();
        const int capacity = 5;
        const int total = 20;
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
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

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (writer.EnteredCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.True(writer.EnteredCount > 0, "processor never entered writer");

            var producer = Task.Run(() =>
            {
                for (var i = 1; i < total; i++)
                    Log.Write($"evt-{i}");
            });

            // A full queue drops. The producers must not sit out the old five-second wait.
            var finished = await Task.WhenAny(producer, Task.Delay(300)) == producer;
            Assert.True(finished, "sync producers blocked instead of dropping");
            await producer.WaitAsync(TimeSpan.FromSeconds(5));

            var reported = false;
            for (var i = 0; i < 50 && !reported; i++)
            {
                lock (lines)
                    reported = lines.Exists(line => line.Contains("events dropped: the background queue is full", StringComparison.Ordinal));
                if (!reported)
                    await Task.Delay(20);
            }

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

    [Fact]
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

            // Let the processor reach idle await on PeriodicTimer / wake CTS.
            await Task.Delay(300).ConfigureAwait(false);

            var enqueueDone = new ManualResetEventSlim(false);
            var producer = Task.Run(() =>
            {
                var ok = queue.TryEnqueue(new LogEvent(
                    DateTime.UtcNow, LogLevel.Info, "wake-test", null, null));
                Assert.True(ok);
                enqueueDone.Set();
            });

            // Hang contract: TryEnqueue returns while writer remains blocked (before Release).
            // Do not assert ManagedThreadId ≠ WriteThreadId — pool may reuse the same id after Yield.
            var returned = enqueueDone.Wait(TimeSpan.FromSeconds(2));
            Assert.True(returned, "TryEnqueue hung — wake Cancel likely continued into blocking Write on producer");
            Assert.Empty(writer.Events);

            var enteredDeadline = DateTime.UtcNow.AddSeconds(5);
            while (writer.EnteredCount == 0 && DateTime.UtcNow < enteredDeadline)
                await Task.Delay(10).ConfigureAwait(false);

            Assert.True(writer.EnteredCount > 0, "processor never entered blocking writer");
            Assert.Empty(writer.Events);

            writer.Release();
            await producer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await queue.FlushAsync().ConfigureAwait(false);
        });
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
