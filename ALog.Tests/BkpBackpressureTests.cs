using System.Globalization;
using System.Text;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using ALog.Public.Interfaces;
using ALog.Writers.File;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// QueueFullMode.Block, the file buffer, and the configurable flush timeout.
/// </summary>
public sealed class BkpBackpressureTests : IDisposable
{
    private const string AllowedMode = "Invalid ALog configuration at 'ALog:QueueFullMode': '";
    private const string OneWriterProvisional = "Each log file has one writer process; give each process its own file.";
    private const string ChangelogProvisional =
        "A log file now has exactly one writer process while ALog runs; give each process its own file (other processes can still read, rename, or delete it).";

    /// <summary>Drops a static logger left by another test.</summary>
    public BkpBackpressureTests() => Log.Shutdown();

    /// <summary>Drops the static logger after the test, including when an assert fails.</summary>
    public void Dispose() => Log.Shutdown();

    /// <summary>
    /// Eight callers each enqueue 1000 events into a blocked queue of 10. Every event is kept.
    /// </summary>
    /// <remarks>The 30 s wait is a hang guard. Not thread-safe beyond the eight producer threads.</remarks>
    [Fact(Timeout = 120000)]
    public async Task QueueFullMode_Block_KeepsEveryEventFromEightProducers()
    {
        var clock = NewClock();
        var writer = new BlockingWriter();
        var queue = BlockQueue(writer, clock, capacity: 10);
        Task? dispose = null;
        var accepted = new int[1];
        try
        {
            Assert.True(queue.TryEnqueue(Event("seed")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "writer never entered");
            var producers = new Task[8];
            for (var t = 0; t < producers.Length; t++)
            {
                var id = t;
                producers[t] = Task.Run(() =>
                {
                    for (var i = 0; i < 1000; i++)
                    {
                        if (queue.TryEnqueue(Event("p" + id.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture))))
                            Interlocked.Increment(ref accepted[0]);
                    }
                });
            }

            await DeadlineClock.WaitFor(() => queue.BlockWaitsStarted >= 1, "no caller waited on the full queue");
            writer.Release();
            await DeadlineClock.WaitForCompletion(Task.WhenAll(producers), "producers hung");
            Assert.Equal(8000, Volatile.Read(ref accepted[0]));
            Assert.Equal(8001, queue.AcceptedCount);
            Assert.Equal(0, queue.FullDropCount);
        }
        finally
        {
            writer.Release();
            dispose = Task.Run(queue.Dispose);
            await ReleaseDispose(clock, dispose, BackgroundLogQueue.ShutdownDeadline);
        }
    }

    /// <summary>
    /// A write on the queue's own thread drops instead of waiting, so the queue cannot deadlock.
    /// </summary>
    /// <remarks>The nested call is marked before it runs. A wait fails the 30 s hang guard.</remarks>
    [Fact(Timeout = 120000)]
    public async Task ConsumerThread_DropsInsteadOfWaiting()
    {
        var clock = NewClock();
        BackgroundLogQueue? queue = null;
        var writer = new ReentryWriter(() => queue!.TryEnqueue(Event("nested")));
        queue = BlockQueue(writer, clock, capacity: 1);
        try
        {
            Assert.True(queue.TryEnqueue(Event("seed")));
            await DeadlineClock.WaitFor(() => writer.NestedReturned && queue.FullDropCount >= 1, "nested enqueue did not drop");
            Assert.False(writer.NestedResult);
            Assert.True(queue.FullDropCount >= 1);
        }
        finally
        {
            var dispose = Task.Run(queue.Dispose);
            await ReleaseDispose(clock, dispose, BackgroundLogQueue.ShutdownDeadline);
        }
    }

    /// <summary>
    /// A caller blocked on a full queue is released at the instance timeout and counted as a closed drop.
    /// </summary>
    /// <remarks>The timeout is 1 s, so a queue that still waits 5 s fails. The 30 s waits are hang guards.</remarks>
    [Fact(Timeout = 120000)]
    public async Task Shutdown_ReleasesBlockedProducersAtTheTimeout()
    {
        var timeout = TimeSpan.FromSeconds(1);
        var clock = NewClock();
        var writer = new BlockingWriter();
        var queue = BlockQueue(writer, clock, capacity: 1, timeout: timeout);
        Task? dispose = null;
        var producerResult = new bool?[1];
        try
        {
            Assert.True(queue.TryEnqueue(Event("seed")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "writer never entered");
            var producer = Task.Run(() => producerResult[0] = queue.TryEnqueue(Event("waiting")));
            await DeadlineClock.WaitFor(() => queue.BlockWaitsStarted >= 1, "producer did not wait");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(Shutdown_ReleasesBlockedProducersAtTheTimeout));
            await DeadlineClock.AssertNotElapsed(clock, () => queue.ShutdownDeadlineElapsed != 0, timeout, "shutdown");
            Assert.False(producer.IsCompleted);
            Assert.False(dispose.IsCompleted);
            await DeadlineClock.AssertElapsed(clock, () => queue.ShutdownDeadlineElapsed != 0, timeout, "shutdown");
            await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
            await DeadlineClock.WaitForCompletion(producer, "producer hung");
            Assert.False(producerResult[0]);
            Assert.True(queue.ClosedDropCount >= 1);
            Assert.Equal(0, queue.FullDropCount);
        }
        finally
        {
            writer.Release();
            await ReleaseDispose(clock, dispose, timeout);
        }
    }

    /// <summary>Ten thousand events open the file once.</summary>
    /// <remarks>The flush interval is zero so each line is durable. The open counter is a delta because other tests share the process.</remarks>
    [Fact(Timeout = 120000)]
    public async Task File_OpensOnceForManyEvents()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "once.log");
            var before = LogFileRegistry.OpenCount;
            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                for (var i = 0; i < 10000; i++)
                    writer.Write(Event("n" + i.ToString(CultureInfo.InvariantCulture)));
            }

            Assert.Equal(before + 1, LogFileRegistry.OpenCount);
            Assert.Equal(10000, File.ReadAllLines(path).Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Two writers for one path share one stream and never tear a line.</summary>
    /// <remarks>Not thread-safe beyond the two writer tasks. The 30 s wait is a hang guard.</remarks>
    [Fact(Timeout = 120000)]
    public async Task File_TwoWritersShareOneStreamAndKeepWholeLines()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "shared.log");
            var before = LogFileRegistry.OpenCount;
            using (var left = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            using (var right = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                var first = Task.Run(() =>
                {
                    for (var i = 0; i < 40; i++)
                        left.Write(Event("L" + i.ToString("00", CultureInfo.InvariantCulture)));
                });
                var second = Task.Run(() =>
                {
                    for (var i = 0; i < 40; i++)
                        right.Write(Event("R" + i.ToString("00", CultureInfo.InvariantCulture)));
                });
                await DeadlineClock.WaitForCompletion(Task.WhenAll(first, second), "writers hung");
            }

            Assert.Equal(before + 1, LogFileRegistry.OpenCount);
            var lines = File.ReadAllLines(path);
            Assert.Equal(80, lines.Length);
            for (var i = 0; i < 40; i++)
            {
                var left = "L" + i.ToString("00", CultureInfo.InvariantCulture);
                var right = "R" + i.ToString("00", CultureInfo.InvariantCulture);
                Assert.Contains(lines, line => line.Contains(left, StringComparison.Ordinal));
                Assert.Contains(lines, line => line.Contains(right, StringComparison.Ordinal));
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Another handle can read the open file and then rename and delete it.</summary>
    /// <remarks>Does not assert that a second writer fails. Linux does not enforce the share mode.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_AllowsReadAndRenameWhileOpen()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "open.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("visible"));
            writer.Flush();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                var text = reader.ReadToEnd();
                Assert.Contains("visible", text, StringComparison.Ordinal);
            }

            var moved = path + ".moved";
            File.Move(path, moved);
            Assert.True(File.Exists(moved));
            File.Delete(moved);
            Assert.False(File.Exists(moved));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A full buffer is written without moving the clock. A line longer than the buffer is written whole.</summary>
    /// <remarks>The interval is 60 s so a time flush cannot explain the bytes.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_FlushesWhenTheBufferFills()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "buffer.log");
            var clock = NewClock();
            using var writer = new FileLogWriter(
                path,
                fileBufferSize: 4096,
                fileFlushInterval: TimeSpan.FromSeconds(60),
                clock: clock);
            var payload = new string('B', 3000);
            writer.Write(Event(payload));
            Assert.DoesNotContain(payload, ReadShared(path), StringComparison.Ordinal);
            writer.Write(Event(payload));
            Assert.Contains(payload, ReadShared(path), StringComparison.Ordinal);

            var wide = new string('W', 5000);
            writer.Write(Event(wide));
            Assert.Contains(wide, ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>The interval flush stays quiet one tick early and writes on the interval.</summary>
    /// <remarks>The buffer is larger than the line, so only the timer can explain the bytes.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_FlushesWhenTheIntervalElapses()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "interval.log");
            var clock = NewClock();
            var interval = TimeSpan.FromSeconds(1);
            using var writer = new FileLogWriter(path, fileFlushInterval: interval, clock: clock);
            writer.Write(Event("later"));
            Assert.DoesNotContain("later", ReadShared(path), StringComparison.Ordinal);
            clock.Advance(interval - DeadlineClock.Tick);
            Assert.DoesNotContain("later", ReadShared(path), StringComparison.Ordinal);
            clock.Advance(DeadlineClock.Tick);
            Assert.Contains("later", ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Flush writes a partial buffer. A flush before the first event does not throw.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_FlushPushesBufferedLines()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "flush.log");
            var clock = NewClock();
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            writer.Flush();
            writer.Write(Event("held"));
            Assert.DoesNotContain("held", ReadShared(path), StringComparison.Ordinal);
            writer.Flush();
            Assert.Contains("held", ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Dispose writes a partial buffer.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_DisposePushesBufferedLines()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "dispose.log");
            var clock = NewClock();
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            writer.Write(Event("held"));
            Assert.DoesNotContain("held", ReadShared(path), StringComparison.Ordinal);
            writer.Dispose();
            Assert.Contains("held", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A second dispose is ignored, including before any write.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_DisposeAgainIsIgnored()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var idle = Path.Combine(directory, "idle.log");
            var idleWriter = new FileLogWriter(idle, fileFlushInterval: TimeSpan.Zero);
            idleWriter.Dispose();
            idleWriter.Dispose();

            var path = Path.Combine(directory, "twice.log");
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("kept"));
            writer.Dispose();
            writer.Dispose();
            Assert.Contains("kept", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Zero, one tick, five seconds, and sixty seconds are accepted.</summary>
    [Fact(Timeout = 30000)]
    public async Task FlushTimeout_BoundsAcceptTheDocumentedValues()
    {
        await Task.CompletedTask;
        var path = LogOptionRules.PathOf("FlushTimeout");
        LogOptionRules.RequireDuration(TimeSpan.Zero, path);
        LogOptionRules.RequireDuration(TimeSpan.FromTicks(1), path);
        LogOptionRules.RequireDuration(TimeSpan.FromSeconds(5), path);
        LogOptionRules.RequireDuration(TimeSpan.FromSeconds(60), path);

        var config = new LoggerConfig();
        config.SetFileBufferSize(4096);
        config.SetFileBufferSize(16777216);
        config.SetFileFlushInterval(TimeSpan.Zero);
        config.SetFileFlushInterval(TimeSpan.FromSeconds(60));
        config.SetFlushTimeout(TimeSpan.Zero);
        config.SetFlushTimeout(TimeSpan.FromSeconds(60));
        config.UseBackgroundQueue(false, 1);
        config.UseBackgroundQueue(false, 100000);
        config.SetQueueFullMode(QueueFullMode.Block);
        Assert.Equal(QueueFullMode.Block, config.QueueFullMode);
        Assert.Equal(100000, config.BackgroundQueueCapacity);
    }

    /// <summary>Values outside the documented ranges fail at startup and name the path.</summary>
    [Fact(Timeout = 30000)]
    public async Task FlushTimeout_RejectsValuesOutsideTheDocumentedRange()
    {
        await Task.CompletedTask;
        var path = LogOptionRules.PathOf("FlushTimeout");
        AssertRejected(() => LogOptionRules.RequireDuration(TimeSpan.FromTicks(-1), path));
        AssertRejected(() => LogOptionRules.RequireDuration(TimeSpan.FromSeconds(60) + TimeSpan.FromTicks(1), path));
        AssertRejected(() => LogOptionRules.RequireDuration(Timeout.InfiniteTimeSpan, path));

        var config = new LoggerConfig();
        AssertRejected(() => config.SetFileBufferSize(4095));
        AssertRejected(() => config.SetFlushTimeout(TimeSpan.FromSeconds(61)));
        AssertRejected(() => config.UseBackgroundQueue(true, 0));
        AssertRejected(() => config.SetQueueFullMode((QueueFullMode)9));
    }

    /// <summary>
    /// Dispose stays open through the cutoff and returns on the flush timeout.
    /// </summary>
    /// <param name="boundMilliseconds">1000 or 30000. The cutoff is 100 ms earlier.</param>
    /// <remarks>The writer stays blocked, so dispose cannot finish when the cutoff fires. The 30 s waits are hang guards.</remarks>
    [Theory(Timeout = 120000)]
    [InlineData(1000)]
    [InlineData(30000)]
    public async Task FlushTimeout_ShortBound_StaysOpenUntilTheBound(int boundMilliseconds)
    {
        var timeout = TimeSpan.FromMilliseconds(boundMilliseconds);
        var cutoff = LogOptionRules.DeriveCutoff(timeout);
        var clock = NewClock();
        var writer = new BlockingWriter();
        var queue = BlockQueue(writer, clock, capacity: 1, timeout: timeout);
        Task? dispose = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "writer never entered");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(FlushTimeout_ShortBound_StaysOpenUntilTheBound));
            await DeadlineClock.AssertNotElapsed(clock, () => queue.CutoffElapsed != 0, cutoff, "cutoff");
            await DeadlineClock.AssertElapsed(clock, () => queue.CutoffElapsed != 0, cutoff, "cutoff");
            Assert.False(dispose.IsCompleted);
            var rest = timeout - cutoff;
            await DeadlineClock.AssertNotElapsed(clock, () => queue.ShutdownDeadlineElapsed != 0, rest, "shutdown");
            await DeadlineClock.AssertElapsed(clock, () => queue.ShutdownDeadlineElapsed != 0, rest, "shutdown");
            await DeadlineClock.WaitForCompletion(dispose, "shutdown hung");
        }
        finally
        {
            writer.Release();
            await ReleaseDispose(clock, dispose, timeout);
        }
    }

    /// <summary>The cutoff is half below 200 ms and 100 ms earlier at and above 200 ms.</summary>
    [Fact(Timeout = 30000)]
    public async Task Cutoff_IsDerivedFromTheTimeout()
    {
        await Task.CompletedTask;
        Assert.Equal(TimeSpan.Zero, LogOptionRules.DeriveCutoff(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, LogOptionRules.DeriveCutoff(TimeSpan.FromTicks(1)));
        Assert.Equal(TimeSpan.FromMilliseconds(50), LogOptionRules.DeriveCutoff(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), LogOptionRules.DeriveCutoff(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.FromMilliseconds(900), LogOptionRules.DeriveCutoff(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromMilliseconds(4900), LogOptionRules.DeriveCutoff(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMilliseconds(29900), LogOptionRules.DeriveCutoff(TimeSpan.FromSeconds(30)));
        Assert.Equal("5 s", BackgroundLogQueue.DeadlineText(TimeSpan.FromSeconds(5)));
        Assert.Equal("0 s", BackgroundLogQueue.DeadlineText(TimeSpan.Zero));
        Assert.Equal("60 s", BackgroundLogQueue.DeadlineText(TimeSpan.FromSeconds(60)));
        Assert.Equal(
            TimeSpan.FromTicks(1).ToString("c", CultureInfo.InvariantCulture),
            BackgroundLogQueue.DeadlineText(TimeSpan.FromTicks(1)));
    }

    /// <summary>JSON-shaped values bind, including a trimmed mode name the binder would otherwise leave as Drop.</summary>
    [Fact(Timeout = 30000)]
    public async Task Options_BindFromConfiguration()
    {
        await Task.CompletedTask;
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:QueueFullMode"] = " block ",
            ["ALog:FileBufferSize"] = " 8192 ",
            ["ALog:FileFlushInterval"] = "00:00:02",
            ["ALog:FlushTimeout"] = " 00:00:10 ",
            ["ALog:BackgroundQueueCapacity"] = "50"
        }).Build().GetALogOptions();

        Assert.Equal(QueueFullMode.Block, options.QueueFullMode);
        Assert.Equal(8192, options.FileBufferSize);
        Assert.Equal(TimeSpan.FromSeconds(2), options.FileFlushInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.FlushTimeout);
        Assert.Equal(50, options.BackgroundQueueCapacity);

        var edges = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:QueueFullMode"] = "DROP",
            ["ALog:FileBufferSize"] = "4096",
            ["ALog:FileFlushInterval"] = "00:00:00",
            ["ALog:FlushTimeout"] = "00:01:00",
            ["ALog:BackgroundQueueCapacity"] = "100000"
        }).Build().GetALogOptions();
        Assert.Equal(QueueFullMode.Drop, edges.QueueFullMode);
        Assert.Equal(4096, edges.FileBufferSize);
        Assert.Equal(TimeSpan.Zero, edges.FileFlushInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), edges.FlushTimeout);
        Assert.Equal(100000, edges.BackgroundQueueCapacity);

        var wide = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:FileBufferSize"] = "16777216"
        }).Build().GetALogOptions();
        Assert.Equal(16777216, wide.FileBufferSize);
    }

    /// <summary>Invalid raw values fail in both configuration entries. The echoed text is one line and at most 64 characters.</summary>
    [Fact(Timeout = 30000)]
    public async Task Options_RejectInvalidConfigurationValues()
    {
        await Task.CompletedTask;
        var mode = LogOptionRules.NotAllowed(LogOptionRules.PathOf("QueueFullMode"), "1");
        Assert.Equal(mode, MessageOf(new Dictionary<string, string?> { ["ALog:QueueFullMode"] = "1" }));
        Assert.Equal(mode, AddMessage(new Dictionary<string, string?> { ["ALog:QueueFullMode"] = "1" }));
        Assert.StartsWith(AllowedMode, mode, StringComparison.Ordinal);

        var broken = "No\nMode";
        var brokenMessage = MessageOf(new Dictionary<string, string?> { ["ALog:QueueFullMode"] = broken });
        Assert.Equal(LogOptionRules.NotAllowed(LogOptionRules.PathOf("QueueFullMode"), broken), brokenMessage);
        Assert.DoesNotContain("\n", brokenMessage, StringComparison.Ordinal);
        Assert.Contains("No Mode", brokenMessage, StringComparison.Ordinal);

        var longValue = new string('Q', 80);
        var longMessage = MessageOf(new Dictionary<string, string?> { ["ALog:QueueFullMode"] = longValue });
        Assert.Equal(LogOptionRules.NotAllowed(LogOptionRules.PathOf("QueueFullMode"), longValue), longMessage);
        Assert.DoesNotContain(new string('Q', 65), longMessage, StringComparison.Ordinal);

        var buffer = LogOptionRules.Outside(LogOptionRules.PathOf("FileBufferSize"), "4095", LogOptionRules.BufferRange);
        Assert.Equal(buffer, MessageOf(new Dictionary<string, string?> { ["ALog:FileBufferSize"] = "4095" }));
        Assert.Equal(buffer, AddMessage(new Dictionary<string, string?> { ["ALog:FileBufferSize"] = "4095" }));
        Assert.Equal(
            LogOptionRules.Unreadable(LogOptionRules.PathOf("FileBufferSize"), "nope", LogOptionRules.BufferRange),
            MessageOf(new Dictionary<string, string?> { ["ALog:FileBufferSize"] = "nope" }));
        Assert.Equal(
            LogOptionRules.Outside(LogOptionRules.PathOf("BackgroundQueueCapacity"), "0", LogOptionRules.CapacityRange),
            MessageOf(new Dictionary<string, string?> { ["ALog:BackgroundQueueCapacity"] = "0" }));
        Assert.Equal(
            LogOptionRules.Outside(LogOptionRules.PathOf("BackgroundQueueCapacity"), "100001", LogOptionRules.CapacityRange),
            MessageOf(new Dictionary<string, string?> { ["ALog:BackgroundQueueCapacity"] = "100001" }));
        Assert.Equal(
            LogOptionRules.Unreadable(LogOptionRules.PathOf("FileFlushInterval"), "soon", LogOptionRules.DurationRange),
            MessageOf(new Dictionary<string, string?> { ["ALog:FileFlushInterval"] = "soon" }));

        var infinite = Timeout.InfiniteTimeSpan.ToString("c", CultureInfo.InvariantCulture);
        var infiniteMessage = MessageOf(new Dictionary<string, string?> { ["ALog:FlushTimeout"] = infinite });
        Assert.Equal(LogOptionRules.Outside(LogOptionRules.PathOf("FlushTimeout"), infinite, LogOptionRules.DurationRange), infiniteMessage);
        Assert.Contains("outside the allowed range", infiniteMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("not readable", infiniteMessage, StringComparison.Ordinal);
    }

    /// <summary>A change after Init is ignored and ALOG0105 is reported once per option.</summary>
    [Fact(Timeout = 30000)]
    public async Task Startup_IgnoresChangesAfterInit()
    {
        await Task.CompletedTask;
        var config = new LoggerConfig()
            .AddWriter(new RecordingWriter())
            .UseBackgroundQueue(true, 10)
            .SetQueueFullMode(QueueFullMode.Drop);
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        try
        {
            Log.Init(config);
            config.SetQueueFullMode(QueueFullMode.Block);
            config.SetQueueFullMode(QueueFullMode.Block);
            config.SetFileBufferSize(8192);
            Assert.Equal(QueueFullMode.Drop, config.QueueFullMode);
            Assert.Equal(LogOptionRules.DefaultBuffer, config.FileBufferSize);
            Assert.Single(lines, line => line.Contains("ALOG0105: QueueFullMode", StringComparison.Ordinal));
            Assert.Contains(
                "ALOG0105: FileBufferSize is read only at startup. The new value was ignored. Restart the app to apply it.",
                lines,
                StringComparer.Ordinal);
        }
        finally
        {
            SelfLog.Target = previous;
            Log.Shutdown();
        }
    }

    /// <summary>The provider seals the same four options when it is constructed.</summary>
    [Fact(Timeout = 30000)]
    public async Task Provider_IgnoresChangesAfterRegistration()
    {
        await Task.CompletedTask;
        var config = new LoggerConfig().AddWriter(new RecordingWriter());
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        try
        {
            using var provider = new ALogLoggerProvider(config);
            config.SetFlushTimeout(TimeSpan.FromSeconds(9));
            config.SetFileFlushInterval(TimeSpan.Zero);
            Assert.Equal(TimeSpan.FromSeconds(5), config.FlushTimeout);
            Assert.Equal(TimeSpan.FromSeconds(1), config.FileFlushInterval);
            Assert.Contains(
                "ALOG0105: FlushTimeout is read only at startup. The new value was ignored. Restart the app to apply it.",
                lines,
                StringComparer.Ordinal);
            Assert.Contains(
                "ALOG0105: FileFlushInterval is read only at startup. The new value was ignored. Restart the app to apply it.",
                lines,
                StringComparer.Ordinal);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>Shutdown writes every queued line when the caller blocks instead of dropping.</summary>
    /// <remarks>The fact timeout is a hang guard. The file is read after dispose, which flushes the buffer.</remarks>
    [Fact(Timeout = 180000)]
    public async Task Shutdown_WritesEveryQueuedLine()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "all.log");
            var config = new LoggerConfig()
                .AddWriter(new FileLogWriter(path))
                .UseBackgroundQueue(true, 1000)
                .SetQueueFullMode(QueueFullMode.Block);
            using var engine = new LoggerEngine(config);
            for (var i = 0; i < 100000; i++)
                engine.Log("n" + i.ToString(CultureInfo.InvariantCulture));

            engine.Dispose();
            Assert.Equal(0, engine.Queue!.FullDropCount);
            Assert.Equal(100000, File.ReadAllLines(path).Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>An older configuration that does not declare the new members still builds an engine.</summary>
    [Fact(Timeout = 30000)]
    public async Task LegacyConfiguration_KeepsDefaultMembers()
    {
        await Task.CompletedTask;
        ILogConfiguration legacy = new LegacyConfiguration();
        Assert.Equal(QueueFullMode.Drop, legacy.QueueFullMode);
        Assert.Equal(LogOptionRules.DefaultBuffer, legacy.FileBufferSize);
        Assert.Equal(LogOptionRules.DefaultInterval, legacy.FileFlushInterval);
        Assert.Equal(LogOptionRules.DefaultFlushTimeout, legacy.FlushTimeout);
        using var engine = new LoggerEngine(legacy);
        engine.Dispose();
    }

    /// <summary>The README and changelog use the final sentences once, and the changelog does not say backpressure.</summary>
    [Fact(Timeout = 30000)]
    public async Task Docs_LockBackpressureTexts()
    {
        await Task.CompletedTask;
        var readme = ReadRepoFile("README.md");
        var changelog = ReadRepoFile("CHANGELOG.md");
        Assert.Equal(1, CountOf(readme, "backpressure"));
        var at = readme.IndexOf("backpressure", StringComparison.Ordinal);
        var window = readme.Substring(at, Math.Min(80, readme.Length - at));
        Assert.Contains("QueueFullMode = Block", window, StringComparison.Ordinal);
        Assert.Contains(
            "When the queue is full, ALog drops the new event by default and counts it (SelfLog ALOG0070). To make the caller wait instead, set `QueueFullMode = Block` (in JSON: `\"ALog\": { \"QueueFullMode\": \"Block\" }`). Even with `Block`, a call from the queue's own writer thread or after shutdown has started is dropped and counted, so logging can never deadlock.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("#### Queue, file buffer, and shutdown options", readme, StringComparison.Ordinal);
        Assert.Contains("`QueueFullMode`: `Drop` (default) drops and counts new events while the queue is full. `Block` makes the caller wait until there is room.", readme, StringComparison.Ordinal);
        Assert.Contains("`FileBufferSize`: how much `FileLogWriter` collects before it writes to the file. Default 64 KiB, 4 KiB to 16 MiB.", readme, StringComparison.Ordinal);
        Assert.Contains("`FileFlushInterval`: how often buffered lines are written even if the buffer is not full. Default 1 s, 0 to 60 s. `0` writes every event right away.", readme, StringComparison.Ordinal);
        Assert.Contains("`FlushTimeout`: how long `Flush`, `Dispose`, and `Shutdown` wait for queued events. Default 5 s, 0 to 60 s. Events still unwritten after that are counted (ALOG0071).", readme, StringComparison.Ordinal);
        Assert.Contains("Each log file has one writer process. Give each process its own file; other programs can still read, tail, and rotate it.", readme, StringComparison.Ordinal);
        Assert.Contains("If the process crashes, the file can miss up to one `FileFlushInterval` or one `FileBufferSize` of lines, whichever comes first. Set `FileFlushInterval` to `0` if every line must reach the operating system immediately.", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(OneWriterProvisional, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(ChangelogProvisional, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(ChangelogProvisional, changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("backpressure", changelog, StringComparison.Ordinal);
        Assert.Contains("`QueueFullMode`: `Drop` (default, as before) or `Block`. With `Block`, a full background queue makes the caller wait instead of dropping the event.", changelog, StringComparison.Ordinal);
        Assert.Contains("`FileBufferSize` and `FileFlushInterval` control how `FileLogWriter` buffers lines.", changelog, StringComparison.Ordinal);
        Assert.Contains("`FlushTimeout` sets how long `Flush`, `Dispose`, and `Shutdown` wait (0 to 60 s, default 5 s; previously fixed at 5 s).", changelog, StringComparison.Ordinal);
        Assert.Contains("`FileLogWriter` keeps the file open and writes buffered lines instead of opening and closing it for every event. By default, lines reach the file within 1 second; set `FileFlushInterval` to `0` for the previous per-event behavior.", changelog, StringComparison.Ordinal);
        Assert.Contains("A log file now has a single writer process. A second process that opens the same file for writing gets an error; give each process its own file. Reading, tailing, and rotating the file still work.", changelog, StringComparison.Ordinal);
    }

    /// <summary>A zero flush timeout reports events that are still open and does not arm a wait.</summary>
    /// <remarks>The writer stays blocked so the accepted event is still unwritten. The 30 s wait is a hang guard.</remarks>
    [Fact(Timeout = 120000)]
    public async Task FlushTimeout_Zero_ReportsOpenEventsWithoutWaiting()
    {
        var clock = NewClock();
        var writer = new BlockingWriter();
        var queue = BlockQueue(writer, clock, capacity: 2, timeout: TimeSpan.Zero);
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        try
        {
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "writer never entered");
            await DeadlineClock.WaitForCompletion(queue.FlushAsync(), "flush hung");
            Assert.Equal(0, queue.FlushWaitsStarted);
            var dispose = Task.Run(queue.Dispose);
            await DeadlineClock.WaitForCompletion(dispose, "dispose hung");
            Assert.Equal(0, queue.DisposeWaitsStarted);
            Assert.Contains(lines, line => line.Contains("Shutdown deadline of 0 s reached:", StringComparison.Ordinal));
        }
        finally
        {
            SelfLog.Target = previous;
            writer.Release();
        }
    }

    /// <summary>FlushAsync pushes the file buffer with and without a background queue.</summary>
    /// <remarks>The interval is 30 s and the clock is not advanced, so the timer cannot explain the bytes.</remarks>
    [Fact(Timeout = 120000)]
    public async Task FlushAsync_PushesTheFileBuffer()
    {
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var direct = Path.Combine(directory, "direct.log");
            var directEngine = new LoggerEngine(
                new LoggerConfig()
                    .AddWriter(new FileLogWriter(direct))
                    .SetFileFlushInterval(TimeSpan.FromSeconds(30)),
                clock);
            directEngine.Log("direct-line");
            await directEngine.FlushAsync();
            Assert.Contains("direct-line", ReadShared(direct), StringComparison.Ordinal);
            directEngine.Dispose();

            var queued = Path.Combine(directory, "queued.log");
            var queuedEngine = new LoggerEngine(
                new LoggerConfig()
                    .AddWriter(new FileLogWriter(queued))
                    .UseBackgroundQueue(true, 100, 10, TimeSpan.FromHours(1))
                    .SetFileFlushInterval(TimeSpan.FromSeconds(30)),
                clock);
            queuedEngine.Log("queued-line");
            await queuedEngine.FlushAsync();
            Assert.Contains("queued-line", ReadShared(queued), StringComparison.Ordinal);
            queuedEngine.Dispose();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A writer that throws from Flush does not escape FlushAsync.</summary>
    [Fact(Timeout = 30000)]
    public async Task Flush_SwallowsAWriterFailure()
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            using var engine = new LoggerEngine(new LoggerConfig().AddWriter(new ThrowingFlushWriter()));
            await engine.FlushAsync();
            Assert.Contains("flush-broke", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>Builds a block queue. The idle interval is one hour so the test clock does not have to move for a drain.</summary>
    private static BackgroundLogQueue BlockQueue(ILogWriter writer, TimeProvider clock, int capacity, TimeSpan? timeout = null)
    {
        return new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(capacity, 8, TimeSpan.FromHours(1), false, clock)
            {
                FullMode = QueueFullMode.Block,
                FlushTimeout = timeout ?? BackgroundLogQueue.ShutdownDeadline
            });
    }

    /// <summary>Unblocks a dispose that is still waiting, then waits up to the hang guard.</summary>
    private static async Task ReleaseDispose(FakeTimeProvider clock, Task? dispose, TimeSpan bound)
    {
        if (dispose is null)
            return;

        if (!dispose.IsCompleted)
            clock.Advance(bound);

        _ = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    private static void AssertRejected(Action call)
    {
        var error = Record.Exception(call);
        Assert.IsType<InvalidOperationException>(error);
    }

    private static string MessageOf(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Record.Exception(() => configuration.GetALogOptions());
        return error is null ? "(no exception)" : error.Message;
    }

    private static string AddMessage(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Record.Exception(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddALog(configuration));
        });
        return error is null ? "(no exception)" : error.Message;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static FakeTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static LogEvent Event(string message)
        => new(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-bkp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "ALog.sln");
            if (File.Exists(candidate))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));

            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found.");
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(value, index, StringComparison.Ordinal);
            if (found < 0)
                return count;

            count++;
            index = found + value.Length;
        }

        return count;
    }

    /// <summary>Calls the queue from inside Write and records the bool before any later assert.</summary>
    private sealed class ReentryWriter : ILogWriter
    {
        private readonly Func<bool> _tryEnqueue;
        private int _disposeOnce;

        internal ReentryWriter(Func<bool> tryEnqueue)
        {
            _tryEnqueue = tryEnqueue;
        }

        internal bool NestedReturned { get; private set; }

        internal bool NestedResult { get; private set; }

        public void Write(LogEvent logEvent)
        {
            NestedReturned = true;
            NestedResult = _tryEnqueue();
        }

        public Task WriteAsync(LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeOnce, 1);
        }
    }

    /// <summary>Flush throws. Write and dispose do not.</summary>
    private sealed class ThrowingFlushWriter : ILogWriter
    {
        public void Write(LogEvent logEvent)
        {
        }

        public Task WriteAsync(LogEvent logEvent) => Task.CompletedTask;

        public void Flush() => throw new InvalidOperationException("flush-broke");

        public void Dispose()
        {
        }
    }

    /// <summary>Declares only the members that existed before the queue mode and the file buffer.</summary>
    private sealed class LegacyConfiguration : ILogConfiguration
    {
        public IReadOnlyList<ILogWriter> Writers { get; } = Array.Empty<ILogWriter>();

        public IReadOnlyList<ILogEnricher> Enrichers { get; } = Array.Empty<ILogEnricher>();

        public LogLevel MinimumLevel => LogLevel.Info;

        public ILogFilter? Filter => null;

        public ILogFormatter? Formatter => null;

        public bool AsyncEnabled => false;

        public bool BackgroundQueueEnabled => false;

        public int BackgroundQueueCapacity => 1000;

        public int BackgroundQueueBatchSize => 10;

        public TimeSpan BackgroundQueueFlushInterval => TimeSpan.FromMilliseconds(100);
    }
}
