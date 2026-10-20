using System.Reflection;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using ALog;
using ALog.Formatters;
using ALog.Writers.Cloud;
using Microsoft.Extensions.Time.Testing;
using LogEvent = ALog.Core.LogEvent;

namespace ALog.Tests;

/// <summary>
/// AWS V4 behavior: null SDK responses, one-time group and stream creation,
/// batched puts, template properties, and the injected transport timeout.
/// </summary>
public sealed class AwsV4Tests
{
    private const int PutByteLimit = 1_048_576;
    private const int EventOverhead = 26;
    private const long DayMilliseconds = 24L * 60L * 60L * 1000L;

    /// <summary>
    /// A V4 client may return a null response and null value fields.
    /// The transport must not throw, and the writer must not throw to the caller.
    /// </summary>
    /// <remarks>
    /// Before the transport treats those fields as nullable, the put dereferences
    /// a null response and this test fails with <see cref="NullReferenceException"/>.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task NullSdkResponses_DoNotEscapeTheTransportOrTheWriter()
    {
        var client = CountingCloudWatch.Create();
        client.NullResponses = true;
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client, ownsClient: true);
        var thrown = await Record.ExceptionAsync(() =>
            transport.PutLogEventAsync("group", "stream", "hello", 1_577_936_645_000));
        Assert.Null(thrown);

        client.ResetCounts();
        client.NullResponses = true;
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        var writerThrown = Record.Exception(() => writer.Write(Plain("hello")));
        Assert.Null(writerThrown);
        await writer.WriteAsync(Plain("hello-async"));
    }

    /// <summary>
    /// Three events passed together become one PutLogEvents call, in the same order,
    /// after a single group and stream create and with no describe call.
    /// </summary>
    /// <remarks>Fails while each event still opens its own put.</remarks>
    [Fact(Timeout = 30000)]
    public async Task BatchOfThree_CreatesOnce_AndPutsOnceInOrder()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        IAwsCloudWatchLogsTransport batch = transport;
        var events = new AwsCloudWatchLogEvent[]
        {
            new("one", 1_000),
            new("two", 2_000),
            new("three", 3_000),
        };

        await batch.PutLogEventsAsync("group", "stream", events);

        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(1, client.Puts);
        Assert.Equal(new[] { "one", "two", "three" }, client.Batches[0]);
        Assert.All(client.SequenceTokens, token => Assert.Null(token));
    }

    /// <summary>
    /// 10,001 events need two puts because one request holds at most 10,000.
    /// Order is unchanged and neither request carries a sequence token.
    /// </summary>
    /// <remarks>Fails while the transport sends one put per event.</remarks>
    [Fact(Timeout = 30000)]
    public async Task TenThousandAndOneEvents_SplitIntoTwoPuts()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var events = new AwsCloudWatchLogEvent[10_001];
        for (var index = 0; index < events.Length; index++)
            events[index] = new AwsCloudWatchLogEvent("e" + index.ToString(), 1_000 + index);

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(2, client.Puts);
        Assert.Equal(10_000, client.Batches[0].Count);
        Assert.Single(client.Batches[1]);
        Assert.Equal("e0", client.Batches[0][0]);
        Assert.Equal("e9999", client.Batches[0][9_999]);
        Assert.Equal("e10000", client.Batches[1][0]);
        Assert.All(client.SequenceTokens, token => Assert.Null(token));
    }

    /// <summary>
    /// A batch whose UTF-8 size, plus 26 bytes per event, exceeds 1,048,576 is split.
    /// Two large events fit; the third is a second request.
    /// </summary>
    /// <remarks>Fails while the whole list is sent as one put per event or as one oversized put.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ByteLimit_SplitsIntoTwoPuts()
    {
        var payload = new string('a', (PutByteLimit / 2) - EventOverhead);
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var events = new AwsCloudWatchLogEvent[]
        {
            new(payload, 1_000),
            new(payload, 2_000),
            new(payload, 3_000),
        };

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(2, client.Puts);
        Assert.Equal(2, client.Batches[0].Count);
        Assert.Single(client.Batches[1]);
    }

    /// <summary>
    /// Events whose timestamps span more than 24 hours are not placed in one request.
    /// </summary>
    /// <remarks>The service rejects a wider span. The split keeps the original order.</remarks>
    [Fact(Timeout = 30000)]
    public async Task DaySpan_SplitsTheBatch()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var events = new AwsCloudWatchLogEvent[]
        {
            new("early", 1_000),
            new("late", 1_000 + DayMilliseconds + 1),
        };

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(2, client.Puts);
        Assert.Equal("early", client.Batches[0][0]);
        Assert.Equal("late", client.Batches[1][0]);
    }

    /// <summary>
    /// A timestamp that goes backwards starts a new request. The events are not reordered.
    /// </summary>
    /// <remarks>One request must be chronological. The following event keeps its place in the list.</remarks>
    [Fact(Timeout = 30000)]
    public async Task OutOfOrderTimestamp_StartsANewPut()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var events = new AwsCloudWatchLogEvent[]
        {
            new("second", 2_000),
            new("first", 1_000),
        };

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(2, client.Puts);
        Assert.Equal("second", client.Batches[0][0]);
        Assert.Equal("first", client.Batches[1][0]);
    }

    /// <summary>
    /// Three separate <see cref="AwsCloudWatchWriter.Write"/> calls create the group and the stream once
    /// and send three puts. Describe is not called.
    /// </summary>
    /// <remarks>Fails while every write calls create again.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_ThreeSingleEvents_CreatesOnceAndPutsThree()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        writer.Write(Plain("a"));
        writer.Write(Plain("b"));
        writer.Write(Plain("c"));

        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(3, client.Puts);
    }

    /// <summary>
    /// Three separate <see cref="AwsCloudWatchWriter.WriteAsync"/> calls create once and send three puts.
    /// </summary>
    /// <remarks>Same call budget as <see cref="Write_ThreeSingleEvents_CreatesOnceAndPutsThree"/>.</remarks>
    /// <returns>A task that finishes after the third put.</returns>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_ThreeSingleEvents_CreatesOnceAndPutsThree()
    {
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        await writer.WriteAsync(Plain("a"));
        await writer.WriteAsync(Plain("b"));
        await writer.WriteAsync(Plain("c"));

        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(3, client.Puts);
    }

    /// <summary>
    /// One thousand synchronous writes create the group and the stream once and describe nothing.
    /// Each write is its own put.
    /// </summary>
    /// <remarks>Fails while create runs on every write. The count after the fix is 1/1/0/1000.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_OneThousandSingleEvents_CreatesOnce()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        for (var index = 0; index < 1000; index++)
            writer.Write(Plain("n" + index.ToString()));

        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(1000, client.Puts);
    }

    /// <summary>
    /// One thousand asynchronous writes create once and describe nothing.
    /// </summary>
    /// <remarks>The put count stays 1000 because each call is still one event.</remarks>
    /// <returns>A task that finishes after the last put.</returns>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_OneThousandSingleEvents_CreatesOnce()
    {
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        for (var index = 0; index < 1000; index++)
            await writer.WriteAsync(Plain("n" + index.ToString()));

        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(1000, client.Puts);
    }

    /// <summary>
    /// <see cref="ResourceAlreadyExistsException"/> from create is success.
    /// The event is still put, and describe is not used to recover a token.
    /// </summary>
    /// <remarks>Fails while an existing stream triggers <c>DescribeLogStreams</c>.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_AlreadyExists_DeliversWithoutDescribe()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        client.GroupExists = true;
        client.StreamExists = true;
        using var writer = OwnedWriter(client);
        writer.Write(Plain("kept"));

        Assert.Equal(1, client.Puts);
        Assert.Equal("kept", client.Batches[0][0]);
        Assert.Equal(0, client.Describes);
    }

    /// <summary>
    /// The async path treats an existing group and stream as success and does not describe them.
    /// </summary>
    /// <returns>A task that finishes after the put.</returns>
    /// <remarks>Separate from the synchronous already-exists case.</remarks>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_AlreadyExists_DeliversWithoutDescribe()
    {
        var client = CountingCloudWatch.Create();
        client.GroupExists = true;
        client.StreamExists = true;
        using var writer = OwnedWriter(client);
        await writer.WriteAsync(Plain("kept"));

        Assert.Equal(1, client.Puts);
        Assert.Equal("kept", client.Batches[0][0]);
        Assert.Equal(0, client.Describes);
    }

    /// <summary>
    /// A network failure while creating the group is reported and not thrown.
    /// The next write creates the group and the stream, then delivers the event.
    /// </summary>
    /// <remarks>
    /// Fails while the failure is not sent through <see cref="SelfLog"/>.
    /// Also fails when the stream is marked ready before the create succeeds, because the next write then skips <c>CreateLogStream</c>.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_NetworkFailure_IsReported_AndNextWriteDelivers()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        client.GroupFailures = 1;
        using var writer = OwnedWriter(client);
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line => lines.Add(line));
        try
        {
            var error = CaptureError(() => writer.Write(Plain("lost")));
            Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
            Assert.Contains("AwsCloudWatchWriter", string.Join("\n", lines), StringComparison.Ordinal);
            Assert.Equal(0, client.Puts);
            Assert.Equal(1, client.CreateGroups);
            Assert.Equal(0, client.CreateStreams);

            writer.Write(Plain("kept"));
            Assert.Equal(2, client.CreateGroups);
            Assert.Equal(1, client.CreateStreams);
            Assert.Equal(1, client.Puts);
            Assert.Equal("kept", client.Batches[0][0]);
            Assert.Equal(0, client.Describes);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// The async path reports a create failure and delivers on the following call.
    /// The second call is the one that creates the stream.
    /// </summary>
    /// <returns>A task that finishes after the second write.</returns>
    /// <remarks>Separate from the synchronous network-failure case. A ready flag set before create leaves <c>CreateLogStream</c> at zero.</remarks>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_NetworkFailure_IsReported_AndNextWriteDelivers()
    {
        var client = CountingCloudWatch.Create();
        client.GroupFailures = 1;
        using var writer = OwnedWriter(client);
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line => lines.Add(line));
        try
        {
            var error = await CaptureErrorAsync(() => writer.WriteAsync(Plain("lost")));
            Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
            Assert.Contains("AwsCloudWatchWriter", string.Join("\n", lines), StringComparison.Ordinal);
            Assert.Equal(0, client.Puts);
            Assert.Equal(1, client.CreateGroups);
            Assert.Equal(0, client.CreateStreams);

            await writer.WriteAsync(Plain("kept"));
            Assert.Equal(2, client.CreateGroups);
            Assert.Equal(1, client.CreateStreams);
            Assert.Equal(1, client.Puts);
            Assert.Equal("kept", client.Batches[0][0]);
            Assert.Equal(0, client.Describes);
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Two writers that share one transport and use different streams both deliver.
    /// Each stream is created once. The shared group is created once.
    /// </summary>
    /// <remarks>Fails while one ready flag on the transport skips the second stream.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_TwoWritersSharingTransport_CreateEachStreamOnce()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        using var alpha = new AwsCloudWatchWriter("group", "alpha", transport);
        using var beta = new AwsCloudWatchWriter("group", "beta", transport);
        WriteMany(alpha, "a", 100);
        WriteMany(beta, "b", 100);

        Assert.Equal(200, client.Puts);
        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(2, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(100, CountMessages(client, "a"));
        Assert.Equal(100, CountMessages(client, "b"));
    }

    /// <summary>
    /// The async path delivers both streams when two writers share one transport.
    /// </summary>
    /// <returns>A task that finishes after both writers have sent 100 events.</returns>
    /// <remarks>Separate from the synchronous shared-transport case.</remarks>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_TwoWritersSharingTransport_CreateEachStreamOnce()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        using var alpha = new AwsCloudWatchWriter("group", "alpha", transport);
        using var beta = new AwsCloudWatchWriter("group", "beta", transport);
        await WriteManyAsync(alpha, "a", 100);
        await WriteManyAsync(beta, "b", 100);

        Assert.Equal(200, client.Puts);
        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(2, client.CreateStreams);
        Assert.Equal(0, client.Describes);
        Assert.Equal(100, CountMessages(client, "a"));
        Assert.Equal(100, CountMessages(client, "b"));
    }

    /// <summary>
    /// Eight concurrent writes create the stream once.
    /// The fake holds every create until seven writers are blocked on the gate, or until eight creates have entered.
    /// </summary>
    /// <returns>A task that finishes after all eight writes return.</returns>
    /// <remarks>
    /// Removing only the gate lets all eight creates enter, so the create count is not one.
    /// The fact timeout is the only wall-clock bound. The fake waits on a task, not on a blocked pool thread.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task EightThreads_CreateTheStreamOnce()
    {
        var client = CountingCloudWatch.Create();
        client.HoldCreatesUntilEntered = 8;
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var sevenBlocked = transport.SignalWhenReadyGateWaitersReach(7);
        var eightCreates = client.WhenCreatesEntered(8);
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        var writes = new Task[8];
        for (var index = 0; index < writes.Length; index++)
            writes[index] = writer.WriteAsync(Plain("t" + index.ToString()));

        await Task.WhenAny(sevenBlocked, eightCreates);
        if (!eightCreates.IsCompleted)
        {
            Assert.True(sevenBlocked.IsCompleted);
            Assert.Equal(7, transport.ReadyGateWaiters);
            var alreadyWaiting = transport.SignalWhenReadyGateWaitersReach(7);
            Assert.True(alreadyWaiting.IsCompleted);
            client.ReleaseCreates();
        }

        await Task.WhenAll(writes);
        var alreadyEntered = client.WhenCreatesEntered(1);
        Assert.True(alreadyEntered.IsCompleted);
        Assert.Equal(1, client.CreateStreams);
        Assert.Equal(1, client.CreateGroups);
        Assert.Equal(8, client.Puts);
        Assert.Equal(0, client.Describes);
    }

    /// <summary>
    /// Two events whose UTF-8 size plus 26 bytes each is exactly 1,048,576 go in one put.
    /// </summary>
    /// <remarks>An overhead of 27 bytes makes this pair look too large and splits it.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ByteLimit_Exact1048576Bytes_SendsOnePut()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var message = new string('a', (PutByteLimit / 2) - EventOverhead);
        AwsCloudWatchLogEvent[] events = [new(message, 1_000), new(message, 2_000)];

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(1, client.Puts);
        Assert.Equal(2, client.Batches[0].Count);
    }

    /// <summary>
    /// One byte past 1,048,576, counting 26 bytes of overhead per event, needs a second put.
    /// </summary>
    /// <remarks>An overhead of 25 bytes still fits this pair in one put.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ByteLimit_OneByteOver1048576_SendsTwoPuts()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        var message = new string('a', (PutByteLimit / 2) - EventOverhead);
        AwsCloudWatchLogEvent[] events = [new(message, 1_000), new(message + "b", 2_000)];

        await ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events);

        Assert.Equal(2, client.Puts);
        Assert.Single(client.Batches[0]);
        Assert.Single(client.Batches[1]);
    }

    /// <summary>
    /// A stream deleted after the first put is created again and the same event is sent once more.
    /// </summary>
    /// <remarks>The following event does not create again. Fails while a missing stream is not retried.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_StreamDeletedOnce_RecreatesAndResendsTheBatch()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        writer.Write(Plain("first"));
        client.StreamMissing = true;
        writer.Write(Plain("second"));
        writer.Write(Plain("third"));

        Assert.Equal(2, client.CreateGroups);
        Assert.Equal(2, client.CreateStreams);
        Assert.Equal(4, client.Puts);
        Assert.Equal(2, CountMessages(client, "second"));
        Assert.Equal(1, CountMessages(client, "third"));
        Assert.Equal(0, client.Describes);
    }

    /// <summary>
    /// The async path recreates a deleted stream and resends that event once.
    /// </summary>
    /// <returns>A task that finishes after the recovered write.</returns>
    /// <remarks>Separate from the synchronous deleted-stream case.</remarks>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_StreamDeletedOnce_RecreatesAndResendsTheBatch()
    {
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        await writer.WriteAsync(Plain("first"));
        client.StreamMissing = true;
        await writer.WriteAsync(Plain("second"));

        Assert.Equal(2, client.CreateStreams);
        Assert.Equal(3, client.Puts);
        Assert.Equal(2, CountMessages(client, "second"));
    }

    /// <summary>
    /// Two later batches each find the stream missing. Each one creates it once and resends once.
    /// </summary>
    /// <remarks>A loop inside one batch would create more than once per event.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_StreamDeletedTwice_RecreatesOnceEachTime()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        writer.Write(Plain("first"));
        client.StreamMissing = true;
        writer.Write(Plain("second"));
        client.StreamMissing = true;
        writer.Write(Plain("third"));

        Assert.Equal(3, client.CreateGroups);
        Assert.Equal(3, client.CreateStreams);
        Assert.Equal(5, client.Puts);
        Assert.Equal(2, CountMessages(client, "second"));
        Assert.Equal(2, CountMessages(client, "third"));
    }

    /// <summary>
    /// When the re-create fails, the writer reports it, does not throw, and does not try another put.
    /// The next event creates the stream and delivers.
    /// </summary>
    /// <remarks>Fails while a failed re-create is retried inside the same batch.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_RecreateFailure_IsReportedWithoutLooping()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        writer.Write(Plain("first"));
        client.StreamMissing = true;
        client.StreamFailures = 1;
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line => lines.Add(line));
        try
        {
            string error = string.Empty;
            var thrown = Record.Exception(() =>
            {
                error = CaptureError(() => writer.Write(Plain("lost")));
            });
            Assert.Null(thrown);
            Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
            Assert.Contains("AwsCloudWatchWriter", string.Join("\n", lines), StringComparison.Ordinal);
            Assert.Equal(2, client.CreateGroups);
            Assert.Equal(2, client.CreateStreams);
            Assert.Equal(2, client.Puts);

            writer.Write(Plain("kept"));
            Assert.Equal(2, client.CreateGroups);
            Assert.Equal(3, client.CreateStreams);
            Assert.Equal(3, client.Puts);
            Assert.Equal(1, CountMessages(client, "kept"));
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Three batches that keep finding the stream missing each call CreateLogStream once.
    /// The first of those batches re-creates a stream that was already recorded. The next two only create.
    /// </summary>
    /// <remarks>
    /// Fails while a batch both creates and re-creates. The writer reports the failure and does not throw.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Write_PermanentNotFound_RecreatesOncePerBatch()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        using var writer = OwnedWriter(client);
        writer.Write(Plain("first"));
        client.AlwaysNotFound = true;
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line => lines.Add(line));
        try
        {
            string[] names = ["a", "b", "c"];
            var errors = new string[names.Length];
            var thrown = Record.Exception(() =>
            {
                for (var index = 0; index < names.Length; index++)
                {
                    var before = client.CreateStreams;
                    errors[index] = CaptureError(() => writer.Write(Plain(names[index])));
                    Assert.Equal(before + 1, client.CreateStreams);
                }
            });
            Assert.Null(thrown);
            Assert.Contains("ResourceNotFoundException", errors[0], StringComparison.Ordinal);
            Assert.Contains("ResourceNotFoundException", errors[1], StringComparison.Ordinal);
            Assert.Contains("ResourceNotFoundException", errors[2], StringComparison.Ordinal);
            Assert.Contains("AwsCloudWatchWriter", string.Join("\n", lines), StringComparison.Ordinal);
            Assert.Equal(4, client.CreateGroups);
            Assert.Equal(4, client.CreateStreams);
            Assert.Equal(5, client.Puts);
            Assert.Equal(2, CountMessages(client, "a"));
            Assert.Equal(1, CountMessages(client, "b"));
            Assert.Equal(1, CountMessages(client, "c"));
        }
        finally
        {
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// A stream that was already created gets one re-create when its first slice is missing.
    /// The later slice is also missing and is not created again.
    /// The next batch creates the stream, because the failed batch left it unready.
    /// </summary>
    /// <returns>A task that finishes after the follow-up event is accepted.</returns>
    /// <remarks>Fails while a missing slice recreates the stream more than once in the same batch.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Put_LaterSliceMissing_RecreatesOnceAndTheNextBatchCreates()
    {
        var client = CountingCloudWatch.Create();
        client.NotFoundAtPuts = [2, 4];
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        await transport.PutLogEventAsync("group", "stream", "warm", 1_000);
        AwsCloudWatchLogEvent[] events =
        [
            new("early", 1_000),
            new("late", 1_000 + DayMilliseconds + 1),
        ];

        var thrown = await Record.ExceptionAsync(() =>
            ((IAwsCloudWatchLogsTransport)transport).PutLogEventsAsync("group", "stream", events));

        Assert.IsType<ResourceNotFoundException>(thrown);
        Assert.Equal(2, client.CreateGroups);
        Assert.Equal(2, client.CreateStreams);
        Assert.Equal(4, client.Puts);

        await transport.PutLogEventAsync("group", "stream", "after", 2_000);
        Assert.Equal(3, client.CreateGroups);
        Assert.Equal(3, client.CreateStreams);
        Assert.Equal(5, client.Puts);
        Assert.Equal(1, CountMessages(client, "after"));
        Assert.Equal(0, client.Describes);
    }

    /// <summary>
    /// Past 1024 ready pairs, and past 1024 ready groups, the transport clears the sets.
    /// A later write still delivers, and both sets stay at or under the cap.
    /// </summary>
    /// <returns>A task that finishes after the pair past the cap and the group past the cap have been checked.</returns>
    /// <remarks>Create is idempotent, so clearing a live pair is safe. The bound check throws one past the cap.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ReadyPairs_Past1024_ClearAndStillDeliver()
    {
        var cap = AwsSdkCloudWatchLogsTransport.MaxReadyPairs;
        var streams = CountingCloudWatch.Create();
        using var streamTransport = new AwsSdkCloudWatchLogsTransport(streams.Client);
        for (var index = 0; index < cap; index++)
            await streamTransport.PutLogEventAsync("group", "s" + index.ToString(), "m", 1_000);

        Assert.Equal(cap, streamTransport.ReadyStreamCount);
        Assert.Equal(1, streamTransport.ReadyGroupCount);
        await streamTransport.PutLogEventAsync("group", "s" + cap.ToString(), "overflow", 1_000);
        Assert.Equal(1, streamTransport.ReadyStreamCount);
        Assert.Equal(1, streamTransport.ReadyGroupCount);
        await streamTransport.PutLogEventAsync("group", "s0", "again", 1_000);
        Assert.Equal(2, streamTransport.ReadyStreamCount);
        Assert.Equal(1, streamTransport.ReadyGroupCount);
        Assert.True(streamTransport.ReadyStreamCount <= cap);
        Assert.True(streamTransport.ReadyGroupCount <= cap);
        Assert.Equal(cap + 2, streams.CreateStreams);
        Assert.Equal(2, streams.CreateGroups);
        Assert.Equal(cap + 2, streams.Puts);
        Assert.Equal(1, CountMessages(streams, "again"));
        Assert.Equal(1, CountMessages(streams, "overflow"));

        var groups = CountingCloudWatch.Create();
        groups.StreamFailures = cap;
        using var groupTransport = new AwsSdkCloudWatchLogsTransport(groups.Client);
        for (var index = 0; index < cap; index++)
        {
            var failed = await Record.ExceptionAsync(() =>
                groupTransport.PutLogEventAsync("g" + index.ToString(), "s", "m", 1_000));
            Assert.IsType<AmazonServiceException>(failed);
        }

        Assert.Equal(cap, groupTransport.ReadyGroupCount);
        Assert.Equal(0, groupTransport.ReadyStreamCount);
        await groupTransport.PutLogEventAsync("g" + cap.ToString(), "s", "delivered", 1_000);
        Assert.Equal(1, groupTransport.ReadyGroupCount);
        Assert.Equal(1, groupTransport.ReadyStreamCount);
        Assert.True(groupTransport.ReadyGroupCount <= cap);
        Assert.True(groupTransport.ReadyStreamCount <= cap);
        Assert.Equal(1, CountMessages(groups, "delivered"));

        Assert.Throws<System.InvalidOperationException>(() =>
            AwsSdkCloudWatchLogsTransport.CheckReadyBound(cap + 1, 0));
        Assert.Throws<System.InvalidOperationException>(() =>
            AwsSdkCloudWatchLogsTransport.CheckReadyBound(0, cap + 1));
        AwsSdkCloudWatchLogsTransport.CheckReadyBound(cap, cap);
    }

    /// <summary>
    /// Without a formatter the put carries the rendered message, the template property, and the context.
    /// </summary>
    /// <remarks>
    /// Fails while the writer sends only <see cref="LogEvent.Message"/>.
    /// Property <c>A</c> and context <c>B</c> must both appear.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task PlainMessage_IncludesPropertyA_AndContextB()
    {
        var transport = new RecordingAwsTransport();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        var evt = new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "hello alpha",
            null,
            new Dictionary<string, object> { ["B"] = "beta" },
            new Dictionary<string, object> { ["A"] = "alpha" },
            "hello {A}");

        writer.Write(evt);
        await writer.WriteAsync(evt);

        Assert.Equal(2, transport.Calls.Count);
        Assert.All(transport.Calls, call =>
        {
            Assert.Contains("hello alpha", call.Message, StringComparison.Ordinal);
            Assert.Contains("A=alpha", call.Message, StringComparison.Ordinal);
            Assert.Contains("B=beta", call.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A <see cref="JsonFormatter"/> owns the message. Properties are not appended a second time.
    /// </summary>
    /// <remarks>The formatted text is compared to <see cref="ILogFormatter.Format"/> exactly.</remarks>
    [Fact(Timeout = 30000)]
    public async Task JsonFormatter_MessageIsExactlyTheFormatterOutput()
    {
        await Task.CompletedTask;
        var transport = new RecordingAwsTransport();
        var formatter = new JsonFormatter();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport, formatter);
        var evt = new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "hello alpha",
            null,
            new Dictionary<string, object> { ["B"] = "beta" },
            new Dictionary<string, object> { ["A"] = "alpha" },
            "hello {A}");

        writer.Write(evt);

        Assert.Equal(formatter.Format(evt), transport.Calls[0].Message);
    }

    /// <summary>
    /// Disposing the owned writer twice does not throw. A write after dispose is reported and does not call AWS.
    /// </summary>
    /// <remarks>The second dispose must not dispose the SDK client again.</remarks>
    [Fact(Timeout = 30000)]
    public async Task Dispose_Twice_AndWriteAfterDispose_DoesNotThrow()
    {
        await Task.CompletedTask;
        var client = CountingCloudWatch.Create();
        var writer = OwnedWriter(client);
        writer.Dispose();
        var second = Record.Exception(() => writer.Dispose());
        Assert.Null(second);

        var error = CaptureError(() => writer.Write(Plain("late")));
        var asyncError = await CaptureErrorAsync(() => writer.WriteAsync(Plain("late-async")));
        Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
        Assert.Contains("[AwsCloudWatchWriter]", asyncError, StringComparison.Ordinal);
        Assert.Equal(0, client.Puts);
        Assert.Equal(0, client.CreateGroups);
    }

    /// <summary>
    /// An empty batch and a null list do not call AWS.
    /// </summary>
    /// <returns>A task that finishes after both calls.</returns>
    /// <remarks>Covers the guard in front of create and put.</remarks>
    [Fact(Timeout = 30000)]
    public async Task EmptyBatch_DoesNotCallAws()
    {
        var client = CountingCloudWatch.Create();
        using var transport = new AwsSdkCloudWatchLogsTransport(client.Client);
        IAwsCloudWatchLogsTransport batch = transport;
        await batch.PutLogEventsAsync("group", "stream", []);
        await batch.PutLogEventsAsync("group", "stream", null);

        Assert.Equal(0, client.CreateGroups);
        Assert.Equal(0, client.Puts);
    }

    /// <summary>
    /// Sequence-token failures stay inside the writer. The caller sees no exception.
    /// </summary>
    /// <returns>A task that finishes after both exception types are checked.</returns>
    /// <remarks>
    /// <see cref="InvalidSequenceTokenException"/> and <see cref="DataAlreadyAcceptedException"/>
    /// leave the transport. Write and WriteAsync report them on the error stream.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SequenceTokenExceptions_AreReportedAndDoNotReachTheCaller()
    {
        await AssertFaultIsReported(() => new InvalidSequenceTokenException("bad"));
        await AssertFaultIsReported(() => new DataAlreadyAcceptedException("accepted"));
    }

    /// <summary>
    /// The synchronous put stays open until the injected clock reaches the 100 second timeout.
    /// One tick earlier it is still running. At the timeout it is cancelled, reported, and not thrown.
    /// </summary>
    /// <remarks>
    /// <see cref="FactAttribute.Timeout"/> is the only wall-clock hang net.
    /// The product deadline moves only when the fake clock advances.
    /// Fails while the wait uses the wall clock, because advancing the fake clock does not cancel it.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task SyncPut_TimesOutOnTheInjectedClock()
    {
        var clock = new SignalingFakeTimeProvider();
        var transport = new GateAwsTransport();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport, clock);
        Exception? thrown = null;
        string error = string.Empty;
        var write = Task.Run(() =>
        {
            try
            {
                error = CaptureError(() => writer.Write(Plain("slow")));
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        await Task.WhenAll(transport.Entered, clock.TimersArmed);
        clock.Advance(TimeSpan.FromSeconds(100) - TimeSpan.FromTicks(1));
        Assert.False(write.IsCompleted);
        Assert.False(transport.Cancelled);

        clock.Advance(TimeSpan.FromTicks(1));
        await write;
        Assert.Null(thrown);
        Assert.True(transport.Cancelled);
        Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
        Assert.Contains("100 second", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// One synchronous write and one async write must report the fault and return.
    /// </summary>
    /// <param name="fault">Builds a fresh exception for each put. Not null.</param>
    /// <returns>A task that finishes after both writes have been checked.</returns>
    /// <remarks>The transport throws. The writer must not.</remarks>
    private static async Task AssertFaultIsReported(Func<Exception> fault)
    {
        var name = fault().GetType().Name;
        var transport = new FaultingAwsTransport(fault);
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        string syncError = string.Empty;
        var syncThrown = Record.Exception(() =>
        {
            syncError = CaptureError(() => writer.Write(Plain("seq")));
        });
        Assert.Null(syncThrown);
        Assert.Contains("[AwsCloudWatchWriter]", syncError, StringComparison.Ordinal);
        Assert.Contains(name, syncError, StringComparison.Ordinal);

        var asyncError = string.Empty;
        var asyncThrown = await Record.ExceptionAsync(async () =>
        {
            asyncError = await CaptureErrorAsync(() => writer.WriteAsync(Plain("seq-async")));
        });
        Assert.Null(asyncThrown);
        Assert.Contains("[AwsCloudWatchWriter]", asyncError, StringComparison.Ordinal);
        Assert.Contains(name, asyncError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a writer that owns a transport around the counting client.
    /// </summary>
    /// <param name="client">The fake CloudWatch client. Not null.</param>
    /// <returns>A writer the caller disposes. The writer does not own the transport.</returns>
    /// <remarks>The proxy has no network resources. The transport does not own the client.</remarks>
    private static AwsCloudWatchWriter OwnedWriter(CountingCloudWatch client)
    {
        var transport = new AwsSdkCloudWatchLogsTransport(client.Client, ownsClient: false);
        return new AwsCloudWatchWriter("group", "stream", transport);
    }

    /// <summary>
    /// Writes <paramref name="count"/> events whose messages start with <paramref name="prefix"/>.
    /// </summary>
    /// <param name="writer">Writer under test. Not null.</param>
    /// <param name="prefix">Message prefix. Not null.</param>
    /// <param name="count">How many events. Zero writes nothing.</param>
    /// <remarks>The loop is bounded by <paramref name="count"/>.</remarks>
    private static void WriteMany(AwsCloudWatchWriter writer, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
            writer.Write(Plain(prefix + index.ToString()));
    }

    /// <summary>
    /// Writes <paramref name="count"/> events asynchronously.
    /// </summary>
    /// <param name="writer">Writer under test. Not null.</param>
    /// <param name="prefix">Message prefix. Not null.</param>
    /// <param name="count">How many events. Zero writes nothing.</param>
    /// <returns>A task that finishes after the last write.</returns>
    /// <remarks>The loop is bounded by <paramref name="count"/>.</remarks>
    private static async Task WriteManyAsync(AwsCloudWatchWriter writer, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
            await writer.WriteAsync(Plain(prefix + index.ToString()));
    }

    /// <summary>
    /// Counts recorded put messages that start with <paramref name="prefix"/>.
    /// A failed put is still recorded, so a resend of the same text counts once per attempt that stored it.
    /// </summary>
    /// <param name="client">Fake client. Not null.</param>
    /// <param name="prefix">Message prefix. Not null.</param>
    /// <returns>How many stored messages start with <paramref name="prefix"/>.</returns>
    /// <remarks>The loops are bounded by the recorded batches.</remarks>
    private static int CountMessages(CountingCloudWatch client, string prefix)
    {
        var found = 0;
        var batches = client.Batches.Count;
        for (var batch = 0; batch < batches; batch++)
        {
            var messages = client.Batches[batch];
            var count = messages.Count;
            for (var index = 0; index < count; index++)
            {
                if (messages[index].StartsWith(prefix, StringComparison.Ordinal))
                    found++;
            }
        }

        return found;
    }

    /// <summary>
    /// Builds an info event whose message is the whole text. No property bag and no context.
    /// </summary>
    /// <param name="message">Rendered message. Not null.</param>
    /// <returns>An event at the fixed coverage clock.</returns>
    /// <remarks>Does not read the wall clock.</remarks>
    private static LogEvent Plain(string message)
        => new(CoverageClock.UtcNow, LogLevel.Info, message, null, null);

    /// <summary>
    /// Redirects the error stream for one action and restores it afterwards.
    /// </summary>
    /// <param name="body">Work whose error text is returned. Not null.</param>
    /// <returns>Everything written to the error stream during <paramref name="body"/>.</returns>
    /// <remarks>Restores the previous writer even when <paramref name="body"/> throws.</remarks>
    private static string CaptureError(Action body)
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            body();
            return buffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>
    /// Redirects the error stream for one asynchronous action and restores it afterwards.
    /// </summary>
    /// <param name="body">Work whose error text is returned. Not null.</param>
    /// <returns>Everything written to the error stream during <paramref name="body"/>.</returns>
    /// <remarks>The previous writer is restored even when <paramref name="body"/> faults.</remarks>
    private static async Task<string> CaptureErrorAsync(Func<Task> body)
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            await body().ConfigureAwait(false);
            return buffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }
}

/// <summary>
/// Fake CloudWatch client. Records creates, describes, and puts. Makes no network calls.
/// </summary>
internal class CountingCloudWatch : DispatchProxy
{
    /// <summary>Creates the proxy and returns the stub that records the calls.</summary>
    /// <returns>The stub. <see cref="Client"/> is the same instance.</returns>
    /// <remarks>Does not open a connection.</remarks>
    internal static CountingCloudWatch Create()
    {
        var client = DispatchProxy.Create<IAmazonCloudWatchLogs, CountingCloudWatch>();
        return (CountingCloudWatch)(object)client;
    }

    /// <summary>The proxy as the SDK interface.</summary>
    internal IAmazonCloudWatchLogs Client => (IAmazonCloudWatchLogs)(object)this;

    /// <summary>When true, create group throws <see cref="ResourceAlreadyExistsException"/>.</summary>
    internal bool GroupExists { get; set; }

    /// <summary>When true, create stream throws <see cref="ResourceAlreadyExistsException"/>.</summary>
    internal bool StreamExists { get; set; }

    /// <summary>How many create-group calls still throw a network error. Decremented on each throw.</summary>
    internal int GroupFailures { get; set; }

    /// <summary>How many create-stream calls still throw a network error. Decremented on each throw.</summary>
    internal int StreamFailures { get; set; }

    /// <summary>When true, the next put throws <see cref="ResourceNotFoundException"/> until a create-stream succeeds.</summary>
    internal bool StreamMissing { get; set; }

    /// <summary>When true, every put throws <see cref="ResourceNotFoundException"/>.</summary>
    internal bool AlwaysNotFound { get; set; }

    /// <summary>
    /// 1-based numbers of live puts that throw <see cref="ResourceNotFoundException"/>.
    /// Empty schedules nothing. The array is not changed by the fake.
    /// </summary>
    internal int[] NotFoundAtPuts { get; set; } = [];

    /// <summary>
    /// When positive, each create-stream call waits until this many create-stream calls have entered,
    /// or until <see cref="ReleaseCreates"/> runs. Zero disables the wait.
    /// </summary>
    /// <remarks>
    /// Every entered call waits, so a call cannot return and let the transport mark the stream ready
    /// before the others have entered. The fact timeout is the hang net. The wait is a task.
    /// </remarks>
    internal int HoldCreatesUntilEntered { get; set; }

    /// <summary>When true, every operation returns a null response object.</summary>
    internal bool NullResponses { get; set; }

    /// <summary>How many create-group calls have been seen.</summary>
    internal int CreateGroups { get; private set; }

    /// <summary>How many create-stream calls have been seen.</summary>
    internal int CreateStreams { get; private set; }

    /// <summary>How many describe-stream calls have been seen.</summary>
    internal int Describes { get; private set; }

    /// <summary>How many put calls have been seen.</summary>
    internal int Puts { get; private set; }

    private readonly object _sync = new();
    private readonly HashSet<string> _liveStreams = new(StringComparer.Ordinal);
    private int _putOrdinal;
    private int _createsEntered;
    private int _createSignalAt = int.MaxValue;
    private TaskCompletionSource<bool> _createSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _createGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Messages of each put, in call order. One list per request.</summary>
    internal List<List<string>> Batches { get; } = [];

    /// <summary>Sequence token of each put. Null means the request did not set one.</summary>
    internal List<string?> SequenceTokens { get; } = [];

    /// <summary>Clears the counters and the recorded batches. Leaves the failure switches as they are.</summary>
    /// <remarks>Used when one client serves a transport call and then a writer call.</remarks>
    internal void ResetCounts()
    {
        CreateGroups = 0;
        CreateStreams = 0;
        Describes = 0;
        Puts = 0;
        Batches.Clear();
        SequenceTokens.Clear();
    }

    /// <summary>
    /// Lets the held first create-stream call continue.
    /// </summary>
    /// <remarks>Does nothing when that call already continued. Does not block.</remarks>
    internal void ReleaseCreates() => _createGate.TrySetResult(true);

    /// <summary>
    /// Completes when <paramref name="count"/> create-stream calls have entered the hold.
    /// </summary>
    /// <param name="count">How many entries to wait for. A count already reached completes immediately.</param>
    /// <returns>A task that completes once that many creates have entered.</returns>
    /// <remarks>Does not block a thread. A later call replaces the signal.</remarks>
    internal Task WhenCreatesEntered(int count)
    {
        lock (_sync)
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _createSignal = signal;
            _createSignalAt = count;
            if (_createsEntered >= count)
                signal.TrySetResult(true);

            return signal.Task;
        }
    }

    /// <summary>
    /// Handles the CloudWatch operations the transport calls.
    /// Any other method throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="targetMethod">Interface method being invoked.</param>
    /// <param name="args">Arguments from the transport. Null is treated as empty.</param>
    /// <returns>A response task, or null for dispose. Create-stream may stay incomplete until the hold opens.</returns>
    /// <remarks>Does not block a thread and does not read the clock. The create hold awaits a task.</remarks>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? string.Empty;
        if (name == "Dispose")
            return null;

        args ??= [];
        if (name == "CreateLogGroupAsync")
            return Task.FromResult(CreateGroup());

        if (name == "CreateLogStreamAsync")
            return CreateStreamHeld(args);

        if (name == "DescribeLogStreamsAsync")
        {
            Describes++;
            return Task.FromResult(NullResponses ? null : new DescribeLogStreamsResponse());
        }

        if (name == "PutLogEventsAsync")
            return Task.FromResult(Put(args));

        throw new NotSupportedException(name);
    }

    /// <summary>Records one create-group call and returns the configured response or failure.</summary>
    /// <returns>A response, or null when null responses are requested.</returns>
    /// <exception cref="ResourceAlreadyExistsException">The group already exists.</exception>
    /// <exception cref="AmazonServiceException">A configured network failure is still pending.</exception>
    private CreateLogGroupResponse? CreateGroup()
    {
        lock (_sync)
        {
            CreateGroups++;
            if (GroupFailures > 0)
            {
                GroupFailures--;
                throw new AmazonServiceException("network");
            }

            if (GroupExists)
                throw new ResourceAlreadyExistsException("group");

            return NullResponses ? null : new CreateLogGroupResponse();
        }
    }

    /// <summary>
    /// Records one create-stream call. Each call waits when a hold is armed.
    /// </summary>
    /// <param name="args">The create request is argument 0.</param>
    /// <returns>A task that completes with the response, or that faults with the configured failure.</returns>
    /// <remarks>
    /// The call that reaches <see cref="HoldCreatesUntilEntered"/> opens the wait.
    /// A transport with no gate therefore lets every writer enter before any create returns.
    /// A hold of zero runs the create immediately.
    /// </remarks>
    private Task<CreateLogStreamResponse?> CreateStreamHeld(object?[] args)
    {
        if (HoldCreatesUntilEntered <= 0)
            return Task.FromResult(CreateStream(args));

        return CreateStreamAfterHoldAsync(args);
    }

    /// <summary>
    /// Waits for the create hold, then records the create.
    /// </summary>
    /// <param name="args">The create request is argument 0.</param>
    /// <returns>The create response after the hold opens.</returns>
    /// <remarks>The wait does not block a pool thread.</remarks>
    private async Task<CreateLogStreamResponse?> CreateStreamAfterHoldAsync(object?[] args)
    {
        await WaitForPeerCreatesAsync().ConfigureAwait(false);
        return CreateStream(args);
    }

    /// <summary>
    /// Counts one create entry and waits until the hold opens.
    /// </summary>
    /// <returns>A task that completes when this create may be recorded.</returns>
    /// <remarks>
    /// Does not block a thread. The entry at the hold limit opens the wait.
    /// Every entry waits, including that one, so none return early.
    /// </remarks>
    private async Task WaitForPeerCreatesAsync()
    {
        TaskCompletionSource<bool> signal;
        int entered;
        int target;
        var open = false;
        lock (_sync)
        {
            entered = ++_createsEntered;
            signal = _createSignal;
            target = _createSignalAt;
            if (entered >= HoldCreatesUntilEntered)
                open = true;
        }

        if (entered >= target)
            signal.TrySetResult(true);

        if (open)
            _createGate.TrySetResult(true);

        await _createGate.Task.ConfigureAwait(false);
    }

    /// <summary>Records one create-stream call and returns the configured response or failure.</summary>
    /// <param name="args">The create request is argument 0.</param>
    /// <returns>A response, or null when null responses are requested.</returns>
    /// <exception cref="ResourceAlreadyExistsException">The stream already exists.</exception>
    /// <exception cref="AmazonServiceException">A configured network failure is still pending.</exception>
    private CreateLogStreamResponse? CreateStream(object?[] args)
    {
        var request = (CreateLogStreamRequest)args[0]!;
        var stream = request.LogStreamName ?? string.Empty;
        lock (_sync)
        {
            CreateStreams++;
            if (StreamFailures > 0)
            {
                StreamFailures--;
                throw new AmazonServiceException("network");
            }

            StreamMissing = false;
            _liveStreams.Add(stream);
            if (StreamExists)
                throw new ResourceAlreadyExistsException("stream");

            return NullResponses ? null : new CreateLogStreamResponse();
        }
    }

    /// <summary>Records one put, including a null sequence token and a null rejected-info payload.</summary>
    /// <param name="args">The put request is argument 0.</param>
    /// <returns>A response with null token and null indexes, or a null response when requested.</returns>
    /// <remarks>
    /// A null log-event list is recorded as an empty batch.
    /// A stream this fake never created is not <see cref="ResourceNotFoundException"/>.
    /// The missing-stream resend must not hide a create that was skipped.
    /// </remarks>
    private PutLogEventsResponse? Put(object?[] args)
    {
        var request = (PutLogEventsRequest)args[0]!;
        var stream = request.LogStreamName ?? string.Empty;
        var missing = false;
        var neverCreated = false;
        lock (_sync)
        {
            neverCreated = !_liveStreams.Contains(stream);
            if (!neverCreated)
            {
                _putOrdinal++;
                missing = AlwaysNotFound || StreamMissing || ScheduledNotFound(_putOrdinal);
                Puts++;
                SequenceTokens.Add(request.SequenceToken);
                Batches.Add(CopyMessages(request.LogEvents));
            }
        }

        if (neverCreated)
            throw new AmazonServiceException("stream was never created");

        if (missing)
            throw new ResourceNotFoundException("missing");

        if (NullResponses)
            return null;

        if (Puts == 1)
            return new PutLogEventsResponse { NextSequenceToken = null };

        return new PutLogEventsResponse
        {
            NextSequenceToken = null,
            RejectedLogEventsInfo = new RejectedLogEventsInfo
            {
                ExpiredLogEventEndIndex = null,
                TooNewLogEventStartIndex = null,
                TooOldLogEventEndIndex = null,
            },
        };
    }

    /// <summary>True when this 1-based put number was listed in <see cref="NotFoundAtPuts"/>.</summary>
    /// <param name="ordinal">Put number for this call. The first counted put is 1.</param>
    /// <returns>True when the test scheduled a missing-stream failure for <paramref name="ordinal"/>.</returns>
    /// <remarks>The loop is bounded by the schedule length. An empty schedule matches nothing.</remarks>
    private bool ScheduledNotFound(int ordinal)
    {
        var at = NotFoundAtPuts;
        var count = at.Length;
        for (var index = 0; index < count; index++)
        {
            if (at[index] == ordinal)
                return true;
        }

        return false;
    }

    /// <summary>Copies put messages so a later mutation of the request cannot change the assertion.</summary>
    /// <param name="events">Events on the request. Null is an empty copy.</param>
    /// <returns>The message texts. A null element becomes empty.</returns>
    /// <remarks>The loop is bounded by the request list.</remarks>
    private static List<string> CopyMessages(List<InputLogEvent>? events)
    {
        var copy = new List<string>();
        if (events is null)
            return copy;

        var count = events.Count;
        for (var index = 0; index < count; index++)
        {
            var entry = events[index];
            copy.Add(entry is null ? string.Empty : entry.Message ?? string.Empty);
        }

        return copy;
    }
}

/// <summary>
/// Transport that never completes and records cancellation of the caller's token.
/// </summary>
internal sealed class GateAwsTransport : IAwsCloudWatchLogsTransport
{
    private int _cancelled;

    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a put has registered its cancellation callback.</summary>
    internal Task Entered => _entered.Task;

    /// <summary>True after the token passed to the put is cancelled.</summary>
    internal bool Cancelled => Volatile.Read(ref _cancelled) == 1;

    /// <summary>
    /// Returns a task that stays incomplete. Cancellation is recorded and does not complete the task.
    /// </summary>
    /// <param name="logGroupName">Ignored.</param>
    /// <param name="logStreamName">Ignored.</param>
    /// <param name="message">Ignored.</param>
    /// <param name="timestampUnixMs">Ignored.</param>
    /// <param name="cancellationToken">Observed only to record cancellation.</param>
    /// <returns>An incomplete task.</returns>
    /// <remarks>The writer must stop waiting on its own clock. This task never finishes.</remarks>
    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.Register(() => Volatile.Write(ref _cancelled, 1));
        _entered.TrySetResult();
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    /// <summary>No resources to release.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Fake clock that signals when the product registers a timer.
/// The test waits on that signal, then advances time.
/// </summary>
internal sealed class SignalingFakeTimeProvider : FakeTimeProvider
{
    private int _timers;
    private readonly TaskCompletionSource _timersArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after two timers exist: the cancellation source and the wait.</summary>
    internal Task TimersArmed => _timersArmed.Task;

    /// <summary>
    /// Creates the inner timer and signals <see cref="TimersArmed"/> once both waits exist.
    /// </summary>
    /// <param name="callback">Timer callback from the wait. Not null.</param>
    /// <param name="state">State passed back to the callback.</param>
    /// <param name="dueTime">Delay until the first tick.</param>
    /// <param name="period">Repeat interval. The timeout uses a one-shot timer.</param>
    /// <returns>The timer the base provider created.</returns>
    /// <remarks>The signal lets the test advance the clock only after both waits are armed.</remarks>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (Interlocked.Increment(ref _timers) >= 2)
            _timersArmed.TrySetResult();
        return timer;
    }
}

/// <summary>
/// Transport that faults every put with a caller-built exception.
/// </summary>
internal sealed class FaultingAwsTransport : IAwsCloudWatchLogsTransport
{
    private readonly Func<Exception> _fault;

    /// <summary>
    /// Stores the factory used for each put.
    /// </summary>
    /// <param name="fault">Builds a new exception per call. Not null.</param>
    internal FaultingAwsTransport(Func<Exception> fault) => _fault = fault;

    /// <summary>
    /// Returns a faulted task. The writer must catch it.
    /// </summary>
    /// <param name="logGroupName">Ignored.</param>
    /// <param name="logStreamName">Ignored.</param>
    /// <param name="message">Ignored.</param>
    /// <param name="timestampUnixMs">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns>A faulted task whose exception comes from the factory.</returns>
    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
        => Task.FromException(_fault());

    /// <summary>No resources to release.</summary>
    public void Dispose()
    {
    }
}
