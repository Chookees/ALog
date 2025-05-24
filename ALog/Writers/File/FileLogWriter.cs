namespace ALog.Writers.File;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALog.Core;
using ALog.Public.Interfaces;

public class FileLogWriter : ILogWriter
{
    private readonly string _filePath;
    private readonly long? _maxFileSizeInBytes;
    private readonly ILogFormatter? _formatter;

    public FileLogWriter(string filePath, ILogFormatter? formatter = null, long? maxFileSizeInBytes = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _formatter = formatter;
        _maxFileSizeInBytes = maxFileSizeInBytes;

        EnsureDirectoryExists(_filePath);
    }

    public void Write(LogEvent logEvent) =>
        WriteInternal(logEvent, asyncMode: false).GetAwaiter().GetResult();

    public Task WriteAsync(LogEvent logEvent) =>
        WriteInternal(logEvent, asyncMode: true);

    private async Task WriteInternal(LogEvent logEvent, bool asyncMode)
    {
        try
        {
            var line = _formatter?.Format(logEvent) ?? FormatFallback(logEvent);
            var lineWithNewline = line + Environment.NewLine;

            RotateIfNeeded();

            if (asyncMode)
                await File.AppendAllTextAsync(_filePath, lineWithNewline);
            else
                File.AppendAllText(_filePath, lineWithNewline);
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
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            var rotated = Path.Combine(directory ?? ".", $"{filename}_{timestamp}{ext}");

            File.Move(_filePath, rotated, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileLogWriter] Rolling failed: {ex}");
        }
    }

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
}
