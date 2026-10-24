namespace ALog.Writers.File;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ALog.Config;
using ALog.Core;
using ALog.Formatters;
using ALog.Public.Interfaces;

public class FileLogWriter : ILogWriter, IFormattableLogWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] NewLineBytes = Utf8.GetBytes(Environment.NewLine);

    [ThreadStatic]
    private static byte[]? _utf8Scratch;

    /// <summary>Largest scratch buffer kept on the thread. A longer line uses an array that is not kept.</summary>
    private const int MaxRetainedUtf8 = 4096;

    /// <summary>Length of the scratch buffer still kept on this thread. Zero when none is kept.</summary>
    /// <remarks>Does not throw. Another write on this thread can change it. Other threads have their own buffer.</remarks>
    internal static int RetainedUtf8Bytes => _utf8Scratch?.Length ?? 0;

    private readonly string _filePath;
    private readonly long? _maxFileSizeInBytes;
    private readonly object _writeLock = new();
    private readonly SharedLogFile _shared;
    private int _disposed;

    public ILogFormatter? Formatter { get; set; }

    /// <summary>
    /// Keeps <paramref name="filePath"/> open from the first event until dispose, rotation, or shutdown.
    /// </summary>
    /// <param name="filePath">Destination path. Null is rejected. The directory is created when it can be.</param>
    /// <param name="formatter">Optional formatter. Null uses the built-in line.</param>
    /// <param name="maxFileSizeInBytes">Rotate before a line when the logical length is already at least this value. Null never rotates.</param>
    /// <param name="fileBufferSize">Bytes to collect before a write. Null uses 64 KiB. An explicit value must be 4096..16777216.</param>
    /// <param name="fileFlushInterval">How often to write a partial buffer. Null uses 1 second. Zero writes every event. An explicit value must be 0..60 seconds.</param>
    /// <param name="clock">Clock for the flush interval. Null uses <see cref="ProductionClock.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is null.</exception>
    /// <exception cref="InvalidOperationException">An explicit buffer or interval is outside its range.</exception>
    /// <remarks>
    /// Does not open the file. The first write does. Two writers in this process for the same normalized path share one stream.
    /// On Windows a second process writing the same file gets a sharing violation. On Linux and macOS that second writer is not detected.
    /// A zero <paramref name="fileFlushInterval"/> writes each line immediately and keeps the write handle.
    /// Windows <c>File.ReadAllText</c> can open the path while this writer is alive only after <see cref="Flush"/>,
    /// after a positive interval elapses with no further append, or after dispose. <see cref="Flush"/> and that idle
    /// timer replace the write handle with a read handle. The next append opens a write handle again. Dispose closes the handle.
    /// An explicit buffer or interval is kept when the <see cref="LoggerConfig"/> never calls
    /// <see cref="LoggerConfig.SetFileBufferSize"/> or <see cref="LoggerConfig.SetFileFlushInterval"/>.
    /// A configuration that does call the setter replaces this constructor value.
    /// </remarks>
    public FileLogWriter(
        string filePath,
        ILogFormatter? formatter = null,
        long? maxFileSizeInBytes = null,
        int? fileBufferSize = null,
        TimeSpan? fileFlushInterval = null,
        TimeProvider? clock = null)
        : this(clock ?? ProductionClock.Default, filePath, formatter, maxFileSizeInBytes, fileBufferSize, fileFlushInterval)
    {
    }

    /// <summary>Stores the path and takes a reference on the shared file. The public constructor ends here.</summary>
    /// <param name="clock">Clock for the interval. Not null.</param>
    /// <param name="filePath">Destination path. Not null.</param>
    /// <param name="formatter">Optional formatter.</param>
    /// <param name="maxFileSizeInBytes">Rotation cap. Null never rotates.</param>
    /// <param name="fileBufferSize">Explicit buffer, or null for 64 KiB.</param>
    /// <param name="fileFlushInterval">Explicit interval, or null for 1 second.</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is null.</exception>
    /// <exception cref="InvalidOperationException">An explicit buffer or interval is outside its range.</exception>
    /// <remarks>Does not throw for a missing directory. That failure is written to the error stream.</remarks>
    private FileLogWriter(
        TimeProvider clock,
        string filePath,
        ILogFormatter? formatter,
        long? maxFileSizeInBytes,
        int? fileBufferSize,
        TimeSpan? fileFlushInterval)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Formatter = formatter;
        _maxFileSizeInBytes = maxFileSizeInBytes;
        var buffer = LogOptionRules.DefaultBuffer;
        var interval = LogOptionRules.DefaultInterval;
        if (fileBufferSize is int explicitBuffer)
        {
            LogOptionRules.RequireBuffer(explicitBuffer, LogOptionRules.PathOf("FileBufferSize"));
            buffer = explicitBuffer;
        }

        if (fileFlushInterval is TimeSpan explicitInterval)
        {
            LogOptionRules.RequireDuration(explicitInterval, LogOptionRules.PathOf("FileFlushInterval"));
            interval = explicitInterval;
        }

        EnsureDirectoryExists(_filePath);
        _shared = LogFileRegistry.Acquire(_filePath, buffer, interval, clock);
    }

    /// <summary>
    /// Applies the engine clock, and the buffer or interval when the configuration set that option.
    /// </summary>
    /// <param name="bufferSize">Bytes collected before a write, or null to keep this writer's constructor size.</param>
    /// <param name="interval">Flush interval, or null to keep this writer's constructor interval. Zero writes every event.</param>
    /// <param name="clock">The engine clock. Not null.</param>
    /// <remarks>
    /// Does not throw. A call after the file is open keeps the settings that opened it.
    /// Null means the configuration did not set that option, so an explicit constructor value stays.
    /// </remarks>
    internal void ApplyStartup(int? bufferSize, TimeSpan? interval, TimeProvider clock)
    {
        _shared.ApplyStartup(bufferSize, interval, clock);
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

    /// <summary>Writes buffered lines to the operating system and parks a read handle. There is no fsync.</summary>
    /// <remarks>
    /// Does not throw. Safe to call before the first event and after dispose.
    /// After a successful flush the file is held for read, so <c>File.ReadAllText</c> can open the path on Windows
    /// while this writer is still alive. The next append closes that read handle and opens a write handle.
    /// A write does not hold the file for read, including when the flush interval is zero.
    /// </remarks>
    public void Flush()
    {
        lock (_writeLock)
            _shared.Flush();
    }

    /// <summary>The rotated path for <paramref name="path"/>. The stamp comes from <see cref="RotationStamp"/>.</summary>
    /// <param name="path">The open file. Not null.</param>
    /// <returns>A path in the same directory with <c>_yyyyMMdd_HHmmss</c> before the extension.</returns>
    /// <remarks>Does not throw. Called only when a file is large enough to rotate.</remarks>
    internal static string RotationName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var filename = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var timestamp = RotationStamp();
        return Path.Combine(directory ?? ".", $"{filename}_{timestamp}{ext}");
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

            if (TryWriteFast(logEvent))
                return;

            var line = Formatter?.Format(logEvent) ?? FormatFallback(logEvent);
            if (TryWriteAsciiLine(line))
                return;

            var lineWithNewline = line + Environment.NewLine;
            var bytes = Utf8.GetBytes(lineWithNewline);
            lock (_writeLock)
                _shared.Append(bytes, _maxFileSizeInBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
        }
    }

    /// <summary>
    /// Writes one ASCII line straight into the file buffer when the formatter is exactly <see cref="PlainTextFormatter"/>.
    /// </summary>
    /// <param name="logEvent">Event to append. Not null.</param>
    /// <returns>True when the line is in the file buffer. False when the caller uses the string path.</returns>
    /// <remarks>
    /// Does not throw. The scratch buffer is per thread. The file lock covers only the copy.
    /// <see cref="object.GetType"/> must be <see cref="PlainTextFormatter"/> itself. A subclass can replace
    /// <see cref="ILogFormatter.Format"/>, and <c>is</c> would still match, so the base fast path would hide that text.
    /// Safe to call from several threads. Each thread keeps its own scratch buffer.
    /// </remarks>
    private bool TryWriteFast(LogEvent logEvent)
    {
        if (Formatter is not PlainTextFormatter plain || plain.GetType() != typeof(PlainTextFormatter))
            return false;

        var buffer = _utf8Scratch ??= new byte[1024];
        if (!plain.TryWriteUtf8(logEvent, buffer, out var written))
        {
            if (buffer.Length >= 64 * 1024)
                return false;
            var next = buffer.Length * 2;
            if (next > 64 * 1024)
                next = 64 * 1024;
            buffer = new byte[next];
            if (next <= MaxRetainedUtf8)
                _utf8Scratch = buffer;
            if (!plain.TryWriteUtf8(logEvent, buffer, out written))
                return false;
        }

        var newline = NewLineBytes;
        if (written + newline.Length > buffer.Length)
            return false;
        newline.CopyTo(buffer.AsSpan(written));
        lock (_writeLock)
            _shared.Append(buffer.AsSpan(0, written + newline.Length), _maxFileSizeInBytes);
        return true;
    }

    /// <summary>Copies an ASCII line plus the newline into the file buffer.</summary>
    /// <param name="line">Line without a newline. Not null.</param>
    /// <returns>False when the line is not ASCII or does not fit the scratch buffer.</returns>
    /// <remarks>Does not throw. The caller uses the encoding path when this returns false.</remarks>
    private bool TryWriteAsciiLine(string line)
    {
        var newline = NewLineBytes;
        var needed = line.Length + newline.Length;
        if (needed > 64 * 1024)
            return false;
        byte[] buffer;
        if (needed <= MaxRetainedUtf8)
        {
            buffer = _utf8Scratch ??= new byte[1024];
            if (needed > buffer.Length)
            {
                var size = buffer.Length * 2;
                if (size < needed)
                    size = needed;
                if (size > MaxRetainedUtf8)
                    size = MaxRetainedUtf8;
                buffer = _utf8Scratch = new byte[size];
            }
        }
        else
        {
            buffer = new byte[needed];
        }

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch > 127)
                return false;
            buffer[i] = (byte)ch;
        }

        newline.CopyTo(buffer.AsSpan(line.Length));
        lock (_writeLock)
            _shared.Append(buffer.AsSpan(0, needed), _maxFileSizeInBytes);
        return true;
    }

    /// <summary>
    /// Renders the line used when no formatter was attached.
    /// The exception segment comes from <see cref="ExceptionReport"/>, the same helper the other text sinks use.
    /// </summary>
    /// <param name="logEvent">Event to render. Not null.</param>
    /// <returns>One line, without a trailing newline.</returns>
    /// <remarks>
    /// Does not throw. The message is always included, including when it is empty, matching the previous file fallback.
    /// The category is not written here. The message, context keys, and context values are escaped so none of them can forge a <c>|</c> separator or a second line.
    /// Safe to call from the thread that holds the writer lock. It does not touch shared formatter state.
    /// </remarks>
    private string FormatFallback(LogEvent logEvent)
    {
        var head = "[" + logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + logEvent.Level + "]";
        head += " " + ExceptionReport.EscapeLine(logEvent.Message);
        var exception = logEvent.Exception == null ? null : ExceptionReport.FormatText(logEvent.Exception);
        return ExceptionReport.JoinLine(head, exception, props: null, ExceptionReport.FormatContext(logEvent.Context));
    }

    /// <summary>
    /// The stamp in a rotated file name. Not a shutdown or flush deadline.
    /// </summary>
    /// <returns>UTC time as <c>yyyyMMdd_HHmmss</c>.</returns>
    /// <remarks>Does not throw. Called only when a file is large enough to rotate.</remarks>
    [SuppressMessage(
        "ApiDesign",
        "RS0030",
        Justification = "Rotation file name, not a shutdown or flush deadline; removed in P1.")]
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

    /// <summary>Flushes and drops this writer's reference. A second call does nothing.</summary>
    /// <remarks>Does not throw. The last reference closes the stream. An earlier one still flushes.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_writeLock)
            LogFileRegistry.Release(_shared);
    }
}
