// readme:step1:begin
using ALog;
using ALog.Config;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Formatters;

internal static class Program
{
    /// <summary>
    /// Runs the Quick Start and waits until the console and file writers have finished.
    /// Step 2 throws and catches <see cref="InvalidOperationException"/> ("Card declined")
    /// and logs that exception at error, so the recorded frame is <c>Program.Main</c>.
    /// The entry point stays synchronous; an async Main would record a compiler state machine instead.
    /// </summary>
    private static void Main()
    {
        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true, formatter: new PlainTextFormatter("HH:mm:ss")))
            .AddWriter(new FileLogWriter("logs/app.log", new JsonFormatter(pretty: true), maxFileSizeInBytes: 1_048_576)) // 1 MB
            .SetMinimumLevel(LogLevel.Debug);

        Log.Init(config);
// readme:step1:end

// readme:step2:begin
        Log.Write("Application started");

        using (Log.BeginScope("userId", 42))
        {
            using (Log.BeginScope("feature", "Login"))
            {
                Log.Write("User successfully authenticated");
                try
                {
                    throw new InvalidOperationException("Card declined");
                }
                catch (InvalidOperationException ex)
                {
                    Log.Write(ex, "Something went wrong", LogLevel.Error);
                }
            }
        }

        Log.WriteAsync("Async log message").GetAwaiter().GetResult();

        Log.FlushAsync().GetAwaiter().GetResult();
        Log.Shutdown();
    }
}
// readme:step2:end
