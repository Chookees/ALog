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
    /// The next write creates the group and delivers the event.
    /// </summary>
    /// <remarks>Fails while the failure is not sent through <see cref="SelfLog"/>.</remarks>
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

            writer.Write(Plain("kept"));
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
    /// </summary>
    /// <returns>A task that finishes after the second write.</returns>
    /// <remarks>Separate from the synchronous network-failure case.</remarks>
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

            await writer.WriteAsync(Plain("kept"));
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
        Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
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
    /// The synchronous put stays open until the injected clock reaches the 100 second timeout.
    /// One tick earlier it is still running. At the timeout it is cancelled, reported, and not thrown.
    /// </summary>
    /// <remarks>
    /// The hang guards are 30 seconds. The product deadline moves only when the fake clock advances.
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

        Assert.True(clock.TimerCreated.Wait(TimeSpan.FromSeconds(30)));
        clock.Advance(TimeSpan.FromSeconds(100) - TimeSpan.FromTicks(1));
        Assert.False(write.IsCompleted);
        Assert.False(transport.Cancelled);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(write.Wait(TimeSpan.FromSeconds(30)));
        await Task.CompletedTask;
        Assert.Null(thrown);
        Assert.True(transport.Cancelled);
        Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
        Assert.Contains("100 second", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a writer that owns a transport around the counting client.
    /// </summary>
    /// <param name="client">The fake CloudWatch client. Not null.</param>
    /// <returns>A writer the caller disposes. Dispose also disposes the transport, not the proxy.</returns>
    /// <remarks>The proxy has no network resources. The transport does not own the client.</remarks>
    private static AwsCloudWatchWriter OwnedWriter(CountingCloudWatch client)
    {
        var transport = new AwsSdkCloudWatchLogsTransport(client.Client, ownsClient: false);
        return new AwsCloudWatchWriter("group", "stream", transport);
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
    /// Handles the CloudWatch operations the transport calls.
    /// Any other method throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="targetMethod">Interface method being invoked.</param>
    /// <param name="args">Arguments from the transport. Null is treated as empty.</param>
    /// <returns>A completed response task, or null for dispose.</returns>
    /// <remarks>Does not block and does not read the clock.</remarks>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? string.Empty;
        if (name == "Dispose")
            return null;

        args ??= [];
        if (name == "CreateLogGroupAsync")
            return Task.FromResult(CreateGroup());

        if (name == "CreateLogStreamAsync")
            return Task.FromResult(CreateStream());

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

    /// <summary>Records one create-stream call and returns the configured response or failure.</summary>
    /// <returns>A response, or null when null responses are requested.</returns>
    /// <exception cref="ResourceAlreadyExistsException">The stream already exists.</exception>
    private CreateLogStreamResponse? CreateStream()
    {
        CreateStreams++;
        if (StreamExists)
            throw new ResourceAlreadyExistsException("stream");

        return NullResponses ? null : new CreateLogStreamResponse();
    }

    /// <summary>Records one put, including a null sequence token and a null rejected-info payload.</summary>
    /// <param name="args">The put request is argument 0.</param>
    /// <returns>A response with null token and null indexes, or a null response when requested.</returns>
    /// <remarks>A null log-event list is recorded as an empty batch.</remarks>
    private PutLogEventsResponse? Put(object?[] args)
    {
        var request = (PutLogEventsRequest)args[0]!;
        Puts++;
        SequenceTokens.Add(request.SequenceToken);
        Batches.Add(CopyMessages(request.LogEvents));
        if (NullResponses)
            return null;

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
    /// <summary>Set when <see cref="CreateTimer"/> runs. The timeout test waits on this.</summary>
    internal ManualResetEventSlim TimerCreated { get; } = new(false);

    /// <summary>
    /// Creates the inner timer and signals <see cref="TimerCreated"/>.
    /// </summary>
    /// <param name="callback">Timer callback from the wait. Not null.</param>
    /// <param name="state">State passed back to the callback.</param>
    /// <param name="dueTime">Delay until the first tick.</param>
    /// <param name="period">Repeat interval. The timeout uses a one-shot timer.</param>
    /// <returns>The timer the base provider created.</returns>
    /// <remarks>The signal lets the test advance the clock only after the wait is armed.</remarks>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        TimerCreated.Set();
        return timer;
    }
}
