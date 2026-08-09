namespace ALog.Writers.Cloud;

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

    public void Write(LogEvent logEvent)
    {
        WriteInternal(logEvent, asyncMode: false).GetAwaiter().GetResult();
    }

    public Task WriteAsync(LogEvent logEvent) => WriteInternal(logEvent, asyncMode: true);

    private async Task WriteInternal(LogEvent logEvent, bool asyncMode)
    {
        try
        {
            var message = Formatter?.Format(logEvent) ?? logEvent.Message;
            var timestampMs = new DateTimeOffset(logEvent.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds();

            var put = _transport.PutLogEventAsync(
                _logGroupName,
                _logStreamName,
                message,
                timestampMs);

            if (asyncMode)
            {
                await put.ConfigureAwait(false);
            }
            else
            {
                put.GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[AwsCloudWatchWriter] CloudWatch write failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }
}
