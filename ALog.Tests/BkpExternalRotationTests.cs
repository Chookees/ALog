using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using ALog.Config;
using ALog.Core;
using ALog.Public.Interfaces;
using ALog.Writers.File;
using Microsoft.Extensions.Time.Testing;

namespace ALog.Tests;

/// <summary>
/// External rename, delete, and truncate of a file that <see cref="FileLogWriter"/> still has open.
/// </summary>
public sealed class BkpExternalRotationTests
{
    /// <summary>
    /// On Windows, <c>File.ReadAllText</c> reads flushed lines while the writer is still alive.
    /// </summary>
    /// <remarks>
    /// A zero flush interval writes each line and keeps the write handle. <see cref="FileLogWriter.Flush"/>
    /// replaces that handle with a read handle. <c>File.ReadAllText</c> asks for <see cref="FileShare.Read"/>,
    /// which succeeds against that read handle and throws while the write handle is still open.
    /// The writer is not disposed. Other systems skip this test: Linux and macOS do not enforce the share mode,
    /// so the assertion would not prove the Windows contract. Not thread-safe. The wait bound is 30 seconds.
    /// </remarks>
    [SkippableFact(Timeout = 30000)]
    public async Task File_ReadAllTextSucceedsAfterFlushWhileAlive()
    {
        await Task.CompletedTask;
        Skip.IfNot(OperatingSystem.IsWindows(), "FileShare is not enforced on this operating system.");
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "readers.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("first-line"));
            writer.Write(Event("second-line"));
            writer.Flush();
            var text = File.ReadAllText(path);
            Assert.Contains("first-line", text, StringComparison.Ordinal);
            Assert.Contains("second-line", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// On Windows, lines written while a <see cref="FileShare.Read"/> reader holds the file appear after that reader closes.
    /// </summary>
    /// <remarks>
    /// <see cref="FileLogWriter.Flush"/> parks a read handle. The reader then opens with <see cref="FileShare.Read"/>,
    /// so the next write cannot reopen the write handle. The lines stay buffered and the file clock retries.
    /// The writer is not disposed before the lines appear. Other systems skip this test: they do not enforce the share mode.
    /// Not thread-safe. The wait bound is 30 seconds.
    /// </remarks>
    [SkippableFact(Timeout = 60000)]
    public async Task File_LinesAppearAfterAReadShareIsReleased()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "FileShare is not enforced on this operating system.");
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "share.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("first-line"));
            writer.Flush();
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                writer.Write(Event("second-line"));
                writer.Write(Event("third-line"));
            }

            await DeadlineClock.WaitFor(
                () => TestFiles.ReadShared(path).Contains("third-line", StringComparison.Ordinal),
                "lines did not appear after the reader closed");
            var text = TestFiles.ReadShared(path);
            var first = text.IndexOf("first-line", StringComparison.Ordinal);
            var second = text.IndexOf("second-line", StringComparison.Ordinal);
            var third = text.IndexOf("third-line", StringComparison.Ordinal);
            Assert.True(first >= 0 && first < second && second < third);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A rename leaves the first line in the old file and the next line in a new file at the configured path.
    /// </summary>
    /// <remarks>
    /// A zero flush interval compares the path before the next write, so the clock does not move.
    /// Removing the path comparison writes the second line into the renamed file.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalRename_PutsTheNextLineInTheNewFile()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var rotated = path + ".1";
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock);
            writer.Write(Event("first-line"));
            File.Move(path, rotated);
            writer.Write(Event("second-line"));
            AssertOneHandle(path);
            writer.Dispose();

            var oldText = File.ReadAllText(rotated);
            var newText = File.ReadAllText(path);
            AssertNoNul(rotated);
            AssertNoNul(path);
            Assert.Contains("first-line", oldText, StringComparison.Ordinal);
            Assert.DoesNotContain("second-line", oldText, StringComparison.Ordinal);
            Assert.Contains("second-line", newText, StringComparison.Ordinal);
            Assert.DoesNotContain("first-line", newText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A path that someone else recreated is appended to. The renamed file keeps the earlier line.</summary>
    /// <remarks>
    /// The recreated file is shorter than the write position, so the next line is appended there.
    /// Reading <c>FileStream.Length</c> on the open handle instead of <c>FileInfo.Length</c>
    /// on the path keeps writing the renamed file: that handle still names the longer file. This test fails that change.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalRename_AppendsWhenThePathWasRecreated()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var rotated = path + ".1";
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock);
            writer.Write(Event("first-line"));
            File.Move(path, rotated);
            File.WriteAllText(path, "external\n");
            writer.Write(Event("second-line"));
            AssertOneHandle(path);
            writer.Dispose();

            var oldText = File.ReadAllText(rotated);
            var newText = File.ReadAllText(path);
            AssertNoNul(rotated);
            AssertNoNul(path);
            Assert.Contains("first-line", oldText, StringComparison.Ordinal);
            Assert.DoesNotContain("second-line", oldText, StringComparison.Ordinal);
            Assert.Contains("external", newText, StringComparison.Ordinal);
            Assert.Contains("second-line", newText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Bytes still in the buffer are written to the renamed file before that handle is closed.</summary>
    /// <remarks>
    /// The flush interval is the check. Closing without flushing leaves the renamed file empty.
    /// The new file must not receive that buffered line.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalRename_FlushesBufferedLinesIntoTheRenamedFile()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var rotated = path + ".1";
            var interval = TimeSpan.FromSeconds(60);
            using var writer = new FileLogWriter(path, fileFlushInterval: interval, clock: clock);
            writer.Write(Event("buffered-line"));
            Assert.DoesNotContain("buffered-line", TestFiles.ReadShared(path), StringComparison.Ordinal);
            File.Move(path, rotated);
            clock.Advance(interval);
            AssertOneHandle(path);

            AssertNoNul(rotated);
            Assert.Contains("buffered-line", File.ReadAllText(rotated), StringComparison.Ordinal);
            Assert.DoesNotContain("buffered-line", TestFiles.ReadShared(path), StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Size rotation does not rename a file that an external move already took. The buffered line stays with the old file.</summary>
    /// <remarks>The check is forced because a rotation is about to run, even though the flush interval has not elapsed.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalRename_KeepsSizeRotationOffTheReplacement()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var rotated = path + ".1";
            using var writer = new FileLogWriter(
                path,
                maxFileSizeInBytes: 1,
                fileFlushInterval: TimeSpan.FromSeconds(60),
                clock: clock);
            writer.Write(Event("first-line"));
            File.Move(path, rotated);
            writer.Write(Event("second-line"));
            AssertOneHandle(path);
            writer.Dispose();

            AssertNoNul(rotated);
            AssertNoNul(path);
            Assert.Contains("first-line", File.ReadAllText(rotated), StringComparison.Ordinal);
            Assert.Contains("second-line", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>Deleting the path makes the next line create it again.</summary>
    /// <remarks>The open handle still refers to the deleted inode until the check closes it.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalDelete_RecreatesThePath()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock);
            writer.Write(Event("first-line"));
            File.Delete(path);
            Assert.False(File.Exists(path));
            writer.Write(Event("second-line"));
            AssertOneHandle(path);
            writer.Dispose();

            var text = File.ReadAllText(path);
            AssertNoNul(path);
            Assert.Contains("second-line", text, StringComparison.Ordinal);
            Assert.DoesNotContain("first-line", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Copy-and-truncate leaves the next line at the start of the file and does not insert zero bytes.
    /// </summary>
    /// <remarks>
    /// On Unix an append stream does not move when another handle truncates the file.
    /// Skipping the length comparison writes the next line at the old offset and the read-back contains zero bytes.
    /// Windows denies that second write because the share mode excludes writers, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_WritesTheNextLineAtTheStart()
    {
        await Task.CompletedTask;
        if (OperatingSystem.IsWindows())
            return;

        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var copy = path + ".1";
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock);
            writer.Write(Event("first-line"));
            File.Copy(path, copy);
            using (var truncating = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
            }

            writer.Write(Event("second-line"));
            AssertOneHandle(path);
            writer.Dispose();

            var raw = File.ReadAllBytes(path);
            Assert.NotEmpty(raw);
            Assert.Equal(-1, Array.IndexOf(raw, (byte)0));
            Assert.Single(File.ReadAllLines(path));
            var text = Encoding.UTF8.GetString(raw);
            Assert.Contains("second-line", text, StringComparison.Ordinal);
            Assert.DoesNotContain("first-line", text, StringComparison.Ordinal);
            Assert.Equal((byte)'[', raw[0]);
            Assert.Contains("first-line", File.ReadAllText(copy), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A positive flush interval compares the path once after that interval, not on every event.</summary>
    /// <remarks>
    /// Comparing on every event makes the first burst larger than zero. That mutant is this interval, which is one second.
    /// A zero interval compares before every write and is not this test. Removing the comparison leaves the count at zero after the gap.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalCheck_SkipsABurstAndRunsAfterTheGap()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            var before = LogFileRegistry.IdentityCheckCount;
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(1), clock: clock);
            for (var i = 0; i < 8; i++)
                writer.Write(Event("n" + i.ToString(CultureInfo.InvariantCulture)));

            Assert.Equal(0, LogFileRegistry.IdentityCheckCount - before);
            clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
            writer.Write(Event("early"));
            Assert.Equal(0, LogFileRegistry.IdentityCheckCount - before);
            clock.Advance(TimeSpan.FromTicks(1));
            writer.Write(Event("due"));
            Assert.Equal(1, LogFileRegistry.IdentityCheckCount - before);
            for (var i = 0; i < 8; i++)
                writer.Write(Event("m" + i.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(1, LogFileRegistry.IdentityCheckCount - before);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>An explicit zero interval still writes the first event when the configuration does not set one.</summary>
    /// <remarks>Applying the configuration default of one second on every writer leaves this line in the buffer.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExplicitFileInterval_ReachesDiskOnTheFirstEvent()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "explicit.log");
            using var engine = new LoggerEngine(
                new LoggerConfig().AddWriter(new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock)),
                clock);
            engine.Log("on-disk");
            Assert.Contains("on-disk", TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Dispose();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>An explicit 4096-byte buffer flushes when it fills, not at 64 KiB.</summary>
    /// <remarks>The configuration does not set a buffer. Overwriting the constructor with 64 KiB keeps both lines in memory.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExplicitFileBuffer_FlushesWhenThatBufferFills()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "buffer.log");
            using var engine = new LoggerEngine(
                new LoggerConfig().AddWriter(new FileLogWriter(
                    path,
                    fileBufferSize: 4096,
                    fileFlushInterval: TimeSpan.FromSeconds(60),
                    clock: clock)),
                clock);
            var payload = new string('B', 3000);
            engine.Log(payload);
            Assert.DoesNotContain(payload, TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Log(payload);
            Assert.Contains(payload, TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Dispose();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A configuration interval applies when the writer was constructed without one.</summary>
    /// <remarks>Ignoring a set interval leaves the default of one second, so the line stays buffered.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ConfigFileInterval_AppliesWhenTheWriterUsesTheDefault()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "config.log");
            using var engine = new LoggerEngine(
                new LoggerConfig()
                    .AddWriter(new FileLogWriter(path, clock: clock))
                    .SetFileFlushInterval(TimeSpan.Zero),
                clock);
            engine.Log("from-config");
            Assert.Contains("from-config", TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Dispose();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A configuration buffer of 4096 applies when the writer was constructed without a buffer.</summary>
    /// <remarks>The writer's interval stays the explicit one minute, so only the smaller buffer can explain the bytes.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ConfigFileBuffer_AppliesWhenTheWriterUsesTheDefault()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "config-buffer.log");
            using var engine = new LoggerEngine(
                new LoggerConfig()
                    .AddWriter(new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock))
                    .SetFileBufferSize(4096),
                clock);
            var payload = new string('C', 3000);
            engine.Log(payload);
            Assert.DoesNotContain(payload, TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Log(payload);
            Assert.Contains(payload, TestFiles.ReadShared(path), StringComparison.Ordinal);
            engine.Dispose();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Dispose writes buffered lines at the start after an external truncate, with no zero bytes.
    /// </summary>
    /// <remarks>
    /// Fifty lines are flushed, the file is truncated, and ten lines stay in the buffer.
    /// Dispose runs before the flush interval, so only the close path can notice the shorter file.
    /// Skipping that check writes the ten lines at the old position and the file contains a zero byte.
    /// Windows denies the second write, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_DisposeWritesBufferedLinesWithoutNulBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "app.log");
            using (var engine = new LoggerEngine(
                new LoggerConfig().AddWriter(new FileLogWriter(
                    path,
                    fileFlushInterval: TimeSpan.FromSeconds(60),
                    clock: clock)),
                clock))
            {
                for (var i = 0; i < 50; i++)
                    engine.Log("flushed-" + i.ToString(CultureInfo.InvariantCulture));

                await engine.FlushAsync();
                using (var truncating = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    truncating.SetLength(0);
                }

                for (var i = 0; i < 10; i++)
                    engine.Log("kept-" + i.ToString(CultureInfo.InvariantCulture));
            }

            AssertNoHandle(path);
            var raw = File.ReadAllBytes(path);
            Assert.NotEmpty(raw);
            Assert.Equal(-1, Array.IndexOf(raw, (byte)0));
            Assert.Equal((byte)'[', raw[0]);
            var lines = File.ReadAllLines(path);
            Assert.Equal(10, lines.Length);
            Assert.Contains("kept-0", lines[0], StringComparison.Ordinal);
            Assert.Contains("kept-9", lines[9], StringComparison.Ordinal);
            Assert.DoesNotContain("flushed-0", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>After a failed reopen, the next event tries the path again and still drops the line while the directory is locked.</summary>
    /// <remarks>
    /// A zero interval notices the missing path before size rotation. Creating the file fails while the directory is locked.
    /// The following event reports its own write failure. After the directory is restored, the next line is written.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalReopen_RetriesWhenTheDirectoryIsLocked()
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
            var path = Path.Combine(directory, "retry.log");
            var mode = ReadDirectoryMode(directory);
            using var writer = new FileLogWriter(path, maxFileSizeInBytes: 1, fileFlushInterval: TimeSpan.Zero);
            try
            {
                writer.Write(Event("first-line"));
                File.Delete(path);
                LockDirectory(directory, mode);
                writer.Write(Event("second-line"));
                writer.Write(Event("third-line"));
            }
            finally
            {
                RestoreDirectory(directory, mode);
            }

            writer.Write(Event("fourth-line"));
            var text = File.ReadAllText(path);
            Assert.Contains("fourth-line", text, StringComparison.Ordinal);
            Assert.DoesNotContain("third-line", text, StringComparison.Ordinal);
            Assert.Contains("Write failed", buffer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A flush that fails while the path is switched is reported, and the writer opens the path again.
    /// </summary>
    /// <remarks>
    /// The old handle is a full device, so the buffered bytes cannot be written. The buffer size is also
    /// different from the array, which is the branch taken when the stream is opened again.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalReopen_ReportsAFlushFailureOnTheOldHandle()
    {
        await Task.CompletedTask;
        if (!OperatingSystem.IsLinux())
            return;

        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        FileStream? replaced = null;
        try
        {
            var path = Path.Combine(directory, "full.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            writer.Write(Event("buffered-line"));
            var shared = SharedOf(writer);
            var original = (FileStream)GetField(shared, "_stream")!;
            replaced = new FileStream(
                "/dev/full",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            SetField(shared, "_stream", replaced);
            original.Dispose();
            File.Delete(path);
            SetField(shared, "_bufferSize", 8192);
            writer.Flush();
            Assert.Contains("Write failed", buffer.ToString(), StringComparison.Ordinal);
            writer.Write(Event("after-failure"));
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Contains("after-failure", TestFiles.ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                replaced?.Dispose();
            }
            catch (IOException)
            {
            }

            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>A missing check time compares the path on the next event.</summary>
    /// <remarks>Opening the stream records a time, so this clears that field and writes again without moving the clock.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalCheck_RunsWhenNoCheckWasRecorded()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "due.log");
            var before = LogFileRegistry.IdentityCheckCount;
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            writer.Write(Event("first"));
            Assert.Equal(0, LogFileRegistry.IdentityCheckCount - before);
            SetField(SharedOf(writer), "_lastIdentityCheck", default(DateTimeOffset));
            writer.Write(Event("second"));
            Assert.Equal(1, LogFileRegistry.IdentityCheckCount - before);
            writer.Flush();
            Assert.Contains("second", TestFiles.ReadShared(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A flush tick after dispose returns without touching the file.</summary>
    /// <remarks>The timer is disposed with the writer. This calls the tick once more, which is what a late callback does.</remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalFlush_StopsWhenTheWriterIsClosed()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        try
        {
            var path = Path.Combine(directory, "closed.log");
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(30), clock: clock);
            writer.Write(Event("kept"));
            var shared = SharedOf(writer);
            writer.Dispose();
            var tick = typeof(SharedLogFile).GetMethod("OnInterval", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tick);
            tick!.Invoke(shared, null);
            Assert.Contains("kept", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Dispose after an external truncate of a pre-filled file keeps the new lines and does not seek.
    /// </summary>
    /// <remarks>
    /// Twenty lines exist before ALog opens the file, so the append start is past byte 0.
    /// Fifty lines are flushed, the file is truncated, and ten lines stay in the buffer until dispose.
    /// Re-adding a seek to byte 0 throws because append mode cannot move before that start, and these ten lines are missing.
    /// Windows denies the second write, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_PrefilledDisposeKeepsTheNewLines()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "app.log");
            WritePlainLines(path, 20, "prefilled-");
            using (var engine = new LoggerEngine(
                new LoggerConfig().AddWriter(new FileLogWriter(
                    path,
                    fileFlushInterval: TimeSpan.FromSeconds(60),
                    clock: clock)),
                clock))
            {
                for (var i = 0; i < 50; i++)
                    engine.Log("flushed-" + i.ToString(CultureInfo.InvariantCulture));

                await engine.FlushAsync();
                SetLengthShared(path, 0);
                for (var i = 0; i < 10; i++)
                    engine.Log("kept-" + i.ToString(CultureInfo.InvariantCulture));
            }

            AssertNoSeekFailure(errors.ToString());
            AssertNoHandle(path);
            AssertClean(path, "kept-0", "kept-9");
            Assert.Equal(10, File.ReadAllLines(path).Length);
            Assert.DoesNotContain("prefilled-0", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("flushed-0", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal((byte)'[', File.ReadAllBytes(path)[0]);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>A pre-filled file that is renamed and recreated receives the next line, with one open handle.</summary>
    /// <remarks>
    /// The recreated file is shorter than the write position. The buffered line is written only after the new stream opens.
    /// Writing it into the old stream first leaves a zero byte. Seeking to byte 0 throws because the file was not empty at open.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalRename_PrefilledRecreateKeepsTheNewLines()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "app.log");
            var rotated = path + ".1";
            WritePlainLines(path, 20, "prefilled-");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            writer.Write(Event("buffered-line"));
            File.Move(path, rotated);
            File.WriteAllText(path, "external\n");
            writer.Flush();
            AssertOneHandle(path);
            AssertNoSeekFailure(errors.ToString());
            writer.Dispose();

            AssertNoNul(rotated);
            AssertClean(path, "external", "buffered-line");
            Assert.Contains("prefilled-0", File.ReadAllText(rotated), StringComparison.Ordinal);
            Assert.DoesNotContain("buffered-line", File.ReadAllText(rotated), StringComparison.Ordinal);
            Assert.Equal((byte)'e', File.ReadAllBytes(path)[0]);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>A later truncate in the same process still appends the new lines and leaves one handle.</summary>
    /// <remarks>
    /// The first truncate leaves 100 bytes, which is above zero. The writer reopens, then the file is truncated to empty.
    /// Dispose writes the later batch. A seek on either reopen throws or leaves a zero byte.
    /// Windows denies the second write, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_FollowUpCopyKeepsTheNewLines()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "app.log");
            WritePlainLines(path, 20, "prefilled-");
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            using (var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer), clock))
            {
                for (var i = 0; i < 50; i++)
                    engine.Log("flushed-" + i.ToString(CultureInfo.InvariantCulture));

                await engine.FlushAsync();
                SetLengthShared(path, 100);
                for (var i = 0; i < 10; i++)
                    engine.Log("wave-a-" + i.ToString(CultureInfo.InvariantCulture));

                writer.Flush();
                AssertOneHandle(path);
                AssertNoSeekFailure(errors.ToString());
                AssertClean(path, "wave-a-0", "wave-a-9");

                SetLengthShared(path, 0);
                for (var i = 0; i < 10; i++)
                    engine.Log("wave-b-" + i.ToString(CultureInfo.InvariantCulture));
            }

            AssertNoSeekFailure(errors.ToString());
            AssertNoHandle(path);
            AssertClean(path, "wave-b-0", "wave-b-9");
            Assert.Equal(10, File.ReadAllLines(path).Length);
            Assert.DoesNotContain("wave-a-0", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal((byte)'[', File.ReadAllBytes(path)[0]);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>Copy-and-truncate disposes the old stream, so the path has one handle and no buffered prefix.</summary>
    /// <remarks>
    /// The ten lines were still in the buffer. They are written at the start of the truncated file, not at the old offset.
    /// Writing them into the old stream first inserts a zero byte before them. The old handle is closed.
    /// Windows denies the second write, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_LeavesOneHandleAndNoOldPrefix()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "app.log");
            WritePlainLines(path, 20, "prefilled-");
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.FromSeconds(60), clock: clock);
            using (var engine = new LoggerEngine(new LoggerConfig().AddWriter(writer), clock))
            {
                for (var i = 0; i < 50; i++)
                    engine.Log("flushed-" + i.ToString(CultureInfo.InvariantCulture));

                await engine.FlushAsync();
                var oldStream = (FileStream)GetField(SharedOf(writer), "_stream")!;
                SetLengthShared(path, 0);
                for (var i = 0; i < 10; i++)
                    engine.Log("kept-" + i.ToString(CultureInfo.InvariantCulture));

                writer.Flush();
                Assert.True(oldStream.SafeFileHandle.IsClosed);
                AssertOneHandle(path);
                AssertNoSeekFailure(errors.ToString());
                AssertClean(path, "kept-0", "kept-9");
                Assert.DoesNotContain("flushed-0", File.ReadAllText(path), StringComparison.Ordinal);
                Assert.Equal((byte)'[', File.ReadAllBytes(path)[0]);
            }

            AssertNoHandle(path);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    /// <summary>A zero flush interval notices a truncate before the next line reaches the operating system.</summary>
    /// <remarks>
    /// Fifty lines are already on disk. The file is then emptied, and ten more lines are written without waiting out a one-second gap.
    /// The clock then moves 1200 milliseconds. Dropping the per-write check writes those lines at the old offset, so the file contains a zero byte.
    /// The same check is what notices a truncate to 100 bytes. Windows denies the second write, so this case is the Unix one.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ExternalTruncate_ZeroIntervalNoticesAShorterFile()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.CompletedTask;
            return;
        }

        var directory = NewDirectory();
        var clock = NewClock();
        var previous = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "app.log");
            WritePlainLines(path, 20, "prefilled-");
            AssertZeroIntervalTruncate(path, clock, errors, 0);
            var hundred = Path.Combine(directory, "hundred.log");
            WritePlainLines(hundred, 20, "prefilled-");
            AssertZeroIntervalTruncate(hundred, clock, errors, 100);
        }
        finally
        {
            Console.SetError(previous);
            DeleteDirectory(directory);
        }
    }

    private static object SharedOf(FileLogWriter writer)
    {
        var field = typeof(FileLogWriter).GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var shared = field!.GetValue(writer);
        Assert.NotNull(shared);
        return shared!;
    }

    private static object? GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    [SupportedOSPlatform("linux")]
    private static UnixFileMode ReadDirectoryMode(string directory)
        => File.GetUnixFileMode(directory);

    /// <summary>Removes write permission so a create in this directory fails.</summary>
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


    /// <summary>Writes <paramref name="count"/> plain lines before ALog opens the file.</summary>
    /// <param name="path">File to create. Not null.</param>
    /// <param name="count">How many lines. Greater than zero.</param>
    /// <param name="prefix">Text before the line number. Not null.</param>
    /// <remarks>Does not throw. The file is non-empty, so an append stream cannot seek back to byte 0.</remarks>
    private static void WritePlainLines(string path, int count, string prefix)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var i = 0; i < count; i++)
        {
            writer.Write(prefix);
            writer.WriteLine(i.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Truncates an open log from another handle.</summary>
    /// <param name="path">The log path. Not null.</param>
    /// <param name="length">The new length. Zero or a positive prefix.</param>
    /// <remarks>Does not throw on Unix. Windows rejects this while ALog holds the file without write sharing.</remarks>
    private static void SetLengthShared(string path, int length)
    {
        using var truncating = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        truncating.SetLength(length);
    }

    /// <summary>
    /// Writes fifty flushed lines, truncates, then ten more lines at interval zero.
    /// </summary>
    /// <param name="path">A pre-filled log. Not null.</param>
    /// <param name="clock">Clock for the writer. Not null.</param>
    /// <param name="errors">Error sink already installed as <see cref="Console.Error"/>. Not null.</param>
    /// <param name="length">Length after the external truncate. Zero or 100.</param>
    /// <remarks>
    /// The ten lines are written before the clock moves 1200 milliseconds.
    /// A check that waits for that gap leaves a zero byte, because those lines already extended the file.
    /// </remarks>
    private static void AssertZeroIntervalTruncate(string path, FakeTimeProvider clock, StringWriter errors, int length)
    {
        using (var engine = new LoggerEngine(
            new LoggerConfig().AddWriter(new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero, clock: clock)),
            clock))
        {
            for (var i = 0; i < 50; i++)
                engine.Log("flushed-" + i.ToString(CultureInfo.InvariantCulture));

            SetLengthShared(path, length);
            var marked = LogFileRegistry.IdentityCheckCount;
            for (var i = 0; i < 10; i++)
                engine.Log("kept-" + i.ToString(CultureInfo.InvariantCulture));

            Assert.True(LogFileRegistry.IdentityCheckCount - marked >= 10);
            AssertOneHandle(path);
            clock.Advance(TimeSpan.FromMilliseconds(1200));
        }

        AssertNoSeekFailure(errors.ToString());
        AssertNoHandle(path);
        AssertClean(path, "kept-0", "kept-9");
        if (length == 0)
        {
            Assert.Equal(10, File.ReadAllLines(path).Length);
            Assert.Equal((byte)'[', File.ReadAllBytes(path)[0]);
            Assert.DoesNotContain("prefilled-0", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("flushed-0", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>The file contains each required fragment and no zero byte.</summary>
    /// <param name="path">File to read. Not null.</param>
    /// <param name="required">Fragments that must appear. Not null.</param>
    /// <remarks>Does not throw for a missing fragment. That is an assertion failure.</remarks>
    private static void AssertClean(string path, params string[] required)
    {
        AssertNoNul(path);
        var text = File.ReadAllText(path);
        foreach (var item in required)
            Assert.Contains(item, text, StringComparison.Ordinal);
    }

    /// <summary>The file has bytes and none of them are zero.</summary>
    /// <param name="path">File to read. Not null.</param>
    /// <remarks>Does not throw when the file exists. A zero byte is an assertion failure.</remarks>
    private static void AssertNoNul(string path)
    {
        var raw = File.ReadAllBytes(path);
        Assert.NotEmpty(raw);
        Assert.Equal(-1, Array.IndexOf(raw, (byte)0));
    }

    /// <summary>The error text is not an append-mode seek failure.</summary>
    /// <param name="errors">Text written to the error stream. Not null.</param>
    /// <remarks>Does not throw. A seek failure is an assertion failure.</remarks>
    private static void AssertNoSeekFailure(string errors)
    {
        Assert.DoesNotContain("IOException", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to seek", errors, StringComparison.Ordinal);
    }

    /// <summary>On Linux the path has one open handle in this process.</summary>
    /// <param name="path">The log path. Not null.</param>
    /// <remarks>Other systems return. The count is this process only.</remarks>
    private static void AssertOneHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Equal(1, CountHandles(path));
    }

    /// <summary>On Linux the path has no open handle in this process.</summary>
    /// <param name="path">The log path. Not null.</param>
    /// <remarks>Other systems return. A leaked stream keeps a handle after dispose.</remarks>
    private static void AssertNoHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Equal(0, CountHandles(path));
    }

    /// <summary>Counts file descriptors in this process whose target is <paramref name="path"/>.</summary>
    /// <param name="path">Absolute or relative path. Not null.</param>
    /// <returns>How many descriptors name that path. A deleted target is not counted.</returns>
    /// <remarks>Linux only. Does not throw for a descriptor that disappears while it is read.</remarks>
    private static int CountHandles(string path)
    {
        var full = Path.GetFullPath(path);
        var count = 0;
        foreach (var entry in Directory.EnumerateFiles("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(entry).LinkTarget;
            }
            catch (IOException)
            {
                continue;
            }

            if (target is null)
                continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(target);
            }
            catch (IOException)
            {
                continue;
            }

            if (string.Equals(resolved, full, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static FakeTimeProvider NewClock()
        => new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static LogEvent Event(string message)
        => new(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-bkp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

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
}
