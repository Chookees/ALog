// Sends one event to every writer. A writer that throws does not stop the rest of the batch (C4-04).
// Next file: ALog/Core/BackgroundLogQueue.cs.

namespace ALog.Core;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALog.Public.Interfaces;

public class LogDispatcher
{
    private readonly IReadOnlyList<ILogWriter> _writers;
    private readonly bool _asyncEnabled;

    public LogDispatcher(IEnumerable<ILogWriter> writers, bool asyncEnabled = true)
    {
        _writers = writers.ToList();
        _asyncEnabled = asyncEnabled;
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
        if (!_asyncEnabled)
        {
            Dispatch(logEvent);
            return;
        }

        foreach (var writer in _writers)
        {
            try
            {
                await writer.WriteAsync(logEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[ALog Dispatcher] Async write failed: {ex}");
            }
        }
    }

    public async Task DispatchBatchAsync(IEnumerable<LogEvent> logEvents)
    {
        if (!_asyncEnabled)
        {
            foreach (var logEvent in logEvents)
            {
                Dispatch(logEvent);
            }
            return;
        }

        var tasks = new List<Task>();

        foreach (var writer in _writers)
        {
            tasks.Add(ProcessBatchForWriterAsync(writer, logEvents));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Most events one batch write will call. A longer sequence is stopped so the loop has a fixed bound.</summary>
    private const int MaxBatchEvents = 1_000_000;

    /// <summary>
    /// Writes each event in <paramref name="logEvents"/> to <paramref name="writer"/>.
    /// A throw on one event is reported and the later events are still offered to this writer.
    /// </summary>
    /// <param name="writer">Destination. Not null.</param>
    /// <param name="logEvents">Events in order. Not null. At most <see cref="MaxBatchEvents"/> are written.</param>
    /// <remarks>
    /// Does not throw. The error line is the existing batch line, one per failed event.
    /// Safe to run beside another writer task. The loop stops at <see cref="MaxBatchEvents"/>.
    /// </remarks>
    private async Task ProcessBatchForWriterAsync(ILogWriter writer, IEnumerable<LogEvent> logEvents)
    {
        var seen = 0;
        foreach (var logEvent in logEvents)
        {
            if (seen >= MaxBatchEvents)
                break;

            seen++;
            await WriteOneInBatchAsync(writer, logEvent).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one batch event. A failure is written to the error stream and does not stop the batch.
    /// </summary>
    /// <param name="writer">Destination. Not null.</param>
    /// <param name="logEvent">The event. Passed through to the writer.</param>
    /// <remarks>Does not throw. The text matches the historical batch error line.</remarks>
    private static async Task WriteOneInBatchAsync(ILogWriter writer, LogEvent logEvent)
    {
        try
        {
            await writer.WriteAsync(logEvent).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[ALog Dispatcher] Batch write failed for {writer.GetType().Name}: {ex}");
        }
    }
}
