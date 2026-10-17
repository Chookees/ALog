using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ALog.Core;
using ALog.Writers.Cloud;

namespace ALog.Tests;

/// <summary>
/// Proves the Azure sync write blocks on a single-thread context.
/// Z1 removes the blocking wait. These tests only record the current behavior.
/// </summary>
public sealed class AzureDeadlockTests
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// Runs <see cref="AzureLogWriter.Write"/> on a one-thread context whose handler
    /// yields without <c>ConfigureAwait(false)</c>. The call is still blocked after
    /// the 2 second client timeout, and the handler has not finished the request.
    /// Z1 reverses this deadlock.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> cancels the token passed to the handler.
    /// This handler does not observe that token, and the yield continuation is
    /// queued on the blocked pump, so the timeout does not complete <c>GetResult</c>.
    /// The test therefore treats a call that is still running after 2 seconds as
    /// the deadlock. <see cref="SingleThreadContext.Send"/> throws after 25 seconds
    /// on a background thread so the suite does not wait for it. The fact timeout
    /// is 30 seconds. The client, writer, and pump are left alive because disposing
    /// them does not release the blocked wait.
    /// </remarks>
    [Fact(Timeout = 30000)]
    [Trait("Category", "KnownBug")]
    public async Task Write_OnSingleThreadContext_BlocksUntilClientTimeout()
    {
        // Z1 reverses this deadlock.
        var handler = new YieldHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var writer = new AzureLogWriter(InstrumentationKey, httpClient: client);
        var pump = new SingleThreadContext();
        var watch = Stopwatch.StartNew();
        var send = Task.Run(() =>
        {
            try
            {
                pump.Send(() => writer.Write(Event()));
            }
            catch (TimeoutException)
            {
                // The 25 second cap fired. Write is still blocked on the pump.
            }
        });

        while (handler.Started == 0)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("The Azure handler did not start.");

            await Task.Delay(10);
        }

        var remaining = TimeSpan.FromMilliseconds(2000) - watch.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        Assert.False(send.IsCompleted);
        Assert.False(handler.Finished.IsCompleted);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(1900), $"elapsed {watch.Elapsed}");
    }

    /// <summary>
    /// Runs <see cref="AzureLogWriter.WriteAsync"/> with the same yielding handler.
    /// The async path uses <c>ConfigureAwait(false)</c>, and the pump keeps running,
    /// so the yield continuation runs and the request finishes.
    /// </summary>
    /// <remarks>
    /// Does not block the pump inside the send callback. Fails if the request is
    /// still unfinished after 5 seconds. The fact timeout is 30 seconds.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task WriteAsync_OnSingleThreadContext_SendsTheRequest()
    {
        using var handler = new YieldHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var writer = new AzureLogWriter(InstrumentationKey, httpClient: client);
        using var pump = new SingleThreadContext();
        Task? pending = null;

        pump.Send(() => { pending = writer.WriteAsync(Event()); });

        Assert.NotNull(pending);
        await pending!.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Started);
    }

    private const string InstrumentationKey = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Builds the event both deadlock tests send. The timestamp is fixed.
    /// </summary>
    /// <returns>One info event with no context and no properties.</returns>
    private static LogEvent Event()
        => new(Stamp, LogLevel.Info, "deadlock-probe", null, null);
}

/// <summary>
/// One-thread <see cref="SynchronizationContext"/>. Posted work runs only on the pump thread.
/// </summary>
internal sealed class SingleThreadContext : IDisposable
{
    private readonly BlockingCollection<WorkItem> queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Thread pump;

    /// <summary>
    /// Starts the pump thread and waits until that thread owns this context.
    /// </summary>
    /// <remarks>
    /// The thread is a background thread. <see cref="Dispose"/> stops the pump.
    /// </remarks>
    public SingleThreadContext()
    {
        var ready = new ManualResetEventSlim(false);
        pump = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(thisContext());
            ready.Set();
            Run();
        })
        {
            IsBackground = true,
            Name = "alog-single-thread-context"
        };
        pump.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The single-thread context pump did not start.");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the pump thread and waits until it returns.
    /// </summary>
    /// <param name="action">Work that runs on the pump thread.</param>
    /// <returns>How long <paramref name="action"/> ran on the pump.</returns>
    /// <exception cref="TimeoutException">Thrown when the pump does not finish the action within 25 seconds.</exception>
    /// <remarks>
    /// A timeout leaves the pump thread blocked if the action is still inside a sync wait.
    /// The thread is a background thread, so the test process can still exit.
    /// </remarks>
    public TimeSpan Send(Action action)
    {
        var item = new WorkItem(action);
        queue.Add(item);
        if (!item.Done.Wait(TimeSpan.FromSeconds(25)))
            throw new TimeoutException("Send on the single-thread context exceeded 25s.");

        if (item.Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();

        return item.Elapsed;
    }

    /// <summary>
    /// Stops the pump. A pump blocked inside <see cref="Send"/> is not interrupted.
    /// </summary>
    public void Dispose()
    {
        stop.Cancel();
        queue.CompleteAdding();
    }

    /// <summary>
    /// Returns this instance for the pump thread. A local function keeps the constructor short.
    /// </summary>
    /// <returns>This context.</returns>
    private SynchronizationContext thisContext() => new PumpContext(this);

    /// <summary>
    /// Drains the queue until <see cref="Dispose"/> cancels the pump.
    /// </summary>
    private void Run()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!queue.TryTake(out var item, TimeSpan.FromMilliseconds(50)))
                    continue;

                item.Run();
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose completed the queue while the pump was taking.
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with TryTake.
        }
    }

    private sealed class WorkItem
    {
        private readonly Action action;

        public WorkItem(Action action) => this.action = action;

        public ManualResetEventSlim Done { get; } = new(false);

        public TimeSpan Elapsed { get; private set; }

        public Exception? Error { get; private set; }

        /// <summary>
        /// Runs the posted action and records its duration and exception.
        /// </summary>
        public void Run()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            finally
            {
                Elapsed = watch.Elapsed;
                Done.Set();
            }
        }
    }

    /// <summary>
    /// Posts onto the shared queue. <see cref="Send"/> on this wrapper waits on the pump.
    /// </summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly SingleThreadContext owner;

        public PumpContext(SingleThreadContext owner) => this.owner = owner;

        /// <summary>
        /// Queues <paramref name="d"/> for the pump thread.
        /// </summary>
        /// <param name="d">Callback.</param>
        /// <param name="state">State passed to <paramref name="d"/>.</param>
        public override void Post(SendOrPostCallback d, object? state)
            => owner.queue.Add(new WorkItem(() => d(state)));

        /// <summary>
        /// Runs <paramref name="d"/> on the pump and waits up to 25 seconds.
        /// </summary>
        /// <param name="d">Callback.</param>
        /// <param name="state">State passed to <paramref name="d"/>.</param>
        /// <exception cref="TimeoutException">Thrown when the pump does not finish within 25 seconds.</exception>
        public override void Send(SendOrPostCallback d, object? state)
            => owner.Send(() => d(state));
    }
}

/// <summary>
/// Handler whose <see cref="SendAsync"/> yields without <c>ConfigureAwait(false)</c>.
/// </summary>
internal sealed class YieldHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes only after the yield continuation builds the response.</summary>
    public Task Finished => finished.Task;

    /// <summary>How many times <see cref="SendAsync"/> was entered.</summary>
    public int Started { get; private set; }

    /// <summary>
    /// Yields onto the caller context, then returns HTTP 200.
    /// The yield has no <c>ConfigureAwait(false)</c>, so a blocked pump never resumes it.
    /// </summary>
    /// <param name="request">Ignored. The test does not send a body that must be read.</param>
    /// <param name="cancellationToken">Not observed. The deadlock is the point of the test.</param>
    /// <returns>HTTP 200 after the continuation runs.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Started++;
        await Task.Yield();
        finished.TrySetResult();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
