using System.Globalization;
using ALog.Config;
using ALog.Core;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>
/// The first queued event is written by the processor, including when dispose runs at the same time.
/// </summary>
/// <remarks>
/// These tests are not safe to run in parallel with other tests that replace <see cref="SelfLog"/>.
/// The assembly already disables parallelization. Each test restores <see cref="SelfLog"/> before it returns.
/// </remarks>
public sealed class QueueFirstEventTests
{
    /// <summary>
    /// A log call that returns while another thread disposes must leave the line in the file or a drop report.
    /// </summary>
    /// <returns>A task that finishes after every round.</returns>
    /// <remarks>
    /// One hundred rounds. A silent round is a returned log, an empty file, and no diagnostic line.
    /// A reported drop is not a failure. The 120 s timeout is a hang guard. Not safe beside another SelfLog test.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task DisposeRace_DoesNotSilentlyLoseAReturnedEvent()
    {
        await Task.CompletedTask;
        const int rounds = 100;
        var silent = 0;
        var written = 0;
        var previous = SelfLog.Target;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                var path = Path.Combine(Path.GetTempPath(), "alog-race-" + Guid.NewGuid().ToString("N") + ".log");
                var reports = new List<string>();
                SelfLog.Enable(reports.Add);
                LoggerEngine? engine = null;
                FileLogWriter? writer = null;
                try
                {
                    writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
                    engine = new LoggerEngine(new LoggerConfig()
                        .AddWriter(writer)
                        .UseBackgroundQueue(true, 1000, 10, TimeSpan.FromMilliseconds(100))
                        .SetQueueFullMode(QueueFullMode.Block));
                    using var go = new ManualResetEventSlim(false);
                    var inLog = 0;
                    var logger = new Thread(() =>
                    {
                        go.Wait();
                        Volatile.Write(ref inLog, 1);
                        engine.Log("t0 i00000 end", LogLevel.Info);
                    });
                    var disposer = new Thread(() =>
                    {
                        go.Wait();
                        var spins = 0;
                        while (Volatile.Read(ref inLog) == 0 && spins < 1_000_000)
                            spins++;
                        Thread.SpinWait((round * 40) % 8000);
                        engine.Dispose();
                    });
                    logger.Start();
                    disposer.Start();
                    go.Set();
                    logger.Join();
                    disposer.Join();
                    writer.Dispose();
                    var lines = File.Exists(path) ? File.ReadAllLines(path).Length : 0;
                    var reported = reports.Count > 0;
                    if (lines == 0 && !reported)
                        silent++;
                    if (lines == 1)
                        written++;
                }
                finally
                {
                    engine?.Dispose();
                    writer?.Dispose();
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        Assert.Equal(0, silent);
        Assert.True(written > 0, "every round was a drop, so the write path was not exercised");
    }

    /// <summary>
    /// Log then dispose on one thread keeps the line. The processor, not the caller, writes it.
    /// </summary>
    /// <returns>A task that finishes after the rounds.</returns>
    /// <remarks>
    /// Twenty rounds. Each file must contain the one line. The 60 s timeout is a hang guard.
    /// Not safe beside another SelfLog test. The test restores the previous target.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task LogThenDispose_WritesTheFirstEvent()
    {
        await Task.CompletedTask;
        var previous = SelfLog.Target;
        var reports = new List<string>();
        SelfLog.Enable(reports.Add);
        try
        {
            for (var round = 0; round < 20; round++)
            {
                var path = Path.Combine(Path.GetTempPath(), "alog-first-" + Guid.NewGuid().ToString("N") + ".log");
                try
                {
                    using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
                    using var engine = new LoggerEngine(new LoggerConfig()
                        .AddWriter(writer)
                        .UseBackgroundQueue(true, 100, 10, TimeSpan.FromHours(1))
                        .SetQueueFullMode(QueueFullMode.Block));
                    engine.Log("one", LogLevel.Info);
                    engine.Dispose();
                    var lines = File.ReadAllLines(path);
                    Assert.Single(lines);
                    Assert.Contains("one", lines[0], StringComparison.Ordinal);
                }
                finally
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
        }
        finally
        {
            SelfLog.Target = previous;
        }

        Assert.Empty(reports);
    }

    /// <summary>
    /// The first events of several threads and the rest of the burst are all written, in per-thread order.
    /// </summary>
    /// <returns>A task that finishes after dispose.</returns>
    /// <remarks>
    /// Four threads write twenty-five lines each and start together. Dispose runs after they join.
    /// The 60 s timeout is a hang guard. Not thread-safe beyond those four threads.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task FirstLineParallelToABurst_KeepsEveryEventInOrder()
    {
        await Task.CompletedTask;
        const int threads = 4;
        const int perThread = 25;
        var path = Path.Combine(Path.GetTempPath(), "alog-burst-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            using var engine = new LoggerEngine(new LoggerConfig()
                .AddWriter(writer)
                .UseBackgroundQueue(true, 10000, 10, TimeSpan.FromMilliseconds(100))
                .SetQueueFullMode(QueueFullMode.Block));
            using var go = new ManualResetEventSlim(false);
            var workers = new Thread[threads];
            for (var t = 0; t < threads; t++)
            {
                var id = t;
                workers[t] = new Thread(() =>
                {
                    go.Wait();
                    for (var i = 0; i < perThread; i++)
                    {
                        engine.Log(
                            "t" + id.ToString(CultureInfo.InvariantCulture)
                            + " i" + i.ToString("00", CultureInfo.InvariantCulture)
                            + " end",
                            LogLevel.Info);
                    }
                });
                workers[t].Start();
            }

            go.Set();
            for (var t = 0; t < threads; t++)
                workers[t].Join();
            engine.Dispose();

            var lines = File.ReadAllLines(path);
            Assert.Equal(threads * perThread, lines.Length);
            for (var t = 0; t < threads; t++)
            {
                var prefix = "t" + t.ToString(CultureInfo.InvariantCulture) + " i";
                var sequence = new List<int>();
                for (var line = 0; line < lines.Length; line++)
                {
                    var text = lines[line];
                    var at = text.IndexOf(prefix, StringComparison.Ordinal);
                    if (at < 0)
                        continue;
                    var digits = text.Substring(at + prefix.Length, 2);
                    sequence.Add(int.Parse(digits, CultureInfo.InvariantCulture));
                }

                Assert.Equal(perThread, sequence.Count);
                for (var i = 0; i < sequence.Count; i++)
                    Assert.Equal(i, sequence[i]);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
