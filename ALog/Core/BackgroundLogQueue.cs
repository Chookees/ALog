// Background queue for UseBackgroundQueue (C4).
// One opt-in queue. A full queue drops the new event and counts it.
// Shutdown has one close point and a fixed 5 s deadline.
// Writers are disposed by LoggerEngine only after this processor has finished.
// Next file: ALog/Core/LoggerEngine.cs.

namespace ALog.Core;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ALog.Public.Interfaces;

/// <summary>
/// Accepts log events on a bounded channel and writes them on a pool thread.
/// A full queue drops the new event. Shutdown stops accepting and drains until
/// the deadline. The caller never waits without a bound.
/// </summary>
public class BackgroundLogQueue : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> may take in total. C4-02. Not configurable.</summary>
    internal static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);

    /// <summary>
    /// New writes are not started after this much of <see cref="ShutdownDeadline"/>.
    /// The rest of the deadline lets a write that already started finish.
    /// </summary>
    private static readonly TimeSpan NewWriteBudget = TimeSpan.FromMilliseconds(4900);

    /// <summary>Shortest delay before the first full-queue line, so a burst shares one count.</summary>
    private static readonly TimeSpan FirstReportDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>At most one ALOG0070 line per window. The count is everything since the previous line.</summary>
    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(60);

    /// <summary>Most events read in one pass. The channel never holds more than its capacity.</summary>
    private const int MaxRead = 100_000;

    private readonly Channel<LogEvent> _channel;
    private readonly LogDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdownSignal = new();
    private readonly Task _backgroundTask;
    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly bool _asyncEnabled;
    private readonly TimeProvider _clock;
    private readonly ITimer _reportTimer;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly ManualResetEventSlim _disposeDone = new(false);

    private CancellationTokenSource _wakeCts = new();
    private TaskCompletionSource<bool>? _flushTcs;
    private int _inflight;
    private int _outstanding;
    private int _disposeOnce;
    private int _stop;
    private int _timerArmed;
    private int _shutdownReported;
    private long _accepted;
    private long _written;
    private long _unwritten;
    private long _fullDrops;
    private long _closedDrops;
    private long _pendingFullDrops;
    private long _nextReportTicks;
    private long _disposeStartTimestamp;
    private bool _closed;
    private bool _frozen;

    /// <summary>
    /// Starts the processor. Defaults are capacity 1000, batch 10, and a 100 ms flush interval.
    /// </summary>
    /// <param name="writers">Writers that receive each event. The same instances the engine owns.</param>
    /// <param name="capacity">Most events waiting plus the one being written. Default 1000. Must be at least 1.</param>
    /// <param name="batchSize">Most events taken from the channel before the next wake. Default 10.</param>
    /// <param name="flushInterval">Idle wait between looks at the channel. Null means 100 ms.</param>
    /// <param name="asyncEnabled">False uses the synchronous writer method. True uses the async method.</param>
    /// <remarks>
    /// Does not throw for a writer failure. The processor is a pool task, so it does not keep the process alive.
    /// Safe to construct on any thread. Enqueue from other threads after the constructor returns.
    /// </remarks>
    public BackgroundLogQueue(
        IReadOnlyList<ILogWriter> writers,
        int capacity = 1000,
        int batchSize = 10,
        TimeSpan? flushInterval = null,
        bool asyncEnabled = true)
        : this(writers, new QueueSettings(
            capacity,
            batchSize,
            flushInterval ?? TimeSpan.FromMilliseconds(100),
            asyncEnabled,
            TimeProvider.System))
    {
    }

    /// <summary>
    /// Starts the processor with a clock the tests can move. Production uses <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <param name="writers">Writers that receive each event. Not null.</param>
    /// <param name="capacity">Most events accepted while writers are behind. At least 1.</param>
    /// <param name="batchSize">Most events read before the processor looks at the deadline again.</param>
    /// <param name="flushInterval">Idle wait. Not infinite; tests may pass a long interval and wake instead.</param>
    /// <param name="clock">Clock for the ALOG0070 window. Not null. Not used for the shutdown deadline.</param>
    /// <remarks>
    /// Async writes are enabled. The shutdown deadline still uses a wall clock so a frozen test clock cannot stall Dispose.
    /// </remarks>
    internal BackgroundLogQueue(
        IReadOnlyList<ILogWriter> writers,
        int capacity,
        int batchSize,
        TimeSpan flushInterval,
        TimeProvider clock)
        : this(writers, new QueueSettings(capacity, batchSize, flushInterval, true, clock))
    {
    }

    /// <summary>
    /// Stores the settings and starts the processor. Both public entry points end here.
    /// </summary>
    /// <param name="writers">Writers passed to the dispatcher. Not null.</param>
    /// <param name="settings">Capacity, batch, interval, async flag, and clock. The clock is not null.</param>
    private BackgroundLogQueue(IReadOnlyList<ILogWriter> writers, QueueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings.Clock);
        _capacity = settings.Capacity;
        _batchSize = settings.BatchSize;
        _flushInterval = settings.FlushInterval;
        _asyncEnabled = settings.AsyncEnabled;
        _clock = settings.Clock;

        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(settings.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _dispatcher = new LogDispatcher(writers, settings.AsyncEnabled);
        _reportTimer = _clock.CreateTimer(
            static state => ((BackgroundLogQueue)state!).DeliverFullDropReport(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _backgroundTask = ProcessLogsAsync();
    }

    /// <summary>Events the queue accepted before the close point. Not full drops and not closed drops.</summary>
    internal long AcceptedCount
    {
        get { lock (_gate) return _accepted; }
    }

    /// <summary>Accepted events whose writer call returned before the shutdown snapshot.</summary>
    internal long WrittenCount
    {
        get { lock (_gate) return _written; }
    }

    /// <summary>Accepted events that were still queued or in a writer call at the snapshot.</summary>
    internal long UnwrittenCount
    {
        get { lock (_gate) return _unwritten; }
    }

    /// <summary>Events dropped because the queue was already at capacity. Counted with an interlocked increment.</summary>
    internal long FullDropCount => Interlocked.Read(ref _fullDrops);

    /// <summary>Events offered after the close point. They were not queued.</summary>
    internal long ClosedDropCount => Interlocked.Read(ref _closedDrops);

    /// <summary>
    /// True when <see cref="Dispose"/> saw the processor finish, so the engine may dispose writers.
    /// False when a writer call was still running at the deadline.
    /// </summary>
    internal bool ReleaseWriters { get; private set; }

    /// <summary>
    /// Queues <paramref name="logEvent"/> or drops it. The returned task is already complete unless it was cancelled.
    /// </summary>
    /// <param name="logEvent">Event to write. A null event is passed through to the channel like any other value.</param>
    /// <returns>A completed task when the event was accepted or dropped. Never an unbounded wait.</returns>
    /// <remarks>
    /// Does not throw when the queue is closed or full. Safe to call from several threads.
    /// A cancellation token is not observed; use the internal overload when the caller has one.
    /// </remarks>
    public ValueTask EnqueueAsync(LogEvent logEvent)
        => EnqueueAsync(logEvent, CancellationToken.None);

    /// <summary>
    /// Queues <paramref name="logEvent"/>, drops it, or returns a cancelled task.
    /// A token that is already cancelled on entry counts as neither accepted nor dropped.
    /// </summary>
    /// <param name="logEvent">Event to write. Not inspected by the queue.</param>
    /// <param name="cancellationToken">Observed only at entry. A later cancel does not remove an accepted event.</param>
    /// <returns>
    /// A cancelled task when the token is already cancelled.
    /// Otherwise a completed task, including when the event was dropped.
    /// </returns>
    /// <remarks>
    /// Does not throw for a full or closed queue. Does not emit ALOG0070 for a cancelled entry.
    /// Safe to call from several threads. The task does not capture a caller context.
    /// </remarks>
    internal ValueTask EnqueueAsync(LogEvent logEvent, CancellationToken cancellationToken)
    {
        var result = Offer(logEvent, cancellationToken);
        if (result == OfferResult.Cancelled)
            return ValueTask.FromCanceled(cancellationToken);

        if (result == OfferResult.Accepted)
            WakeProcessor();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Queues <paramref name="logEvent"/> or drops it. Returns as soon as the decision is made.
    /// </summary>
    /// <param name="logEvent">Event to write. Not inspected.</param>
    /// <returns>True when the event was accepted. False when it was dropped or the queue is closed.</returns>
    /// <remarks>
    /// Does not throw. Does not wait for a writer. Safe to call from several threads.
    /// A false return is a drop, not a five-second timeout.
    /// </remarks>
    public bool TryEnqueue(LogEvent logEvent)
    {
        if (Offer(logEvent, CancellationToken.None) != OfferResult.Accepted)
            return false;

        WakeProcessor();
        return true;
    }

    /// <summary>
    /// Decides whether <paramref name="logEvent"/> is accepted, dropped, or ignored because of cancellation.
    /// The lock is the one close point: an event taken under the lock is accepted, and an event that sees the gate shut is not.
    /// </summary>
    /// <param name="logEvent">Event to place on the channel when there is room. Not null is not required.</param>
    /// <param name="cancellationToken">When already cancelled, the event is neither accepted nor dropped.</param>
    /// <returns>The decision. Accepted means the channel holds the event and the outstanding count includes it.</returns>
    /// <remarks>
    /// Does not throw. Does not call a writer. May arm the ALOG0070 timer. The lock is not held while a writer runs.
    /// </remarks>
    private OfferResult Offer(LogEvent logEvent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return OfferResult.Cancelled;

        lock (_gate)
        {
            if (_closed)
            {
                Interlocked.Increment(ref _closedDrops);
                return OfferResult.Closed;
            }

            if (_outstanding >= _capacity || !_channel.Writer.TryWrite(logEvent))
            {
                NoteFullDrop();
                return OfferResult.Full;
            }

            _outstanding++;
            _accepted++;
            return OfferResult.Accepted;
        }
    }

    /// <summary>
    /// Counts one full-queue drop and arms at most one report timer.
    /// </summary>
    /// <remarks>
    /// Called only while <see cref="_gate"/> is held, so the drop and the accept decision cannot split.
    /// The timer callback does not take <see cref="_gate"/>. The increment is interlocked so a reader outside the lock still sees it.
    /// </remarks>
    private void NoteFullDrop()
    {
        Interlocked.Increment(ref _fullDrops);
        Interlocked.Increment(ref _pendingFullDrops);
        ArmReportTimer();
    }

    /// <summary>
    /// Arms the report timer when no fire is already pending.
    /// The first line in a quiet period waits 1 ms so a burst becomes one count.
    /// A line inside an open 60 s window waits until that window ends.
    /// </summary>
    /// <remarks>Does not throw. <see cref="ITimer.Change"/> is false when shutdown already disposed the timer.</remarks>
    private void ArmReportTimer()
    {
        if (Interlocked.CompareExchange(ref _timerArmed, 1, 0) != 0)
            return;

        var due = DueForReport();
        // A false result means Dispose already disposed the timer. The count stays on the counter.
        _ = _reportTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// How long until the next ALOG0070 line may be written.
    /// </summary>
    /// <returns>1 ms when no window is open. Otherwise the time left in the 60 s window, at least 1 ms.</returns>
    /// <remarks>Does not throw. Reads the clock and the next-window stamp. No side effects.</remarks>
    private TimeSpan DueForReport()
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var next = Volatile.Read(ref _nextReportTicks);
        if (next <= now)
            return FirstReportDelay;

        var left = next - now;
        var floor = FirstReportDelay.Ticks;
        if (left < floor)
            left = floor;

        return TimeSpan.FromTicks(left);
    }

    /// <summary>
    /// Writes one ALOG0070 line for every full drop since the previous line, then opens a 60 s window.
    /// </summary>
    /// <remarks>
    /// Does not throw. Runs on the timer. A zero count writes nothing and does not move the window.
    /// Drops that arrive after the exchange arm the next window instead of a second immediate line.
    /// </remarks>
    private void DeliverFullDropReport()
    {
        var count = Interlocked.Exchange(ref _pendingFullDrops, 0);
        if (count > 0)
        {
            var now = _clock.GetUtcNow();
            Volatile.Write(ref _nextReportTicks, now.UtcTicks + ReportWindow.Ticks);
            SelfLog.ReportLine(FormatFullDrop(count));
        }

        // Drops that arrived after the exchange still see the timer as armed and do not start a second one.
        Interlocked.Exchange(ref _timerArmed, 0);
        if (Volatile.Read(ref _pendingFullDrops) > 0)
            ArmReportTimer();
    }

    /// <summary>
    /// Builds the ALOG0070 sentence. The count and the capacity use invariant digits.
    /// </summary>
    /// <param name="count">Drops since the previous line. Greater than zero.</param>
    /// <returns>The catalog sentence, with no extra prefix.</returns>
    /// <remarks>Does not throw. Allocates the line. Does not write it.</remarks>
    private string FormatFullDrop(long count)
    {
        return string.Concat(
            count.ToString(CultureInfo.InvariantCulture),
            " events dropped: the background queue is full (capacity ",
            _capacity.ToString(CultureInfo.InvariantCulture),
            "). The writers are slower than your log rate. Lower the log level or check the slowest writer.");
    }

    /// <summary>
    /// Builds the ALOG0071 sentence. The deadline in the text is the fixed 5 s.
    /// </summary>
    /// <param name="count">Accepted events that were not written when the deadline won. Greater than zero.</param>
    /// <returns>The catalog sentence, with no extra prefix.</returns>
    /// <remarks>Does not throw. Allocates the line. Called only from <see cref="Dispose"/>.</remarks>
    private static string FormatShutdown(long count)
    {
        return string.Concat(
            "Shutdown deadline of 5 s reached: ",
            count.ToString(CultureInfo.InvariantCulture),
            " queued events were not written. A writer is too slow to drain the queue in time; check the target or reduce the log volume before shutdown.");
    }

    /// <summary>
    /// Returns whether a failed enqueue is a closed channel, disposal, or a cancelled wait.
    /// Kept so callers that still see a channel failure can treat it as "not accepted".
    /// </summary>
    /// <param name="ex">Exception from a wait. Not null.</param>
    /// <returns>True when there is exactly one inner exception of a closed, disposed, or cancelled kind.</returns>
    /// <remarks>Does not throw. No side effects. Two inner exceptions return false so they are not hidden.</remarks>
    internal static bool IsQueueClosed(AggregateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var flat = ex.Flatten();
        if (flat.InnerExceptions.Count != 1)
            return false;

        return flat.InnerException is ChannelClosedException
            or ObjectDisposedException
            or OperationCanceledException;
    }

    /// <summary>
    /// Cancels the idle wait so the processor looks at the channel.
    /// The processor then hops to the pool, so this thread does not run the writer.
    /// </summary>
    /// <remarks>Does not throw. Safe to call after the wait source has been replaced or disposed.</remarks>
    private void WakeProcessor()
    {
        try
        {
            _wakeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Teardown already replaced or disposed the source. The processor is leaving or has left.
        }
    }

    /// <summary>
    /// Wakes the processor for shutdown. Cancels the idle wait and the shutdown signal together.
    /// </summary>
    /// <remarks>Does not throw. Cancel does not abandon a writer call that has already started.</remarks>
    private void SignalProcessor()
    {
        _shutdownSignal.Cancel();
        WakeProcessor();
    }

    /// <summary>
    /// Reads and writes until shutdown closes the gate and the channel is idle, or until the stop flag is set.
    /// The loop has no numeric cap because it is the life of the logger. It ends when the stop flag is set
    /// or the gate is closed and nothing is in flight.
    /// </summary>
    /// <remarks>
    /// Writer failures are handled inside the dispatcher and do not stop the loop.
    /// A bad flush interval fails the timer and ends the loop. The method runs on the pool after its first await,
    /// so it is not a foreground thread. It ends when the stop flag is set or the gate is closed and idle.
    /// </remarks>
    private async Task ProcessLogsAsync()
    {
        var batch = new List<LogEvent>(BatchCapacity());
        using var timer = new PeriodicTimer(_flushInterval);
        try
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                var count = ReadBatch(batch);
                if (count > 0)
                {
                    await WriteAcceptedAsync(batch, count).ConfigureAwait(false);
                    batch.Clear();
                    TryCompleteFlushIfIdle();
                    continue;
                }

                TryCompleteFlushIfIdle();
                if (IsClosedAndIdle())
                    break;

                await WaitForWorkAsync(timer).ConfigureAwait(false);
            }
        }
        finally
        {
            _stopped.Set();
        }
    }

    /// <summary>
    /// How many slots to reserve for one read. At least 1 and at most <see cref="MaxRead"/>.
    /// </summary>
    /// <returns>The list capacity used for a batch.</returns>
    /// <remarks>Does not throw. No side effects.</remarks>
    private int BatchCapacity()
    {
        if (_batchSize < 1)
            return 1;

        if (_batchSize > MaxRead)
            return MaxRead;

        return _batchSize;
    }

    /// <summary>
    /// Moves up to one batch from the channel into <paramref name="batch"/>.
    /// </summary>
    /// <param name="batch">Destination. Cleared by the caller before the next read. Not null.</param>
    /// <returns>How many events were added. Zero when the channel has nothing ready.</returns>
    /// <remarks>Does not throw. Does not write. The loop stops at the batch size or at <see cref="MaxRead"/>.</remarks>
    private int ReadBatch(List<LogEvent> batch)
    {
        var limit = BatchCapacity();
        var count = 0;
        while (count < limit && _channel.Reader.TryRead(out var logEvent))
        {
            batch.Add(logEvent);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Writes each accepted event until the batch ends or the shutdown budget says to stop starting writes.
    /// Events left unwritten stay in the accepted count and are reported at the deadline.
    /// </summary>
    /// <param name="batch">Events just read. Not null. Only the first <paramref name="count"/> entries are live.</param>
    /// <param name="count">How many entries to consider. Zero writes nothing. Not above <see cref="MaxRead"/>.</param>
    /// <remarks>
    /// Does not throw. A writer exception is handled inside the dispatcher.
    /// The in-flight flag keeps <see cref="FlushAsync"/> from completing during the call.
    /// </remarks>
    private async Task WriteAcceptedAsync(List<LogEvent> batch, int count)
    {
        Interlocked.Increment(ref _inflight);
        try
        {
            var limit = count;
            for (var i = 0; i < limit; i++)
            {
                if (Volatile.Read(ref _stop) != 0 || NewWritesBudgetSpent())
                    return;

                await DispatchOneAsync(batch[i]).ConfigureAwait(false);
                CommitWritten();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>
    /// True when shutdown has used the new-write budget. False before <see cref="Dispose"/> starts the clock.
    /// </summary>
    /// <returns>True when a new writer call must not start.</returns>
    /// <remarks>Does not throw. Uses the wall clock, not the report clock.</remarks>
    private bool NewWritesBudgetSpent()
    {
        var start = Volatile.Read(ref _disposeStartTimestamp);
        if (start == 0)
            return false;

        return Stopwatch.GetElapsedTime(start) >= NewWriteBudget;
    }

    /// <summary>
    /// Hands one event to every writer. A writer failure stays on the dispatcher error path.
    /// </summary>
    /// <param name="logEvent">The event. May be the default record value.</param>
    /// <remarks>Does not throw. Allocates nothing beyond what the dispatcher allocates. One event, so the deadline can be checked between events.</remarks>
    private async Task DispatchOneAsync(LogEvent logEvent)
    {
        try
        {
            if (!_asyncEnabled)
            {
                _dispatcher.Dispatch(logEvent);
                return;
            }

            await _dispatcher.DispatchAsync(logEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[BackgroundLogQueue] Batch processing error: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Records that one accepted event finished its writer call, unless the shutdown snapshot already froze the counts.
    /// </summary>
    /// <remarks>
    /// Does not throw. Takes <see cref="_gate"/>. A frozen count leaves the event inside the unwritten snapshot
    /// so a call that returns after the deadline cannot change the reported number.
    /// </remarks>
    private void CommitWritten()
    {
        lock (_gate)
        {
            if (_outstanding > 0)
                _outstanding--;

            if (!_frozen)
                _written++;
        }
    }

    /// <summary>
    /// True when shutdown has closed the gate, the channel is empty, and no writer call is running.
    /// </summary>
    /// <returns>True when the processor can leave without losing an accepted event that still has room in the deadline.</returns>
    /// <remarks>Does not throw. The gate is read under the lock. The channel count is the reader's count.</remarks>
    private bool IsClosedAndIdle()
    {
        if (!IsGateClosed())
            return false;

        if (_channel.Reader.Count > 0)
            return false;

        return Volatile.Read(ref _inflight) == 0;
    }

    /// <summary>True after <see cref="Dispose"/> has closed the gate.</summary>
    /// <returns>True when new events are no longer accepted.</returns>
    /// <remarks>Does not throw. Takes <see cref="_gate"/>.</remarks>
    private bool IsGateClosed()
    {
        lock (_gate)
            return _closed;
    }

    /// <summary>
    /// Waits for a wake, a shutdown signal, or the flush interval.
    /// A wake hops to the pool so the thread that queued the event does not run the writer.
    /// </summary>
    /// <param name="timer">Interval timer. Not null. Disposed by the caller when the processor exits.</param>
    /// <remarks>
    /// Does not throw. A cancelled wait is a wake or shutdown. The hop keeps the writer off the thread that queued the event.
    /// A tick that is not cancelled returns so the caller can read the channel.
    /// </remarks>
    private async Task WaitForWorkAsync(PeriodicTimer timer)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _wakeCts.Token,
                _shutdownSignal.Token);
            await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReplaceWakeCts();
            // The cancel can resume on the thread that queued the event. This hop is never complete,
            // so the writer call cannot run on that thread.
            await new PoolHop().ConfigureAwait(false);
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
        /// <param name="continuation">The rest of the processor loop. Not null.</param>
        /// <remarks>One work item is allocated. The enqueue thread does not run it.</remarks>
        public void OnCompleted(Action continuation)
            => ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), continuation);

        /// <summary>
        /// Queues <paramref name="continuation"/> without flowing the caller execution context.
        /// </summary>
        /// <param name="continuation">The rest of the processor loop. Not null.</param>
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

    /// <summary>
    /// Replaces the wake source so the next wait is not already cancelled.
    /// </summary>
    /// <remarks>Does not throw. The old source is disposed. Called only on the processor after a wake.</remarks>
    private void ReplaceWakeCts()
    {
        var old = _wakeCts;
        _wakeCts = new CancellationTokenSource();
        old.Dispose();
    }

    /// <summary>
    /// Completes a flush wait when the channel is empty and no writer call is running.
    /// </summary>
    /// <remarks>Does not throw. Does not emit ALOG0071. A flush that expires is completed by <see cref="FlushAsync"/> itself.</remarks>
    private void TryCompleteFlushIfIdle()
    {
        if (_channel.Reader.Count > 0 || Volatile.Read(ref _inflight) > 0)
            return;

        var tcs = Interlocked.Exchange(ref _flushTcs, null);
        tcs?.TrySetResult(true);
    }

    /// <summary>
    /// Waits until the queue is idle or until <see cref="ShutdownDeadline"/>. Does not report ALOG0071.
    /// That line belongs to <see cref="Dispose"/> only.
    /// </summary>
    /// <returns>A task that finishes when the queue is idle, the deadline passes, or the queue is already closed.</returns>
    /// <remarks>
    /// Does not throw. Does not dispose writers. A closed queue returns without waiting.
    /// The delay does not capture a caller context.
    /// </remarks>
    public async Task FlushAsync()
    {
        if (IsGateClosed())
            return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _flushTcs, tcs);
        WakeProcessor();
        TryCompleteFlushIfIdle();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(ShutdownDeadline)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, tcs.Task))
        {
            Interlocked.CompareExchange(ref _flushTcs, null, tcs);
            tcs.TrySetResult(false);
        }

        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the queue, drains until the deadline, and reports events that were still queued.
    /// A second call waits until the first call has finished and does not report again.
    /// </summary>
    /// <remarks>
    /// Does not throw. Does not dispose writers. <see cref="ReleaseWriters"/> tells the engine whether it may.
    /// The wait is at most <see cref="ShutdownDeadline"/>. Events offered after the close are counted and not written.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
        {
            // The first Dispose owns the deadline. This wait ends when that call finishes or the same bound passes.
            _ = _disposeDone.Wait(ShutdownDeadline);
            return;
        }

        try
        {
            lock (_gate)
                _closed = true;

            Volatile.Write(ref _disposeStartTimestamp, Stopwatch.GetTimestamp());
            // False means the channel was already completed. The drain still runs.
            _ = _channel.Writer.TryComplete();
            SignalProcessor();
            WaitForProcessor(NewWriteBudget);
            if (!_stopped.IsSet)
            {
                Volatile.Write(ref _stop, 1);
                SignalProcessor();
                WaitForProcessor(ShutdownDeadline);
            }

            PublishOutcome();
        }
        finally
        {
            _disposeDone.Set();
        }
    }

    /// <summary>
    /// Waits until the processor sets the stopped signal or <paramref name="budget"/> has passed since Dispose started.
    /// </summary>
    /// <param name="budget">Total time from the start of Dispose, not an extra delay on top of time already spent.</param>
    /// <remarks>Does not throw. A false wait result means the budget won. The caller reads the signal again.</remarks>
    private void WaitForProcessor(TimeSpan budget)
    {
        var start = Volatile.Read(ref _disposeStartTimestamp);
        var left = budget - Stopwatch.GetElapsedTime(start);
        if (left <= TimeSpan.Zero)
            return;

        _ = _stopped.Wait(left);
    }

    /// <summary>
    /// Freezes the written count, emits ALOG0071 once when events remain, and releases resources the processor has left.
    /// </summary>
    /// <remarks>
    /// Does not throw. Does not dispose writers. A processor that is still inside a writer call keeps its wait sources,
    /// because disposing them would race that call. ALOG0071 is not emitted for a clean drain.
    /// </remarks>
    private void PublishOutcome()
    {
        var ended = _stopped.IsSet;
        long unwritten;
        lock (_gate)
        {
            _frozen = true;
            unwritten = _accepted - _written;
            _unwritten = unwritten;
        }

        ReleaseWriters = ended;
        if (unwritten > 0 && Interlocked.Exchange(ref _shutdownReported, 1) == 0)
            SelfLog.ReportLine(FormatShutdown(unwritten));

        // Observe the processor so a fault is not an unobserved task exception. The deadline wait is the event above.
        if (_backgroundTask.IsFaulted)
            _ = _backgroundTask.Exception;

        _reportTimer.Dispose();

        if (!ended)
            return;

        DisposeQuiet(_shutdownSignal);
        DisposeQuiet(_wakeCts);
        _stopped.Dispose();
    }

    /// <summary>
    /// Disposes a source that the processor is finished with.
    /// </summary>
    /// <param name="source">The source. Not null. May already be disposed.</param>
    /// <remarks>Does not throw.</remarks>
    private static void DisposeQuiet(CancellationTokenSource source)
    {
        source.Dispose();
    }

    /// <summary>What <see cref="Offer"/> decided for one event.</summary>
    private enum OfferResult
    {
        /// <summary>The channel holds the event.</summary>
        Accepted = 0,

        /// <summary>The queue was at capacity. Counted as a full drop.</summary>
        Full = 1,

        /// <summary>The gate was already closed. Counted as a closed drop.</summary>
        Closed = 2,

        /// <summary>The token was already cancelled. Not counted.</summary>
        Cancelled = 3
    }

    /// <summary>
    /// Values both constructors share. One object so the private constructor stays within five parameters.
    /// </summary>
    private readonly struct QueueSettings
    {
        /// <summary>Creates the settings. <paramref name="clock"/> is checked by the queue constructor.</summary>
        /// <param name="capacity">Channel and outstanding limit.</param>
        /// <param name="batchSize">Read size per wake.</param>
        /// <param name="flushInterval">Idle wait.</param>
        /// <param name="asyncEnabled">False selects the synchronous writer method.</param>
        /// <param name="clock">Clock for ALOG0070. Not null when the constructor runs.</param>
        internal QueueSettings(int capacity, int batchSize, TimeSpan flushInterval, bool asyncEnabled, TimeProvider clock)
        {
            Capacity = capacity;
            BatchSize = batchSize;
            FlushInterval = flushInterval;
            AsyncEnabled = asyncEnabled;
            Clock = clock;
        }

        /// <summary>Channel capacity and the outstanding-event cap.</summary>
        internal int Capacity { get; }

        /// <summary>Most events read before the next deadline check.</summary>
        internal int BatchSize { get; }

        /// <summary>Idle wait between reads.</summary>
        internal TimeSpan FlushInterval { get; }

        /// <summary>False uses <see cref="LogDispatcher.Dispatch"/>.</summary>
        internal bool AsyncEnabled { get; }

        /// <summary>Clock for the full-queue report. The shutdown deadline does not use it.</summary>
        internal TimeProvider Clock { get; }
    }
}
