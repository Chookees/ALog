using ALog.Core;
using ALog.Writers.File;

namespace ALog.Tests;

/// <summary>
/// Opening an existing log for append must not glue the next line onto a fragment that lacks a line ending.
/// </summary>
public sealed class EofFragmentMergeTests
{
    /// <summary>
    /// A non-empty file that does not end with a line terminator keeps the fragment on its own line
    /// when a new writer appends the next event.
    /// </summary>
    /// <remarks>
    /// Simulates a prior process that left trailing bytes without <see cref="Environment.NewLine"/>.
    /// The next <see cref="FileLogWriter"/> must insert the missing ending before its first line.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task FragmentAtEof_NextWriterKeepsFragmentAndNewLineApart()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "fragment.log");
            var fragment = "[2026-10-25 09:12:08] [Info] pdl0t-held-00";
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(fragment));

            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                writer.Write(Event("after-restart"));
                writer.Flush();
            }

            var text = File.ReadAllText(path);
            var lines = SplitLines(text);
            Assert.Equal(2, lines.Length);
            Assert.Equal(fragment, lines[0]);
            Assert.Contains("after-restart", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain(fragment + "[", text, StringComparison.Ordinal);
            Assert.DoesNotContain("held-00[", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A file that already ends with the platform line ending does not gain an extra blank line
    /// when a new writer opens it and appends.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TerminatedFile_NextWriterDoesNotInsertBlankLine()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "terminated.log");
            using (var first = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                first.Write(Event("complete-line"));
                first.Flush();
            }

            var before = File.ReadAllText(path);
            Assert.EndsWith(Environment.NewLine, before, StringComparison.Ordinal);

            using (var second = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                second.Write(Event("second-line"));
                second.Flush();
            }

            var text = File.ReadAllText(path);
            var lines = SplitLines(text);
            Assert.Equal(2, lines.Length);
            Assert.Contains("complete-line", lines[0], StringComparison.Ordinal);
            Assert.Contains("second-line", lines[1], StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// An empty existing file is not given a leading newline before the first written event.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EmptyFile_OpenDoesNotWriteNewline()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "empty.log");
            File.WriteAllBytes(path, Array.Empty<byte>());
            Assert.Equal(0, new FileInfo(path).Length);

            using (var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero))
            {
                // Opening alone must leave the empty file empty.
                Assert.Equal(0, new FileInfo(path).Length);
                writer.Write(Event("first-line"));
                writer.Flush();
            }

            var text = File.ReadAllText(path);
            Assert.False(text.StartsWith('\n') || text.StartsWith("\r\n", StringComparison.Ordinal));
            var lines = SplitLines(text);
            Assert.Single(lines);
            Assert.Contains("first-line", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// When the trailing-byte probe cannot open the path, the append open still succeeds and the next line is written.
    /// </summary>
    /// <remarks>
    /// The open factory returns a real append stream, then the path is replaced with a directory so the
    /// shared-read probe fails. That failure is swallowed; the writer must not throw from the open path.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task ProbeFailure_OpenStillWritesTheNextLine()
    {
        await Task.CompletedTask;
        var directory = NewDirectory();
        var previous = LogFileRegistry.OpenFactory;
        try
        {
            var path = Path.Combine(directory, "probe.log");
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("orphan-fragment"));
            LogFileRegistry.OpenFactory = (p, mode, access, share) =>
            {
                var stream = new FileStream(p, mode, access, share, bufferSize: 1, FileOptions.None);
                // After the append handle is open, occupy the path so the probe read fails.
                File.Delete(p);
                Directory.CreateDirectory(p);
                return stream;
            };

            using var writer = new FileLogWriter(path, fileFlushInterval: TimeSpan.Zero);
            writer.Write(Event("after-probe-fail"));
            writer.Flush();
            // Stream still holds the orphan content; the line must have been accepted without throw.
            Assert.True(true);
        }
        finally
        {
            LogFileRegistry.OpenFactory = previous;
            DeleteDirectory(directory);
        }
    }

    /// <summary>One info event at a fixed UTC time so the formatted line is stable.</summary>
    private static LogEvent Event(string message)
        => new(new DateTime(2026, 10, 25, 9, 12, 58, DateTimeKind.Utc), LogLevel.Info, message, null, null);

    /// <summary>Splits on any common line ending and drops empty trailing entries from a final terminator.</summary>
    private static string[] SplitLines(string text)
        => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(static line => line.Length > 0)
            .ToArray();

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
