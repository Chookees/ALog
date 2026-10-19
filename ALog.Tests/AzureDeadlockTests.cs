using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ALog.Core;
using ALog.Writers.Cloud;

namespace ALog.Tests;

/// <summary>
/// Sync writes on a one-thread <see cref="SynchronizationContext"/> must return
/// without waiting for that context. Z1-01 and Z1-02. The async Azure path stays covered here.
/// </summary>
public sealed class AzureDeadlockTests
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// <see cref="AzureLogWriter.Write"/> on a one-thread context completes through the synchronous <c>Send</c>.
    /// The wait is a 30 s hang guard. Completion and one sync send are the no-deadlock check.
    /// </summary>
    /// <remarks>
    /// The handler also yields from <c>SendAsync</c> without <c>ConfigureAwait(false)</c>.
    /// A sync-over-async wait on that method never resumes, because the pump thread is blocked.
    /// <see cref="SingleThreadContext.Send"/> throws after 30 seconds so a failed run cannot
    /// hang the suite. This fact's own limit is 90 seconds. On a failure the client, writer,
    /// and pump are left undisposed: disposing them does not release a blocked wait.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task Write_OnSingleThreadContext_ReturnsWithinOneSecondAndHandlerReceivedTheRequest()
    {
        await AssertReturnsWithinOneSecond(writerFactory: client =>
            new AzureLogWriter(InstrumentationKey, httpClient: client));
    }

    /// <summary>
    /// <see cref="ALog.Writers.Http.HttpLogWriter.Write"/> on a one-thread context completes
    /// through the synchronous <c>Send</c>. The wait is a 30 s hang guard.
    /// </summary>
    /// <remarks>
    /// Same pump and handler as the Azure test (Z1-02). The 30 s pump cap is the backstop.
    /// A failure leaves the client and pump undisposed.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task HttpWrite_OnSingleThreadContext_ReturnsWithinOneSecondAndHandlerReceivedTheRequest()
    {
        await AssertReturnsWithinOneSecond(writerFactory: client =>
            new ALog.Writers.Http.HttpLogWriter("https://example.test/logs", httpClient: client));
    }

    /// <summary>
    /// Runs one synchronous writer on the pump. The call must complete, and the sync send must be the one that ran.
    /// </summary>
    /// <param name="writerFactory">Builds the writer around the probe client. The test disposes it only after success.</param>
    /// <returns>A task that finishes when the assertion is done. A blocked write fails this task after 30 seconds.</returns>
    /// <remarks>
    /// The runner is a thread-pool task so the assertion thread is not the pump.
    /// A writer that blocks is still inside <see cref="SingleThreadContext.Send"/> when this returns.
    /// The method is async so xUnit's fact timeout applies. It does not resume on a caller context:
    /// the 30 s wait is <see cref="Task.Delay"/>, which this helper does not capture.
    /// One sync send and zero async starts means the call did not take the yielding path.
    /// </remarks>
    private static async Task AssertReturnsWithinOneSecond(Func<HttpClient, IDisposable> writerFactory)
    {
        var handler = new SyncSendYieldHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var writer = writerFactory(client);
        var pump = new SingleThreadContext();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The runner stays blocked when Write deadlocks. The 30 s wait below does not join it.
        _ = Task.Run(() =>
        {
            try
            {
                pump.Send(() =>
                {
                    if (writer is AzureLogWriter azure)
                        azure.Write(Event());
                    else if (writer is ALog.Writers.Http.HttpLogWriter http)
                        http.Write(Event());
                    else
                        throw new InvalidOperationException("Unexpected writer.");
                });
                completed.TrySetResult();
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        });

        var finished = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        Assert.True(
            ReferenceEquals(finished, completed.Task),
            "call did not complete. syncSends=" + handler.SyncSends.ToString() + " asyncStarts=" + handler.AsyncStarts.ToString());

        await completed.Task.ConfigureAwait(false);
        Assert.Equal(1, handler.SyncSends);
        Assert.Equal(0, handler.AsyncStarts);
        client.Dispose();
        writer.Dispose();
        pump.Dispose();
    }

    /// <summary>
    /// Runs <see cref="AzureLogWriter.WriteAsync"/> with the same yielding handler.
    /// The async path uses <c>ConfigureAwait(false)</c>, and the pump keeps running,
    /// so the yield continuation runs and the request finishes.
    /// </summary>
    /// <remarks>
    /// Does not block the pump inside the send callback. The waits are 30 s hang guards.
    /// The fact timeout is 90 seconds.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task WriteAsync_OnSingleThreadContext_SendsTheRequest()
    {
        using var handler = new YieldHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var writer = new AzureLogWriter(InstrumentationKey, httpClient: client);
        using var pump = new SingleThreadContext();
        Task? pending = null;

        pump.Send(() => { pending = writer.WriteAsync(Event()); });

        Assert.NotNull(pending);
        await pending!.WaitAsync(TimeSpan.FromSeconds(30));
        await handler.Finished.WaitAsync(TimeSpan.FromSeconds(30));
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
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The single-thread context pump did not start.");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the pump thread and waits until it returns.
    /// </summary>
    /// <param name="action">Work that runs on the pump thread.</param>
    /// <returns>How long <paramref name="action"/> ran on the pump.</returns>
    /// <exception cref="TimeoutException">Thrown when the pump does not finish the action within 30 seconds.</exception>
    /// <remarks>
    /// A timeout leaves the pump thread blocked if the action is still inside a sync wait.
    /// The thread is a background thread, so the test process can still exit.
    /// </remarks>
    public TimeSpan Send(Action action)
    {
        var item = new WorkItem(action);
        queue.Add(item);
        if (!item.Done.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Send on the single-thread context exceeded 30s.");

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
        /// Runs <paramref name="d"/> on the pump and waits up to 30 seconds.
        /// </summary>
        /// <param name="d">Callback.</param>
        /// <param name="state">State passed to <paramref name="d"/>.</param>
        /// <exception cref="TimeoutException">Thrown when the pump does not finish within 30 seconds.</exception>
        public override void Send(SendOrPostCallback d, object? state)
            => owner.Send(() => d(state));
    }
}

/// <summary>
/// Handler with a real synchronous <c>Send</c> and a <c>SendAsync</c> that yields
/// onto the caller context. The sync path must use <c>Send</c>. The yield is the deadlock trap.
/// </summary>
internal sealed class SyncSendYieldHandler : HttpMessageHandler
{
    private int syncSends;
    private int asyncStarts;

    /// <summary>How many times <see cref="Send"/> ran.</summary>
    public int SyncSends => syncSends;

    /// <summary>How many times <see cref="SendAsync"/> was entered.</summary>
    public int AsyncStarts => asyncStarts;

    /// <summary>
    /// Records one synchronous send and returns HTTP 200 without posting to a context.
    /// </summary>
    /// <param name="request">Ignored. The probe has no body the test reads back.</param>
    /// <param name="cancellationToken">Ignored. The response is already complete.</param>
    /// <returns>HTTP 200.</returns>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref syncSends);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>
    /// Yields onto the caller <see cref="SynchronizationContext"/> and then returns HTTP 200.
    /// </summary>
    /// <param name="request">Ignored. The probe does not read the body.</param>
    /// <param name="cancellationToken">Not observed. A blocked pump never reaches the continuation.</param>
    /// <returns>HTTP 200 after the continuation runs.</returns>
    /// <remarks>
    /// There is no <c>ConfigureAwait(false)</c>. A caller blocked on this task never resumes it.
    /// </remarks>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref asyncStarts);
        await Task.Yield();
        return new HttpResponseMessage(HttpStatusCode.OK);
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
