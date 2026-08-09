namespace ALog.Writers.Cloud;

/// <summary>
/// Abstraction over CloudWatch Logs PutLogEvents for shipping and tests.
/// </summary>
public interface IAwsCloudWatchLogsTransport : IDisposable
{
    Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default);
}
