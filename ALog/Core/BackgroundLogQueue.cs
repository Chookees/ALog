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
    private int _inflight;
    private TaskCompletionSource<bool>? _flushTcs;
    private CancellationTokenSource _wakeCts = new();
    private bool _disposed;

    public BackgroundLogQueue(
        IReadOnlyList<ILogWriter> writers,
        int capacity = 1000,
        int batchSize = 10,
        TimeSpan? flushInterval = null,
        bool asyncEnabled = true)
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
        _dispatcher = new LogDispatcher(writers, asyncEnabled);
        _cancellationTokenSource = new CancellationTokenSource();

        _backgroundTask = ProcessLogsAsync(_cancellationTokenSource.Token);
    }

    public async ValueTask EnqueueAsync(LogEvent logEvent)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BackgroundLogQueue));

        await _channel.Writer.WriteAsync(logEvent);
        WakeProcessor();
    }

    public bool TryEnqueue(LogEvent logEvent)
    {
        if (_disposed)
            return false;

        // Fast path when space is available.
        if (_channel.Writer.TryWrite(logEvent))
        {
            WakeProcessor();
            return true;
        }

        // BoundedChannelFullMode.Wait does not make TryWrite block; sync callers
        // must wait for capacity so events are not silently dropped.
        try
        {
            _channel.Writer.WriteAsync(logEvent).AsTask().GetAwaiter().GetResult();
            WakeProcessor();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void WakeProcessor()
    {
        try
        {
            _wakeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore during teardown
        }
    }

    private async Task ProcessLogsAsync(CancellationToken cancellationToken)
    {
        var batch = new List<LogEvent>(_batchSize);
        using var timer = new PeriodicTimer(_flushInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (batch.Count < _batchSize && _channel.Reader.TryRead(out var logEvent))
                {
                    batch.Add(logEvent);
                }

                if (batch.Count > 0)
                {
                    Interlocked.Increment(ref _inflight);
                    try
                    {
                        await ProcessBatchAsync(batch);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inflight);
                    }

                    batch.Clear();
                    TryCompleteFlushIfIdle();
                    continue;
                }

                TryCompleteFlushIfIdle();

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _wakeCts.Token);

                    await timer.WaitForNextTickAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Woken by enqueue/flush — replace wake CTS
                    ReplaceWakeCts();
                    // Cancel() may resume this method inline on the enqueuer thread.
                    // Yield before dispatch so blocking Write cannot hang TryEnqueue/Log.Write.
                    await Task.Yield();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            while (_channel.Reader.TryRead(out var logEvent))
            {
                batch.Add(logEvent);
            }

            if (batch.Count > 0)
            {
                Interlocked.Increment(ref _inflight);
                try
                {
                    await ProcessBatchAsync(batch);
                }
                finally
                {
                    Interlocked.Decrement(ref _inflight);
                }
            }

            TryCompleteFlushIfIdle();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BackgroundLogQueue] Processing error: {ex}");
        }
    }

    private void ReplaceWakeCts()
    {
        var old = _wakeCts;
        _wakeCts = new CancellationTokenSource();
        old.Dispose();
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

    private void TryCompleteFlushIfIdle()
    {
        if (_channel.Reader.Count > 0 || Volatile.Read(ref _inflight) > 0)
            return;

        var tcs = Interlocked.Exchange(ref _flushTcs, null);
        tcs?.TrySetResult(true);
    }

    public async Task FlushAsync()
    {
        if (_disposed)
            return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _flushTcs, tcs);
        WakeProcessor();

        // Fast path: already idle
        TryCompleteFlushIfIdle();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != tcs.Task)
        {
            Interlocked.CompareExchange(ref _flushTcs, null, tcs);
            tcs.TrySetResult(false);
        }

        await tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _channel.Writer.Complete();
        WakeProcessor();
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
        _wakeCts.Dispose();
    }
}
