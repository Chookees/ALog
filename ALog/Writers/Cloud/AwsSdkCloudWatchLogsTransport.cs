namespace ALog.Writers.Cloud;

using System.Text;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;

/// <summary>
/// CloudWatch Logs transport backed by AWSSDK.CloudWatchLogs.
/// One writer instance creates its group and stream once. Later calls only put events.
/// PutLogEvents limits are applied here: 10,000 events, 1,048,576 bytes, and 24 hours.
/// </summary>
public sealed class AwsSdkCloudWatchLogsTransport : IAwsCloudWatchLogsTransport
{
    private const int MaxEventsPerPut = 10_000;
    private const int MaxPutBytes = 1_048_576;
    private const int EventOverheadBytes = 26;
    private const long MaxSpanMilliseconds = 86_400_000L;

    private readonly IAmazonCloudWatchLogs _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _ready;
    private int _disposed;

    /// <summary>
    /// Builds a client for <paramref name="region"/> and owns it.
    /// Keys are optional. Blank keys use the default credential chain.
    /// </summary>
    /// <param name="region">System name such as <c>us-east-1</c>.</param>
    /// <param name="accessKey">Access key, or null or blank to skip explicit keys.</param>
    /// <param name="secretKey">Secret key. Used only when <paramref name="accessKey"/> is also set.</param>
    /// <remarks>Does not call the network. Dispose releases the client.</remarks>
    public AwsSdkCloudWatchLogsTransport(
        string region = "us-east-1",
        string? accessKey = null,
        string? secretKey = null)
        : this(CreateClient(region, accessKey, secretKey), ownsClient: true)
    {
    }

    /// <summary>
    /// Uses a client the caller already built. Tests pass a fake.
    /// </summary>
    /// <param name="client">CloudWatch Logs client. Not null.</param>
    /// <param name="ownsClient">True when <see cref="Dispose"/> must dispose <paramref name="client"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <remarks>Does not create a group until the first put.</remarks>
    public AwsSdkCloudWatchLogsTransport(IAmazonCloudWatchLogs client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Puts one event by sending a one-event batch.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="message">Event text. Null is stored as empty.</param>
    /// <param name="timestampUnixMs">Event time as Unix milliseconds.</param>
    /// <param name="cancellationToken">Cancels the create and the put. The default is none.</param>
    /// <returns>A task that completes when the put has been accepted or has failed.</returns>
    /// <remarks>
    /// Does not call itself. The batch method does not call this method, so the two cannot recurse.
    /// A failure throws. The writer catches it.
    /// </remarks>
    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
    {
        AwsCloudWatchLogEvent[] one = [new AwsCloudWatchLogEvent(message, timestampUnixMs)];
        return PutLogEventsAsync(logGroupName, logStreamName, one, cancellationToken);
    }

    /// <summary>
    /// Puts the events in list order, splitting where a PutLogEvents limit would be exceeded.
    /// The group and the stream are created at most once. A null or empty list does nothing.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="events">Events in send order. Null or empty sends nothing.</param>
    /// <param name="cancellationToken">Cancels before the next slice and on the SDK calls. The default is none.</param>
    /// <returns>A task that completes after every slice is accepted, or when the first failure escapes.</returns>
    /// <remarks>
    /// Does not sort. Does not set a sequence token. Does not call DescribeLogStreams.
    /// The loop stops after at most <paramref name="events"/> count slices.
    /// A null SDK response is ignored. <see cref="ResourceAlreadyExistsException"/> on create is success.
    /// Any other create failure leaves the group unready so the next batch tries again.
    /// </remarks>
    public async Task PutLogEventsAsync(
        string logGroupName,
        string logStreamName,
        IReadOnlyList<AwsCloudWatchLogEvent>? events,
        CancellationToken cancellationToken = default)
    {
        if (events is null || events.Count == 0)
            return;

        await EnsureReadyAsync(logGroupName, logStreamName, cancellationToken).ConfigureAwait(false);
        var start = 0;
        var count = events.Count;
        for (var step = 0; step < count && start < count; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = SliceLength(events, start);
            var slice = new PutSlice(logGroupName, logStreamName, events, start, length);
            await SendSliceAsync(slice, cancellationToken).ConfigureAwait(false);
            start += length;
        }
    }

    /// <summary>
    /// Creates the group and the stream once. A later call returns without calling AWS.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the create calls.</param>
    /// <returns>A task that completes when both resources exist.</returns>
    /// <remarks>
    /// <see cref="ResourceAlreadyExistsException"/> counts as success.
    /// Any other exception leaves the ready flag clear and escapes.
    /// The gate wait has no timeout. Callers on the sync path bound the whole put instead.
    /// </remarks>
    private async Task EnsureReadyAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _ready) == 1)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _ready) == 1)
                return;

            await CreateGroupAsync(logGroupName, cancellationToken).ConfigureAwait(false);
            await CreateStreamAsync(logGroupName, logStreamName, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _ready, 1);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Creates the log group. An existing group is success.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="cancellationToken">Cancels the SDK call.</param>
    /// <returns>A task that completes when the group exists.</returns>
    /// <remarks>A null response is ignored. Other failures escape, including a network error.</remarks>
    private async Task CreateGroupAsync(string logGroupName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.CreateLogGroupAsync(
                new CreateLogGroupRequest { LogGroupName = logGroupName },
                cancellationToken).ConfigureAwait(false);
            NoteResponse(response);
        }
        catch (ResourceAlreadyExistsException)
        {
            // The group is already there. That is success, not a token refresh.
        }
    }

    /// <summary>
    /// Creates the log stream. An existing stream is success.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="cancellationToken">Cancels the SDK call.</param>
    /// <returns>A task that completes when the stream exists.</returns>
    /// <remarks>Does not describe the stream. A null response is ignored.</remarks>
    private async Task CreateStreamAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.CreateLogStreamAsync(
                new CreateLogStreamRequest
                {
                    LogGroupName = logGroupName,
                    LogStreamName = logStreamName,
                },
                cancellationToken).ConfigureAwait(false);
            NoteResponse(response);
        }
        catch (ResourceAlreadyExistsException)
        {
            // The stream is already there. Sequence tokens are not read.
        }
    }

    /// <summary>
    /// Sends one slice. The request sequence token stays unset.
    /// </summary>
    /// <param name="slice">Group, stream, and the event range. The range is inside the list.</param>
    /// <param name="cancellationToken">Cancels the put.</param>
    /// <returns>A task that completes when the SDK accepts the slice or fails it.</returns>
    /// <remarks>
    /// A null response, a null next token, and null rejected-event indexes are ignored.
    /// <see cref="InvalidSequenceTokenException"/> and <see cref="DataAlreadyAcceptedException"/> are not caught.
    /// </remarks>
    private async Task SendSliceAsync(PutSlice slice, CancellationToken cancellationToken)
    {
        var request = new PutLogEventsRequest
        {
            LogGroupName = slice.LogGroupName,
            LogStreamName = slice.LogStreamName,
            LogEvents = CopySlice(slice),
        };
        var response = await _client.PutLogEventsAsync(request, cancellationToken).ConfigureAwait(false);
        NotePut(response);
    }

    /// <summary>
    /// How many events from <paramref name="start"/> fit in one request.
    /// The first event is always included, even when it alone exceeds the byte cap,
    /// so the caller advances and the loop ends.
    /// </summary>
    /// <param name="events">Full list. Not null. Not empty.</param>
    /// <param name="start">Index of the first event of this slice. Inside the list.</param>
    /// <returns>At least 1 and at most 10,000, and never past the end of the list.</returns>
    /// <remarks>
    /// Splits when the next timestamp goes backwards, the span would exceed 24 hours,
    /// or the UTF-8 size plus 26 bytes per event would exceed 1,048,576.
    /// Does not reorder.
    /// </remarks>
    private static int SliceLength(IReadOnlyList<AwsCloudWatchLogEvent> events, int start)
    {
        var room = events.Count - start;
        if (room > MaxEventsPerPut)
            room = MaxEventsPerPut;

        var first = events[start].TimestampUnixMs;
        var previous = first;
        var bytes = EventBytes(events[start].Message);
        var taken = 1;
        for (var offset = 1; offset < room; offset++)
        {
            var entry = events[start + offset];
            if (entry.TimestampUnixMs < previous)
                break;
            if (entry.TimestampUnixMs - first > MaxSpanMilliseconds)
                break;

            var next = bytes + EventBytes(entry.Message);
            if (next > MaxPutBytes)
                break;

            bytes = next;
            previous = entry.TimestampUnixMs;
            taken++;
        }

        return taken;
    }

    /// <summary>
    /// Bytes this event contributes to the PutLogEvents cap.
    /// </summary>
    /// <param name="message">Event text. Not null. Empty counts as the 26-byte overhead only.</param>
    /// <returns>UTF-8 size of <paramref name="message"/> plus 26.</returns>
    /// <remarks>Does not allocate the encoded bytes.</remarks>
    private static int EventBytes(string message)
        => Encoding.UTF8.GetByteCount(message) + EventOverheadBytes;

    /// <summary>
    /// Copies one slice into the SDK list. Order is unchanged.
    /// </summary>
    /// <param name="slice">The range to copy. Not empty.</param>
    /// <returns>A new list of that length. The request owns it.</returns>
    /// <remarks>The loop is bounded by the slice length.</remarks>
    private static List<InputLogEvent> CopySlice(PutSlice slice)
    {
        var copy = new List<InputLogEvent>(slice.Length);
        var end = slice.Start + slice.Length;
        for (var index = slice.Start; index < end; index++)
        {
            var entry = slice.Events[index];
            copy.Add(new InputLogEvent
            {
                Message = entry.Message,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampUnixMs).UtcDateTime,
            });
        }

        return copy;
    }

    /// <summary>
    /// Accepts a null create response. V4 leaves unused response objects null.
    /// </summary>
    /// <param name="response">SDK response. Null is success with nothing to read.</param>
    /// <remarks>Does not throw. Create responses have no fields this transport reads.</remarks>
    private static void NoteResponse(object? response)
    {
        if (response is null)
            return;
    }

    /// <summary>
    /// Reads the put response without throwing when V4 leaves fields null.
    /// The next sequence token is ignored. The service no longer requires it.
    /// </summary>
    /// <param name="response">SDK response. Null means the call returned no body.</param>
    /// <remarks>
    /// Null rejected-event indexes are read and not acted on.
    /// Does not throw for those nulls. Does not retry.
    /// </remarks>
    private static void NotePut(PutLogEventsResponse? response)
    {
        if (response is null)
            return;

        var token = response.NextSequenceToken;
        var rejected = response.RejectedLogEventsInfo;
        if (rejected is null)
        {
            _ = token;
            return;
        }

        _ = token;
        _ = rejected.ExpiredLogEventEndIndex.HasValue;
        _ = rejected.TooNewLogEventStartIndex.HasValue;
        _ = rejected.TooOldLogEventEndIndex.HasValue;
    }

    /// <summary>
    /// Builds the SDK client. Explicit keys are used only when both are non-blank.
    /// </summary>
    /// <param name="region">System region name.</param>
    /// <param name="accessKey">Access key, or null or blank.</param>
    /// <param name="secretKey">Secret key, or null or blank.</param>
    /// <returns>A client this transport owns.</returns>
    /// <remarks>Does not call the network.</remarks>
    private static IAmazonCloudWatchLogs CreateClient(string region, string? accessKey, string? secretKey)
    {
        var endpoint = RegionEndpoint.GetBySystemName(region);
        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            return new AmazonCloudWatchLogsClient(credentials, endpoint);
        }

        return new AmazonCloudWatchLogsClient(endpoint);
    }

    /// <summary>
    /// Releases the gate, and the client when this transport owns it.
    /// A second call does nothing.
    /// </summary>
    /// <remarks>Does not throw when the client dispose runs only once.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _gate.Dispose();
        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>
    /// One PutLogEvents range: the group, the stream, and a half-open index span.
    /// </summary>
    private readonly struct PutSlice
    {
        /// <summary>
        /// Stores one slice. The caller keeps <paramref name="length"/> inside the list.
        /// </summary>
        /// <param name="logGroupName">Log group name. Not null.</param>
        /// <param name="logStreamName">Log stream name. Not null.</param>
        /// <param name="events">Source events. Not null.</param>
        /// <param name="start">First index to send.</param>
        /// <param name="length">How many events to send. At least 1.</param>
        internal PutSlice(
            string logGroupName,
            string logStreamName,
            IReadOnlyList<AwsCloudWatchLogEvent> events,
            int start,
            int length)
        {
            LogGroupName = logGroupName;
            LogStreamName = logStreamName;
            Events = events;
            Start = start;
            Length = length;
        }

        /// <summary>Log group name passed to the request.</summary>
        internal string LogGroupName { get; }

        /// <summary>Log stream name passed to the request.</summary>
        internal string LogStreamName { get; }

        /// <summary>Source list. Not copied until the request is built.</summary>
        internal IReadOnlyList<AwsCloudWatchLogEvent> Events { get; }

        /// <summary>First index in <see cref="Events"/>.</summary>
        internal int Start { get; }

        /// <summary>Number of events from <see cref="Start"/>.</summary>
        internal int Length { get; }
    }
}
