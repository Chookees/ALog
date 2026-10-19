namespace ALog.Writers.File;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

public class FileLogWriter : ILogWriter, IFormattableLogWriter
{
    private readonly string _filePath;
    private readonly long? _maxFileSizeInBytes;
    private readonly object _writeLock = new();

    public ILogFormatter? Formatter { get; set; }

    public FileLogWriter(string filePath, ILogFormatter? formatter = null, long? maxFileSizeInBytes = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Formatter = formatter;
        _maxFileSizeInBytes = maxFileSizeInBytes;

        EnsureDirectoryExists(_filePath);
    }

    /// <summary>
    /// Appends one line on the calling thread. Does not queue work and does not wait on a task.
    /// </summary>
    /// <param name="logEvent">Event to append. Null is reported and dropped.</param>
    /// <remarks>
    /// Without the background queue the caller blocks for the file I/O.
    /// Failures are written to the error stream and are not thrown to the caller.
    /// </remarks>
    public void Write(LogEvent logEvent) => WriteCore(logEvent);

    /// <summary>
    /// Appends one line on the calling thread and returns an already completed task.
    /// </summary>
    /// <param name="logEvent">Event to append. Null is reported and the task still completes.</param>
    /// <returns>
    /// <see cref="Task.CompletedTask"/> after the line is written, or after the error is reported.
    /// <see cref="Task.IsCompleted"/> is true before the caller awaits it.
    /// </returns>
    /// <remarks>
    /// There is no thread-pool hop. Asynchrony for file I/O comes only from the background queue.
    /// </remarks>
    public Task WriteAsync(LogEvent logEvent)
    {
        WriteCore(logEvent);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Formats one event and appends it under the writer lock.
    /// </summary>
    /// <param name="logEvent">Event to append. Null is reported and dropped.</param>
    /// <remarks>
    /// The lock covers rotation and append so two callers cannot split a line.
    /// A formatting or I/O failure is reported and does not leave the lock held.
    /// </remarks>
    private void WriteCore(LogEvent logEvent)
    {
        try
        {
            if (logEvent is null)
                throw new ArgumentNullException(nameof(logEvent));

            var line = Formatter?.Format(logEvent) ?? FormatFallback(logEvent);
            var lineWithNewline = line + Environment.NewLine;
            lock (_writeLock)
            {
                RotateIfNeeded();
                System.IO.File.AppendAllText(_filePath, lineWithNewline);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
        }
    }

    private string FormatFallback(LogEvent logEvent)
    {
        var baseLine = $"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}] [{logEvent.Level}] {logEvent.Message}";

        if (logEvent.Exception is not null)
            baseLine += $" | EX: {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";

        if (logEvent.Context is not null && logEvent.Context.Count > 0)
        {
            var context = string.Join(", ", logEvent.Context.Select(kv => $"{kv.Key}={kv.Value}"));
            baseLine += $" | ctx: {context}";
        }

        return baseLine;
    }

    private void RotateIfNeeded()
    {
        if (_maxFileSizeInBytes == null) return;

        try
        {
            var fileInfo = new FileInfo(_filePath);
            if (!fileInfo.Exists || fileInfo.Length < _maxFileSizeInBytes) return;

            var directory = Path.GetDirectoryName(_filePath);
            var filename = Path.GetFileNameWithoutExtension(_filePath);
            var ext = Path.GetExtension(_filePath);
            var timestamp = RotationStamp();

            var rotated = Path.Combine(directory ?? ".", $"{filename}_{timestamp}{ext}");

            System.IO.File.Move(_filePath, rotated, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileLogWriter] Rolling failed: {ex}");
        }
    }

    /// <summary>
    /// The stamp in a rotated file name. Not a shutdown or flush deadline.
    /// </summary>
    /// <returns>UTC time as <c>yyyyMMdd_HHmmss</c>.</returns>
    /// <remarks>Does not throw. Called only when a file is large enough to rotate.</remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "Rotation file name, not a shutdown or flush deadline. removed in P1")]
    private static string RotationStamp()
        => DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

    private static void EnsureDirectoryExists(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileLogWriter] Failed to create directory: {ex}");
        }
    }

    public void Dispose()
    {
        // no unmanaged resources
    }
}
