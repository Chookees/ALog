namespace ALog.Writers.Cloud;

using System.Diagnostics.CodeAnalysis;
using ALog.Core;
using ALog.Public.Interfaces;

/// <summary>
/// AWS CloudWatch Logs writer. Ships via <see cref="IAwsCloudWatchLogsTransport"/> (SDK by default).
/// </summary>
public class AwsCloudWatchWriter : ILogWriter, IFormattableLogWriter
{
    private readonly string _logGroupName;
    private readonly string _logStreamName;
    private readonly IAwsCloudWatchLogsTransport _transport;
    private readonly bool _ownsTransport;

    /// <summary>
    /// Bound for a synchronous put. The AWS SDK has no synchronous API.
    /// 100 seconds is the default <see cref="HttpClient.Timeout"/>, which the SDK uses
    /// when <c>ClientConfig.Timeout</c> is unset in legacy mode.
    /// Tests may shorten it and must restore the previous value.
    /// </summary>
    internal static TimeSpan SyncPutTimeout = TimeSpan.FromSeconds(100);

    public ILogFormatter? Formatter { get; set; }

    public AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        string region = "us-east-1",
        ILogFormatter? formatter = null,
        string? accessKey = null,
        string? secretKey = null)
        : this(
            logGroupName,
            logStreamName,
            new AwsSdkCloudWatchLogsTransport(region, accessKey, secretKey),
            ownsTransport: true,
            formatter)
    {
    }

    public AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        IAwsCloudWatchLogsTransport transport,
        ILogFormatter? formatter = null)
        : this(logGroupName, logStreamName, transport, ownsTransport: false, formatter)
    {
    }

    private AwsCloudWatchWriter(
        string logGroupName,
        string logStreamName,
        IAwsCloudWatchLogsTransport transport,
        bool ownsTransport,
        ILogFormatter? formatter)
    {
        _logGroupName = logGroupName ?? throw new ArgumentNullException(nameof(logGroupName));
        _logStreamName = logStreamName ?? throw new ArgumentNullException(nameof(logStreamName));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
        Formatter = formatter;
    }

    /// <summary>
    /// Puts one event and waits at most <see cref="SyncPutTimeout"/>.
    /// </summary>
    /// <param name="logEvent">Event to put. Null is reported and dropped.</param>
    /// <remarks>
    /// The SDK call starts on the thread pool, so the caller
    /// <see cref="SynchronizationContext"/> is not captured.
    /// A timeout or a transport failure is written to the error stream and is not thrown.
    /// </remarks>
    public void Write(LogEvent logEvent)
    {
        try
        {
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
    /// Puts one event and waits at most <see cref="SyncPutTimeout"/>.
    /// </summary>
    /// <param name="logEvent">Event to put. Not null.</param>
    /// <exception cref="TimeoutException">The put did not finish within <see cref="SyncPutTimeout"/>.</exception>
    /// <remarks>
    /// Does not catch a transport failure. <see cref="Write"/> reports that.
    /// The timeout is the AWS transport bound, not a shutdown deadline.
    /// </remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "CloudWatch put uses a 100 s transport timeout, not a shutdown or flush deadline; removed in AWS V4.")]
    private void PutWithinTransportTimeout(LogEvent logEvent)
    {
        using var timeout = new CancellationTokenSource(SyncPutTimeout);
        var pending = StartPut(logEvent, timeout.Token);
        if (!pending.Wait(SyncPutTimeout))
        {
            timeout.Cancel();
            throw new TimeoutException("CloudWatch put exceeded the 100 second transport timeout.");
        }
    }

    /// <summary>
    /// Puts one event without resuming on the caller context.
    /// </summary>
    /// <param name="logEvent">Event to put. Null is reported and the task still completes.</param>
    /// <returns>A task that completes after the put, including when the put fails.</returns>
    /// <remarks>
    /// The await uses <c>ConfigureAwait(false)</c>. Failures are written to the error stream.
    /// </remarks>
    public async Task WriteAsync(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            var described = Describe(logEvent);
            await _transport.PutLogEventAsync(
                _logGroupName,
                _logStreamName,
                described.Message,
                described.TimestampMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Starts the SDK put on the thread pool.
    /// </summary>
    /// <param name="logEvent">Event to put. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the put when the sync wait expires.</param>
    /// <returns>The put task. It does not capture the caller context.</returns>
    private Task StartPut(LogEvent logEvent, CancellationToken cancellationToken)
    {
        var described = Describe(logEvent);
        var group = _logGroupName;
        var stream = _logStreamName;
        var transport = _transport;
        return Task.Run(
            () => transport.PutLogEventAsync(
                group,
                stream,
                described.Message,
                described.TimestampMs,
                cancellationToken),
            CancellationToken.None);
    }

    /// <summary>
    /// Renders the message and the CloudWatch timestamp for one event.
    /// </summary>
    /// <param name="logEvent">Event to describe. Must not be null.</param>
    /// <returns>The message text and the Unix timestamp in milliseconds.</returns>
    private (string Message, long TimestampMs) Describe(LogEvent logEvent)
    {
        var message = Formatter?.Format(logEvent) ?? logEvent.Message;
        var timestampMs = new DateTimeOffset(logEvent.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds();
        return (message, timestampMs);
    }

    /// <summary>
    /// Writes one CloudWatch failure to the error stream. The event is dropped.
    /// </summary>
    /// <param name="ex">Failure from the transport or the sync wait. Must not be null.</param>
    private static void Report(Exception ex)
    {
        System.Console.Error.WriteLine($"[AwsCloudWatchWriter] CloudWatch write failed: {ex}");
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
