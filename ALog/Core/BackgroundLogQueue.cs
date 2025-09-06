namespace ALog.Core;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ALog.Public.Interfaces;

/// <summary>
/// Channel-based background log queue for asynchronous log processing
/// </summary>
public class BackgroundLogQueue : IDisposable
{
    private readonly Channel<LogEvent> _channel;
    private readonly LogDispatcher _dispatcher;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _backgroundTask;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private bool _disposed;

    public BackgroundLogQueue(
        IReadOnlyList<ILogWriter> writers,
        int capacity = 1000,
        int batchSize = 10,
        TimeSpan? flushInterval = null)
    {
        _batchSize = batchSize;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<LogEvent>(options);
        _dispatcher = new LogDispatcher(writers);
        _cancellationTokenSource = new CancellationTokenSource();
        
        _backgroundTask = ProcessLogsAsync(_cancellationTokenSource.Token);
    }

    public async ValueTask EnqueueAsync(LogEvent logEvent)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BackgroundLogQueue));

        await _channel.Writer.WriteAsync(logEvent);
    }

    public bool TryEnqueue(LogEvent logEvent)
    {
        if (_disposed)
            return false;

        return _channel.Writer.TryWrite(logEvent);
    }

    private async Task ProcessLogsAsync(CancellationToken cancellationToken)
    {
        var batch = new List<LogEvent>(_batchSize);
        var timer = new PeriodicTimer(_flushInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Collect batch of log events
                while (batch.Count < _batchSize && _channel.Reader.TryRead(out var logEvent))
                {
                    batch.Add(logEvent);
                }

                // Process batch if we have items
                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch);
                    batch.Clear();
                }

                // Wait for next flush interval or cancellation
                if (batch.Count == 0)
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            // Process remaining items
            while (_channel.Reader.TryRead(out var logEvent))
            {
                batch.Add(logEvent);
            }

            if (batch.Count > 0)
            {
                await ProcessBatchAsync(batch);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BackgroundLogQueue] Processing error: {ex}");
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task ProcessBatchAsync(List<LogEvent> batch)
    {
        try
        {
            await _dispatcher.DispatchBatchAsync(batch);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BackgroundLogQueue] Batch processing error: {ex}");
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed)
            return;

        // Wait for channel to be empty
        while (_channel.Reader.Count > 0)
        {
            await Task.Delay(10);
        }

        // Give background task time to process
        await Task.Delay(50);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _channel.Writer.Complete();
        _cancellationTokenSource.Cancel();
        
        try
        {
            _backgroundTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected when cancelling
        }

        _cancellationTokenSource.Dispose();
    }
}
