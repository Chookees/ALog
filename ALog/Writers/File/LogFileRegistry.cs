namespace ALog.Writers.File;

using ALog.Config;
using ALog.Core;

/// <summary>
/// One open file per normalized path in this process.
/// The first opener's buffer, interval, and clock are kept until the engine applies startup settings.
/// </summary>
/// <remarks>
/// The dictionary key is <see cref="Path.GetFullPath(string)"/>. Windows compares it without case. Other systems do not.
/// The stream is opened with <see cref="FileShare.Read"/> and <see cref="FileShare.Delete"/>, and not <see cref="FileShare.Write"/>.
/// On Windows that share mode makes a second writer fail with a sharing violation. On Linux and macOS it does not,
/// so lines from two processes can mix. This registry does not take an advisory lock.
/// </remarks>
internal static class LogFileRegistry
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly Dictionary<string, SharedLogFile> Files = new(PathComparer);
    private static int _openCount;
    private static int _identityChecks;

    /// <summary>The lock around <see cref="Files"/>. Callers must not take it while they hold a file lock.</summary>
    internal static object Gate { get; } = new();

    /// <summary>How many times a stream was opened, including a reopen after rotation.</summary>
    internal static int OpenCount => Volatile.Read(ref _openCount);

    /// <summary>Records one successful open.</summary>
    /// <remarks>Does not throw. Called under the file lock from the stream that just opened.</remarks>
    internal static void NoteOpen()
    {
        Interlocked.Increment(ref _openCount);
    }

    /// <summary>How many times an open file was compared with its path. A skipped check does not count.</summary>
    internal static int IdentityCheckCount => Volatile.Read(ref _identityChecks);

    /// <summary>Records one path comparison.</summary>
    /// <remarks>Does not throw. Called under the file lock when the check is due or forced.</remarks>
    internal static void NoteIdentityCheck()
    {
        Interlocked.Increment(ref _identityChecks);
    }

    /// <summary>
    /// Returns the shared file for <paramref name="path"/> and keeps it alive.
    /// </summary>
    /// <param name="path">The caller's path. Not null. It is normalized with <see cref="Path.GetFullPath(string)"/>.</param>
    /// <param name="bufferSize">Buffer used only when this call creates the shared file.</param>
    /// <param name="interval">Interval used only when this call creates the shared file.</param>
    /// <param name="clock">Clock used only when this call creates the shared file. Not null.</param>
    /// <returns>The shared file. The caller must <see cref="Release"/> it once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    /// <remarks>Does not open the stream. The first write opens it under <see cref="Gate"/>.</remarks>
    internal static SharedLogFile Acquire(string path, int bufferSize, TimeSpan interval, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var full = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!Files.TryGetValue(full, out var shared))
            {
                shared = new SharedLogFile(full, bufferSize, interval, clock);
                Files.Add(full, shared);
            }

            shared.AddRef();
            return shared;
        }
    }

    /// <summary>
    /// Drops one reference. The last reference closes the stream outside <see cref="Gate"/>.
    /// A reference that is not the last one still flushes, so that writer's dispose is durable.
    /// </summary>
    /// <param name="shared">The instance <see cref="Acquire"/> returned. Not null.</param>
    /// <remarks>Does not throw for a flush or close failure. Those are written to the error stream by the file.</remarks>
    internal static void Release(SharedLogFile shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        bool last;
        lock (Gate)
        {
            last = shared.DropRef();
            if (last)
                Files.Remove(shared.Path);
        }

        if (last)
            shared.Close();
        else
            shared.Flush();
    }
}

/// <summary>
/// The one stream for a path. Lines are appended whole. The buffer is flushed on size, on the interval, and on flush.
/// </summary>
/// <remarks>
/// The stream is opened with <see cref="FileShare.Read"/> and <see cref="FileShare.Delete"/> so another handle can read or rename it.
/// Omitting <see cref="FileShare.Write"/> stops a second writer on Windows only. Linux and macOS still allow one, and the lines can mix.
/// The timer is disposed outside the file lock. The callback also takes that lock.
/// An external rename, delete, or truncate is noticed on the flush tick, on <see cref="Flush"/>, and before this process rotates the file.
/// The path comparison runs at most once per <see cref="FileFlushInterval"/>, or once per second when that interval is zero.
/// </remarks>
internal sealed class SharedLogFile
{
    private readonly object _fileLock = new();
    private readonly string _path;
    private int _refs;
    private int _bufferSize;
    private TimeSpan _interval;
    private TimeProvider _clock;
    private byte[] _buffer;
    private int _pending;
    private long _logicalLength;
    private int _opened;
    private int _closed;
    private FileStream? _stream;
    private ITimer? _timer;
    private DateTimeOffset _lastIdentityCheck;

    /// <summary>Stores the first opener's settings. The stream stays closed until the first append.</summary>
    /// <param name="path">Normalized full path. Not null.</param>
    /// <param name="bufferSize">User buffer length. At least 1. The caller already checked the startup range.</param>
    /// <param name="interval">Flush interval. Zero writes every line and creates no timer.</param>
    /// <param name="clock">Clock for the interval timer. Not null.</param>
    /// <remarks>Does not throw. Does not touch the disk.</remarks>
    internal SharedLogFile(string path, int bufferSize, TimeSpan interval, TimeProvider clock)
    {
        _path = path;
        _bufferSize = bufferSize;
        _interval = interval;
        _clock = clock;
        _buffer = new byte[bufferSize];
    }

    /// <summary>The normalized path. The registry uses it as the dictionary key.</summary>
    internal string Path => _path;

    /// <summary>Adds one live writer. The caller holds <see cref="LogFileRegistry.Gate"/>.</summary>
    /// <remarks>Does not throw. Does not take the file lock.</remarks>
    internal void AddRef()
    {
        _refs++;
    }

    /// <summary>Drops one live writer.</summary>
    /// <returns>True when no writer remains.</returns>
    /// <remarks>The caller holds <see cref="LogFileRegistry.Gate"/>. Does not close the stream.</remarks>
    internal bool DropRef()
    {
        if (_refs > 0)
            _refs--;

        return _refs == 0;
    }

    /// <summary>
    /// Replaces the clock, and the buffer or interval when the configuration set that option, before the stream opens.
    /// </summary>
    /// <param name="bufferSize">New buffer, or null to keep the constructor size.</param>
    /// <param name="interval">New interval, or null to keep the constructor interval. Zero writes every line.</param>
    /// <param name="clock">Clock for the interval and the path check. Not null.</param>
    /// <remarks>
    /// Does not throw. A call after the first open keeps the settings that opened the stream.
    /// Null leaves the value the constructor stored, so an explicit writer argument is not replaced by the default.
    /// </remarks>
    internal void ApplyStartup(int? bufferSize, TimeSpan? interval, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_fileLock)
        {
            if (_opened != 0)
                return;

            if (bufferSize is int size)
            {
                _bufferSize = size;
                if (_buffer.Length != size)
                    _buffer = new byte[size];
            }

            if (interval is TimeSpan gap)
                _interval = gap;

            _clock = clock;
        }
    }

    /// <summary>Appends one encoded line, rotating first when the logical length is already at the cap.</summary>
    /// <param name="line">The line plus its newline, in UTF-8 without a BOM. Not null.</param>
    /// <param name="maxBytes">Rotate before this line when the logical length is above zero and at least this value. Null never rotates.</param>
    /// <remarks>
    /// Does not throw. A failure is the caller's to report. The line is copied or written whole under the file lock.
    /// The loop that copies the line is bounded by <paramref name="line"/>'s length.
    /// </remarks>
    internal void Append(byte[] line, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureOpen();
        lock (_fileLock)
        {
            if (_closed != 0)
                return;

            if (_stream is null)
                TryReopen();
            if (_stream is null)
                return;

            Reconcile(force: false);
            if (_stream is null)
                return;

            RotateIfNeeded(maxBytes);
            if (_stream is null)
                return;

            WriteLine(line);
        }
    }

    /// <summary>Writes any buffered bytes and asks the stream to hand them to the operating system. There is no fsync.</summary>
    /// <remarks>Does not throw. A closed file returns. Safe to call before the first write.</remarks>
    internal void Flush()
    {
        lock (_fileLock)
        {
            if (_closed != 0 && _stream is null)
                return;

            Reconcile(force: true);
            FlushPending();
        }
    }

    /// <summary>Flushes, then closes the stream. The timer is disposed after the file lock is released.</summary>
    /// <remarks>Does not throw. A second call returns. Called only for the last reference, outside the registry lock.</remarks>
    internal void Close()
    {
        ITimer? timer;
        FileStream? stream;
        lock (_fileLock)
        {
            if (_closed != 0)
                return;

            _closed = 1;
            try
            {
                FlushPending();
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
            }

            timer = _timer;
            _timer = null;
            stream = _stream;
            _stream = null;
        }

        timer?.Dispose();
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
        }
    }

    /// <summary>Opens the stream once. A second caller sees the open flag and returns.</summary>
    /// <remarks>Takes the registry lock, then the file lock. Does not take the registry lock while the file lock is already held.</remarks>
    private void EnsureOpen()
    {
        if (Volatile.Read(ref _opened) != 0 || Volatile.Read(ref _closed) != 0)
            return;

        lock (LogFileRegistry.Gate)
        {
            lock (_fileLock)
            {
                if (_opened != 0 || _closed != 0)
                    return;

                OpenStream();
            }
        }
    }

    /// <summary>Creates the append stream and, when the interval is positive, the flush timer.</summary>
    /// <remarks>The caller holds the file lock. Does not dispose the timer. A failure leaves the stream unset and throws.</remarks>
    private void OpenStream()
    {
        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.None);
        _logicalLength = _stream.Length;
        _pending = 0;
        if (_buffer.Length != _bufferSize)
            _buffer = new byte[_bufferSize];

        Volatile.Write(ref _opened, 1);
        LogFileRegistry.NoteOpen();
        _lastIdentityCheck = _clock.GetUtcNow();
        if (_timer is null && _interval > TimeSpan.Zero)
        {
            _timer = _clock.CreateTimer(
                static state => ((SharedLogFile)state!).OnInterval(),
                this,
                _interval,
                _interval);
        }
    }

    /// <summary>Flushes when the interval elapses. A closed file returns before taking the lock for long.</summary>
    /// <remarks>Does not throw. Does not dispose the timer. The callback and <see cref="Close"/> share the file lock.</remarks>
    private void OnInterval()
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        lock (_fileLock)
        {
            if (_closed != 0)
                return;

            try
            {
                Reconcile(force: true);
                FlushPending();
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Reopens the configured path when it no longer names the open file, and seeks to the end after an external truncate.
    /// </summary>
    /// <param name="force">True on a flush tick, <see cref="Flush"/>, and before this process rotates. False waits for the check gap.</param>
    /// <remarks>
    /// The caller holds the file lock. A forced call still runs when the gap has not elapsed.
    /// A call that is not forced returns until <see cref="FileFlushInterval"/> has elapsed, or one second when that interval is zero.
    /// A missing path or a different file is flushed into the old stream, closed, and opened again.
    /// The same file whose length is behind the write position is seeked to the end before the next write, so a truncate does not leave a gap.
    /// </remarks>
    private void Reconcile(bool force)
    {
        if (_closed != 0 || _stream is null)
            return;

        if (!force && !IdentityCheckDue())
            return;

        LogFileRegistry.NoteIdentityCheck();
        _lastIdentityCheck = _clock.GetUtcNow();
        if (!FileIdentity.TryMatch(_stream.SafeFileHandle, _path, out var openLength))
        {
            ReopenPath();
            return;
        }

        if (openLength < _stream.Position)
        {
            _stream.Seek(0, SeekOrigin.End);
            _logicalLength = _stream.Position + _pending;
        }
    }

    /// <summary>True when the clock says the path may be compared again.</summary>
    /// <returns>True when no check has been recorded or the gap has elapsed.</returns>
    /// <remarks>The caller holds the file lock. Does not touch the disk. The gap is the flush interval, or one second when the interval is zero.</remarks>
    private bool IdentityCheckDue()
    {
        if (_lastIdentityCheck == default)
            return true;

        var gap = _interval > TimeSpan.Zero ? _interval : TimeSpan.FromSeconds(1);
        return _clock.GetUtcNow() - _lastIdentityCheck >= gap;
    }

    /// <summary>Writes the buffer into the current stream, closes it, and opens <see cref="Path"/> again.</summary>
    /// <remarks>
    /// The caller holds the file lock. The buffered bytes are written before the old stream is closed.
    /// A failed flush or close is reported and the path is still opened again.
    /// A failed open leaves the stream unset and throws, so the caller reports it on the write-error path.
    /// </remarks>
    private void ReopenPath()
    {
        var stream = _stream!;
        try
        {
            FlushPending();
            _stream = null;
            stream.Dispose();
        }
        catch (Exception ex)
        {
            _stream = null;
            System.Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
        }

        OpenStream();
    }

    /// <summary>Moves the file when the next line would start at or past <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The cap. Null skips rotation.</param>
    /// <remarks>The caller holds the file lock. A failed move is reported and the stream is reopened so the line can still be written.</remarks>
    private void RotateIfNeeded(long? maxBytes)
    {
        if (maxBytes is null || _logicalLength <= 0 || _logicalLength < maxBytes.Value)
            return;

        try
        {
            Reconcile(force: true);
            if (_stream is null || _logicalLength <= 0 || _logicalLength < maxBytes.Value)
                return;

            FlushPending();
            var stream = _stream;
            _stream = null;
            stream?.Dispose();
            System.IO.File.Move(_path, FileLogWriter.RotationName(_path), overwrite: true);
            OpenStream();
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[FileLogWriter] Rolling failed: {ex}");
            TryReopen();
        }
    }

    /// <summary>Opens a stream again after a failed rotation disposed the previous one.</summary>
    /// <remarks>The caller holds the file lock. A second failure is reported and leaves the stream unset.</remarks>
    private void TryReopen()
    {
        if (_stream is not null)
            return;

        try
        {
            OpenStream();
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[FileLogWriter] Write failed: {ex}");
        }
    }

    /// <summary>Writes <paramref name="line"/> into the buffer, or straight to the stream when it does not fit.</summary>
    /// <param name="line">One whole line. Not null. May be longer than the buffer.</param>
    /// <remarks>The caller holds the file lock. A null stream returns. The copy is bounded by the line length.</remarks>
    private void WriteLine(byte[] line)
    {
        if (_stream is null)
            return;

        if (_interval <= TimeSpan.Zero || line.Length > _buffer.Length)
        {
            FlushPending();
            _stream.Write(line, 0, line.Length);
            _stream.Flush();
            _logicalLength += line.Length;
            return;
        }

        if (_pending > 0 && _pending + line.Length > _buffer.Length)
            FlushPending();

        Buffer.BlockCopy(line, 0, _buffer, _pending, line.Length);
        _pending += line.Length;
        _logicalLength += line.Length;
        if (_pending >= _buffer.Length)
            FlushPending();
    }

    /// <summary>Writes the pending bytes and flushes the stream. An empty buffer still flushes an open stream.</summary>
    /// <remarks>The caller holds the file lock. Does not catch failures.</remarks>
    private void FlushPending()
    {
        if (_stream is null)
            return;

        if (_pending > 0)
        {
            _stream.Write(_buffer, 0, _pending);
            _pending = 0;
        }

        _stream.Flush();
    }
}
