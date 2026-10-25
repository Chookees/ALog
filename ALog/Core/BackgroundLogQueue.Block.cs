namespace ALog.Core;

using ALog.Config;

public partial class BackgroundLogQueue
{
    /// <summary>Most callers that may wait at once. One more full queue is a drop, so the list cannot grow without a bound.</summary>
    private const int MaxWaiters = 100_000;

    /// <summary>Most times one caller may wait. The next decision is a closed drop, so a lost wakeup cannot spin forever.</summary>
    private const int MaxBlockAttempts = 1_000_000;

    /// <summary>The queue whose writer is running on this execution context. Null on every other context.</summary>
    private static readonly AsyncLocal<BackgroundLogQueue?> ConsumerQueue = new();

    /// <summary>
    /// True when this call is the queue processor writing an event.
    /// A file that cannot reopen throws <see cref="WriteDeferredException"/> only in that case.
    /// </summary>
    /// <remarks>Does not throw. False on an application thread. The async-local value is set for the processor call and cleared after it.</remarks>
    internal static bool IsOnProcessor => ConsumerQueue.Value is not null;

    private readonly List<BlockWaiter> _registered = new();
    private int _registeredCount;
    private readonly Stack<BlockWaiter> _freeWaiters = new();

    private QueueFullMode _fullMode;
    private TimeSpan _shutdownDeadline;
    private TimeSpan _newWriteBudget;
    private int _consumerThread;
    private int _blockWaitsStarted;
    private bool _blockRelease;
    private int _blockedReleaseUnwritten;

    /// <summary>How many Block callers have reached the wait. A drop that returns immediately does not increment it.</summary>
    internal int BlockWaitsStarted => Volatile.Read(ref _blockWaitsStarted);

    /// <summary>
    /// True when this thread is inside <see cref="DispatchOneAsync"/> for this queue.
    /// </summary>
    /// <returns>True when the async-local queue is this instance or the stored thread id is the current thread.</returns>
    /// <remarks>Does not throw. A thread id of zero means no writer call is marked.</remarks>
    private bool OnConsumer()
    {
        if (ReferenceEquals(ConsumerQueue.Value, this))
            return true;

        var id = Volatile.Read(ref _consumerThread);
        return id != 0 && id == Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Registers <paramref name="waiter"/> when this is the first wait, resets its signal, and tries to take a slot.
    /// </summary>
    /// <param name="logEvent">The event to accept when a slot exists.</param>
    /// <param name="waiter">The caller's waiter. Null until the first wait. Cleared when the caller must not wait.</param>
    /// <param name="firstClosed">Set when this call is the first closed drop.</param>
    /// <returns>Accepted, Closed, Full, or <see cref="OfferResult.NeedWait"/>.</returns>
    /// <remarks>The caller holds <see cref="_gate"/>. Does not wait and does not call SelfLog.</remarks>
    private OfferResult PrepareWait(LogEvent logEvent, ref BlockWaiter? waiter, ref bool firstClosed)
    {
        if (waiter is null)
        {
            if (_registered.Count >= MaxWaiters)
            {
                NoteFullDrop();
                return OfferResult.Full;
            }

            waiter = RentWaiter();
            _registered.Add(waiter);
            Volatile.Write(ref _registeredCount, _registered.Count);
        }

        waiter.Signal.Reset();
        var result = TryAcceptReserved(logEvent, ref firstClosed);
        if (result != OfferResult.NeedWait)
        {
            Unregister(waiter);
            waiter = null;
        }

        return result;
    }

    /// <summary>
    /// Accepts <paramref name="logEvent"/> when the queue has room and shutdown has not released waiters.
    /// After the release, the event is a closed drop even when a slot is free.
    /// </summary>
    /// <param name="logEvent">The event to place on the channel.</param>
    /// <param name="firstClosed">Set when this call is the first closed drop.</param>
    /// <returns>Accepted, Closed, or <see cref="OfferResult.NeedWait"/>.</returns>
    /// <remarks>
    /// The caller holds <see cref="_gate"/>. Ignores <see cref="_closed"/> until <see cref="_blockRelease"/>.
    /// A parked file batch returns Accepted because the disposing thread already counted the event as unwritten.
    /// Any other release is a closed drop. A new call uses <see cref="DecideUnderGate"/> and never reaches this method after the gate is shut.
    /// </remarks>
    private OfferResult TryAcceptReserved(LogEvent logEvent, ref bool firstClosed)
    {
        if (_blockRelease)
        {
            // Counted with the shutdown remainder before the signal. Not a closed drop.
            if (_blockedReleaseUnwritten > 0)
                return OfferResult.Accepted;

            NoteClosed(ref firstClosed);
            return OfferResult.Closed;
        }

        var next = Interlocked.Increment(ref _outstanding);
        if (next > _capacity)
        {
            Interlocked.Decrement(ref _outstanding);
            return OfferResult.NeedWait;
        }

        _queue.Enqueue(logEvent);
        Interlocked.Increment(ref _accepted);
        return OfferResult.Accepted;
    }

    /// <summary>Counts one closed drop and reports whether it is the first.</summary>
    /// <param name="firstClosed">Set when this queue has not reported ALOG0072 yet.</param>
    /// <remarks>The caller holds <see cref="_gate"/> or is the only disposer. Does not call SelfLog.</remarks>
    private void NoteClosed(ref bool firstClosed)
    {
        Interlocked.Increment(ref _closedDrops);
        firstClosed = Interlocked.CompareExchange(ref _closedReported, 1, 0) == 0;
    }

    /// <summary>
    /// Wakes every Block caller still registered. A parked file batch counts those events as unwritten.
    /// A slow writer that is still inside its call counts them as closed drops instead.
    /// </summary>
    /// <remarks>
    /// Does not throw. Takes <see cref="_gate"/>. Safe to call again after the first release.
    /// The accepted count moves before the signal, so the disposing thread can snapshot it after this returns.
    /// The loop inside the signal is bounded by <see cref="MaxWaiters"/>.
    /// </remarks>
    private void ReleaseBlockedCallers()
    {
        lock (_gate)
        {
            if (_blockRelease)
                return;

            _blockRelease = true;
            var waiting = _registered.Count;
            if (waiting > 0 && Volatile.Read(ref _writeDeferred) != 0)
            {
                Interlocked.Add(ref _accepted, waiting);
                _blockedReleaseUnwritten = waiting;
            }

            SignalWaiters();
        }
    }

    /// <summary>Wakes every registered Block caller. The caller has already published the state those callers recheck.</summary>
    /// <remarks>The caller holds <see cref="_gate"/>. The loop is bounded by <see cref="MaxWaiters"/>.</remarks>
    private void SignalWaiters()
    {
        for (var i = 0; i < _registered.Count; i++)
            _registered[i].Signal.Set();
    }

    /// <summary>Takes a waiter from the free list or allocates one.</summary>
    /// <returns>A waiter that is not registered yet.</returns>
    /// <remarks>The caller holds <see cref="_gate"/>. The waiter is kept for the life of the queue.</remarks>
    private BlockWaiter RentWaiter()
    {
        if (_freeWaiters.Count > 0)
            return _freeWaiters.Pop();

        return new BlockWaiter();
    }

    /// <summary>Removes <paramref name="waiter"/> from the registered list and returns it to the free list.</summary>
    /// <param name="waiter">The waiter to drop. Not null.</param>
    /// <remarks>The caller holds <see cref="_gate"/>. A waiter that is not registered is left as it is.</remarks>
    private void Unregister(BlockWaiter waiter)
    {
        for (var i = _registered.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_registered[i], waiter))
                continue;

            _registered.RemoveAt(i);
            _freeWaiters.Push(waiter);
            Volatile.Write(ref _registeredCount, _registered.Count);
            return;
        }
    }

    /// <summary>One Block caller. The signal is reset under the queue lock and waited on outside it.</summary>
    private sealed class BlockWaiter
    {
        /// <summary>Set when a slot may exist or when shutdown releases the caller.</summary>
        internal readonly ManualResetEventSlim Signal = new(false);
    }
}
