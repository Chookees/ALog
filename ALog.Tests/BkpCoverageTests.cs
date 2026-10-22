using System.Runtime.Versioning;
using System.Text;
using ALog.Config;
using ALog.Core;
using ALog.Formatters;
using ALog.Internal;
using ALog.Public.Interfaces;
using ALog.Writers.File;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// Covers the file-buffer and shutdown branches the main backpressure tests leave closed.
/// </summary>
public sealed class BkpCoverageTests
{
    /// <summary>
    /// A startup call before the first write replaces the buffer. A call after the open does not.
    /// </summary>
    /// <remarks>The wide line fits in the constructor buffer and not in the replacement, so only the replacement writes it immediately.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_ApplyStartupReplacesTheBufferOnlyBeforeOpen()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "startup.log");
            using var writer = new FileLogWriter(
                path,
                fileBufferSize: 8192,
                fileFlushInterval: TimeSpan.FromSeconds(60),
                clock: clock);
            writer.ApplyStartup(4096, TimeSpan.FromSeconds(60), clock);
            var wide = new string('W', 5000);
            writer.Write(Event(wide));
            Assert.Contains(wide, ReadShared(path), StringComparison.Ordinal);

            writer.ApplyStartup(4096, TimeSpan.Zero, clock);
            var beforeShort = ReadShared(path).Length;
            writer.Write(Event("short-line"));
            Assert.Equal(beforeShort, ReadShared(path).Length);
            writer.Flush();
            Assert.Contains("short-line", ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A write and a flush after dispose leave the file as the first dispose wrote it.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_WriteAndFlushAfterDisposeLeaveTheFile()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "closed.log");
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("kept"));
            writer.Dispose();
            writer.Write(Event("dropped"));
            writer.Flush();
            var text = File.ReadAllText(path);
            Assert.Contains("kept", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dropped", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>The last reference closes the stream. Releasing that reference again does not throw or rewrite the file.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_CloseAgainIsIgnored()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        FileLogWriter? writer = null;
        SharedLogFile? extra = null;
        try
        {
            var path = Path.Combine(directory, "twice.log");
            writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            extra = LogFileRegistry.Acquire(path, 4096, TimeSpan.Zero, TimeProvider.System);
            writer.Write(Event("kept"));
            writer.Dispose();
            writer = null;
            LogFileRegistry.Release(extra);
            LogFileRegistry.Release(extra);
            extra = null;
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Contains("kept", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            writer?.Dispose();
            if (extra is not null)
            {
                LogFileRegistry.Release(extra);
                LogFileRegistry.Release(extra);
            }

            DeleteDirectory(directory);
        }
    }

    /// <summary>A line that fills the buffer exactly is written without waiting for the interval.</summary>
    /// <remarks>The prefix is 29 characters and the newline is one byte, so the message is 4066 characters.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_FlushesALineThatFillsTheBuffer()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "full.log");
            var prefix = "[2026-06-01 00:00:00] [Info] ";
            var messageLength = 4096 - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(Environment.NewLine);
            var message = new string('B', messageLength);
            using (var writer = new FileLogWriter(
                path,
                fileBufferSize: 4096,
                fileFlushInterval: TimeSpan.FromSeconds(60),
                clock: clock))
            {
                writer.Write(Event(message));
                Assert.Equal(4096, LengthShared(path));
            }

            Assert.Contains(message, File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A failed move reports the rolling error and opens the same file again, so the next line is still whole.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_ReopensAfterRotationCannotMove()
    {
        await Task.CompletedTask;
        if (!OperatingSystem.IsLinux())
            return;

        var directory = NewDirectory();
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            var path = Path.Combine(directory, "rotate.log");
            var before = LogFileRegistry.OpenCount;
            var mode = ReadDirectoryMode(directory);
            using var writer = new FileLogWriter(path, maxFileSizeInBytes: 1, fileFlushInterval: TimeSpan.Zero);
            try
            {
                writer.Write(Event("first-line"));
                LockDirectory(directory, mode);
                writer.Write(Event("second-line"));
            }
            finally
            {
                RestoreDirectory(directory, mode);
            }

            Assert.Equal(before + 2, LogFileRegistry.OpenCount);
            var text = ReadShared(path);
            Assert.Contains("first-line", text, StringComparison.Ordinal);
            Assert.Contains("second-line", text, StringComparison.Ordinal);
            Assert.Contains("Rolling failed", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>When the moved file cannot be opened again, the line is dropped and both failures are reported.</summary>
    [Fact(Timeout = 30000)]
    public async Task File_DropsTheLineWhenTheFileCannotReopen()
    {
        await Task.CompletedTask;
        if (!OperatingSystem.IsLinux())
            return;

        var directory = NewDirectory();
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            var path = Path.Combine(directory, "gone.log");
            var before = LogFileRegistry.OpenCount;
            var mode = ReadDirectoryMode(directory);
            using var writer = new FileLogWriter(path, maxFileSizeInBytes: 1, fileFlushInterval: TimeSpan.Zero);
            try
            {
                writer.Write(Event("first-line"));
                File.Delete(path);
                LockDirectory(directory, mode);
                writer.Write(Event("second-line"));
            }
            finally
            {
                RestoreDirectory(directory, mode);
            }

            Assert.Equal(before + 1, LogFileRegistry.OpenCount);
            Assert.False(File.Exists(path));
            var errors = buffer.ToString();
            Assert.Contains("Rolling failed", errors, StringComparison.Ordinal);
            Assert.Contains("Write failed", errors, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A stream that rejects the flush reports the failure from the interval, from rotation, and from close.
    /// </summary>
    /// <remarks>The device accepts the open and a buffered write, then fails the flush. Dispose still returns.</remarks>
    [Fact(Timeout = 30000)]
    public async Task File_ReportsFlushFailuresWhileTheStreamRejectsWrites()
    {
        await Task.CompletedTask;
        var clock = NewClock();
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            using var writer = new FileLogWriter(
                "/dev/full",
                maxFileSizeInBytes: 1,
                fileFlushInterval: TimeSpan.FromSeconds(30),
                clock: clock);
            writer.Write(Event("buffered"));
            clock.Advance(TimeSpan.FromSeconds(30));
            writer.Write(Event("rotate"));
            writer.Dispose();
            var errors = buffer.ToString();
            Assert.Contains("Write failed", errors, StringComparison.Ordinal);
            Assert.Contains("Rolling failed", errors, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>A one-tick timeout takes the zero cutoff and still waits for that tick.</summary>
    /// <remarks>The 30 s waits are hang guards. The writer stays blocked until the clock has moved.</remarks>
    [Fact(Timeout = 120000)]
    public async Task FlushTimeout_OneTick_WaitsUntilThatTick()
    {
        var timeout = TimeSpan.FromTicks(1);
        var clock = NewClock();
        var writer = new BlockingWriter();
        var queue = new BackgroundLogQueue(
            new ILogWriter[] { writer },
            new BackgroundLogQueue.QueueSettings(1, 8, TimeSpan.FromHours(1), false, clock)
            {
                FlushTimeout = timeout
            });
        Task? dispose = null;
        var lines = new List<string>();
        var previous = SelfLog.Target;
        SelfLog.Enable(lines.Add);
        try
        {
            Assert.True(queue.TryEnqueue(Event("held")));
            await DeadlineClock.WaitFor(() => writer.EnteredCount > 0, "writer never entered");
            dispose = await DeadlineClock.RunUntilArmed(
                queue.Dispose,
                () => queue.DisposeWaitsStarted,
                nameof(FlushTimeout_OneTick_WaitsUntilThatTick));
            Assert.True(queue.CutoffElapsed >= 1);
            Assert.False(dispose.IsCompleted);
            clock.Advance(timeout);
            await DeadlineClock.WaitForCompletion(dispose, "dispose hung");
            Assert.True(queue.ShutdownDeadlineElapsed >= 1);
            Assert.Contains(
                lines,
                line => line.Contains(
                    "Shutdown deadline of " + BackgroundLogQueue.DeadlineText(timeout),
                    StringComparison.Ordinal));
        }
        finally
        {
            SelfLog.Target = previous;
            writer.Release();
            await ReleaseDispose(clock, dispose, timeout);
        }
    }

    /// <summary>A due of zero runs the deadline action and does not arm a timer.</summary>
    [Fact(Timeout = 30000)]
    public async Task Wait_ZeroDueFiresWithoutArming()
    {
        await Task.CompletedTask;
        var before = BackgroundLogQueue.DeadlineWaitEntries;
        var armed = 0;
        var fired = 0;
        using var signaled = new ManualResetEventSlim(false);
        BackgroundLogQueue.WaitUntilSignaledOrDeadline(
            NewClock(),
            signaled,
            () => fired++,
            TimeSpan.Zero,
            ref armed);
        Assert.Equal(before + 1, BackgroundLogQueue.DeadlineWaitEntries);
        Assert.Equal(0, armed);
        Assert.Equal(1, fired);
    }

    /// <summary>Without a queue, a writer that throws from dispose is reported and does not escape.</summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_DisposeReportsAWriterThatThrows()
    {
        await Task.CompletedTask;
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            var engine = new LoggerEngine(new LoggerConfig().AddWriter(new ThrowingDisposeWriter()));
            engine.Dispose();
            Assert.Contains("dispose-broke", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    /// <summary>Async logging without a queue still reaches the writer.</summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_AsyncWriteWithoutAQueueReachesTheWriter()
    {
        var writer = new RecordingWriter();
        using var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer));
        await engine.LogAsync("async-line");
        Assert.Equal(1, writer.AsyncWriteCount);
        Assert.Equal("async-line", writer.Events[0].Message);
    }

    /// <summary>A filter that rejects the event returns from the async path before a writer runs.</summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_AsyncWriteRejectedByTheFilterReturns()
    {
        var writer = new RecordingWriter();
        using var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer).SetFilter(new DenyAll()));
        await engine.LogAsync("hidden");
        Assert.Empty(writer.Events);
    }

    /// <summary>A minimum level changed after the engine is built is used on the next event.</summary>
    [Fact(Timeout = 30000)]
    public async Task Engine_UsesALevelChangedAfterConstruction()
    {
        await Task.CompletedTask;
        var writer = new RecordingWriter();
        var config = new LoggerConfig().AddWriter(writer);
        using var engine = new LoggerEngine(config);
        config.SetMinimumLevel(LogLevel.Debug);
        engine.Log("seen", LogLevel.Debug);
        Assert.Equal("seen", Assert.Single(writer.Events).Message);
    }

    /// <summary>A null or empty configuration value is shown as an empty pair of quotes.</summary>
    [Fact(Timeout = 30000)]
    public async Task Shown_BlankValueIsEmpty()
    {
        await Task.CompletedTask;
        Assert.Equal(string.Empty, LogOptionRules.Shown(null));
        Assert.Equal(string.Empty, LogOptionRules.Shown(string.Empty));
        Assert.Equal("a b", LogOptionRules.Shown("a\rb"));
    }

    /// <summary>Begin keeps a usable pair and ignores a pair whose key is null.</summary>
    [Fact(Timeout = 30000)]
    public async Task Scope_BeginCopiesAUsablePairAndSkipsABlankOne()
    {
        await Task.CompletedTask;
        var scope = ScopeContext.Begin(new[] { new KeyValuePair<string, object?>("job", "1") });
        try
        {
            Assert.Equal("1", ContextManager.GetContext()["job"]);
        }
        finally
        {
            scope.Dispose();
        }

        var skipped = ScopeContext.Begin(new[] { new KeyValuePair<string, object?>(null!, "x") });
        Assert.IsType<NoOpLogScope>(skipped);
        skipped.Dispose();
        ContextManager.Remove(null!);
        Assert.False(ContextManager.GetContext().ContainsKey("job"));
    }

    /// <summary>An empty stack is left off the line. A carriage return before a newline is not its own indented line.</summary>
    [Fact(Timeout = 30000)]
    public async Task PlainText_OmitsAnEmptyStackAndIndentsACarriageReturn()
    {
        await Task.CompletedTask;
        var when = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var blank = new PlainTextFormatter(includeStackTrace: true).Format(
            new LogEvent(when, LogLevel.Error, "blank", new BlankException()));
        Assert.Contains("blank", blank, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", blank, StringComparison.Ordinal);

        var cr = new PlainTextFormatter(includeStackTrace: true).Format(
            new LogEvent(when, LogLevel.Error, "cr", new CrException()));
        Assert.Contains("    top", cr, StringComparison.Ordinal);
        Assert.Contains("    bottom", cr, StringComparison.Ordinal);
        Assert.DoesNotContain("    \r", cr, StringComparison.Ordinal);
    }

    /// <summary>Unblocks a dispose that is still waiting, then waits up to the hang guard.</summary>
    private static async Task ReleaseDispose(FakeTimeProvider clock, Task? dispose, TimeSpan bound)
    {
        if (dispose is null)
            return;

        if (!dispose.IsCompleted)
            clock.Advance(bound);

        _ = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>Reads the file while a writer may still hold it.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The file length while a writer may still hold it.</summary>
    private static long LengthShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    /// <summary>A clock parked at 2026-06-01 so file lines and flush timers are deterministic.</summary>
    private static FakeTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>One info event at the parked clock time.</summary>
    private static LogEvent Event(string message)
        => new(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>The directory's current mode. Linux only.</summary>
    /// <param name="directory">The directory. Not null.</param>
    /// <returns>The mode to restore later.</returns>
    [SupportedOSPlatform("linux")]
    private static UnixFileMode ReadDirectoryMode(string directory)
        => File.GetUnixFileMode(directory);

    /// <summary>Removes write permission so a rename in this directory fails.</summary>
    /// <param name="directory">The directory. Not null.</param>
    /// <param name="mode">The mode to start from. Write bits are cleared.</param>
    [SupportedOSPlatform("linux")]
    private static void LockDirectory(string directory, UnixFileMode mode)
        => File.SetUnixFileMode(directory, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

    /// <summary>Puts the directory mode back so the test can delete it.</summary>
    /// <param name="directory">The directory. Not null.</param>
    /// <param name="mode">The mode from <see cref="ReadDirectoryMode"/>.</param>
    [SupportedOSPlatform("linux")]
    private static void RestoreDirectory(string directory, UnixFileMode mode)
        => File.SetUnixFileMode(directory, mode);

    /// <summary>Creates an empty directory that belongs to this test.</summary>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-bkp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes the directory. A file still open elsewhere is left for the process to drop.</summary>
    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Dispose throws. Write and flush do not.</summary>
    private sealed class ThrowingDisposeWriter : ILogWriter
    {
        /// <summary>Accepts the event.</summary>
        public void Write(LogEvent logEvent)
        {
        }

        /// <summary>Accepts the event and returns a completed task.</summary>
        public Task WriteAsync(LogEvent logEvent) => Task.CompletedTask;

        /// <summary>Returns. Flush is not the path under test.</summary>
        public void Flush()
        {
        }

        /// <summary>Throws so the engine can report the failure.</summary>
        public void Dispose() => throw new InvalidOperationException("dispose-broke");
    }

    /// <summary>Rejects every event.</summary>
    private sealed class DenyAll : ILogFilter
    {
        /// <summary>Always false.</summary>
        /// <param name="logEvent">The event the engine asked about. Ignored.</param>
        /// <returns>False.</returns>
        public bool ShouldLog(LogEvent logEvent) => false;
    }

    /// <summary><see cref="Exception.ToString"/> is empty, so the stack segment is omitted.</summary>
    private sealed class BlankException : Exception
    {
        /// <summary>Returns an empty stack.</summary>
        /// <returns>An empty string.</returns>
        public override string ToString() => string.Empty;
    }

    /// <summary><see cref="Exception.ToString"/> contains a carriage return before a newline.</summary>
    private sealed class CrException : Exception
    {
        /// <summary>Returns two lines separated by a carriage return and a newline.</summary>
        /// <returns><c>top</c>, then <c>bottom</c>.</returns>
        public override string ToString() => "top\r\nbottom";
    }
}
