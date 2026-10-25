// Shutdown accounting for an accept that races the deadline snapshot.
// A post-snapshot accept is ALOG0072. An in-flight write may also be ALOG0071.
// Next file: ALog.Tests/C4QueueFixTests.cs.

using System.Globalization;
using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

public sealed partial class C4QueueFixTests
{
    /// <summary>When a held accept is released relative to the shutdown snapshot.</summary>
    public enum AcceptWindow
    {
        /// <summary>Queued while dispose is waiting and the deadline has not fired.</summary>
        BeforeSnapshot = 0,

        /// <summary>Queued after the deadline snapshot, before the processor leaves the writer.</summary>
        AfterSnapshotBeforeExit = 1,

        /// <summary>Queued after dispose has returned and the processor has left.</summary>
        AfterProcessorExit = 2,
    }

    /// <summary>
    /// An accept released after the deadline snapshot is ALOG0072, not a second ALOG0071 line, and not a silent loss.
    /// The event already inside the writer may be both written and counted. The overcount is at most that one event.
    /// </summary>
    /// <returns>A task that finishes after the writer is released and the processor has left.</returns>
    /// <remarks>
    /// The flush timeout is 300 ms on <see cref="TimeProvider.System"/>. The 20 second waits are hang guards.
    /// Not thread-safe. The finally block releases the writer and restores SelfLog.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task Hook_EnqueueAfterDeadlineSnapshot()
    {
        await AssertDeadlineLateAccept();
    }

    /// <summary>
    /// Checks <c>written + counted</c> against the accepts for each release window.
    /// A hanging writer may overcount by the in-flight batch. Every other window is exact.
    /// </summary>
    /// <param name="window">Which moment the held accept is released.</param>
    /// <returns>A task that finishes after that window has been checked.</returns>
    /// <remarks>
    /// The deadline window uses a 300 ms flush timeout. The other windows do not hit the deadline.
    /// The 20 second waits are hang guards. Not thread-safe. Each case restores SelfLog.
    /// </remarks>
    [Theory(Timeout = 30000)]
    [InlineData(AcceptWindow.BeforeSnapshot)]
    [InlineData(AcceptWindow.AfterSnapshotBeforeExit)]
    [InlineData(AcceptWindow.AfterProcessorExit)]
    public async Task Hook_AccountingInvariant(AcceptWindow window)
    {
        if (window == AcceptWindow.BeforeSnapshot)
            await AssertBeforeSnapshotExact();
        else if (window == AcceptWindow.AfterSnapshotBeforeExit)
            await AssertDeadlineLateAccept();
        else
            await AssertAfterExitExact();
    }

    /// <summary>
    /// A writer that stays inside its call past the flush timeout does not lose accepts, in Block or Drop.
    /// Two hundred rounds each. The overcount stays within the one-event batch that was inside the writer.
    /// </summary>
    /// <param name="mode">How a full queue treats a new event.</param>
    /// <returns>A task that finishes after all rounds of that mode.</returns>
    /// <remarks>
    /// Each round uses <see cref="TimeProvider.System"/> and a 20 ms flush timeout. The writer is released only after dispose returns.
    /// The 5 second waits are hang guards. Not thread-safe. SelfLog is restored when the theory case ends.
    /// </remarks>
    [Theory(Timeout = 180000)]
    [InlineData(QueueFullMode.Block)]
    [InlineData(QueueFullMode.Drop)]
    public async Task Deadline_HangingWriter_NoUndercount(QueueFullMode mode)
    {
        const int rounds = 200;
        const int batch = 1;
        var previous = SelfLog.Target;
        var maxOver = 0;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                var over = await RunHangingRound(mode, round);
                if (over > maxOver)
                    maxOver = over;
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        Console.WriteLine(
            "STRESS mode="
            + mode
            + " rounds="
            + rounds.ToString(CultureInfo.InvariantCulture)
            + " maxOver="
            + maxOver.ToString(CultureInfo.InvariantCulture));
        Assert.InRange(maxOver, 0, batch);
    }

    /// <summary>
    /// One dispose while a writer is blocked. Returns how far <c>written + counted</c> passed the attempts.
    /// </summary>
    /// <param name="mode">Block waits. Drop counts a full queue.</param>
    /// <param name="round">The round number copied into a failure.</param>
    /// <returns>The overcount. Negative when an accept was lost.</returns>
    /// <remarks>
    /// Does not throw except through an assertion. The flush timeout is 20 ms. The writer is released after dispose.
    /// Four producers attempt twenty events each. The waits are 5 second hang guards.
    /// </remarks>
    private async Task<int> RunHangingRound(QueueFullMode mode, int round)
    {
        var lines = new List<string>();
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, TimeSpan.FromHours(1), true, TimeProvider.System)
            {
                FullMode = mode,
                FlushTimeout = TimeSpan.FromMilliseconds(20),
            });
        Task? disposing = null;
        var calls = 0;
        try
        {
            Assert.True(queue.TryEnqueue(Event("seed")));
            calls = 1;
            Assert.True(
                await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(5)),
                "round " + round.ToString(CultureInfo.InvariantCulture) + " never entered the writer");
            var stop = 0;
            var producers = new Task[4];
            for (var p = 0; p < producers.Length; p++)
            {
                var id = p;
                producers[p] = Task.Run(() =>
                {
                    for (var n = 0; n < 20; n++)
                    {
                        if (Volatile.Read(ref stop) != 0)
                            return;

                        queue.TryEnqueue(Event(
                            "p" + id.ToString(CultureInfo.InvariantCulture)
                            + "-" + n.ToString(CultureInfo.InvariantCulture)));
                        Interlocked.Increment(ref calls);
                    }
                });
            }

            disposing = Task.Run(() => queue.Dispose());
            var disposeDone = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5))) == disposing;
            Assert.True(disposeDone, "round " + round.ToString(CultureInfo.InvariantCulture) + " dispose hung");
            await disposing;
            Volatile.Write(ref stop, 1);
            var pending = Task.WhenAll(producers);
            var producersDone = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))) == pending;
            if (!producersDone)
            {
                ReleaseWriter(writer);
                producersDone = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))) == pending;
            }

            Assert.True(producersDone, "round " + round.ToString(CultureInfo.InvariantCulture) + " producers hung");
            await pending;
            ReleaseWriter(writer);
            Assert.True(
                await WaitUntil(() => queue.ProcessorHasLeft, TimeSpan.FromSeconds(5)),
                "round " + round.ToString(CultureInfo.InvariantCulture) + " processor stayed inside the writer");
            var written = writer.Events.Count;
            var counted = SumShutdown(lines) + queue.ClosedDropCount + queue.FullDropCount;
            var over = (int)(written + counted - calls);
            Assert.True(
                over >= 0,
                "round " + round.ToString(CultureInfo.InvariantCulture)
                + " undercount calls=" + calls.ToString(CultureInfo.InvariantCulture)
                + " written=" + written.ToString(CultureInfo.InvariantCulture)
                + " counted=" + counted.ToString(CultureInfo.InvariantCulture));
            Assert.True(
                over <= 1,
                "round " + round.ToString(CultureInfo.InvariantCulture)
                + " overcount " + over.ToString(CultureInfo.InvariantCulture)
                + " calls=" + calls.ToString(CultureInfo.InvariantCulture)
                + " written=" + written.ToString(CultureInfo.InvariantCulture)
                + " a71=" + SumShutdown(lines).ToString(CultureInfo.InvariantCulture)
                + " closed=" + queue.ClosedDropCount.ToString(CultureInfo.InvariantCulture)
                + " full=" + queue.FullDropCount.ToString(CultureInfo.InvariantCulture));
            return over;
        }
        finally
        {
            ReleaseWriter(writer);
            if (disposing is not null && !disposing.IsCompleted)
                await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5)));

            queue.Dispose();
        }
    }

    /// <summary>
    /// Releases the second accept while dispose is waiting. Both events are written. The totals match exactly.
    /// </summary>
    /// <returns>A task that finishes after dispose returns.</returns>
    /// <remarks>
    /// The clock does not advance, so the deadline does not fire. The 20 second waits are hang guards.
    /// Not thread-safe. The finally block releases the writer and restores SelfLog.
    /// </remarks>
    private async Task AssertBeforeSnapshotExact()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, TimeSpan.FromHours(1), false, clock));
        using var release = new ManualResetEventSlim(false);
        var inHook = 0;
        Task? disposing = null;
        try
        {
            Assert.True(queue.TryEnqueue(Event("owned-a")));
            Assert.True(
                await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(20)),
                "processor never entered the writer");
            queue.BeforeEnqueue = () =>
            {
                Interlocked.Increment(ref inHook);
                release.Wait(TimeSpan.FromSeconds(20));
            };
            var second = Task.Run(() => queue.TryEnqueue(Event("owned-b")));
            Assert.True(
                await WaitUntil(() => Volatile.Read(ref inHook) >= 1, TimeSpan.FromSeconds(20)),
                "second accept did not reach the hook");
            disposing = Task.Run(() => queue.Dispose());
            Assert.True(
                await WaitUntil(() => queue.DisposeWaitsStarted >= 1, TimeSpan.FromSeconds(20)),
                "dispose did not arm the shutdown wait");
            release.Set();
            Assert.True(await AwaitEnqueue(second), "second accept did not return");
            writer.Release();
            Assert.True(await AwaitTask(disposing), "dispose did not return");
            Assert.Equal(2, writer.Events.Count);
            AssertAccounted(writer.Events.Count, lines, queue, expected: 2, maxOver: 0);
        }
        finally
        {
            release.Set();
            ReleaseWriter(writer);
            if (disposing is not null && !disposing.IsCompleted)
                await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(20)));

            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Releases the second accept after the deadline snapshot. It is ALOG0072. The in-flight event may also be written.
    /// </summary>
    /// <returns>A task that finishes after the processor has left.</returns>
    /// <remarks>
    /// The flush timeout is 300 ms. The 20 second waits are hang guards. Not thread-safe.
    /// The finally block releases the writer and restores SelfLog.
    /// </remarks>
    private async Task AssertDeadlineLateAccept()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(8, 1, TimeSpan.FromHours(1), true, TimeProvider.System)
            {
                FlushTimeout = TimeSpan.FromMilliseconds(300),
            });
        using var release = new ManualResetEventSlim(false);
        var inHook = 0;
        try
        {
            Assert.True(queue.TryEnqueue(Event("late-a")));
            Assert.True(
                await WaitUntil(() => writer.EnteredCount > 0, TimeSpan.FromSeconds(20)),
                "processor never entered the writer");
            queue.BeforeEnqueue = () =>
            {
                Interlocked.Increment(ref inHook);
                release.Wait(TimeSpan.FromSeconds(20));
            };
            var second = Task.Run(() => queue.TryEnqueue(Event("late-b")));
            Assert.True(
                await WaitUntil(() => Volatile.Read(ref inHook) >= 1, TimeSpan.FromSeconds(20)),
                "second accept did not reach the hook");
            queue.BeforeEnqueue = null;
            var disposing = Task.Run(() => queue.Dispose());
            Assert.True(await AwaitTask(disposing), "dispose did not return");
            Assert.False(queue.ProcessorHasLeft);
            var snapshot = queue.UnwrittenCount;
            release.Set();
            Assert.True(await AwaitEnqueue(second), "second accept did not return");
            Assert.True(await second);
            ReleaseWriter(writer);
            Assert.True(
                await WaitUntil(() => queue.ProcessorHasLeft, TimeSpan.FromSeconds(20)),
                "processor stayed inside the writer");
            await Task.Delay(50);
            var expectedShutdown = "Shutdown deadline of "
                + BackgroundLogQueue.DeadlineText(TimeSpan.FromMilliseconds(300))
                + " reached: "
                + snapshot.ToString(CultureInfo.InvariantCulture)
                + " queued events were not written. A writer is too slow to drain the queue in time; check the target or reduce the log volume before shutdown.";
            string joined;
            lock (lines)
                joined = string.Join('\n', lines);

            Assert.Contains(expectedShutdown, joined, StringComparison.Ordinal);
            Assert.Equal(1, CountLines(lines, "Shutdown deadline of"));
            Assert.Equal(1, CountLines(lines, ClosedLine));
            Assert.DoesNotContain("they were logged while shutdown was finishing", joined, StringComparison.Ordinal);
            Assert.Equal(snapshot, SumShutdown(lines));
            Assert.Equal(1, queue.ClosedDropCount);
            Assert.Contains(writer.Events, item => item.Message == "late-a");
            Assert.DoesNotContain(writer.Events, item => item.Message == "late-b");
            AssertAccounted(writer.Events.Count, lines, queue, expected: 2, maxOver: 1);
            Console.WriteLine(
                "HOOK deadline written="
                + writer.Events.Count.ToString(CultureInfo.InvariantCulture)
                + " a71="
                + SumShutdown(lines).ToString(CultureInfo.InvariantCulture)
                + " closed="
                + queue.ClosedDropCount.ToString(CultureInfo.InvariantCulture)
                + " snapshot="
                + snapshot.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            release.Set();
            ReleaseWriter(writer);
            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Two accepts released after the processor has left are both ALOG0072. Nothing is written. The totals match exactly.
    /// </summary>
    /// <returns>A task that finishes after both accepts return.</returns>
    /// <remarks>
    /// Dispose returns before the hook is released, so the deadline does not own these events.
    /// The 20 second waits are hang guards. Not thread-safe. The finally block restores SelfLog.
    /// </remarks>
    private async Task AssertAfterExitExact()
    {
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var writer = new RecordingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            capacity: 8,
            batchSize: 1,
            flushInterval: TimeSpan.FromHours(1));
        var waiting = 0;
        using var bothWaiting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        queue.BeforeEnqueue = () =>
        {
            if (Interlocked.Increment(ref waiting) == 2)
                bothWaiting.Set();

            release.Wait(TimeSpan.FromSeconds(20));
        };
        try
        {
            var first = Task.Run(() => queue.TryEnqueue(Event("race-a")));
            var second = Task.Run(() => queue.TryEnqueue(Event("race-b")));
            Assert.True(
                await WaitUntil(() => bothWaiting.IsSet, TimeSpan.FromSeconds(20)),
                "both accepts did not reach the hook");
            queue.Dispose();
            Assert.True(queue.ProcessorHasLeft);
            release.Set();
            Assert.True(await AwaitEnqueue(first), "first accept did not return");
            Assert.True(await AwaitEnqueue(second), "second accept did not return");
            Assert.True(await first);
            Assert.True(await second);
            Assert.Empty(writer.Events);
            Assert.Equal(2, queue.ClosedDropCount);
            Assert.Equal(0, queue.UnwrittenCount);
            Assert.Equal(1, CountLines(lines, ClosedLine));
            Assert.Equal(0, CountLines(lines, "Shutdown deadline of"));
            Assert.DoesNotContain(
                "they were logged while shutdown was finishing",
                string.Join('\n', lines),
                StringComparison.Ordinal);
            AssertAccounted(writer.Events.Count, lines, queue, expected: 2, maxOver: 0);
        }
        finally
        {
            release.Set();
            queue.Dispose();
            SelfLog.Target = previous;
        }
    }

    /// <summary>
    /// Checks that written events plus shutdown, closed, and full counts cover <paramref name="expected"/>.
    /// </summary>
    /// <param name="written">Events the writer accepted. Not negative.</param>
    /// <param name="lines">Captured SelfLog lines. Not null.</param>
    /// <param name="queue">The queue whose drop counters are read. Not null.</param>
    /// <param name="expected">Accepts that returned true.</param>
    /// <param name="maxOver">Largest allowed <c>written + counted - expected</c>. Zero requires an exact match.</param>
    /// <remarks>Does not throw except through an assertion. Reads the queue after dispose has published its snapshot.</remarks>
    private static void AssertAccounted(
        int written,
        List<string> lines,
        BackgroundLogQueue queue,
        int expected,
        int maxOver)
    {
        var counted = SumShutdown(lines) + queue.ClosedDropCount + queue.FullDropCount;
        var total = written + counted;
        Assert.True(total >= expected, "undercount written+counted=" + total.ToString(CultureInfo.InvariantCulture));
        Assert.InRange(total - expected, 0, maxOver);
    }

    /// <summary>Sums the counts in ALOG0071 lines.</summary>
    /// <param name="lines">Captured SelfLog lines. Not null.</param>
    /// <returns>The sum. Zero when no deadline line was written.</returns>
    /// <remarks>Does not throw. The loop is bounded by the list. The list is locked.</remarks>
    private static long SumShutdown(List<string> lines)
    {
        const string marker = " queued events were not written";
        long sum = 0;
        lock (lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at <= 0)
                    continue;

                var start = at;
                while (start > 0 && char.IsDigit(line[start - 1]))
                    start--;

                if (start == at)
                    continue;

                sum += long.Parse(line.AsSpan(start, at - start), CultureInfo.InvariantCulture);
            }
        }

        return sum;
    }

    /// <summary>How many captured lines contain <paramref name="fragment"/>.</summary>
    /// <param name="lines">Captured SelfLog lines. Not null.</param>
    /// <param name="fragment">The text to count. Not null.</param>
    /// <returns>The number of matches.</returns>
    /// <remarks>Does not throw. The loop is bounded by the list. The list is locked.</remarks>
    private static int CountLines(List<string> lines, string fragment)
    {
        var count = 0;
        lock (lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(fragment, StringComparison.Ordinal))
                    count++;
            }
        }

        return count;
    }

    /// <summary>Waits until <paramref name="enqueue"/> finishes and returns its value.</summary>
    /// <param name="enqueue">The accept. Not null.</param>
    /// <returns>True when the accept returned true within 20 seconds.</returns>
    /// <remarks>Does not throw except through an assertion. Does not block the caller on the accept itself.</remarks>
    private static async Task<bool> AwaitEnqueue(Task<bool> enqueue)
    {
        Assert.True(await AwaitTask(enqueue), "accept did not return");
        return await enqueue;
    }

    /// <summary>Waits until <paramref name="work"/> finishes.</summary>
    /// <param name="work">The operation. Not null.</param>
    /// <returns>True when it finished within 20 seconds.</returns>
    /// <remarks>Does not throw. A timed-out operation is left running for the caller to release.</remarks>
    private static async Task<bool> AwaitTask(Task work)
    {
        return await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20))) == work;
    }

    /// <summary>Releases a blocked writer. A writer disposed at the deadline has already dropped its gate.</summary>
    /// <param name="writer">The writer. Not null.</param>
    /// <remarks>Does not throw. Safe after the queue has disposed the writer.</remarks>
    private static void ReleaseWriter(BlockingWriter writer)
    {
        try
        {
            writer.Release();
        }
        catch (ObjectDisposedException)
        {
            // The deadline disposes an idle writer, and that dispose closes the gate.
        }
    }
}
