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
    private readonly IWriterCallGate? _gate;

    /// <summary>
    /// Sends each event to every writer. No shutdown gate: every writer is called.
    /// </summary>
    /// <param name="writers">Destinations. Null throws. The list is copied.</param>
    /// <param name="asyncEnabled">False uses <see cref="ILogWriter.Write"/> even from <see cref="DispatchAsync"/>.</param>
    public LogDispatcher(IEnumerable<ILogWriter> writers, bool asyncEnabled = true)
        : this(writers, asyncEnabled, null)
    {
    }

    /// <summary>
    /// Sends each event to every writer, asking <paramref name="gate"/> before each call.
    /// </summary>
    /// <param name="writers">Destinations. Null throws. The list is copied. Order is the call order.</param>
    /// <param name="asyncEnabled">False uses the synchronous write.</param>
    /// <param name="gate">Null calls every writer. Otherwise a false begin skips that writer and the later ones still ask.</param>
    /// <remarks>The gate is the queue's deadline. The batch path does not use it; the queue writes one event at a time.</remarks>
    internal LogDispatcher(IEnumerable<ILogWriter> writers, bool asyncEnabled, IWriterCallGate? gate)
    {
        ArgumentNullException.ThrowIfNull(writers);
        _writers = writers.ToList();
        _asyncEnabled = asyncEnabled;
        _gate = gate;
    }

    /// <summary>
    /// Writes <paramref name="logEvent"/> to each writer that the gate allows.
    /// A writer that throws does not stop the later writers, except <see cref="WriteDeferredException"/>, which stops the dispatch so the queue can retry the event.
    /// </summary>
    /// <param name="logEvent">Event to write. Passed through. A null event is the writer's problem.</param>
    /// <remarks>
    /// Does not throw for a writer failure. <see cref="WriteDeferredException"/> is rethrown so the queue can keep the event.
    /// One error line per other failed writer. The loop is bounded by the writer list captured at construction.
    /// </remarks>
    public void Dispatch(LogEvent logEvent)
    {
        var count = _writers.Count;
        for (var i = 0; i < count; i++)
        {
            if (!BeginWriterCall(i))
                continue;

            try
            {
                _writers[i].Write(logEvent);
            }
            catch (WriteDeferredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[ALog Dispatcher] Sync write failed: {ex}");
            }
            finally
            {
                EndWriterCall(i);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="logEvent"/> to each writer that the gate allows.
    /// When async is disabled, this uses <see cref="Dispatch"/>.
    /// </summary>
    /// <param name="logEvent">Event to write. Passed through.</param>
    /// <returns>A task that finishes when every allowed writer has returned or failed.</returns>
    /// <remarks>
    /// Does not throw for a writer failure. <see cref="WriteDeferredException"/> is rethrown so the queue can keep the event.
    /// Any other writer exception stays on the error line. The await does not capture a caller context.
    /// The loop is bounded by the writer list.
    /// </remarks>
    public async Task DispatchAsync(LogEvent logEvent)
    {
        if (!_asyncEnabled)
        {
            Dispatch(logEvent);
            return;
        }

        var count = _writers.Count;
        for (var i = 0; i < count; i++)
        {
            if (!BeginWriterCall(i))
                continue;

            try
            {
                await _writers[i].WriteAsync(logEvent).ConfigureAwait(false);
            }
            catch (WriteDeferredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[ALog Dispatcher] Async write failed: {ex}");
            }
            finally
            {
                EndWriterCall(i);
            }
        }
    }

    /// <summary>
    /// Asks the gate whether writer <paramref name="index"/> may be called. No gate means yes.
    /// </summary>
    /// <param name="index">Position in the copied writer list. Always inside the list when the loop calls this.</param>
    /// <returns>False when the shutdown deadline has passed and this writer must not be called.</returns>
    /// <remarks>Does not throw. A true return is paired with <see cref="EndWriterCall"/> in a finally block.</remarks>
    private bool BeginWriterCall(int index)
    {
        if (_gate is null)
            return true;

        return _gate.BeginWriterCall(index);
    }

    /// <summary>
    /// Tells the gate that the call on writer <paramref name="index"/> has returned.
    /// </summary>
    /// <param name="index">The same index passed to <see cref="BeginWriterCall"/>.</param>
    /// <remarks>Does not throw. A missing gate does nothing. Called from finally so a writer failure still clears the in-flight mark.</remarks>
    private void EndWriterCall(int index)
    {
        _gate?.EndWriterCall(index);
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

/// <summary>
/// The file could not open its write handle. The queue keeps the event and tries again on a later flush tick.
/// </summary>
/// <remarks>Does not carry a message. Only the processor's dispatcher lets it escape. A direct write holds the line instead.</remarks>
internal sealed class WriteDeferredException : Exception
{
    /// <summary>Creates the marker. There is no message because the file already reported ALOG0073.</summary>
    /// <remarks>Does not throw. Safe to construct on the processor thread.</remarks>
    internal WriteDeferredException()
    {
    }
}

/// <summary>
/// Lets the background queue stop writer calls at the shutdown deadline and see which call is still running.
/// </summary>
internal interface IWriterCallGate
{
    /// <summary>
    /// Marks writer <paramref name="index"/> in flight, then says whether the call may start.
    /// </summary>
    /// <param name="index">Zero-based position in the dispatcher list. Outside the list returns false.</param>
    /// <returns>False when the deadline has already been marked and the writer must not be called.</returns>
    /// <remarks>
    /// Does not throw. The in-flight mark is taken before the deadline is read, so shutdown cannot dispose
    /// a writer it has not yet observed as busy. A false return has already cleared the in-flight mark.
    /// </remarks>
    bool BeginWriterCall(int index);

    /// <summary>
    /// Marks writer <paramref name="index"/> idle. After the deadline, an idle writer may be disposed.
    /// </summary>
    /// <param name="index">The index passed to <see cref="BeginWriterCall"/>.</param>
    /// <remarks>Does not throw. Called after the writer returns, so dispose is not concurrent with that call.</remarks>
    void EndWriterCall(int index);
}
