namespace ALog.Writers.Cloud;

using ALog.Core;
using ALog.Public.Interfaces;
using SelfLog = ALog.SelfLog;

/// <summary>
/// AWS CloudWatch Logs writer. Ships via <see cref="IAwsCloudWatchLogsTransport"/> (SDK by default).
/// The synchronous put waits on an injected clock. The message includes template properties and context
/// unless a formatter is set, in which case the formatter text is the whole message.
/// </summary>
public class AwsCloudWatchWriter : ILogWriter, IFormattableLogWriter
{
    private readonly string _logGroupName;
    private readonly string _logStreamName;
    private readonly IAwsCloudWatchLogsTransport _transport;
    private readonly bool _ownsTransport;
    private readonly TimeProvider _clock;
    private int _disposed;

    /// <summary>
    /// Bound for a synchronous put. The AWS SDK has no synchronous API.
    /// 100 seconds is the default <see cref="HttpClient.Timeout"/>, which the SDK uses
    /// when <c>ClientConfig.Timeout</c> is unset in legacy mode.
    /// Tests may shorten it and must restore the previous value.
    /// </summary>
    internal static TimeSpan SyncPutTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Formatter that owns the message when it is not null.
    /// Null sends the rendered message, then the template properties, then the context.
    /// </summary>
    public ILogFormatter? Formatter { get; set; }

    /// <summary>
    /// Builds a writer that owns an SDK transport for <paramref name="region"/>.
    /// The synchronous timeout uses <see cref="ProductionClock.Default"/>.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="region">System region name. The default is <c>us-east-1</c>.</param>
    /// <param name="formatter">Optional formatter. Null sends the plain message.</param>
    /// <param name="accessKey">Access key, or null to use the default credential chain.</param>
    /// <param name="secretKey">Secret key. Used only when <paramref name="accessKey"/> is also set.</param>
    /// <exception cref="ArgumentNullException">The group, the stream, or the transport is null.</exception>
    /// <remarks>Does not call the network. Dispose releases the owned transport.</remarks>
    public AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        string region = "us-east-1",
        ILogFormatter? formatter = null,
        string? accessKey = null,
        string? secretKey = null)
        : this(new WriterParts(
            new WriterLink(
                logGroupName,
                logStreamName,
                new AwsSdkCloudWatchLogsTransport(region, accessKey, secretKey),
                true),
            formatter,
            ProductionClock.Default))
    {
    }

    /// <summary>
    /// Builds a writer around a transport the caller owns.
    /// The synchronous timeout uses <see cref="ProductionClock.Default"/>.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="transport">Transport that receives each put. Not null. Not disposed by this writer.</param>
    /// <param name="formatter">Optional formatter. Null sends the plain message.</param>
    /// <exception cref="ArgumentNullException">The group, the stream, or <paramref name="transport"/> is null.</exception>
    /// <remarks>Does not call the network.</remarks>
    public AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        IAwsCloudWatchLogsTransport transport,
        ILogFormatter? formatter = null)
        : this(new WriterParts(
            new WriterLink(logGroupName, logStreamName, transport, false),
            formatter,
            ProductionClock.Default))
    {
    }

    /// <summary>
    /// Test seam. <paramref name="clock"/> bounds the synchronous put.
    /// </summary>
    /// <param name="logGroupName">Log group name. Not null.</param>
    /// <param name="logStreamName">Log stream name. Not null.</param>
    /// <param name="transport">Transport that receives the put. Not null. Not owned.</param>
    /// <param name="clock">Clock for the synchronous put timeout. Not null.</param>
    /// <param name="formatter">Optional formatter. Null sends the plain message.</param>
    /// <remarks>Does not own <paramref name="transport"/>. Does not read the wall clock.</remarks>
    internal AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        IAwsCloudWatchLogsTransport transport,
        TimeProvider clock,
        ILogFormatter? formatter = null)
        : this(new WriterParts(
            new WriterLink(logGroupName, logStreamName, transport, false),
            formatter,
            clock))
    {
    }

    /// <summary>
    /// Stores the parts. The public constructors and the test constructor end here.
    /// </summary>
    /// <param name="parts">Group, stream, transport, ownership, formatter, and clock.</param>
    /// <exception cref="ArgumentNullException">The group, the stream, the transport, or the clock is null.</exception>
    /// <remarks>Does not call the network. The clock cannot be changed after this returns.</remarks>
    private AwsCloudWatchWriter(WriterParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts.Link.LogGroupName);
        ArgumentNullException.ThrowIfNull(parts.Link.LogStreamName);
        ArgumentNullException.ThrowIfNull(parts.Link.Transport);
        ArgumentNullException.ThrowIfNull(parts.Clock);
        _logGroupName = parts.Link.LogGroupName;
        _logStreamName = parts.Link.LogStreamName;
        _transport = parts.Link.Transport;
        _ownsTransport = parts.Link.OwnsTransport;
        _clock = parts.Clock;
        Formatter = parts.Formatter;
    }

    /// <summary>
    /// Puts one event and waits at most <see cref="SyncPutTimeout"/> on <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="logEvent">Event to put. Null is reported and dropped.</param>
    /// <remarks>
    /// The SDK call starts on the thread pool, so the caller
    /// <see cref="SynchronizationContext"/> is not captured.
    /// A timeout, a dispose, or a transport failure is reported and is not thrown.
    /// </remarks>
    public void Write(LogEvent logEvent)
    {
        try
        {
            ThrowIfDisposed();
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            PutWithinTransportTimeout(logEvent);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Puts one event without resuming on the caller context.
    /// </summary>
    /// <param name="logEvent">Event to put. Null is reported and the task still completes.</param>
    /// <returns>A task that completes after the put, including when the put fails.</returns>
    /// <remarks>
    /// Does not use <see cref="Task.Run(Action)"/>. The await uses <c>ConfigureAwait(false)</c>.
    /// Failures are reported and are not thrown. There is no separate timeout; the caller's token
    /// is not forwarded because this method has none.
    /// </remarks>
    public async Task WriteAsync(LogEvent logEvent)
    {
        try
        {
            ThrowIfDisposed();
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            await SendAsync(logEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Waits for one put on the injected clock.
    /// </summary>
    /// <param name="logEvent">Event to put. Must not be null.</param>
    /// <exception cref="TimeoutException">The put did not finish within <see cref="SyncPutTimeout"/>.</exception>
    /// <remarks>
    /// Does not catch a transport failure. <see cref="Write"/> reports that.
    /// The timeout is the AWS transport bound, not a shutdown deadline.
    /// The put starts on the thread pool. This method blocks the caller only on the wait.
    /// A cancellation from this timeout is the same 100 second timeout. The token can win the race
    /// with the wait when both are due on the same clock tick.
    /// </remarks>
    private void PutWithinTransportTimeout(LogEvent logEvent)
    {
        using var timeout = new CancellationTokenSource(SyncPutTimeout, _clock);
        var pending = Task.Run(() => SendAsync(logEvent, timeout.Token), CancellationToken.None);
        try
        {
            pending.WaitAsync(SyncPutTimeout, _clock).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsTransportTimeout(ex, timeout))
        {
            timeout.Cancel();
            throw new TimeoutException("CloudWatch put exceeded the 100 second transport timeout.");
        }
    }

    /// <summary>True when the sync wait ended because <paramref name="timeout"/> elapsed.</summary>
    /// <param name="ex">The exception from the wait. Not null.</param>
    /// <param name="timeout">The source created for this put. Not null.</param>
    /// <returns>True for a wait timeout, or for cancellation after that source has fired.</returns>
    /// <remarks>Does not throw. A transport failure that is not this timeout returns false so <see cref="Write"/> can report it.</remarks>
    private static bool IsTransportTimeout(Exception ex, CancellationTokenSource timeout)
    {
        if (ex is TimeoutException)
            return true;

        return ex is OperationCanceledException && timeout.IsCancellationRequested;
    }

    /// <summary>
    /// Sends one event through the batch transport method.
    /// </summary>
    /// <param name="logEvent">Event to render and put. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the transport call. None on the async writer path.</param>
    /// <returns>The transport task. It is not wrapped.</returns>
    /// <remarks>Does not catch. Does not hop to the thread pool by itself.</remarks>
    private Task SendAsync(LogEvent logEvent, CancellationToken cancellationToken)
    {
        var message = Render(logEvent);
        var timestampMs = new DateTimeOffset(logEvent.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds();
        AwsCloudWatchLogEvent[] one = [new AwsCloudWatchLogEvent(message, timestampMs)];
        return _transport.PutLogEventsAsync(_logGroupName, _logStreamName, one, cancellationToken);
    }

    /// <summary>
    /// Renders one event. A formatter replaces the plain text. Otherwise properties and context are appended.
    /// </summary>
    /// <param name="logEvent">Event to render. Must not be null.</param>
    /// <returns>The CloudWatch message. Never null.</returns>
    /// <remarks>
    /// A null or empty bag adds nothing, so a message with no properties stays exact.
    /// Does not throw when a value's text conversion throws. That value becomes a capture-failed marker.
    /// </remarks>
    private string Render(LogEvent logEvent)
    {
        if (Formatter is not null)
            return Formatter.Format(logEvent);

        return AppendBag(AppendBag(logEvent.Message, logEvent.Properties), logEvent.Context);
    }

    /// <summary>
    /// Appends one bag as <c>key=text</c> pairs when the bag has entries.
    /// </summary>
    /// <param name="message">Text so far. Not null.</param>
    /// <param name="bag">Properties or context. Null and empty add nothing.</param>
    /// <returns><paramref name="message"/>, or that text plus a space and the pairs.</returns>
    /// <remarks>Does not throw. The loop lives in <see cref="CaptureText.Join"/>.</remarks>
    private static string AppendBag(string message, IReadOnlyDictionary<string, object>? bag)
    {
        if (bag is null || bag.Count == 0)
            return message;

        return message + " " + CaptureText.Join(bag);
    }

    /// <summary>
    /// Rejects a write after <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This writer has been disposed.</exception>
    /// <remarks><see cref="Write"/> and <see cref="WriteAsync"/> catch this and report it. The transport is not called.</remarks>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(AwsCloudWatchWriter));
    }

    /// <summary>
    /// Writes one CloudWatch failure to SelfLog and to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from the transport, the dispose check, or the sync wait. Must not be null.</param>
    /// <remarks>
    /// The error-stream line stays <c>[AwsCloudWatchWriter] CloudWatch write failed:</c> so existing checks still match.
    /// SelfLog receives the same failure. Neither path throws. There is no new catalog id.
    /// </remarks>
    private static void Report(Exception ex)
    {
        SelfLog.Report("AwsCloudWatchWriter", ex);
        System.Console.Error.WriteLine($"[AwsCloudWatchWriter] CloudWatch write failed: {ex}");
    }

    /// <summary>
    /// Releases the owned transport. A second call does nothing.
    /// A write after this returns is reported and does not call AWS.
    /// </summary>
    /// <remarks>Does not throw. A transport this writer does not own is left alone.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsTransport)
            _transport.Dispose();
    }

    /// <summary>
    /// Group, stream, and transport ownership. Kept separate so each constructor stays within five parameters.
    /// </summary>
    private readonly struct WriterLink
    {
        /// <summary>
        /// Stores the endpoint. Null checks happen in the writer constructor.
        /// </summary>
        /// <param name="logGroupName">Log group name.</param>
        /// <param name="logStreamName">Log stream name.</param>
        /// <param name="transport">Transport that receives puts.</param>
        /// <param name="ownsTransport">True when the writer disposes <paramref name="transport"/>.</param>
        internal WriterLink(
            string logGroupName,
            string logStreamName,
            IAwsCloudWatchLogsTransport transport,
            bool ownsTransport)
        {
            LogGroupName = logGroupName;
            LogStreamName = logStreamName;
            Transport = transport;
            OwnsTransport = ownsTransport;
        }

        /// <summary>Log group name.</summary>
        internal string LogGroupName { get; }

        /// <summary>Log stream name.</summary>
        internal string LogStreamName { get; }

        /// <summary>Transport that receives puts.</summary>
        internal IAwsCloudWatchLogsTransport Transport { get; }

        /// <summary>True when the writer disposes <see cref="Transport"/>.</summary>
        internal bool OwnsTransport { get; }
    }

    /// <summary>
    /// Endpoint plus formatter and clock. One object so the private constructor takes one argument.
    /// </summary>
    private readonly struct WriterParts
    {
        /// <summary>
        /// Stores the remaining construction arguments.
        /// </summary>
        /// <param name="link">Group, stream, transport, and ownership.</param>
        /// <param name="formatter">Optional formatter. Null sends the plain message.</param>
        /// <param name="clock">Clock for the synchronous timeout. Checked by the writer constructor.</param>
        internal WriterParts(WriterLink link, ILogFormatter? formatter, TimeProvider clock)
        {
            Link = link;
            Formatter = formatter;
            Clock = clock;
        }

        /// <summary>Group, stream, transport, and ownership.</summary>
        internal WriterLink Link { get; }

        /// <summary>Optional formatter.</summary>
        internal ILogFormatter? Formatter { get; }

        /// <summary>Clock for the synchronous timeout.</summary>
        internal TimeProvider Clock { get; }
    }
}
