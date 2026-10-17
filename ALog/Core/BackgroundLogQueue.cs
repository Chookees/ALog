namespace ALog.Core;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Bound for a synchronous enqueue and for stopping the processor.
    /// Five seconds matches the flush deadline in <see cref="FlushAsync"/>.
    /// </summary>
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(5);

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

        await _channel.Writer.WriteAsync(logEvent).ConfigureAwait(false);
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

        // BoundedChannelFullMode.Wait does not make TryWrite block. Sync callers
        // wait for capacity so events are not silently dropped. The wait is bounded.
        return WaitForCapacity(logEvent);
    }

    /// <summary>
    /// Waits until the channel accepts <paramref name="logEvent"/> or the wait expires.
    /// </summary>
    /// <param name="logEvent">Event to enqueue. Must not be null.</param>
    /// <returns>True when the event was accepted. False when the channel is closed or the wait expires.</returns>
    /// <remarks>
    /// Channel completions run on the reader, not on the caller context.
    /// The timed wait uses a wait handle, so a caller context cannot swallow the timeout.
    /// shortcut: if the write is accepted in the same instant the timeout fires, the event can still be queued after false is returned. Revisit if a caller needs an exact accept or reject.
    /// </remarks>
    private bool WaitForCapacity(LogEvent logEvent)
    {
        using var timeout = new CancellationTokenSource();
        var pending = _channel.Writer.WriteAsync(logEvent, timeout.Token).AsTask();
        try
        {
            if (!pending.Wait(BoundedWait))
            {
                timeout.Cancel();
                return false;
            }

            WakeProcessor();
            return true;
        }
        catch (AggregateException ex) when (IsQueueClosed(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether a failed enqueue is a closed channel, disposal, or the wait being cancelled.
    /// </summary>
    /// <param name="ex">Exception from the timed wait. Must not be null.</param>
    /// <returns>True when the caller should treat the enqueue as "not accepted".</returns>
    internal static bool IsQueueClosed(AggregateException ex)
    {
        var flat = ex.Flatten();
        if (flat.InnerExceptions.Count != 1)
            return false;

        return flat.InnerException is ChannelClosedException
            or ObjectDisposedException
            or OperationCanceledException;
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
                        await ProcessBatchAsync(batch).ConfigureAwait(false);
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

                    await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Woken by enqueue/flush — replace wake CTS
                    ReplaceWakeCts();
                    // Cancel resumes this method on the enqueuer thread even after ConfigureAwait(false).
                    // A task that is already complete would keep running there. This hop is never complete
                    // at the await, so the blocking write cannot sit inside TryEnqueue.
                    await new PoolHop().ConfigureAwait(false);
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
                    await ProcessBatchAsync(batch).ConfigureAwait(false);
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

    /// <summary>
    /// Awaitable that always resumes on the thread pool and never on a caller context.
    /// </summary>
    /// <remarks>
    /// <c>Task.Yield</c> cannot take <c>ConfigureAwait</c>. A completed <see cref="Task"/>
    /// would resume on the enqueue thread. <see cref="PoolHopAwaiter.IsCompleted"/> is false,
    /// so the await always queues the continuation.
    /// </remarks>
    private readonly struct PoolHop
    {
        /// <summary>
        /// Returns the awaiter used when a caller forgets <see cref="ConfigureAwait"/>.
        /// </summary>
        /// <returns>An awaiter that is never already complete.</returns>
        public PoolHopAwaiter GetAwaiter() => default;

        /// <summary>
        /// Returns the same pool hop. The flag is accepted so call sites can say
        /// <c>ConfigureAwait(false)</c>. The hop never resumes on a captured context.
        /// </summary>
        /// <param name="continueOnCapturedContext">Ignored. False is the only intended value.</param>
        /// <returns>An awaitable that resumes on the thread pool.</returns>
        public ConfiguredPoolHop ConfigureAwait(bool continueOnCapturedContext) => default;
    }

    /// <summary>
    /// The result of <see cref="PoolHop.ConfigureAwait"/>. Same hop, named so the call is visible.
    /// </summary>
    private readonly struct ConfiguredPoolHop
    {
        /// <summary>
        /// Returns the pool awaiter.
        /// </summary>
        /// <returns>An awaiter that is never already complete.</returns>
        public PoolHopAwaiter GetAwaiter() => default;
    }

    /// <summary>
    /// Schedules the continuation on the thread pool. <see cref="IsCompleted"/> is always false.
    /// </summary>
    private readonly struct PoolHopAwaiter : ICriticalNotifyCompletion
    {
        /// <summary>Always false, so the await cannot continue inline on the enqueue thread.</summary>
        public bool IsCompleted => false;

        /// <summary>
        /// Queues <paramref name="continuation"/> on the thread pool.
        /// </summary>
        /// <param name="continuation">The rest of the processor loop. Must not be null.</param>
        /// <remarks>One work item is allocated. The enqueue thread does not run it.</remarks>
        public void OnCompleted(Action continuation)
            => ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), continuation);

        /// <summary>
        /// Queues <paramref name="continuation"/> without flowing the caller execution context.
        /// </summary>
        /// <param name="continuation">The rest of the processor loop. Must not be null.</param>
        /// <remarks>
        /// The compiler prefers this method. Skipping the context flow keeps the caller
        /// <see cref="SynchronizationContext"/> off the processor.
        /// </remarks>
        public void UnsafeOnCompleted(Action continuation)
            => ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!).Invoke(), continuation);

        /// <summary>Nothing to return. The hop has no result and no failure of its own.</summary>
        public void GetResult()
        {
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
            await _dispatcher.DispatchBatchAsync(batch).ConfigureAwait(false);
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

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(BoundedWait)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            Interlocked.CompareExchange(ref _flushTcs, null, tcs);
            tcs.TrySetResult(false);
        }

        await tcs.Task.ConfigureAwait(false);
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
            _backgroundTask.Wait(BoundedWait);
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected when cancelling
        }

        _cancellationTokenSource.Dispose();
        _wakeCts.Dispose();
    }
}
