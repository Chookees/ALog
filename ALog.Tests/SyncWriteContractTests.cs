using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Threading.Channels;
using ALog.Core;
using ALog.Public.Interfaces;
using ALog.Writers.Cloud;
using ALog.Writers.File;
using ALog.Writers.Http;
using ALog.Writers.Sql;

namespace ALog.Tests;

/// <summary>
/// Synchronous writer contracts from Z1-03, Z1-06, and Z1-07.
/// A missing synchronous HTTP send is a reported writer error.
/// Async writes do not resume on the caller <see cref="SynchronizationContext"/>.
/// The file writer finishes on the calling thread.
/// Next file: <c>AzureDeadlockTests.cs</c> for the one-thread pump.
/// </summary>
public sealed class SyncWriteContractTests
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private const string RefusedSql =
        "Server=127.0.0.1,1;User ID=sa;Password=unused;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True;Pooling=false";

    /// <summary>
    /// <see cref="AzureLogWriter.Write"/> reports a handler that has no synchronous send.
    /// </summary>
    /// <remarks>
    /// Z1-03. The call returns, does not throw, and does not call <c>SendAsync</c>.
    /// A handler without synchronous <c>Send</c> throws, and the writer reports that.
    /// There is no product deadline on this path. The 30 s limit is the hang guard.
    /// The pump cap is 30 seconds so a hang fails the test instead of the suite.
    /// The fact limit is 90 seconds so the pump can report that hang.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task AzureWrite_WithoutSyncSend_ReportsAndReturns()
    {
        await Task.CompletedTask;
        AssertSyncSendMissingIsReported(
            client => new AzureLogWriter(InstrumentationKey, httpClient: client),
            "[AzureLogWriter]");
    }

    /// <summary>
    /// <see cref="HttpLogWriter.Write"/> reports a handler that has no synchronous send.
    /// </summary>
    /// <remarks>
    /// Z1-03. Same 30 s hang guard and error-path checks as the Azure test. There is no fallback to
    /// <c>SendAsync</c>. The fact limit is 90 seconds so the pump can report a hang.
    /// </remarks>
    [Fact(Timeout = 90000)]
    public async Task HttpWrite_WithoutSyncSend_ReportsAndReturns()
    {
        await Task.CompletedTask;
        AssertSyncSendMissingIsReported(
            client => new HttpLogWriter("https://example.test/logs", httpClient: client),
            "[HttpLogWriter]");
    }

    /// <summary>
    /// <see cref="HttpLogWriter.WriteAsync"/> does not post back to the caller context.
    /// </summary>
    /// <remarks>
    /// Z1-06. The handler completes on the thread pool. A missing
    /// <c>ConfigureAwait(false)</c> would increment <see cref="PostCountingContext.Posts"/>.
    /// </remarks>
    /// <returns>A task that finishes after the post and the post-count assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task HttpWriteAsync_DoesNotResumeOnCallerContext()
    {
        var handler = new PoolCompletionHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var writer = new HttpLogWriter("https://example.test/logs", httpClient: client);
        await AssertPostsStayZero(() => writer.WriteAsync(Event("http-async")));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// <see cref="AzureLogWriter.WriteAsync"/> does not post back to the caller context.
    /// </summary>
    /// <remarks>
    /// Same probe as the HTTP async test. The Azure await uses <c>ConfigureAwait(false)</c>.
    /// </remarks>
    /// <returns>A task that finishes after the post and the post-count assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task AzureWriteAsync_DoesNotResumeOnCallerContext()
    {
        var handler = new PoolCompletionHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var writer = new AzureLogWriter(InstrumentationKey, httpClient: client);
        await AssertPostsStayZero(() => writer.WriteAsync(Event("azure-async")));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// <see cref="SqlLogWriter.WriteAsync"/> does not post back to the caller context.
    /// </summary>
    /// <remarks>
    /// Z1-06. The server is closed, so the open fails and the error is reported.
    /// A resume on the test context would increment <see cref="PostCountingContext.Posts"/>.
    /// </remarks>
    /// <returns>A task that finishes after the failed open and the post-count assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task SqlWriteAsync_DoesNotResumeOnCallerContext()
    {
        using var writer = new SqlLogWriter(RefusedSql, autoCreateTable: false);
        var error = await CaptureErrorAsync(() =>
            AssertPostsStayZero(() => writer.WriteAsync(Event("sql-async"))));
        Assert.Contains("[SqlLogWriter]", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="SqlLogWriter.Write"/> opens synchronously and reports a refused connection.
    /// </summary>
    /// <remarks>
    /// The caller does not receive the driver exception. A null event is reported the same way
    /// and does not open a connection.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task SqlWrite_RefusedConnectionAndNullEvent_AreReported()
    {
        await Task.CompletedTask;
        using var writer = new SqlLogWriter(RefusedSql, autoCreateTable: false);
        var error = CaptureError(() =>
        {
            writer.Write(null!);
            writer.Write(Event("sql-sync"));
        });
        var asyncError = await CaptureErrorAsync(() => writer.WriteAsync(null!));
        Assert.Contains("[SqlLogWriter]", error, StringComparison.Ordinal);
        Assert.Contains("[SqlLogWriter]", asyncError, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="FileLogWriter.WriteAsync"/> appends on the calling thread and is already complete.
    /// </summary>
    /// <remarks>
    /// Z1-07. The formatter records the managed thread id. A <c>Task.Run</c> hop would leave
    /// the task incomplete or run the formatter on another thread.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task FileWriteAsync_CompletesInlineOnCallerThread()
    {
        await Task.CompletedTask;
        var directory = Path.Combine(Path.GetTempPath(), "alog-z1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "inline.log");
        var formatter = new ThreadStampFormatter();
        try
        {
            using var writer = new FileLogWriter(path, formatter);
            var caller = Environment.CurrentManagedThreadId;
            var pending = writer.WriteAsync(Event("inline-line"));
            Assert.True(pending.IsCompleted);
            Assert.Same(Task.CompletedTask, pending);
            Assert.Equal(caller, formatter.ThreadId);
            Assert.Contains("inline-line", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A null file event is reported and does not throw to the caller.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FileWrite_NullEvent_IsReported()
    {
        await Task.CompletedTask;
        var directory = Path.Combine(Path.GetTempPath(), "alog-z1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "null.log");
            using var writer = new FileLogWriter(path);
            var error = CaptureError(() =>
            {
                writer.Write(null!);
                var pending = writer.WriteAsync(null!);
                Assert.True(pending.IsCompleted);
                writer.Write(new LogEvent(
                    Stamp,
                    LogLevel.Error,
                    "with-ex",
                    new InvalidOperationException("boom"),
                    new Dictionary<string, object> { ["User"] = "ada" }));
            });
            Assert.Contains("[FileLogWriter]", error, StringComparison.Ordinal);
            var text = File.ReadAllText(path);
            Assert.Contains("EX:", text, StringComparison.Ordinal);
            Assert.Contains("User=ada", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="AwsCloudWatchWriter.Write"/> waits on the thread pool, not the caller context.
    /// </summary>
    /// <remarks>
    /// The transport yields without <c>ConfigureAwait(false)</c>. That yield runs on the pool
    /// thread started for the put, so the caller context's post count stays zero.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task AwsWrite_DoesNotPostToCallerContext()
    {
        await Task.CompletedTask;
        var transport = new YieldingAwsTransport();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        var previous = SynchronizationContext.Current;
        var context = new PostCountingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            writer.Write(Event("aws-sync"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(0, context.Posts);
        Assert.Equal(1, transport.Calls);

        var error = CaptureError(() => writer.Write(null!));
        var asyncError = await CaptureErrorAsync(() => writer.WriteAsync(null!));
        Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
        Assert.Contains("[AwsCloudWatchWriter]", asyncError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The public CloudWatch constructor owns its transport and releases it on dispose.
    /// </summary>
    /// <remarks>
    /// No put is sent. Construction only builds the SDK client in memory.
    /// </remarks>
    /// <returns>A task that finishes after the writer is disposed.</returns>
    [Fact(Timeout = 30000)]
    public async Task AwsWriter_PublicConstructor_DisposesOwnedTransport()
    {
        await Task.CompletedTask;
        var writer = new AwsCloudWatchWriter("group", "stream");
        writer.Dispose();
    }

    /// <summary>
    /// A synchronous CloudWatch put that never finishes is reported when the wait expires.
    /// </summary>
    /// <remarks>
    /// The production wait is 100 seconds. This test shortens that private field, checks the
    /// timeout path, and restores the field before returning. The transport never completes.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task AwsWrite_Timeout_IsReported()
    {
        await Task.CompletedTask;
        var original = AwsCloudWatchWriter.SyncPutTimeout;
        var transport = new NeverCompletingAwsTransport();
        using var writer = new AwsCloudWatchWriter("group", "stream", transport);
        try
        {
            AwsCloudWatchWriter.SyncPutTimeout = TimeSpan.FromMilliseconds(200);
            var error = CaptureError(() => writer.Write(Event("aws-timeout")));
            Assert.Contains("[AwsCloudWatchWriter]", error, StringComparison.Ordinal);
            Assert.Contains("100 second", error, StringComparison.Ordinal);
        }
        finally
        {
            AwsCloudWatchWriter.SyncPutTimeout = original;
        }
    }

    /// <summary>
    /// An injected HTTP client sends at most 64 request headers.
    /// </summary>
    /// <remarks>
    /// The writer is given 65 headers. The 65th is not on the request. The body is still sent.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task HttpWrite_StopsAfter64Headers()
    {
        await Task.CompletedTask;
        var headers = new Dictionary<string, string>();
        for (var i = 0; i < 65; i++)
            headers["H" + i.ToString("00", CultureInfo.InvariantCulture)] = "v";

        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var writer = new HttpLogWriter(
            "https://example.test/logs",
            headers: headers,
            httpClient: client);
        writer.Write(Event("headers"));
        Assert.Single(handler.Requests);
        var sent = handler.Requests[0].Headers.Keys.Count(
            key => key.StartsWith('H'));
        Assert.Equal(64, sent);
        Assert.False(handler.Requests[0].Headers.ContainsKey("H64"));
    }

    /// <summary>
    /// Null HTTP and Azure events are reported and do not throw.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task HttpAndAzure_NullEvents_AreReported()
    {
        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var http = new HttpLogWriter("https://example.test/logs", httpClient: client);
        using var azure = new AzureLogWriter(InstrumentationKey, httpClient: client);
        var error = CaptureError(() =>
        {
            http.Write(null!);
            azure.Write(null!);
        });
        var asyncError = await CaptureErrorAsync(async () =>
        {
            await http.WriteAsync(null!);
            await azure.WriteAsync(null!);
        });
        Assert.Contains("[HttpLogWriter]", error, StringComparison.Ordinal);
        Assert.Contains("[AzureLogWriter]", error, StringComparison.Ordinal);
        Assert.Contains("[HttpLogWriter]", asyncError, StringComparison.Ordinal);
        Assert.Contains("[AzureLogWriter]", asyncError, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A full queue returns false from <see cref="BackgroundLogQueue.TryEnqueue"/> without waiting for the writer.
    /// The drop counter moves, the writer has finished nothing, and the call stays under the 30 s hang guard.
    /// </summary>
    /// <remarks>
    /// The reader is blocked inside the writer, so the outstanding count stays at capacity.
    /// The writer is released afterwards so dispose can finish.
    /// </remarks>
    /// <returns>A task that finishes after the overflow enqueue returns false.</returns>
    [Fact(Timeout = 60000)]
    public async Task Queue_TryEnqueue_ReturnsFalseWhenTheWaitExpires()
    {
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            capacity: 1,
            batchSize: 1,
            flushInterval: TimeSpan.FromSeconds(30),
            asyncEnabled: false);
        try
        {
            Assert.True(queue.TryEnqueue(Event("prime")));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (writer.EnteredCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.True(writer.EnteredCount > 0);
            var started = Stopwatch.StartNew();
            var accepted = queue.TryEnqueue(Event("overflow"));
            Assert.False(accepted);
            Assert.Empty(writer.Events);
            Assert.Equal(1, queue.FullDropCount);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "hang " + started.Elapsed.ToString());
        }
        finally
        {
            writer.Release();
            queue.Dispose();
        }
    }

    /// <summary>
    /// An enqueue after the queue has started to close returns false without waiting for the writer.
    /// One tick before <see cref="BackgroundLogQueue.ShutdownDeadline"/> dispose has not returned. The next tick returns it.
    /// </summary>
    /// <remarks>
    /// The gate is already closed once the shutdown wait is armed, so the false return does not wait for the deadline.
    /// The blocked writer is released after the deadline steps so the processor can leave.
    /// A second dispose waits for the first and does not throw.
    /// </remarks>
    /// <returns>A task that finishes after dispose returns.</returns>
    [Fact(Timeout = 60000)]
    public async Task Queue_TryEnqueue_ReturnsFalseWhenTheChannelCloses()
    {
        DeadlineClock.AssertValues();
        var clock = DeadlineClock.Create();
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(1, 1, TimeSpan.FromSeconds(30), false, clock));
        Task? disposing = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("prime")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "processor never entered the blocked writer");
            disposing = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(Queue_TryEnqueue_ReturnsFalseWhenTheChannelCloses));
            var enqueueStarted = Stopwatch.StartNew();
            var accepted = queue.TryEnqueue(Event("closing"));
            Assert.False(accepted);
            Assert.Empty(writer.Events);
            Assert.True(enqueueStarted.Elapsed < TimeSpan.FromSeconds(30), "hang " + enqueueStarted.Elapsed.ToString());
            DeadlineClock.AssertNotElapsed(
                clock,
                () => queue.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            DeadlineClock.AssertElapsed(
                clock,
                () => queue.ShutdownDeadlineElapsed != 0,
                BackgroundLogQueue.ShutdownDeadline,
                "shutdown");
            await DeadlineClock.WaitForCompletion(disposing, "shutdown hung");
            Assert.False(queue.TryEnqueue(Event("after")));
            queue.Dispose();
        }
        finally
        {
            writer.Release();
            if (disposing is not null && !disposing.IsCompleted)
                clock.Advance(BackgroundLogQueue.ShutdownDeadline);

            if (disposing is not null)
                _ = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    /// <summary>
    /// <see cref="BackgroundLogQueue.IsQueueClosed"/> accepts one closed-channel failure
    /// and rejects anything else.
    /// </summary>
    /// <remarks>
    /// The three accepted types are a closed channel, a disposed channel, and a cancelled wait.
    /// Two failures at once are not treated as a quiet reject, so the caller still sees them.
    /// </remarks>
    /// <returns>A task that finishes after the classifications are checked.</returns>
    [Fact(Timeout = 30000)]
    public async Task Queue_IsQueueClosed_ClassifiesTheWaitFailure()
    {
        await Task.CompletedTask;
        Assert.True(BackgroundLogQueue.IsQueueClosed(new AggregateException(new ChannelClosedException())));
        Assert.True(BackgroundLogQueue.IsQueueClosed(new AggregateException(new ObjectDisposedException("channel"))));
        Assert.True(BackgroundLogQueue.IsQueueClosed(new AggregateException(new OperationCanceledException())));
        Assert.False(BackgroundLogQueue.IsQueueClosed(new AggregateException(new InvalidOperationException("other"))));
        Assert.False(BackgroundLogQueue.IsQueueClosed(
            new AggregateException(new ChannelClosedException(), new ObjectDisposedException("channel"))));
    }

    /// <summary>
    /// Runs one synchronous write against a handler whose base send is unsupported.
    /// </summary>
    /// <param name="writerFactory">Builds the writer around the probe client. This method disposes it.</param>
    /// <param name="errorTag">Text the writer writes to the error stream. Must not be empty.</param>
    /// <exception cref="TimeoutException">The pump does not finish within 30 seconds.</exception>
    /// <remarks>
    /// Side effect: one HTTP client and one writer are constructed and disposed.
    /// The call does not resume on a caller context. The test thread is not the pump.
    /// </remarks>
    private static void AssertSyncSendMissingIsReported(
        Func<HttpClient, IDisposable> writerFactory,
        string errorTag)
    {
        var handler = new AsyncOnlyHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var writer = writerFactory(client);
        var pump = new SingleThreadContext();
        try
        {
            var error = string.Empty;
            var elapsed = pump.Send(() =>
            {
                error = CaptureError(() => WriteSync(writer));
            });
            Assert.True(elapsed < TimeSpan.FromSeconds(30), "hang " + elapsed.ToString());
            Assert.Contains(errorTag, error, StringComparison.Ordinal);
            Assert.Equal(0, handler.AsyncStarts);
        }
        finally
        {
            client.Dispose();
            writer.Dispose();
            pump.Dispose();
        }
    }

    /// <summary>
    /// Calls the synchronous write on the writer the factory built.
    /// </summary>
    /// <param name="writer">Azure or HTTP writer. Other types are a test bug.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="writer"/> is neither writer.</exception>
    /// <remarks>The writer swallows its own failures. This method does not.</remarks>
    private static void WriteSync(IDisposable writer)
    {
        if (writer is AzureLogWriter azure)
            azure.Write(Event("missing-send"));
        else if (writer is HttpLogWriter http)
            http.Write(Event("missing-send"));
        else
            throw new InvalidOperationException("Unexpected writer.");
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a context that counts posts.
    /// </summary>
    /// <param name="body">Work that must not resume on the caller context. Must not be null.</param>
    /// <returns>A task that finishes when <paramref name="body"/> finishes.</returns>
    /// <remarks>
    /// The await here uses <c>ConfigureAwait(false)</c> so this helper is not the source of a post.
    /// The previous context is restored even when <paramref name="body"/> throws.
    /// </remarks>
    private static async Task AssertPostsStayZero(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        var context = new PostCountingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            await body().ConfigureAwait(false);
            Assert.Equal(0, context.Posts);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Redirects the error stream for one action and restores it afterwards.
    /// </summary>
    /// <param name="body">Work whose error lines are returned. Must not be null.</param>
    /// <returns>Text written to the error stream during <paramref name="body"/>.</returns>
    /// <remarks>Restores the previous writer even when <paramref name="body"/> throws.</remarks>
    private static string CaptureError(Action body)
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            body();
            return buffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>
    /// Redirects the error stream for one asynchronous action and restores it afterwards.
    /// </summary>
    /// <param name="body">Work whose error lines are returned. Must not be null.</param>
    /// <returns>Text written to the error stream during <paramref name="body"/>.</returns>
    /// <remarks>
    /// The await uses <c>ConfigureAwait(false)</c>. The previous writer is restored either way.
    /// </remarks>
    private static async Task<string> CaptureErrorAsync(Func<Task> body)
    {
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            await body().ConfigureAwait(false);
            return buffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>
    /// Builds the event these contract tests send. The timestamp is fixed.
    /// </summary>
    /// <param name="message">Message text. Must not be null.</param>
    /// <returns>One info event with no context and no properties.</returns>
    private static LogEvent Event(string message)
        => new(Stamp, LogLevel.Info, message, null, null);

    private const string InstrumentationKey = "11111111-1111-1111-1111-111111111111";
}

/// <summary>
/// Counts <see cref="SynchronizationContext.Post"/> and runs the callback on the thread pool.
/// A missing <c>ConfigureAwait(false)</c> increments <see cref="Posts"/> instead of deadlocking.
/// </summary>
internal sealed class PostCountingContext : SynchronizationContext
{
    private int posts;

    /// <summary>How many times <see cref="Post"/> ran.</summary>
    public int Posts => posts;

    /// <summary>
    /// Counts one post and runs <paramref name="callback"/> on the thread pool.
    /// </summary>
    /// <param name="callback">Continuation. Must not be null.</param>
    /// <param name="state">State passed to <paramref name="callback"/>. May be null.</param>
    /// <remarks>
    /// The pool hop is deliberate. The continuation must not run on the caller thread,
    /// or a blocked caller would look like a successful resume.
    /// </remarks>
    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref posts);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}

/// <summary>
/// HTTP handler that implements only <c>SendAsync</c>. Synchronous <c>Send</c> stays unsupported.
/// </summary>
internal sealed class AsyncOnlyHandler : HttpMessageHandler
{
    private int asyncStarts;

    /// <summary>How many times <see cref="SendAsync"/> was entered.</summary>
    public int AsyncStarts => asyncStarts;

    /// <summary>
    /// Records one async send and returns HTTP 200.
    /// </summary>
    /// <param name="request">Ignored. The test does not read the body.</param>
    /// <param name="cancellationToken">Ignored. The response is already complete.</param>
    /// <returns>HTTP 200.</returns>
    /// <remarks>
    /// <c>Send</c> is not overridden. <see cref="HttpClient.Send"/> must not call this method.
    /// </remarks>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref asyncStarts);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>
/// HTTP handler whose response completes on the thread pool.
/// </summary>
internal sealed class PoolCompletionHandler : HttpMessageHandler
{
    private int calls;

    /// <summary>How many times <see cref="SendAsync"/> was entered.</summary>
    public int Calls => calls;

    /// <summary>
    /// Completes the response on the thread pool so the caller has a real await.
    /// </summary>
    /// <param name="request">Ignored. The test does not read the body.</param>
    /// <param name="cancellationToken">Ignored. The response is already scheduled.</param>
    /// <returns>A task that completes as HTTP 200 on the thread pool.</returns>
    /// <remarks>
    /// Continuations are asynchronous. A caller that captures a context must post to resume.
    /// </remarks>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        var done = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(_ =>
            done.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)));
        return done.Task;
    }
}

/// <summary>
/// Formatter that records the managed thread that called <see cref="Format"/>.
/// </summary>
internal sealed class ThreadStampFormatter : ILogFormatter
{
    /// <summary>Managed thread id from the last <see cref="Format"/> call. Zero before the first call.</summary>
    public int ThreadId { get; private set; }

    /// <summary>
    /// Records the caller thread and returns the event message.
    /// </summary>
    /// <param name="logEvent">Event to format. Must not be null.</param>
    /// <returns>The event message, unchanged.</returns>
    /// <remarks>The thread id is the proof that formatting did not hop to the pool.</remarks>
    public string Format(LogEvent logEvent)
    {
        ThreadId = Environment.CurrentManagedThreadId;
        return logEvent.Message;
    }
}

/// <summary>
/// CloudWatch transport that yields once without leaving the caller context.
/// </summary>
internal sealed class YieldingAwsTransport : IAwsCloudWatchLogsTransport
{
    private int calls;

    /// <summary>How many times <see cref="PutLogEventAsync"/> was entered.</summary>
    public int Calls => calls;

    /// <summary>
    /// Counts one put and yields without <c>ConfigureAwait(false)</c>.
    /// </summary>
    /// <param name="logGroupName">Ignored.</param>
    /// <param name="logStreamName">Ignored.</param>
    /// <param name="message">Ignored.</param>
    /// <param name="timestampUnixMs">Ignored.</param>
    /// <param name="cancellationToken">Ignored. The yield is not cancelled.</param>
    /// <returns>A task that completes after the yield.</returns>
    /// <remarks>
    /// The missing <c>ConfigureAwait(false)</c> is the trap. The writer must start this
    /// method on the thread pool so the yield does not post to the caller.
    /// </remarks>
    public async Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        await Task.Yield();
    }

    /// <summary>No resources to release.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// CloudWatch transport whose put never completes.
/// </summary>
internal sealed class NeverCompletingAwsTransport : IAwsCloudWatchLogsTransport
{
    /// <summary>
    /// Returns a task that stays incomplete so the sync wait can expire.
    /// </summary>
    /// <param name="logGroupName">Ignored.</param>
    /// <param name="logStreamName">Ignored.</param>
    /// <param name="message">Ignored.</param>
    /// <param name="timestampUnixMs">Ignored.</param>
    /// <param name="cancellationToken">Not observed. The task stays incomplete after cancel too.</param>
    /// <returns>An incomplete task.</returns>
    /// <remarks>
    /// Cancellation is ignored on purpose. The writer must stop waiting on its own clock.
    /// </remarks>
    public Task PutLogEventAsync(
        string logGroupName,
        string logStreamName,
        string message,
        long timestampUnixMs,
        CancellationToken cancellationToken = default)
        => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>No resources to release.</summary>
    public void Dispose()
    {
    }
}
