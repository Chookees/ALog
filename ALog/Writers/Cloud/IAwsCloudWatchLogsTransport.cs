namespace ALog.Writers.Cloud;

/// <summary>
/// One CloudWatch event already rendered for the stream.
/// The timestamp is Unix time in milliseconds. A custom transport receives these
/// from <see cref="IAwsCloudWatchLogsTransport.PutLogEventsAsync"/>.
/// </summary>
public readonly struct AwsCloudWatchLogEvent
{
    /// <summary>
    /// Stores the text and the time of one event.
    /// </summary>
    /// <param name="message">Text placed in the CloudWatch message. Null is stored as empty.</param>
    /// <param name="timestampUnixMs">When the event happened, milliseconds since 1970-01-01 UTC.</param>
    /// <remarks>Does not throw. Does not read the clock.</remarks>
    public AwsCloudWatchLogEvent(string? message, long timestampUnixMs)
    {
        Message = message ?? string.Empty;
        TimestampUnixMs = timestampUnixMs;
    }

    /// <summary>The event text. Empty when the caller passed null. Never null.</summary>
    public string Message { get; }

    /// <summary>Event time as Unix milliseconds. Not a deadline.</summary>
    public long TimestampUnixMs { get; }
}

/// <summary>
/// Abstraction over CloudWatch Logs PutLogEvents for shipping and tests.
/// </summary>
public interface IAwsCloudWatchLogsTransport : IDisposable
{
    /// <summary>
    /// Puts one event. The SDK transport forwards this to <see cref="PutLogEventsAsync"/>.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="message">Rendered event text.</param>
    /// <param name="timestampUnixMs">Event time as Unix milliseconds.</param>
    /// <param name="cancellationToken">Cancels the put. The default is none.</param>
    /// <returns>A task that completes when the event has been accepted or the call has failed.</returns>
    /// <remarks>A failure throws. The writer catches it and does not rethrow to the application.</remarks>
    Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts every event, in the list order.
    /// The SDK transport packs them into as few PutLogEvents calls as the service limits allow.
    /// A transport that only implements <see cref="PutLogEventAsync"/> sends one event per call.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="events">Events in send order. Null or empty sends nothing.</param>
    /// <param name="cancellationToken">Cancels before the next event. The default is none.</param>
    /// <returns>A task that completes after the events are accepted or the first failure escapes.</returns>
    /// <remarks>
    /// Does not throw when <paramref name="events"/> is null.
    /// The loop is bounded by the list count. A custom implementation keeps compiling
    /// because this member has a body.
    /// </remarks>
    async Task PutLogEventsAsync(
        string logGroupName,
        string logStreamName,
        IReadOnlyList<AwsCloudWatchLogEvent>? events,
        CancellationToken cancellationToken = default)
    {
        if (events is null || events.Count == 0)
            return;

        var count = events.Count;
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = events[index];
            await PutLogEventAsync(
                logGroupName,
                logStreamName,
                entry.Message,
                entry.TimestampUnixMs,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
