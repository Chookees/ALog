// Child process for C4-06. A hanging writer must not keep the process alive
// after Shutdown returns and Main returns. The processor has to be a background thread.
// Next: ALog/Core/BackgroundLogQueue.cs.
using ALog;
using ALog.Config;
using ALog.Core;

/// <summary>
/// Starts one background queue, blocks inside the writer, shuts down, and returns.
/// Prints DISPOSED if the writer is disposed, and MAIN_RETURN just before Main returns.
/// </summary>
internal static class HangProbe
{
    /// <summary>
    /// Runs the probe. Exits 0 when shutdown returns. A foreground processor prevents this return from ending the process.
    /// </summary>
    /// <returns>0 after shutdown. 1 when setup throws.</returns>
    private static int Main()
    {
        try
        {
            SelfLog.Enable(Console.WriteLine);
            var writer = new HangWriter();
            using var engine = new LoggerEngine(new LoggerConfig()
                .AddWriter(writer)
                .UseBackgroundQueue(true, 8, 1, TimeSpan.FromHours(1)));
            engine.Log("hang", LogLevel.Info);
            if (!writer.Entered.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.WriteLine("NEVER_ENTERED");
                return 1;
            }

            engine.Dispose();
            Console.WriteLine("MAIN_RETURN");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE_EXCEPTION " + ex.GetType().Name);
            return 1;
        }
    }

    /// <summary>
    /// Blocks in <see cref="Write"/> until the process ends. Dispose prints DISPOSED.
    /// </summary>
    private sealed class HangWriter : ALog.Public.Interfaces.ILogWriter
    {
        /// <summary>Set when <see cref="Write"/> has been entered. The probe waits on this before shutdown.</summary>
        public ManualResetEventSlim Entered { get; } = new(false);

        /// <summary>
        /// Blocks the caller. The bound is the process exit, which is the point of the probe.
        /// </summary>
        /// <param name="logEvent">Ignored. Not null in normal use.</param>
        public void Write(ALog.Core.LogEvent logEvent)
        {
            Entered.Set();
            Thread.Sleep(Timeout.Infinite);
        }

        /// <summary>
        /// Blocks the same way as <see cref="Write"/>. The queue's async path calls this.
        /// </summary>
        /// <param name="logEvent">Ignored.</param>
        /// <returns>A task that does not complete, because <see cref="Write"/> does not return.</returns>
        public Task WriteAsync(ALog.Core.LogEvent logEvent)
        {
            Write(logEvent);
            return Task.CompletedTask;
        }

        /// <summary>Prints DISPOSED. The C4 contract says a writer still inside Write must not be disposed.</summary>
        public void Dispose() => Console.WriteLine("DISPOSED");
    }
}
