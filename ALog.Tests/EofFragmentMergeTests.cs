using System.Reflection;
using System.Text;
using ALog.Core;
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
            var afterFirst = LogFileRegistry.EofProbeCount;
            writer.Flush();
            writer.Write(Event("two"));
            writer.Flush();
            writer.Write(Event("three"));
            writer.Flush();
            Assert.Equal(afterFirst, LogFileRegistry.EofProbeCount);
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
    /// Probe and replay recovery share one flag: at most one <c>\n</c> is inserted before the next append.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProbeAndReplayFlag_InsertExactlyOneLf()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "both.log");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("frag-from-disk"));
            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            var shared = SharedOf(writer);
            // Probe already set the shared flag; setting it again must not double the separator.
            SetNeedsLineSeparator(shared, 1);
            writer.Write(Event("only-once"));
            writer.Flush();
            var text = File.ReadAllText(path);
            Assert.Contains("frag-from-disk\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("frag-from-disk\n\n", text, StringComparison.Ordinal);
            Assert.Equal(1, CountBetween(text, "frag-from-disk", "only-once", '\n'));
        }
        finally
        {
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
            using (var writer = new FileLogWriter(
                       path,
                       fileBufferSize: 4096,
                       fileFlushInterval: TimeSpan.FromSeconds(60)))
            {
                writer.Write(Event(message));
                var stream = GetStream(SharedOf(writer));
                Assert.NotNull(stream);
                Assert.True(stream!.Length > orphan.Length);
                Assert.True(stream.Length >= orphan.Length + message.Length);
            }

            Assert.DoesNotContain("Write failed", errors.ToString(), StringComparison.Ordinal);
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

    /// <summary>Sets the shared needs-separator flag used by the open probe and replay recovery.</summary>
    private static void SetNeedsLineSeparator(SharedLogFile shared, int value)
    {
        var field = typeof(SharedLogFile).GetField("_needsLineSeparator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(shared, value);
    }

    /// <summary>The live stream field, when present.</summary>
    private static FileStream? GetStream(SharedLogFile shared)
    {
        var field = typeof(SharedLogFile).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(shared) as FileStream;
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
}
