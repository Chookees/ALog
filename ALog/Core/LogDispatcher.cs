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
}