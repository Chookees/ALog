using System.Reflection;
using System.Text;
using ALog.Core;
using ALog.Formatters;
using ALog.Public.Interfaces;
using ALog.Writers.File;
namespace ALog.Tests;

/// <summary>
/// Opening an existing log for append must not glue the next line onto a fragment that lacks a trailing <c>\n</c>.
/// </summary>
public sealed class EofFragmentMergeTests
{
    /// <summary>
    /// A non-empty file that does not end with <c>\n</c> keeps the fragment on its own line
    /// when a new writer appends the next event.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FragmentAtEof_NextWriterKeepsFragmentAndNewLineApart()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "fragment.log");
            var fragment = "[2026-10-25 09:12:08] [Info] pdl0t-held-00";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(fragment));

            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(Event("after-restart"));
                writer.Flush();
            }

            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Equal(fragment + "\n", Encoding.UTF8.GetString(bytes, 0, fragment.Length + 1));
            Assert.Contains("after-restart", text, StringComparison.Ordinal);
            Assert.DoesNotContain("held-00[", text, StringComparison.Ordinal);
            Assert.Equal(2, CountChar(text, '\n'));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>An empty file gains no bytes from the open probe alone.</summary>
    [Fact(Timeout = 30000)]
    public async Task EmptyFile_OpenDoesNotWriteNewline()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "empty.log");
            File.WriteAllBytes(path, Array.Empty<byte>());
            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                Assert.Equal(0, new FileInfo(path).Length);
                writer.Write(Event("first-line"));
                writer.Flush();
            }

            var text = File.ReadAllText(path);
            Assert.False(text.StartsWith('\n'));
            Assert.Single(SplitLines(text));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A file that already ends with <c>\n</c> gains no extra byte on the next open.</summary>
    [Fact(Timeout = 30000)]
    public async Task FileEndingWithLf_OpenAddsZeroBytes()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "lf.log");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("complete\n"));
            var before = File.ReadAllBytes(path);
            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(Event("second-line"));
                writer.Flush();
            }

            var after = File.ReadAllBytes(path);
            Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before));
            Assert.False(HasDoubleLf(after));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A file that ends with <c>\r\n</c> gains no extra byte on the next open.</summary>
    [Fact(Timeout = 30000)]
    public async Task FileEndingWithCrLf_OpenAddsZeroBytes()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "crlf.log");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("complete\r\n"));
            var before = File.ReadAllBytes(path);
            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(Event("second-line"));
                writer.Flush();
            }

            var after = File.ReadAllBytes(path);
            Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before));
            Assert.True(after.Length > before.Length);
            Assert.Equal((byte)'[', after[before.Length]);
            Assert.False(HasDoubleLf(after));
            Assert.Equal(2, CountChar(Encoding.UTF8.GetString(after), '\n'));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>A file that ends with a lone <c>\r</c> gains exactly one <c>\n</c> before the next line.</summary>
    [Fact(Timeout = 30000)]
    public async Task FileEndingWithLoneCr_OpenAddsOneLf()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "cr.log");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("partial\r"));
            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(Event("next"));
                writer.Flush();
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal((byte)'\r', bytes[7]);
            Assert.Equal((byte)'\n', bytes[8]);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Equal(1, CountBetween(text, "partial", "[", '\n'));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The inserted <c>\n</c> is not a logged line: queue <see cref="BackgroundLogQueue.WrittenCount"/>
    /// equals the number of accepted events.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task InsertedLf_DoesNotChangeWrittenCount()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "count.log");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("orphan-fragment"));
            var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            using var queue = new BackgroundLogQueue(
                new ILogWriter[] { writer },
                capacity: 8,
                batchSize: 1,
                flushInterval: TimeSpan.FromHours(1),
                asyncEnabled: false);
            Assert.True(queue.TryEnqueue(Event("only-event")));
            queue.Dispose();
            Assert.Equal(1, queue.AcceptedCount);
            Assert.Equal(1, queue.WrittenCount);
            var text = File.ReadAllText(path);
            Assert.Contains("orphan-fragment\n", text, StringComparison.Ordinal);
            Assert.Contains("only-event", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A normal write/Flush cycle does not run the trailing-byte probe on ALog's own handle swap.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FlushSwap_SameLength_DoesNotProbe()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "flush.log");
            // Non-empty terminated file so the first open runs the probe once.
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("seed\n"));
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("one"));
            var afterFirstProbe = LogFileRegistry.EofProbeCount;
            var opensAfterFirstWrite = LogFileRegistry.OpenCount;
            writer.Flush();
            Assert.True(
                LogFileRegistry.OpenCount > opensAfterFirstWrite,
                "Flush must swap the handle so OpenCount rises.");
            var opensAfterFlush1 = LogFileRegistry.OpenCount;
            writer.Write(Event("two"));
            writer.Flush();
            Assert.True(
                LogFileRegistry.OpenCount > opensAfterFlush1,
                "Each flush cycle must open again.");
            var opensAfterFlush2 = LogFileRegistry.OpenCount;
            writer.Write(Event("three"));
            writer.Flush();
            Assert.True(LogFileRegistry.OpenCount > opensAfterFlush2);
            Assert.Equal(afterFirstProbe, LogFileRegistry.EofProbeCount);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// When the file length changes externally after a Flush swap, the next write open probes and adds <c>\n</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExternalAppendAfterFlush_NextOpenProbesAndAddsLf()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "external.log");
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("seed"));
            writer.Flush();
            var probesAfterFlush = LogFileRegistry.EofProbeCount;
            var lengthAfterFlush = new FileInfo(path).Length;

            // Extend the file while the writer holds only the read handle.
            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var extra = Encoding.UTF8.GetBytes("external-frag");
                append.Write(extra);
                append.Flush();
            }

            Assert.True(new FileInfo(path).Length > lengthAfterFlush);
            writer.Write(Event("after-external"));
            writer.Flush();
            Assert.True(LogFileRegistry.EofProbeCount > probesAfterFlush);
            var text = File.ReadAllText(path);
            Assert.Contains("external-frag\n", text, StringComparison.Ordinal);
            Assert.Contains("after-external", text, StringComparison.Ordinal);
            Assert.DoesNotContain("external-frag[", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// An EOF fragment plus a failed replay length reset share one flag: the next successful append
    /// inserts exactly one <c>\n</c> (no blank line).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProbeAndReplayFlag_InsertExactlyOneLf()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var previous = LogFileRegistry.OpenFactory;
        var previousError = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        var fault = new PartialReplay();
        try
        {
            var path = Path.Combine(directory, "both.log");
            var fragment = Encoding.UTF8.GetBytes("frag-from-disk");
            File.WriteAllBytes(path, fragment);
            LogFileRegistry.OpenFactory = (p, mode, access, share) =>
                new PartialReplayStream(p, mode, access, share, fault);

            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            var shared = SharedOf(writer);
            var line = Encoding.UTF8.GetBytes(new PlainTextFormatter().Format(Event("only-once")) + "\n");
            fault.Arm(failReset: true);
            var threw = false;
            try
            {
                shared.AppendReplay(line, maxBytes: null);
            }
            catch (IOException)
            {
                threw = true;
            }

            Assert.True(threw);
            var half = Volatile.Read(ref fault.Fragment);
            Assert.True(half.Length > 0);
            Assert.DoesNotContain("\n", half, StringComparison.Ordinal);
            Assert.Equal(1, NeedsLineSeparator(shared));

            var afterPartial = ReadSharedBytes(path);
            // Probe separator was written inside the failed WriteThrough and could not be rolled back.
            Assert.True(afterPartial.AsSpan(0, fragment.Length).SequenceEqual(fragment));
            Assert.Equal((byte)'\n', afterPartial[fragment.Length]);
            var halfBytes = Encoding.UTF8.GetBytes(half);
            Assert.True(
                afterPartial.AsSpan(fragment.Length + 1, halfBytes.Length).SequenceEqual(halfBytes));
            Assert.False(HasDoubleLf(afterPartial));

            shared.AppendReplay(line, maxBytes: null);
            var after = ReadSharedBytes(path);
            Assert.False(HasDoubleLf(after));
            Assert.Equal(0, NeedsLineSeparator(shared));

            // Exactly one \n between the stuck half-line and the successful line start.
            var halfAt = IndexOfSequence(after, halfBytes);
            Assert.True(halfAt >= 0);
            var afterHalf = halfAt + halfBytes.Length;
            Assert.Equal((byte)'\n', after[afterHalf]);
            Assert.Equal((byte)'[', after[afterHalf + 1]);
            Assert.Contains("only-once", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            LogFileRegistry.OpenFactory = previous;
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// When the trailing-byte probe cannot open the path, the append open still writes the next line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProbeFailure_OpenStillWritesTheNextLine()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var previous = LogFileRegistry.OpenFactory;
        var previousError = Console.Error;
        var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            var path = Path.Combine(directory, "probe.log");
            var orphan = Encoding.UTF8.GetBytes("orphan-fragment");
            File.WriteAllBytes(path, orphan);
            var probesBefore = LogFileRegistry.EofProbeCount;
            // Probe uses a direct FileStream on _path. Move the path after append opens so that read fails
            // while the write handle (unlinked inode) stays usable. A positive flush interval avoids
            // Reconcile treating the missing path as a reopen on this first write.
            LogFileRegistry.OpenFactory = (p, mode, access, share) =>
            {
                var stream = new FileStream(p, mode, access, share, bufferSize: 1, FileOptions.None);
                if (mode == FileMode.Append && File.Exists(p))
                    File.Move(p, p + ".moved", overwrite: true);
                return stream;
            };

            // Message longer than the configured buffer so the line is written despite a positive flush interval.
            var message = "after-probe-fail-" + new string('x', 5000);
            byte[] bytes;
            using (var writer = new FileLogWriter(
                       path,
                       fileBufferSize: 4096,
                       fileFlushInterval: TimeSpan.FromSeconds(60)))
            {
                writer.Write(Event(message));
                Assert.Equal(probesBefore, LogFileRegistry.EofProbeCount);
                Assert.Equal(0, NeedsLineSeparator(SharedOf(writer)));
                var stream = GetStream(SharedOf(writer));
                Assert.NotNull(stream);
                Assert.True(stream!.Length > orphan.Length);
                Assert.True(stream.Length >= orphan.Length + message.Length);

                // Read via the moved path while the write handle still owns the inode.
                bytes = ReadSharedBytes(path + ".moved");
            }

            // Probe open failed (path moved), so the flag stayed clear and the next line is glued.
            Assert.True(bytes.Length > orphan.Length);
            Assert.True(bytes.AsSpan(0, orphan.Length).SequenceEqual(orphan));
            Assert.Equal((byte)'[', bytes[orphan.Length]);
            Assert.DoesNotContain("Write failed", errors.ToString(), StringComparison.Ordinal);
            Assert.Equal(probesBefore, LogFileRegistry.EofProbeCount);
        }
        finally
        {
            Console.SetError(previousError);
            LogFileRegistry.OpenFactory = previous;
            DeleteDirectory(directory);
        }
    }

    /// <summary>One info event at a fixed UTC time so the formatted line is stable.</summary>
    private static LogEvent Event(string message)
        => new(new DateTime(2026, 10, 25, 9, 12, 58, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>Splits on <c>\n</c> and drops empty trailing entries.</summary>
    private static string[] SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when two <c>\n</c> bytes sit next to each other.</summary>
    private static bool HasDoubleLf(byte[] bytes)
    {
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i - 1] == (byte)'\n' && bytes[i] == (byte)'\n')
                return true;
        }

        return false;
    }

    /// <summary>Counts how many times <paramref name="ch"/> appears.</summary>
    private static int CountChar(string text, char ch)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ch)
                n++;
        }

        return n;
    }

    /// <summary>How many <paramref name="ch"/> sit between the first <paramref name="left"/> and <paramref name="right"/>.</summary>
    private static int CountBetween(string text, string left, string right, char ch)
    {
        var a = text.IndexOf(left, StringComparison.Ordinal);
        var b = text.IndexOf(right, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        var n = 0;
        for (var i = a + left.Length; i < b; i++)
        {
            if (text[i] == ch)
                n++;
        }

        return n;
    }

    /// <summary>The shared file behind a writer.</summary>
    private static SharedLogFile SharedOf(FileLogWriter writer)
    {
        var field = typeof(FileLogWriter).GetField("_shared", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SharedLogFile>(field!.GetValue(writer));
    }

    /// <summary>Reads the shared <c>_needsLineSeparator</c> flag.</summary>
    private static int NeedsLineSeparator(SharedLogFile shared)
    {
        var field = typeof(SharedLogFile).GetField("_needsLineSeparator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<int>(field!.GetValue(shared));
    }

    /// <summary>The live stream field, when present.</summary>
    private static FileStream? GetStream(SharedLogFile shared)
    {
        var field = typeof(SharedLogFile).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(shared) as FileStream;
    }

    /// <summary>Reads the file while another handle may still hold it.</summary>
    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
        var bytes = new byte[stream.Length];
        var read = stream.Read(bytes);
        Assert.Equal(bytes.Length, read);
        return bytes;
    }

    /// <summary>Index of <paramref name="needle"/> in <paramref name="haystack"/>, or -1.</summary>
    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    /// <summary>Creates an empty directory that belongs to this test.</summary>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-eof-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>One partial replay write, and whether the following length reset throws.</summary>
    private sealed class PartialReplay
    {
        internal int Active;
        internal int ThrowsLeft;
        internal int FailReset;
        internal string Fragment = string.Empty;

        /// <summary>Throws on the next multi-byte write, after half of that write has been stored.</summary>
        /// <param name="failReset">True when <see cref="FileStream.SetLength(long)"/> must throw.</param>
        /// <remarks>Does not throw. The reset flag and the budget are stored before the armed flag.</remarks>
        internal void Arm(bool failReset)
        {
            Volatile.Write(ref FailReset, failReset ? 1 : 0);
            Volatile.Write(ref ThrowsLeft, 1);
            Volatile.Write(ref Active, 1);
        }

        /// <summary>Stores the bytes that were written before the throw.</summary>
        /// <param name="buffer">The write buffer. Not null.</param>
        /// <param name="offset">Start of the written half.</param>
        /// <param name="count">How many bytes were written.</param>
        /// <remarks>Does not throw. The copy is bounded by <paramref name="count"/>.</remarks>
        internal void Remember(byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            Volatile.Write(ref Fragment, Encoding.UTF8.GetString(copy));
        }
    }

    /// <summary>A file stream that writes half of one armed replay line and then throws.</summary>
    private sealed class PartialReplayStream : FileStream
    {
        private readonly PartialReplay _fault;

        /// <summary>Opens <paramref name="path"/> with no internal buffer.</summary>
        /// <param name="path">The real file. Not null.</param>
        /// <param name="mode">Passed to <see cref="FileStream"/>.</param>
        /// <param name="access">Passed to <see cref="FileStream"/>.</param>
        /// <param name="share">Passed to <see cref="FileStream"/>.</param>
        /// <param name="fault">The shared fault. Not null.</param>
        /// <remarks>May throw the base constructor's I/O exception. Does not throw the replay fault.</remarks>
        internal PartialReplayStream(string path, FileMode mode, FileAccess access, FileShare share, PartialReplay fault)
            : base(path, mode, access, share, 1, FileOptions.None)
        {
            _fault = fault;
        }

        /// <summary>Writes half of an armed multi-byte buffer and throws. Single-byte writes always succeed.</summary>
        /// <param name="buffer">The bytes. Not null.</param>
        /// <param name="offset">Start in <paramref name="buffer"/>.</param>
        /// <param name="count">How many bytes.</param>
        /// <exception cref="IOException">This is the armed partial write.</exception>
        /// <remarks>
        /// Count 1 is the separator <c>\n</c> from <c>WriteByte</c>; it must not consume the throw budget.
        /// Half is <paramref name="count"/> divided by two. The base write is the array method.
        /// </remarks>
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteFaultable(buffer, offset, count);
        }

        /// <summary>Span writes use the same partial-fault path as the array overload.</summary>
        /// <param name="buffer">The bytes to write.</param>
        /// <exception cref="IOException">This is the armed partial write.</exception>
        /// <remarks>Copies to an array so the fault logic stays in one place.</remarks>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer.ToArray();
            WriteFaultable(copy, 0, copy.Length);
        }

        /// <summary>Shared write body for array and span overloads.</summary>
        /// <param name="buffer">The bytes. Not null.</param>
        /// <param name="offset">Start in <paramref name="buffer"/>.</param>
        /// <param name="count">How many bytes.</param>
        /// <exception cref="IOException">This is the armed partial write.</exception>
        /// <remarks>Does not recurse into the span overload. Count 1 always succeeds.</remarks>
        private void WriteFaultable(byte[] buffer, int offset, int count)
        {
            if (count > 1
                && Volatile.Read(ref _fault.Active) != 0
                && Interlocked.Decrement(ref _fault.ThrowsLeft) >= 0)
            {
                var take = count / 2;
                if (take > 0)
                {
                    base.Write(buffer, offset, take);
                    base.Flush();
                }

                _fault.Remember(buffer, offset, take);
                throw new IOException("partial replay");
            }

            base.Write(buffer, offset, count);
        }

        /// <summary>Throws when the fault wants the reset to fail. Otherwise truncates.</summary>
        /// <param name="value">The length to keep.</param>
        /// <exception cref="IOException">The fault was armed with a failing reset.</exception>
        /// <remarks>The replay path calls this after a partial write. A throw leaves the half line in the file.</remarks>
        public override void SetLength(long value)
        {
            if (Volatile.Read(ref _fault.FailReset) != 0)
                throw new IOException("reset failed");

            base.SetLength(value);
        }
    }
}
