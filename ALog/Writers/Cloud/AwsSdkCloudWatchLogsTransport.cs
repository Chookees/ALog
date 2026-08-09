namespace ALog.Writers.Cloud;

using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;

/// <summary>
/// CloudWatch Logs transport backed by AWSSDK.CloudWatchLogs.
/// </summary>
public sealed class AwsSdkCloudWatchLogsTransport : IAwsCloudWatchLogsTransport
{
    private readonly IAmazonCloudWatchLogs _client;
    private readonly bool _ownsClient;
    private readonly object _sequenceLock = new();
    private string? _sequenceToken;

    public AwsSdkCloudWatchLogsTransport(
        string region = "us-east-1",
        string? accessKey = null,
        string? secretKey = null)
        : this(CreateClient(region, accessKey, secretKey), ownsClient: true)
    {
    }

    public AwsSdkCloudWatchLogsTransport(IAmazonCloudWatchLogs client, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

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

    public async Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
    {
        await EnsureLogGroupAsync(logGroupName, cancellationToken).ConfigureAwait(false);
        await EnsureLogStreamAsync(logGroupName, logStreamName, cancellationToken).ConfigureAwait(false);

        var request = new PutLogEventsRequest
        {
            LogGroupName = logGroupName,
            LogStreamName = logStreamName,
            LogEvents =
            [
                new InputLogEvent
                {
                    Message = message,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixMs).UtcDateTime
                }
            ]
        };

        lock (_sequenceLock)
        {
            request.SequenceToken = _sequenceToken;
        }

        try
        {
            var response = await _client.PutLogEventsAsync(request, cancellationToken).ConfigureAwait(false);
            lock (_sequenceLock)
            {
                _sequenceToken = response.NextSequenceToken;
            }
        }
        catch (InvalidSequenceTokenException ex)
        {
            request.SequenceToken = ex.ExpectedSequenceToken;
            var response = await _client.PutLogEventsAsync(request, cancellationToken).ConfigureAwait(false);
            lock (_sequenceLock)
            {
                _sequenceToken = response.NextSequenceToken;
            }
        }
        catch (DataAlreadyAcceptedException ex)
        {
            lock (_sequenceLock)
            {
                _sequenceToken = ex.ExpectedSequenceToken;
            }
        }
    }

    private async Task EnsureLogGroupAsync(string logGroupName, CancellationToken cancellationToken)
    {
        try
        {
            await _client.CreateLogGroupAsync(
                new CreateLogGroupRequest { LogGroupName = logGroupName },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceAlreadyExistsException)
        {
            // already present
        }
    }

    private async Task EnsureLogStreamAsync(
        string logGroupName,
        string logStreamName,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.CreateLogStreamAsync(
                new CreateLogStreamRequest
                {
                    LogGroupName = logGroupName,
                    LogStreamName = logStreamName
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceAlreadyExistsException)
        {
            // already present — refresh sequence token when possible
            var describe = await _client.DescribeLogStreamsAsync(
                new DescribeLogStreamsRequest
                {
                    LogGroupName = logGroupName,
                    LogStreamNamePrefix = logStreamName,
                    Limit = 1
                },
                cancellationToken).ConfigureAwait(false);

            var stream = describe.LogStreams?.FirstOrDefault(s => s.LogStreamName == logStreamName);
            if (stream?.UploadSequenceToken is not null)
            {
                lock (_sequenceLock)
                {
                    _sequenceToken = stream.UploadSequenceToken;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
