namespace ALog.Config;

/// <summary>
/// What a full background queue does with a new event.
/// </summary>
/// <remarks>
/// <see cref="Drop"/> is the default and keeps today's non-blocking queue.
/// <see cref="Block"/> waits until the queue has room. A call on the queue's own writer thread,
/// or a call after shutdown has started, is still dropped so logging cannot deadlock.
/// </remarks>
public enum QueueFullMode
{
    /// <summary>Drop the new event and count it. This is the default.</summary>
    Drop = 0,

    /// <summary>Wait until the queue has room. The wait has no time limit while the queue is open.</summary>
    Block = 1
}
