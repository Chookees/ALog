// Background queue for UseBackgroundQueue (C4).
// One opt-in queue. Drop counts a full queue (ALOG0070). Block waits for room.
// Shutdown, flush, the new-write cutoff, and the full-drop report use the injected TimeProvider (ALOG0071).
// The report timer is created on that clock before the constructor returns. A drop only changes its due time.
// The static 5000 ms and 4900 ms bounds stay. The instance flush timeout is the real wait.
// Production uses ProductionClock.Default. Tests pass a clock they can move.
// The open ALOG0070 window is written at shutdown. The first event after close is ALOG0072.
// Idle writers are disposed at the deadline. A call that is still running is not.
// Next file: ALog/Core/BackgroundLogQueue.Block.cs.

namespace ALog.Core;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ALog.Config;
using ALog.Public.Interfaces;
using ALog.Writers.Console;
using ALog.Writers.File;

/// <summary>
/// Accepts log events on a bounded channel and writes them on a pool thread.
/// <see cref="QueueFullMode.Drop"/> drops a new event when the queue is full.
/// <see cref="QueueFullMode.Block"/> waits until there is room, unless the caller is the queue's own writer thread or shutdown has started.
/// </summary>
public partial class BackgroundLogQueue : IDisposable
{
    /// <summary>Default wait for <see cref="Dispose"/> and <see cref="FlushAsync"/>. 5000 ms. C4-02. The instance flush timeout is the real wait.</summary>
    internal static readonly TimeSpan ShutdownDeadline = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// Default new-write cutoff. 4900 ms of <see cref="ShutdownDeadline"/>.
    /// The last 100 ms lets a call that already started finish. An instance timeout derives its own cutoff.
    /// </summary>
    internal static readonly TimeSpan NewWriteBudget = TimeSpan.FromMilliseconds(4900);

    /// <summary>Shortest delay before the first full-queue line, so a burst shares one count.</summary>
    private static readonly TimeSpan FirstReportDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>At most one ALOG0070 line per window. The count is everything since the previous line. Shutdown flushes the open window early.</summary>
    private static readonly TimeSpan ReportWindow = TimeSpan.FromSeconds(60);

    /// <summary>First event after the close point. Later closed drops are counted and not reported. ALOG0072.</summary>
    private const string ClosedDropLine =
        "Event dropped: ALog is shut down (the background queue was closed by Shutdown or Dispose). Later events are counted, not reported. Stop logging before you call Shutdown or Dispose.";

    /// <summary>Most events read in one pass. The channel never holds more than its capacity.</summary>
    private const int MaxRead = 100_000;

    private readonly ConcurrentQueue<LogEvent> _queue = new();
    private readonly LogDispatcher _dispatcher;
    private readonly CancellationTokenSource _shutdownSignal = new();
    private readonly Task _backgroundTask;
    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly bool _directDispatch;
    private readonly TimeProvider _clock;
    private ITimer? _reportTimer;
    private readonly object _gate = new();
    private readonly object _reportGate = new();
    private readonly WriterGate _writerGate;
    private readonly ManualResetEventSlim _stopped = new(false);

    private CancellationTokenSource _wakeCts = new();
    private int _idle;
    private int _closedFast;
    private TaskCompletionSource<bool>? _flushTcs;
    private int _inflight;
    private int _outstanding;
    private int _disposeOnce;
    private int _stop;
    private int _timerArmed;
    private int _shutdownReported;
    private int _closedReported;
    private int _processorEntered;
    private int _idleWaitEntered;
    private int _disposeWaitsStarted;
    private static int _deadlineWaitEntries;
    private int _flushWaitsStarted;
    private int _shutdownDeadlineElapsed;
    private int _flushDeadlineElapsed;
    private int _cutoffElapsed;
    private int _cutoffReached;
    private long _accepted;
    private long _written;
    private long _unwritten;
    private long _fullDrops;
    private long _closedDrops;
    private long _pendingFullDrops;
    private long _nextReportTicks;
    /// <summary>Provider timestamp taken when <see cref="Dispose"/> closes the gate. Zero before that.</summary>
    private long _disposeStartTimestamp;
    private bool _closed;
    private bool _frozen;
    private bool _reportSealed;

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
            ProductionClock.Default))
    {
    }

    /// <summary>
    /// Starts the processor with a clock the tests can move. Production uses <see cref="ProductionClock.Default"/>.
    /// </summary>
    /// <param name="writers">Writers that receive each event. Not null.</param>
    /// <param name="capacity">Most events accepted while writers are behind. At least 1.</param>
    /// <param name="batchSize">Most events read before the processor looks at the deadline again.</param>
    /// <param name="flushInterval">Idle wait. Not infinite; tests may pass a long interval and wake instead.</param>
    /// <param name="clock">Clock for shutdown, flush, the new-write cutoff, and the ALOG0070 window. Not null.</param>
    /// <remarks>
    /// Async writes are enabled. A frozen test clock does not move these waits until the test advances it.
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
    /// Stores the settings and starts the processor. The public constructor and the test constructor end here.
    /// </summary>
    /// <param name="writers">Writers passed to the dispatcher. Not null.</param>
    /// <param name="settings">Capacity, batch, interval, async flag, and clock. The clock is not null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> has a null clock.</exception>
    /// <remarks>
    /// The processor starts before this returns, so the clock cannot be changed afterwards.
    /// Does not throw for a writer failure. A flush interval the timer rejects still leaves the
    /// stopped signal unset, so <see cref="Dispose"/> waits out <see cref="ShutdownDeadline"/>.
    /// Every event is queued. The processor writes it. A file or console writer is called
    /// synchronously on that processor thread, which does not capture a caller context.
    /// The full-drop report timer is created on this clock before the method returns.
    /// Its due time and period are infinite, so the callback does not run until a full drop changes the due time.
    /// </remarks>
    internal BackgroundLogQueue(IReadOnlyList<ILogWriter> writers, QueueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings.Clock);
        _capacity = settings.Capacity;
        _batchSize = settings.BatchSize;
        _flushInterval = settings.FlushInterval;
        _clock = settings.Clock;
        _fullMode = LogOptionRules.RequireMode(settings.FullMode);
        _shutdownDeadline = settings.FlushTimeout;
        LogOptionRules.RequireDuration(_shutdownDeadline, LogOptionRules.PathOf("FlushTimeout"));
        _newWriteBudget = LogOptionRules.DeriveCutoff(_shutdownDeadline);

        var writerList = CopyWriters(writers);
        // File and console writers return inside Write, so the processor calls Write and skips the async state machine.
        // Async off is not enough: a writer that waits must stay on the processor or the log call never returns.
        _directDispatch = WritersCompleteOnCaller(writerList);
        _writerGate = new WriterGate(writerList);
        _dispatcher = new LogDispatcher(writerList, settings.AsyncEnabled, _writerGate);
        // Before any enqueue. A full drop only changes the due time; it must not be the first CreateTimer.
        // Infinite due and period: the callback does not run until ArmReportTimer calls Change.
        _reportTimer = _clock.CreateTimer(
            static state => ((BackgroundLogQueue)state!).DeliverFullDropReport(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        // Dedicated thread: the idle wait must not occupy a pool thread, or a frozen test clock
        // cannot deliver its timer callback. LongRunning is a background thread.
        _backgroundTask = Task.Factory.StartNew(
            ProcessLogs,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Events the queue accepted before the close point. Not full drops and not closed drops.</summary>
    internal long AcceptedCount
    {
        get { return Interlocked.Read(ref _accepted); }
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

    /// <summary>Writer calls started and not yet finished. Zero when the processor is not inside a writer.</summary>
    internal int InFlightCount => Volatile.Read(ref _inflight);

    /// <summary>True after the processor method has been entered. False only before that first line.</summary>
    internal bool ProcessorHasStarted => Volatile.Read(ref _processorEntered) != 0;

    /// <summary>True after the processor has entered the idle timer wait at least once.</summary>
    internal bool IsIdleWaitEntered => Volatile.Read(ref _idleWaitEntered) != 0;

    /// <summary>The source cancelled to wake the processor. A wake replaces this instance.</summary>
    internal CancellationTokenSource WakeTokenSource => _wakeCts;

    /// <summary>How many shutdown waits have been registered on the clock. Zero before <see cref="Dispose"/> waits.</summary>
    internal int DisposeWaitsStarted => Volatile.Read(ref _disposeWaitsStarted);

    /// <summary>
    /// How many times <see cref="WaitUntilSignaledOrDeadline"/> has been entered, on any clock.
    /// </summary>
    /// <remarks>
    /// The count moves when the helper is called, including a call that passes <see cref="TimeProvider.System"/>.
    /// A second <see cref="Dispose"/> does not call the helper, so the count stays where the first call left it.
    /// Does not throw. The value does not go backwards.
    /// </remarks>
    internal static int DeadlineWaitEntries => Volatile.Read(ref _deadlineWaitEntries);

    /// <summary>How many flush deadlines have been registered on the clock.</summary>
    internal int FlushWaitsStarted => Volatile.Read(ref _flushWaitsStarted);

    /// <summary>How many times the shutdown deadline timer has fired. Zero until that callback runs.</summary>
    internal int ShutdownDeadlineElapsed => Volatile.Read(ref _shutdownDeadlineElapsed);

    /// <summary>How many times the flush deadline timer has fired. Zero until that callback runs.</summary>
    internal int FlushDeadlineElapsed => Volatile.Read(ref _flushDeadlineElapsed);

    /// <summary>How many times the new-write cutoff timer has fired. Zero until that callback runs.</summary>
    internal int CutoffElapsed => Volatile.Read(ref _cutoffElapsed);

    /// <summary>
    /// True when elapsed time since <see cref="Dispose"/> is at least <see cref="NewWriteBudget"/>.
    /// False before dispose records a timestamp, and false one tick before the budget.
    /// The cutoff timer is a separate signal.
    /// </summary>
    internal bool NewWriteBudgetComparisonSpent
    {
        get
        {
            var start = Volatile.Read(ref _disposeStartTimestamp);
            if (start == 0)
                return false;

            return ElapsedPastNewWriteBudget(start);
        }
    }

    /// <summary>
    /// True when a new writer call must not start.
    /// False before shutdown reaches <see cref="NewWriteBudget"/>.
    /// </summary>
    internal bool NewWriterCallsBlocked => NewWritesBudgetSpent() || _writerGate.DeadlineReached;

    /// <summary>
    /// Queues <paramref name="logEvent"/> or drops it. The returned task is already complete unless it was cancelled.
    /// </summary>
    /// <param name="logEvent">Event to write. A null event is passed through to the channel like any other value.</param>
    /// <returns>A completed task when the event was accepted or dropped. <see cref="QueueFullMode.Block"/> waits inside this call, then completes the task.</returns>
    /// <remarks>
    /// Does not throw when the queue is closed or full. Safe to call from several threads.
    /// A cancellation token is not observed; use the internal overload when the caller has one.
    /// Drop returns as soon as the queue is full. Block waits while the queue is open.
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
    /// <see cref="QueueFullMode.Block"/> waits before that task is returned.
    /// </returns>
    /// <remarks>
    /// Does not throw for a full or closed queue. Does not emit ALOG0070 for a cancelled entry.
    /// Safe to call from several threads. The task does not capture a caller context.
    /// The event is queued. The processor writes it. This call does not write on the caller,
    /// so a dispose that races this call cannot treat an in-progress write as an empty queue.
    /// </remarks>
    internal ValueTask EnqueueAsync(LogEvent logEvent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        if (TryFastAccept(logEvent))
        {
            WakeProcessor();
            return ValueTask.CompletedTask;
        }

        var result = Offer(logEvent, cancellationToken);
        if (result == OfferResult.Cancelled)
            return ValueTask.FromCanceled(cancellationToken);

        if (result == OfferResult.Accepted)
            WakeProcessor();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Queues <paramref name="logEvent"/> or drops it.
    /// <see cref="QueueFullMode.Drop"/> returns as soon as the decision is made.
    /// <see cref="QueueFullMode.Block"/> waits until the queue has room, unless this thread is the queue writer or shutdown has started.
    /// </summary>
    /// <param name="logEvent">Event to write. Not inspected.</param>
    /// <returns>True when the event was accepted. False when it was dropped or the queue is closed.</returns>
    /// <remarks>
    /// Does not throw. Safe to call from several threads.
    /// A false return is a drop. Drop mode does not wait. Block mode can wait while the queue is open.
    /// The event is queued and the processor writes it. This call does not write on the caller.
    /// <see cref="QueueFullMode.Block"/> still waits only while the queue is open and this thread is not the processor.
    /// </remarks>
    public bool TryEnqueue(LogEvent logEvent)
    {
        if (TryFastAccept(logEvent) || Offer(logEvent, CancellationToken.None) == OfferResult.Accepted)
        {
            WakeProcessor();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Accepts <paramref name="logEvent"/> without the gate lock when the queue is open, not full, and nobody is waiting.
    /// </summary>
    /// <param name="logEvent">Event to place on the channel.</param>
    /// <returns>True when the channel holds the event. False when <see cref="Offer"/> must decide.</returns>
    /// <remarks>
    /// Does not throw. Does not wait. A closed queue returns false so the locked path can count the drop.
    /// The outstanding count moves before the close flag is read, so dispose cannot miss an accept that has started.
    /// Block waiters stay on the locked path, so a waiting caller is not skipped by this one.
    /// </remarks>
    private bool TryFastAccept(LogEvent logEvent)
    {
        // The increment is first so Dispose can see an accept that has started
        // and so an accept that has not started observes the close flag.
        var next = Interlocked.Increment(ref _outstanding);
        if (Volatile.Read(ref _closedFast) != 0 || Volatile.Read(ref _registeredCount) != 0)
        {
            Interlocked.Decrement(ref _outstanding);
            return false;
        }

        if (next > _capacity)
        {
            Interlocked.Decrement(ref _outstanding);
            return false;
        }

        _queue.Enqueue(logEvent);
        Interlocked.Increment(ref _accepted);
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
    /// Does not throw. Does not call a writer. May arm the ALOG0070 timer. The first event that sees the gate shut
    /// reports ALOG0072 after the lock is released; later closed drops are only counted. The lock is not held while a writer runs.
    /// </remarks>
    private OfferResult Offer(LogEvent logEvent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return OfferResult.Cancelled;

        BlockWaiter? waiter = null;
        for (var attempt = 0; attempt < MaxBlockAttempts; attempt++)
        {
            OfferResult result;
            var firstClosed = false;
            lock (_gate)
            {
                result = waiter is null
                    ? DecideUnderGate(logEvent, ref firstClosed)
                    : OfferResult.NeedWait;

                if (result == OfferResult.NeedWait)
                    result = PrepareWait(logEvent, ref waiter, ref firstClosed);
            }

            if (firstClosed)
                SelfLog.ReportLine(ClosedDropLine);

            if (result != OfferResult.NeedWait)
                return result;

            Interlocked.Increment(ref _blockWaitsStarted);
            WaitHandle.WaitAny(new WaitHandle[] { waiter!.Signal.WaitHandle });
        }

        var abandoned = false;
        lock (_gate)
        {
            if (waiter is not null)
                Unregister(waiter);

            NoteClosed(ref abandoned);
        }

        if (abandoned)
            SelfLog.ReportLine(ClosedDropLine);

        return OfferResult.Closed;
    }

    /// <summary>
    /// Accepts, drops, or closes <paramref name="logEvent"/> while <see cref="_gate"/> is held.
    /// </summary>
    /// <param name="logEvent">Event to place on the channel when there is room.</param>
    /// <param name="firstClosed">Set when this call is the first closed drop for this queue. The caller reports ALOG0072 after releasing the lock.</param>
    /// <returns>The decision. Closed and full do not queue the event.</returns>
    /// <remarks>
    /// Does not throw and does not call SelfLog. The lock is already held. Reporting waits so a SelfLog target can enqueue without taking this lock twice.
    /// </remarks>
    private OfferResult DecideUnderGate(LogEvent logEvent, ref bool firstClosed)
    {
        if (_closed)
        {
            NoteClosed(ref firstClosed);
            return OfferResult.Closed;
        }

        var next = Interlocked.Increment(ref _outstanding);
        if (next > _capacity)
        {
            Interlocked.Decrement(ref _outstanding);
            if (_fullMode == QueueFullMode.Block && !OnConsumer())
                return OfferResult.NeedWait;

            NoteFullDrop();
            return OfferResult.Full;
        }

        _queue.Enqueue(logEvent);

        Interlocked.Increment(ref _accepted);
        return OfferResult.Accepted;
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
    /// The constructor already created the timer on the injected clock with an infinite due time and period.
    /// This method changes the due time. If the field is empty and dispose has not started, it creates that same timer first.
    /// The first line in a quiet period waits 1 ms so a burst becomes one count.
    /// A line inside an open 60 s window waits until that window ends.
    /// </summary>
    /// <remarks>Does not throw. <see cref="ITimer.Change"/> is false when shutdown already disposed the timer.</remarks>
    private void ArmReportTimer()
    {
        if (Interlocked.CompareExchange(ref _timerArmed, 1, 0) != 0)
            return;

        var due = DueForReport();
        var timer = _reportTimer;
        if (timer is null)
        {
            if (Volatile.Read(ref _disposeOnce) != 0)
                return;
            timer = _clock.CreateTimer(
                static state => ((BackgroundLogQueue)state!).DeliverFullDropReport(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _reportTimer = timer;
        }

        // A false result means Dispose already disposed the timer. The count stays on the counter.
        _ = timer.Change(due, Timeout.InfiniteTimeSpan);
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
        lock (_reportGate)
        {
            if (_reportSealed)
            {
                // Shutdown owns the remainder. A late callback must not write a second line or a line after ALOG0071.
                Interlocked.Exchange(ref _timerArmed, 0);
                return;
            }

            var count = Interlocked.Exchange(ref _pendingFullDrops, 0);
            if (count > 0)
            {
                var now = _clock.GetUtcNow();
                Volatile.Write(ref _nextReportTicks, now.UtcTicks + ReportWindow.Ticks);
                // Under the lock so Dispose cannot emit ALOG0071 between this line and the seal.
                // The target must not call Dispose; a drop re-enters Offer, which does not take this lock.
                SelfLog.ReportLine(FormatFullDrop(count));
            }

            // Drops that arrived after the exchange still see the timer as armed and do not start a second one.
            Interlocked.Exchange(ref _timerArmed, 0);
            if (Volatile.Read(ref _pendingFullDrops) > 0)
                ArmReportTimer();
        }
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
    /// Builds the ALOG0071 sentence. Exactly five seconds stays <c>5 s</c>. Other whole seconds use the same shape. Anything else uses the invariant constant format.
    /// </summary>
    /// <param name="count">Accepted events that were not written when the deadline won. Greater than zero.</param>
    /// <returns>The catalog sentence, with no extra prefix.</returns>
    /// <remarks>Does not throw. Allocates the line. Called only from <see cref="Dispose"/>.</remarks>
    private string FormatShutdown(long count)
    {
        return string.Concat(
            "Shutdown deadline of ",
            DeadlineText(_shutdownDeadline),
            " reached: ",
            count.ToString(CultureInfo.InvariantCulture),
            " queued events were not written. A writer is too slow to drain the queue in time; check the target or reduce the log volume before shutdown.");
    }

    /// <summary>
    /// The duration text inside ALOG0071. Five seconds is <c>5 s</c>.
    /// </summary>
    /// <param name="timeout">The flush timeout. Negative values use the invariant constant format.</param>
    /// <returns><c>5 s</c> for exactly five seconds, <c>{n} s</c> for any other non-negative whole number of seconds, otherwise the invariant <c>c</c> format.</returns>
    /// <remarks>Does not throw. Does not read a clock.</remarks>
    internal static string DeadlineText(TimeSpan timeout)
    {
        if (timeout == TimeSpan.FromSeconds(5))
            return "5 s";

        if (timeout >= TimeSpan.Zero && timeout.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            var seconds = timeout.Ticks / TimeSpan.TicksPerSecond;
            return seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }

        return timeout.ToString("c", CultureInfo.InvariantCulture);
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
        if (Volatile.Read(ref _idle) == 0)
            return;

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
        try
        {
            _shutdownSignal.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // PublishOutcome already disposed the source. The processor has left or is leaving.
        }

        WakeProcessor();
    }

    /// <summary>
    /// Reads and writes until shutdown closes the gate and the channel is idle, the stop flag is set,
    /// or the deadline flag says no further writer call may start.
    /// The loop has no numeric cap because it is the life of the logger. It ends on one of those three conditions.
    /// </summary>
    /// <remarks>
    /// Writer failures are handled inside the dispatcher and do not stop the loop.
    /// A bad flush interval throws before the loop, so the stopped signal stays unset and
    /// <see cref="Dispose"/> still waits out the deadline. The method runs on a background thread,
    /// so it does not keep the process alive. It ends when the stop flag is set, the deadline flag is set, or the gate is closed and idle.
    /// </remarks>
    private void ProcessLogs()
    {
        Volatile.Write(ref _processorEntered, 1);
        var batch = new List<LogEvent>(ReadLimit());
        // Constructed before the loop so a rejected interval never sets the stopped signal.
        using var timer = new PeriodicTimer(_flushInterval, _clock);
        try
        {
            while (Volatile.Read(ref _stop) == 0 && !_writerGate.DeadlineReached)
            {
                var count = ReadBatch(batch);
                if (count > 0)
                {
                    WriteAccepted(batch, count);
                    batch.Clear();
                    TryCompleteFlushIfIdle();
                    continue;
                }

                TryCompleteFlushIfIdle();
                if (IsClosedAndIdle())
                    break;

                WaitForWork(timer);
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
    /// <summary>
    /// How many events to take before the next wait. File and console writers take a larger chunk so a full queue wakes once per chunk.
    /// </summary>
    /// <returns>At least one. At most <see cref="MaxRead"/>.</returns>
    /// <remarks>Does not throw. The configured batch size still applies to writers that do real async I/O.</remarks>
    private int ReadLimit()
    {
        var limit = BatchCapacity();
        if (_directDispatch && limit < 512)
            limit = 512;
        if (limit > MaxRead)
            return MaxRead;
        return limit;
    }

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
        var limit = ReadLimit();
        var count = 0;
        while (count < limit && _queue.TryDequeue(out var logEvent))
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
    private void WriteAccepted(List<LogEvent> batch, int count)
    {
        Interlocked.Increment(ref _inflight);
        var committed = 0;
        try
        {
            var limit = count;
            for (var i = 0; i < limit; i++)
            {
                if (Volatile.Read(ref _stop) != 0 || NewWritesBudgetSpent() || _writerGate.DeadlineReached)
                    return;

                var completed = DispatchOne(batch[i]);
                if (!completed)
                    return;

                committed++;
            }
        }
        finally
        {
            if (committed > 0)
                CommitBatch(committed);
            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>
    /// True when shutdown has used the new-write budget. False before <see cref="Dispose"/> starts the clock.
    /// </summary>
    /// <returns>True when a new writer call must not start.</returns>
    /// <remarks>
    /// Does not throw. Uses the injected clock. The cutoff timer sets the same fact at
    /// <see cref="NewWriteBudget"/> so a blocked call observes it when it next checks.
    /// </remarks>
    private bool NewWritesBudgetSpent()
    {
        if (Volatile.Read(ref _cutoffReached) != 0)
            return true;

        return NewWriteBudgetComparisonSpent;
    }

    /// <summary>
    /// True when <paramref name="start"/> is at least <see cref="NewWriteBudget"/> ago on the injected clock.
    /// </summary>
    /// <param name="start">Provider timestamp taken when dispose closed the gate. Not zero.</param>
    /// <returns>True at the budget and after it. False one tick before it.</returns>
    /// <remarks>
    /// Does not throw. This is the only elapsed comparison. The cutoff timer does not change the result.
    /// </remarks>
    private bool ElapsedPastNewWriteBudget(long start)
        => _clock.GetElapsedTime(start) >= _newWriteBudget;

    /// <summary>
    /// Hands one event to every writer. A writer failure stays on the dispatcher error path.
    /// </summary>
    /// <param name="logEvent">The event. May be the default record value.</param>
    /// <returns>False when the deadline skipped a writer. That event stays unwritten and is part of ALOG0071.</returns>
    /// <remarks>Does not throw. Allocates nothing beyond what the dispatcher allocates. One event, so the deadline can be checked between events.</remarks>
    private bool DispatchOne(LogEvent logEvent)
    {
        var previousQueue = ConsumerQueue.Value;
        var previousThread = Volatile.Read(ref _consumerThread);
        ConsumerQueue.Value = this;
        Volatile.Write(ref _consumerThread, Environment.CurrentManagedThreadId);
        try
        {
            _writerGate.PrepareEvent();
            try
            {
                if (_directDispatch)
                    _dispatcher.Dispatch(logEvent);
                else
                {
                    var task = _dispatcher.DispatchAsync(logEvent);
                    if (!task.IsCompleted)
                        task.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[BackgroundLogQueue] Batch processing error: " + ex.GetType().Name);
            }

            return !_writerGate.EventSkipped;
        }
        finally
        {
            ConsumerQueue.Value = previousQueue;
            Volatile.Write(ref _consumerThread, previousThread);
        }
    }

    /// <summary>
    /// Records that one accepted event finished its writer call, unless the shutdown snapshot already froze the counts.
    /// </summary>
    /// <remarks>
    /// Does not throw. Takes <see cref="_gate"/>. A frozen count leaves the event inside the unwritten snapshot
    /// so a call that returns after the deadline cannot change the reported number.
    /// </remarks>
    /// <summary>
    /// Records <paramref name="written"/> finished events and wakes blocked callers once.
    /// </summary>
    /// <param name="written">Events whose writer call returned. Greater than zero.</param>
    /// <remarks>
    /// Does not throw. One signal per batch, not per event, so a full queue does not wake every caller for every line.
    /// The snapshot lock still decides whether these events count as written.
    /// </remarks>
    private void CommitBatch(int written)
    {
        Interlocked.Add(ref _outstanding, -written);
        lock (_gate)
        {
            if (!_frozen)
                _written += written;
            if (_registeredCount > 0)
                SignalWaiters();
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

        if (!_queue.IsEmpty)
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
    /// Blocks this processor thread until a wake, a shutdown signal, or the flush interval.
    /// The wait stays on this thread, so a cancel from the logging thread does not run the writer there.
    /// </summary>
    /// <param name="timer">Interval timer. Not null. Disposed by the caller when the processor exits.</param>
    /// <remarks>
    /// Does not throw. A cancelled wait is a wake or shutdown. The clock behind <paramref name="timer"/>
    /// is the queue clock, so a frozen test clock does not release this wait until the test advances it.
    /// If the channel already has an event, the method returns without waiting.
    /// </remarks>
    private void WaitForWork(PeriodicTimer timer)
    {
        Volatile.Write(ref _idleWaitEntered, 1);
        Volatile.Write(ref _idle, 1);
        try
        {
            if (!_queue.IsEmpty || Volatile.Read(ref _stop) != 0)
                return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _wakeCts.Token,
                _shutdownSignal.Token);
            try
            {
                timer.WaitForNextTickAsync(linked.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                ReplaceWakeCts();
            }
        }
        finally
        {
            Volatile.Write(ref _idle, 0);
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
        if (!_queue.IsEmpty || Volatile.Read(ref _inflight) > 0)
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
    /// The deadline timer uses the injected clock and does not capture a caller context.
    /// A queue that is already idle returns a completed task and does not arm that timer.
    /// That return does not include an event whose accept has not happened yet.
    /// </remarks>
    public Task FlushAsync()
    {
        if (_shutdownDeadline > TimeSpan.Zero
            && _queue.IsEmpty
            && Volatile.Read(ref _inflight) == 0
            && Volatile.Read(ref _outstanding) == 0
            && !IsGateClosed())
            return Task.CompletedTask;

        return FlushAsyncCore();
    }

    /// <summary>The flush used when the channel is not already idle. Keeps the deadline timer off the idle path.</summary>
    /// <returns>A task that finishes when the queue is idle, the deadline passes, or the queue is already closed.</returns>
    /// <remarks>Does not throw. An idle call never enters this method, so a cold process does not compile it.</remarks>
    private Task FlushAsyncCore()
    {
        if (IsGateClosed())
            return Task.CompletedTask;

        if (_shutdownDeadline <= TimeSpan.Zero)
        {
            TryCompleteFlushIfIdle();
            return Task.CompletedTask;
        }

        var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _flushTcs, idle);
        WakeProcessor();
        TryCompleteFlushIfIdle();
        if (idle.Task.IsCompleted)
            return Task.CompletedTask;

        return WaitForFlushAsync(idle);
    }

    /// <summary>
    /// Waits for <paramref name="idle"/> or the flush deadline. Called only when the queue was not idle at the check.
    /// </summary>
    /// <param name="idle">The task the processor completes. Not null. Already published in <see cref="_flushTcs"/>.</param>
    /// <returns>A task that finishes when the queue is idle or the deadline passes.</returns>
    /// <remarks>
    /// Does not throw. Arms one deadline timer on the injected clock. An idle queue never enters this method,
    /// so a cold process does not compile the timer wait. The deadline still releases a blocked writer.
    /// </remarks>
    private async Task WaitForFlushAsync(TaskCompletionSource<bool> idle)
    {
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The timer keeps a one-tick remainder. A millisecond-truncated delay would fire that tick early.
        using var timer = _clock.CreateTimer(
            _ => CompleteFlushDeadline(deadline),
            null,
            _shutdownDeadline,
            Timeout.InfiniteTimeSpan);
        Interlocked.Increment(ref _flushWaitsStarted);

        var completed = await Task.WhenAny(idle.Task, deadline.Task).ConfigureAwait(false);
        if (!ReferenceEquals(completed, idle.Task))
        {
            Interlocked.CompareExchange(ref _flushTcs, null, idle);
            idle.TrySetResult(false);
        }

        await idle.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Records the flush deadline, then releases the flush wait.
    /// </summary>
    /// <param name="signal">The deadline task. Not null. Completed here.</param>
    /// <remarks>
    /// Does not throw. Called on the clock callback before the flush continuation runs.
    /// The count is visible to the thread that advanced the clock before this callback returns.
    /// </remarks>
    private void CompleteFlushDeadline(TaskCompletionSource<bool> signal)
    {
        Interlocked.Increment(ref _flushDeadlineElapsed);
        signal.TrySetResult(true);
    }

    /// <summary>
    /// Closes the queue, drains until the deadline, and reports events that were still queued.
    /// A second call returns immediately and does not report again.
    /// </summary>
    /// <remarks>
    /// Does not throw. Disposes writers that have no call running. A call still running at the deadline is left for
    /// <see cref="WriterGate.EndWriterCall"/>, which disposes that writer only after the call returns.
    /// The first call waits at most <see cref="ShutdownDeadline"/> on the injected clock. A second call does not wait
    /// and does not emit another ALOG0071. Events offered after the close are counted, and the first one reports ALOG0072.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

        bool idle;
        lock (_gate)
        {
            _closed = true;
            // Published before the outstanding read. An accept that already incremented is visible.
            // An accept that has not incremented observes this flag and does not queue.
            Volatile.Write(ref _closedFast, 1);
            idle = ReferenceEquals(_clock, ProductionClock.Default)
                && _queue.IsEmpty
                && Volatile.Read(ref _inflight) == 0
                && Volatile.Read(ref _outstanding) == 0;
        }

        Volatile.Write(ref _disposeStartTimestamp, _clock.GetTimestamp());
        // Cancelling the idle wait runs the processor callback on this thread.
        // That costs several milliseconds in a cold process, and an empty queue has nothing to drain.
        if (!idle)
        {
            SignalProcessor();
            DrainUntilDeadline();
        }

        PublishOutcome();
    }

    /// <summary>
    /// Waits until the processor stops or <see cref="ShutdownDeadline"/> elapses on the clock.
    /// At <see cref="NewWriteBudget"/> the cutoff timer stops new writer calls.
    /// </summary>
    /// <remarks>
    /// Does not throw. A processor that already stopped returns without registering a timer.
    /// Both timers are disposed before this returns so a late callback cannot touch a published outcome.
    /// </remarks>
    private void DrainUntilDeadline()
    {
        if (_shutdownDeadline <= TimeSpan.Zero)
        {
            OnNewWriteCutoff(this);
            MarkShutdownDeadline();
            return;
        }

        if (_stopped.IsSet)
            return;

        if (_newWriteBudget <= TimeSpan.Zero)
        {
            OnNewWriteCutoff(this);
            WaitUntilSignaledOrDeadline(_clock, _stopped, MarkShutdownDeadline, _shutdownDeadline, ref _disposeWaitsStarted);
            return;
        }

        // Both timers are registered before the wait is visible, so one clock step can hit either bound.
        using var cutoff = _clock.CreateTimer(
            static state => OnNewWriteCutoff(state),
            this,
            _newWriteBudget,
            Timeout.InfiniteTimeSpan);
        WaitUntilSignaledOrDeadline(_clock, _stopped, MarkShutdownDeadline, _shutdownDeadline, ref _disposeWaitsStarted);
    }

    /// <summary>
    /// Marks the new-write cutoff, stops the processor from starting another call, and wakes it.
    /// </summary>
    /// <remarks>
    /// Does not throw. A second fire returns immediately. Safe from the clock callback and from the processor.
    /// </remarks>
    private void ApplyNewWriteCutoff()
    {
        if (Interlocked.Exchange(ref _cutoffReached, 1) != 0)
            return;

        _writerGate.MarkDeadline();
        Volatile.Write(ref _stop, 1);
        SignalProcessor();
    }

    /// <summary>
    /// Runs <see cref="ApplyNewWriteCutoff"/> for the cutoff timer.
    /// </summary>
    /// <param name="state">The queue. Not null.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not a queue.</exception>
    /// <remarks>Does not throw for a second fire. Called on the clock callback.</remarks>
    private static void OnNewWriteCutoff(object? state)
    {
        if (state is not BackgroundLogQueue queue)
            throw new ArgumentException("Cutoff timer is missing the queue.", nameof(state));

        Interlocked.Increment(ref queue._cutoffElapsed);
        queue.ApplyNewWriteCutoff();
    }

    /// <summary>
    /// Records that the shutdown deadline timer fired.
    /// </summary>
    /// <remarks>
    /// Does not throw. Called on the clock callback before the waiter is released.
    /// The count is visible to the thread that advanced the clock before this callback returns.
    /// </remarks>
    private void MarkShutdownDeadline()
    {
        lock (_gate)
        {
            _blockRelease = true;
            SignalWaiters();
        }

        Interlocked.Increment(ref _shutdownDeadlineElapsed);
    }

    /// <summary>
    /// Blocks until <paramref name="signaled"/> is set or <see cref="ShutdownDeadline"/> elapses on <paramref name="clock"/>.
    /// </summary>
    /// <param name="clock">Clock that owns the deadline. Not null.</param>
    /// <param name="signaled">Set by the work this wait is joining. Not null. Not disposed by this method.</param>
    /// <param name="onDeadline">Runs on the clock callback when the deadline elapses, before the waiter is released. Not null.</param>
    /// <param name="armedCount">Incremented after the deadline timer is registered. Not incremented when the signal is already set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/>, <paramref name="signaled"/>, or <paramref name="onDeadline"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The wait ended without the signal or the deadline.</exception>
    /// <remarks>
    /// Does not capture a caller context. The timer is disposed before this returns.
    /// A signal that is already set returns without a timer. The entry is still counted.
    /// There is no wall-clock timeout, because that would release a frozen test clock.
    /// </remarks>
    internal static void WaitUntilSignaledOrDeadline(
        TimeProvider clock,
        ManualResetEventSlim signaled,
        Action onDeadline,
        ref int armedCount)
        => WaitUntilSignaledOrDeadline(clock, signaled, onDeadline, ShutdownDeadline, ref armedCount);

    /// <summary>
    /// Blocks until <paramref name="signaled"/> is set or <paramref name="due"/> elapses on <paramref name="clock"/>.
    /// </summary>
    /// <param name="clock">Clock that owns the deadline. Not null.</param>
    /// <param name="signaled">Set by the work this wait is joining. Not null. Not disposed by this method.</param>
    /// <param name="onDeadline">Runs on the clock callback when the deadline elapses, before the waiter is released. Not null.</param>
    /// <param name="due">How long to wait. Zero or less runs <paramref name="onDeadline"/> and returns without a timer.</param>
    /// <param name="armedCount">Incremented after the deadline timer is registered. Not incremented when the signal is already set or <paramref name="due"/> is not positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/>, <paramref name="signaled"/>, or <paramref name="onDeadline"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The wait ended without the signal or the deadline.</exception>
    /// <remarks>
    /// Does not capture a caller context. The timer is disposed before this returns.
    /// A signal that is already set returns without a timer. The entry is still counted.
    /// There is no wall-clock timeout, because that would release a frozen test clock.
    /// </remarks>
    internal static void WaitUntilSignaledOrDeadline(
        TimeProvider clock,
        ManualResetEventSlim signaled,
        Action onDeadline,
        TimeSpan due,
        ref int armedCount)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(signaled);
        ArgumentNullException.ThrowIfNull(onDeadline);
        // Every entry counts, whichever clock the caller passed. A second Dispose must not get here.
        Interlocked.Increment(ref _deadlineWaitEntries);
        if (signaled.IsSet)
            return;

        if (due <= TimeSpan.Zero)
        {
            onDeadline();
            return;
        }

        using var wake = new ManualResetEventSlim(false);
        var gate = new DeadlineGate(onDeadline);
        var started = clock.GetTimestamp();
        // One due value. The timer and the keep-alive check below both use it.
        using var timer = clock.CreateTimer(
            _ => SignalDeadline(wake, gate.Fire),
            null,
            due,
            Timeout.InfiniteTimeSpan);
        Interlocked.Increment(ref armedCount);
        var which = WaitHandle.WaitAny(new WaitHandle[] { signaled.WaitHandle, wake.WaitHandle });
        if ((uint)which > 1u)
            throw new InvalidOperationException("Shutdown wait ended without the processor or the deadline.");

        // The signal can win while Advance is still firing another timer that was due on the same tick.
        // Disposing this timer then skips its callback, so the deadline is recorded here first.
        // The reading uses the same due value as the timer. A clock that is not due yet does not record it.
        if (!gate.HasFired && clock.GetElapsedTime(started) >= due)
            gate.Fire();
    }

    /// <summary>
    /// Records one deadline callback. The flag is stored before the caller's action runs.
    /// </summary>
    private sealed class DeadlineGate
    {
        private readonly Action _onDeadline;
        private int _fired;

        /// <summary>
        /// Stores the action the timer runs when the deadline elapses.
        /// </summary>
        /// <param name="onDeadline">Not null. The caller already checked.</param>
        /// <remarks>Does not throw. Does not run <paramref name="onDeadline"/>.</remarks>
        internal DeadlineGate(Action onDeadline)
        {
            _onDeadline = onDeadline;
        }

        /// <summary>True after <see cref="Fire"/> has stored the flag.</summary>
        internal bool HasFired => Volatile.Read(ref _fired) != 0;

        /// <summary>
        /// Stores the flag, then runs the deadline action.
        /// </summary>
        /// <remarks>
        /// Does not throw unless the action throws. Safe to call from the clock callback.
        /// A second call still runs the action. The flag stays set.
        /// </remarks>
        internal void Fire()
        {
            Volatile.Write(ref _fired, 1);
            _onDeadline();
        }
    }

    /// <summary>
    /// Records the deadline, then sets the handle the shutdown wait is blocked on.
    /// </summary>
    /// <param name="wake">The handle. Not null. May already be disposed when the wait has finished.</param>
    /// <param name="onDeadline">Records the deadline. Not null. Runs before <paramref name="wake"/> is set.</param>
    /// <remarks>
    /// Does not throw when the handle was already disposed. The wait has already finished in that case.
    /// Called from the clock callback.
    /// </remarks>
    private static void SignalDeadline(ManualResetEventSlim wake, Action onDeadline)
    {
        onDeadline();
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // The wait already finished and disposed the handle.
        }
    }

    /// <summary>
    /// Freezes the written count, flushes ALOG0070, emits ALOG0071 once when events remain, and disposes idle writers.
    /// </summary>
    /// <remarks>
    /// Does not throw. A processor that is still inside a writer call keeps its wait sources,
    /// because disposing them would race that call. ALOG0071 is not emitted for a clean drain.
    /// ALOG0070 for the open window is written before ALOG0071, even when the 60 s window has not ended.
    /// </remarks>
    private void PublishOutcome()
    {
        var ended = _stopped.IsSet;
        var unwritten = FreezeUnwritten();
        PublishReports(unwritten);
        ReleaseWriters = ended;

        // Observe the processor so a fault is not an unobserved task exception. The deadline wait is the event above.
        if (_backgroundTask.IsFaulted)
            _ = _backgroundTask.Exception;

        _reportTimer?.Dispose();
        _writerGate.DisposeIdleWriters();

        if (!ended)
            return;

        DisposeQuiet(_shutdownSignal);
        DisposeQuiet(_wakeCts);
        _stopped.Dispose();
    }

    /// <summary>
    /// Snapshots accepted events that have not been committed. Later commits do not change the snapshot.
    /// </summary>
    /// <returns>The unwritten count. Zero when every accepted event was committed before the snapshot.</returns>
    /// <remarks>Does not throw. Takes <see cref="_gate"/>. Called once from the winning <see cref="Dispose"/>.</remarks>
    private long FreezeUnwritten()
    {
        lock (_gate)
        {
            _frozen = true;
            var unwritten = Interlocked.Read(ref _accepted) - _written;
            _unwritten = unwritten;
            return unwritten;
        }
    }

    /// <summary>
    /// Writes the open ALOG0070 window, then ALOG0071 when <paramref name="unwritten"/> is positive.
    /// A timer callback that arrives during this call sees the seal and writes nothing.
    /// </summary>
    /// <param name="unwritten">Accepted events still outstanding. Zero writes no ALOG0071. Negative is treated as zero.</param>
    /// <remarks>
    /// Does not throw. Holds <see cref="_reportGate"/> across both lines so they cannot be reordered.
    /// A second <see cref="Dispose"/> does not call this. No drops writes no ALOG0070.
    /// </remarks>
    private void PublishReports(long unwritten)
    {
        lock (_reportGate)
        {
            _reportSealed = true;
            PublishPendingFullDrops();
            if (unwritten > 0 && Interlocked.Exchange(ref _shutdownReported, 1) == 0)
                SelfLog.ReportLine(FormatShutdown(unwritten));
        }

        // Covers the sealed callback path and drops a callback that lost the race for the lock.
        DeliverFullDropReport();
    }

    /// <summary>
    /// Writes one ALOG0070 line for drops still waiting in the open window. Zero writes nothing.
    /// </summary>
    /// <remarks>
    /// Does not throw. The caller holds <see cref="_reportGate"/>. The count is removed with an exchange so a timer callback cannot report it too.
    /// </remarks>
    private void PublishPendingFullDrops()
    {
        var count = Interlocked.Exchange(ref _pendingFullDrops, 0);
        if (count <= 0)
            return;

        SelfLog.ReportLine(FormatFullDrop(count));
    }

    /// <summary>
    /// Copies <paramref name="writers"/> so the dispatcher and the dispose gate share one list and one order.
    /// </summary>
    /// <param name="writers">Configured writers. Null throws. The list is not retained.</param>
    /// <returns>A new list of the same instances, in the same order. Empty when the source is empty.</returns>
    /// <remarks>Does not throw for an empty list. Called once from the constructor. The loop is bounded by the source count.</remarks>
    private static List<ILogWriter> CopyWriters(IReadOnlyList<ILogWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);
        var copy = new List<ILogWriter>(writers.Count);
        for (var i = 0; i < writers.Count; i++)
            copy.Add(writers[i]);

        return copy;
    }

    /// <summary>
    /// True when every writer finishes inside <see cref="ILogWriter.Write"/>.
    /// The processor then calls that method and skips the async state machine.
    /// </summary>
    /// <param name="writers">The copied writer list. Not null.</param>
    /// <returns>True for a file or console writer, or an empty list. False when any writer is neither.</returns>
    /// <remarks>
    /// Does not throw. File and console <c>WriteAsync</c> only call <c>Write</c> and return a completed task.
    /// The caller still only queues the event. The processor runs <c>Write</c>.
    /// Any other writer stays on the async path, including when async is off.
    /// HTTP, SQL, and cloud writers stay on the async path.
    /// </remarks>
    private static bool WritersCompleteOnCaller(List<ILogWriter> writers)
    {
        for (var i = 0; i < writers.Count; i++)
        {
            if (writers[i] is not FileLogWriter and not ConsoleLogWriter)
                return false;
        }

        return true;
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
        Cancelled = 3,

        /// <summary><see cref="QueueFullMode.Block"/> needs a slot. The caller waits outside the lock.</summary>
        NeedWait = 4
    }

    /// <summary>
    /// Values the constructors share. One object so each constructor stays within five parameters.
    /// </summary>
    internal readonly struct QueueSettings
    {
        /// <summary>Creates the settings. <paramref name="clock"/> is checked by the queue constructor.</summary>
        /// <param name="capacity">Channel and outstanding limit.</param>
        /// <param name="batchSize">Read size per wake.</param>
        /// <param name="flushInterval">Idle wait.</param>
        /// <param name="asyncEnabled">False selects the synchronous writer method.</param>
        /// <param name="clock">Clock for shutdown, flush, the cutoff, and ALOG0070. Not null when the constructor runs.</param>
        internal QueueSettings(int capacity, int batchSize, TimeSpan flushInterval, bool asyncEnabled, TimeProvider clock)
        {
            Capacity = capacity;
            BatchSize = batchSize;
            FlushInterval = flushInterval;
            AsyncEnabled = asyncEnabled;
            Clock = clock;
            FullMode = QueueFullMode.Drop;
            FlushTimeout = ShutdownDeadline;
        }

        /// <summary>Channel capacity and the outstanding-event cap.</summary>
        internal int Capacity { get; }

        /// <summary>Most events read before the next deadline check.</summary>
        internal int BatchSize { get; }

        /// <summary>Idle wait between reads.</summary>
        internal TimeSpan FlushInterval { get; }

        /// <summary>False uses <see cref="LogDispatcher.Dispatch"/>.</summary>
        internal bool AsyncEnabled { get; }

        /// <summary>Clock for shutdown, flush, the new-write cutoff, and the full-queue report.</summary>
        internal TimeProvider Clock { get; }

        /// <summary>What a full queue does. The constructor starts at <see cref="QueueFullMode.Drop"/>. An initializer can select Block.</summary>
        internal QueueFullMode FullMode { get; init; }

        /// <summary>How long flush and shutdown wait. The constructor starts at <see cref="ShutdownDeadline"/>. An initializer can replace it.</summary>
        internal TimeSpan FlushTimeout { get; init; }
    }

    /// <summary>
    /// Tracks which writer call is running and disposes writers that are idle after the shutdown deadline.
    /// The same instance registered twice shares one slot, so it is disposed only when none of those calls is running, and only once.
    /// </summary>
    private sealed class WriterGate : IWriterCallGate
    {
        private readonly WriterSlot[] _slots;
        private int _deadline;
        private int _skipped;

        /// <summary>
        /// Builds one slot per registration. Registrations of the same instance share that slot.
        /// </summary>
        /// <param name="writers">The dispatcher list. Not null. Not mutated. May be empty. Duplicates are kept.</param>
        /// <remarks>Does not throw. Reference equality decides which registrations share a slot. Called once from the constructor.</remarks>
        internal WriterGate(IReadOnlyList<ILogWriter> writers)
        {
            _slots = BuildSlots(writers);
        }

        /// <summary>True after <see cref="MarkDeadline"/>. The processor must not start another writer call.</summary>
        internal bool DeadlineReached => Volatile.Read(ref _deadline) != 0;

        /// <summary>True when the event just dispatched skipped at least one writer because the deadline had passed.</summary>
        internal bool EventSkipped => Volatile.Read(ref _skipped) != 0;

        /// <summary>
        /// Clears the skip flag for the next event.
        /// </summary>
        /// <remarks>Does not throw. Called on the processor before each event so an earlier skip cannot mark a later event.</remarks>
        internal void PrepareEvent()
        {
            Interlocked.Exchange(ref _skipped, 0);
        }

        /// <summary>
        /// Stops new writer calls. Idle writers can be disposed after this.
        /// </summary>
        /// <remarks>Does not throw. Safe to call more than once. Does not dispose by itself.</remarks>
        internal void MarkDeadline()
        {
            Volatile.Write(ref _deadline, 1);
        }

        /// <inheritdoc />
        public bool BeginWriterCall(int index)
        {
            if ((uint)index >= (uint)_slots.Length)
                return false;

            var slot = _slots[index];
            // Mark the instance busy first. Shutdown then either sees the call and leaves it, or the recheck below skips the call.
            slot.Enter();
            if (Volatile.Read(ref _deadline) == 0)
                return true;

            slot.Leave();
            Interlocked.Exchange(ref _skipped, 1);
            DisposeIfIdle(slot);
            return false;
        }

        /// <inheritdoc />
        public void EndWriterCall(int index)
        {
            if ((uint)index >= (uint)_slots.Length)
                return;

            var slot = _slots[index];
            slot.Leave();
            if (Volatile.Read(ref _deadline) != 0)
                DisposeIfIdle(slot);
        }

        /// <summary>
        /// Disposes every instance that has no call running through any registration.
        /// A call that is still inside Write is left until <see cref="EndWriterCall"/>.
        /// </summary>
        /// <remarks>
        /// Does not throw. Marks the deadline first. The loop is bounded by the registration list.
        /// shortcut: writers listed after a call that is still running are not invoked until that call returns; their events stay in the ALOG0071 count. Per-writer queues are P1.
        /// </remarks>
        internal void DisposeIdleWriters()
        {
            MarkDeadline();
            // shortcut: one shared queue calls writers in list order; a call that is still running blocks later writers until per-writer queues (P1). Those events stay in the ALOG0071 count.
            var count = _slots.Length;
            for (var i = 0; i < count; i++)
                DisposeIfIdle(_slots[i]);
        }

        /// <summary>
        /// Disposes <paramref name="slot"/> when no registration of that instance is inside a call, and only once.
        /// </summary>
        /// <param name="slot">The shared instance slot. Not null.</param>
        /// <remarks>
        /// Does not throw. A writer dispose failure is written to the error stream and the other instances still run.
        /// The disposed flag is taken once per instance, so a second registration cannot dispose it again.
        /// </remarks>
        private void DisposeIfIdle(WriterSlot slot)
        {
            if (Volatile.Read(ref _deadline) == 0)
                return;

            if (slot.InFlight != 0)
                return;

            if (!slot.TryMarkDisposed())
                return;

            if (slot.Writer is null)
                return;

            try
            {
                slot.Writer.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[LoggerEngine] Writer dispose failed: " + ex);
            }
        }

        /// <summary>
        /// One slot per distinct instance, in registration order. Later copies of an instance point at the earlier slot.
        /// </summary>
        /// <param name="writers">Registrations. Not null. The same reference may appear more than once.</param>
        /// <returns>One entry per registration. Empty when there are no writers.</returns>
        /// <remarks>Does not throw. Both loops are bounded by the registration count. Reference equality is the match.</remarks>
        private static WriterSlot[] BuildSlots(IReadOnlyList<ILogWriter> writers)
        {
            var unique = new WriterSlot[writers.Count];
            var uniqueCount = 0;
            var slots = new WriterSlot[writers.Count];
            for (var i = 0; i < writers.Count; i++)
            {
                WriterSlot? match = null;
                for (var j = 0; j < uniqueCount; j++)
                {
                    if (ReferenceEquals(unique[j].Writer, writers[i]))
                    {
                        match = unique[j];
                        break;
                    }
                }

                if (match is null)
                {
                    match = new WriterSlot(writers[i]);
                    unique[uniqueCount] = match;
                    uniqueCount++;
                }

                slots[i] = match;
            }

            return slots;
        }

        /// <summary>
        /// The in-flight count and the single dispose flag for one writer instance.
        /// </summary>
        private sealed class WriterSlot
        {
            private int _inFlight;
            private int _disposed;

            /// <summary>
            /// Stores the instance the registrations share.
            /// </summary>
            /// <param name="writer">The instance. May be null when a registration was null.</param>
            /// <remarks>Does not throw. Called once per distinct instance while the gate is built.</remarks>
            internal WriterSlot(ILogWriter writer) => Writer = writer;

            /// <summary>The registered instance. Null when the registration was null.</summary>
            internal ILogWriter Writer { get; }

            /// <summary>Calls currently inside Write across every registration of this instance. Zero means idle.</summary>
            internal int InFlight => Volatile.Read(ref _inFlight);

            /// <summary>Counts one call as running. Paired with <see cref="Leave"/>.</summary>
            /// <remarks>Does not throw. Safe from the processor and from shutdown.</remarks>
            internal void Enter() => Interlocked.Increment(ref _inFlight);

            /// <summary>Counts one call as finished.</summary>
            /// <remarks>Does not throw. The count does not go negative when every enter has a leave.</remarks>
            internal void Leave() => Interlocked.Decrement(ref _inFlight);

            /// <summary>
            /// Claims the one dispose of this instance.
            /// </summary>
            /// <returns>True the first time. False when another registration or thread already claimed it.</returns>
            /// <remarks>Does not throw. Does not call <see cref="IDisposable.Dispose"/>.</remarks>
            internal bool TryMarkDisposed() => Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;
        }
    }
}
