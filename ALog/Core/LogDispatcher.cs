namespace ALog.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALog.Public.Interfaces;

public class LogDispatcher
{
    private readonly IReadOnlyList<ILogWriter> _writers;

    public LogDispatcher(IEnumerable<ILogWriter> writers)
    {
        _writers = writers.ToList();
    }

    public void Dispatch(LogEvent logEvent)
    {
        foreach (var writer in _writers)
        {
            try
            {
                writer.Write(logEvent);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[ALog Dispatcher] Sync write failed: {ex}");
            }
        }
    }

    public async Task DispatchAsync(LogEvent logEvent)
    {
        foreach (var writer in _writers)
        {
            try
            {
                await writer.WriteAsync(logEvent);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[ALog Dispatcher] Async write failed: {ex}");
            }
        }
    }

    public async Task DispatchBatchAsync(IEnumerable<LogEvent> logEvents)
    {
        var tasks = new List<Task>();

        foreach (var writer in _writers)
        {
            tasks.Add(ProcessBatchForWriterAsync(writer, logEvents));
        }

        await Task.WhenAll(tasks);
    }

    private async Task ProcessBatchForWriterAsync(ILogWriter writer, IEnumerable<LogEvent> logEvents)
    {
        try
        {
            foreach (var logEvent in logEvents)
            {
                await writer.WriteAsync(logEvent);
            }
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[ALog Dispatcher] Batch write failed for {writer.GetType().Name}: {ex}");
        }
    }
}