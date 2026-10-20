namespace ALog.Writers.Cloud;

using System.Text;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;

/// <summary>
/// CloudWatch Logs transport backed by AWSSDK.CloudWatchLogs.
/// Each log group and each log stream is created once, including when writers share this transport,
/// until a put reports the stream missing or the ready sets reach <see cref="MaxReadyPairs"/>.
/// A missing stream that was already recorded is created again and that slice is sent once.
/// A missing stream that this batch itself created is not created again.
/// PutLogEvents limits are applied here: 10,000 events, 1,048,576 bytes, and 24 hours.
/// </summary>
public sealed class AwsSdkCloudWatchLogsTransport : IAwsCloudWatchLogsTransport
{
    private const int MaxEventsPerPut = 10_000;
    private const int MaxPutBytes = 1_048_576;
    private const int EventOverheadBytes = 26;
    private const long MaxSpanMilliseconds = 86_400_000L;

    /// <summary>
    /// Most group and stream pairs kept ready on one transport.
    /// Past this, both ready sets are cleared. Create is idempotent, so a later put records them again.
    /// </summary>
    internal const int MaxReadyPairs = 1024;

    private readonly IAmazonCloudWatchLogs _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _waiterSync = new();
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);
    private readonly HashSet<ReadyKey> _streams = new();
    private TaskCompletionSource<bool> _waiterSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiterSignalAt = int.MaxValue;
    private int _readyGateWaiters;
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
    /// The group and the stream are created at most once until a put reports the stream missing.
    /// A null or empty list does nothing.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="events">Events in send order. Null or empty sends nothing.</param>
    /// <param name="cancellationToken">Cancels before the next slice and on the SDK calls. The default is none.</param>
    /// <returns>A task that completes after every slice is accepted, or when the first failure escapes.</returns>
    /// <remarks>
    /// Does not sort. Does not set a sequence token. Does not call DescribeLogStreams.
    /// The loop stops after at most <paramref name="events"/> count slices.
    /// A missing stream is re-created once only when it was already recorded before this batch.
    /// That re-create resends the failed slice once. A second miss in the same batch stops the batch.
    /// A miss after the create this batch just did is not created again. The batch stops, and the pair is left unready.
    /// A null SDK response is ignored. <see cref="ResourceAlreadyExistsException"/> on create is success.
    /// Any other create failure leaves that resource unready so the next batch tries again.
    /// </remarks>
    public async Task PutLogEventsAsync(
        string logGroupName,
        string logStreamName,
        IReadOnlyList<AwsCloudWatchLogEvent>? events,
        CancellationToken cancellationToken = default)
    {
        if (events is null || events.Count == 0)
            return;

        var createdThisBatch = await EnsureReadyAsync(logGroupName, logStreamName, cancellationToken)
            .ConfigureAwait(false);
        var start = 0;
        var count = events.Count;
        var recovered = false;
        for (var step = 0; step < count && start < count; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = SliceLength(events, start);
            var slice = new PutSlice(logGroupName, logStreamName, events, start, length);
            recovered = await SendOrRecoverAsync(slice, recovered, createdThisBatch, cancellationToken)
                .ConfigureAwait(false);
            start += length;
        }
    }

    /// <summary>
    /// Sends one slice. A stream that was already recorded can be re-created once in this batch.
    /// </summary>
    /// <param name="slice">Group, stream, and the event range.</param>
    /// <param name="recovered">True when this batch already recreated a missing stream.</param>
    /// <param name="createdThisBatch">True when this batch's first create recorded the stream.</param>
    /// <param name="cancellationToken">Cancels the put and the re-create.</param>
    /// <returns>True when this batch has used its one re-create.</returns>
    /// <remarks>
    /// A not-found result after this batch created the stream is not created again.
    /// Readiness is cleared and the exception escapes. The writer reports it and drops the batch.
    /// A second not-found result in the same batch does the same.
    /// </remarks>
    private async Task<bool> SendOrRecoverAsync(
        PutSlice slice,
        bool recovered,
        bool createdThisBatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendSliceAsync(slice, cancellationToken).ConfigureAwait(false);
            return recovered;
        }
        catch (ResourceNotFoundException) when (!recovered && !createdThisBatch)
        {
            await RecreateAndResendAsync(slice, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ResourceNotFoundException)
        {
            await ForgetAsync(slice.LogGroupName, slice.LogStreamName, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Clears the group and stream, creates them once, and sends <paramref name="slice"/> once.
    /// </summary>
    /// <param name="slice">The slice that the service rejected as missing.</param>
    /// <param name="cancellationToken">Cancels the create and the resend.</param>
    /// <returns>A task that completes when the resend is accepted.</returns>
    /// <remarks>
    /// A create failure or a second put failure leaves the stream unready and escapes.
    /// There is no further attempt in this batch.
    /// </remarks>
    private async Task RecreateAndResendAsync(PutSlice slice, CancellationToken cancellationToken)
    {
        await ForgetAsync(slice.LogGroupName, slice.LogStreamName, cancellationToken).ConfigureAwait(false);
        await EnsureReadyAsync(slice.LogGroupName, slice.LogStreamName, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendSliceAsync(slice, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ForgetAsync(slice.LogGroupName, slice.LogStreamName, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates the group and the stream when this pair is not already recorded.
    /// A later call for the same pair returns without calling AWS.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the create calls.</param>
    /// <returns>True when this call created the stream. False when the pair was already recorded.</returns>
    /// <remarks>
    /// <see cref="ResourceAlreadyExistsException"/> counts as success.
    /// A failed group create is not recorded. A failed stream create leaves the group recorded
    /// when that call succeeded, so the next batch retries the stream.
    /// The gate wait has no timeout. Callers on the sync path bound the whole put instead.
    /// </remarks>
    private async Task<bool> EnsureReadyAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        await WaitForReadyGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreatePairIfMissingAsync(logGroupName, logStreamName, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Records the pair when it is missing. The caller holds the ready gate.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="cancellationToken">Cancels the create calls.</param>
    /// <returns>True when this call created the stream. False when the pair was already recorded.</returns>
    /// <remarks>
    /// Does not take the gate. A full ready set is cleared before the create.
    /// The clear is safe because create is idempotent.
    /// </remarks>
    private async Task<bool> CreatePairIfMissingAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        var key = new ReadyKey(logGroupName, logStreamName);
        if (_streams.Contains(key))
            return false;

        MakeRoom(logGroupName);
        if (!_groups.Contains(logGroupName))
        {
            await CreateGroupAsync(logGroupName, cancellationToken).ConfigureAwait(false);
            _groups.Add(logGroupName);
        }

        await CreateStreamAsync(logGroupName, logStreamName, cancellationToken).ConfigureAwait(false);
        _streams.Add(key);
        CheckReadyBound(_groups.Count, _streams.Count);
        return true;
    }

    /// <summary>
    /// Drops both ready sets when recording this pair would pass <see cref="MaxReadyPairs"/>.
    /// </summary>
    /// <param name="logGroupName">Group about to be recorded. Not null.</param>
    /// <remarks>
    /// Does not call AWS. Create is idempotent, so a group or stream that still exists is success next time.
    /// The caller holds the ready gate.
    /// </remarks>
    private void MakeRoom(string logGroupName)
    {
        var addingGroup = !_groups.Contains(logGroupName);
        if (_streams.Count >= MaxReadyPairs || (addingGroup && _groups.Count >= MaxReadyPairs))
        {
            _groups.Clear();
            _streams.Clear();
        }
    }

    /// <summary>
    /// Throws when either ready set is above <see cref="MaxReadyPairs"/>.
    /// </summary>
    /// <param name="groups">Recorded groups after the add.</param>
    /// <param name="streams">Recorded pairs after the add.</param>
    /// <exception cref="System.InvalidOperationException">A ready set passed the cap.</exception>
    /// <remarks>Stays in Release builds. The cap clear runs before the add, so a correct set never throws.</remarks>
    internal static void CheckReadyBound(int groups, int streams)
    {
        if (groups > MaxReadyPairs || streams > MaxReadyPairs)
            throw new System.InvalidOperationException("Ready set exceeded the fixed cap.");
    }

    /// <summary>
    /// Waits until this caller holds the ready gate.
    /// A caller that has to wait is counted until the wait finishes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait. A cancelled wait does not take the gate.</param>
    /// <returns>A task that completes when this caller holds the gate.</returns>
    /// <remarks>
    /// The wait has no timeout. An immediate acquire is not a waiter.
    /// The count is how the eight-writer test sees the other writers blocked on this gate.
    /// </remarks>
    private async Task WaitForReadyGateAsync(CancellationToken cancellationToken)
    {
        var pending = _gate.WaitAsync(cancellationToken);
        var blocked = !pending.IsCompleted;
        if (blocked)
            NoteBlockedWaiter();

        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            if (blocked)
                NoteWaitFinished();
        }
    }

    /// <summary>
    /// Records one caller blocked on the ready gate and completes a waiting signal at its target.
    /// </summary>
    /// <remarks>Does not take the ready gate. The signal is armed by <see cref="SignalWhenReadyGateWaitersReach"/>.</remarks>
    private void NoteBlockedWaiter()
    {
        TaskCompletionSource<bool> signal;
        int waiting;
        int target;
        lock (_waiterSync)
        {
            waiting = ++_readyGateWaiters;
            signal = _waiterSignal;
            target = _waiterSignalAt;
        }

        if (waiting >= target)
            signal.TrySetResult(true);
    }

    /// <summary>
    /// Records that a blocked caller now holds the gate or that its wait ended.
    /// </summary>
    /// <remarks>Does not take the ready gate. Pairs with <see cref="NoteBlockedWaiter"/>.</remarks>
    private void NoteWaitFinished()
    {
        lock (_waiterSync)
            _readyGateWaiters--;
    }

    /// <summary>
    /// Completes when <paramref name="count"/> callers are blocked on the ready gate.
    /// </summary>
    /// <param name="count">Waiter count that completes the returned task. A count already reached completes immediately.</param>
    /// <returns>A task that completes once that many callers are blocked, including when they already are.</returns>
    /// <remarks>
    /// Does not take the ready gate. A later call replaces the signal. The previous task is left as it was.
    /// Tests release a held create when the other writers are blocked here.
    /// </remarks>
    internal Task SignalWhenReadyGateWaitersReach(int count)
    {
        lock (_waiterSync)
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiterSignal = signal;
            _waiterSignalAt = count;
            if (_readyGateWaiters >= count)
                signal.TrySetResult(true);

            return signal.Task;
        }
    }

    /// <summary>How many callers are blocked on the ready gate. Zero when nobody is waiting.</summary>
    internal int ReadyGateWaiters
    {
        get
        {
            lock (_waiterSync)
                return _readyGateWaiters;
        }
    }

    /// <summary>How many groups are recorded ready. The cap is <see cref="MaxReadyPairs"/>.</summary>
    internal int ReadyGroupCount => _groups.Count;

    /// <summary>How many group and stream pairs are recorded ready. The cap is <see cref="MaxReadyPairs"/>.</summary>
    internal int ReadyStreamCount => _streams.Count;

    /// <summary>
    /// Forgets the group entry and one stream so the next create runs again.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate.</param>
    /// <returns>A task that completes after those two entries are removed.</returns>
    /// <remarks>
    /// Does not call AWS. Other streams of the same group stay recorded.
    /// The group entry is removed too because the group may have been deleted.
    /// The next create is idempotent: a group or stream that still exists counts as success.
    /// </remarks>
    private async Task ForgetAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _groups.Remove(logGroupName);
            _streams.Remove(new ReadyKey(logGroupName, logStreamName));
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
    /// Group and stream pair used as the readiness key.
    /// </summary>
    private readonly struct ReadyKey : IEquatable<ReadyKey>
    {
        /// <summary>
        /// Stores one pair. Compared ordinal so two names that differ only by case stay distinct.
        /// </summary>
        /// <param name="group">Log group name. Not null.</param>
        /// <param name="stream">Log stream name. Not null.</param>
        internal ReadyKey(string group, string stream)
        {
            Group = group;
            Stream = stream;
        }

        /// <summary>Log group name.</summary>
        internal string Group { get; }

        /// <summary>Log stream name.</summary>
        internal string Stream { get; }

        /// <summary>True when both names match ordinal.</summary>
        /// <param name="other">The other key.</param>
        /// <returns>True when <paramref name="other"/> names the same group and stream.</returns>
        public bool Equals(ReadyKey other)
            => string.Equals(Group, other.Group, StringComparison.Ordinal)
            && string.Equals(Stream, other.Stream, StringComparison.Ordinal);

        /// <summary>True when <paramref name="obj"/> is the same pair.</summary>
        /// <param name="obj">The value to compare. Null is not equal.</param>
        /// <returns>True when <paramref name="obj"/> is an equal <see cref="ReadyKey"/>.</returns>
        public override bool Equals(object? obj) => obj is ReadyKey other && Equals(other);

        /// <summary>Ordinal hash of the group and the stream.</summary>
        /// <returns>A hash code for this pair.</returns>
        public override int GetHashCode()
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(Group),
                StringComparer.Ordinal.GetHashCode(Stream));
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
