// readme:step1:begin
using ALog;
using ALog.Config;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Formatters;

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
    }
}

using (Log.BeginScope("orderId", 17))
{
    using (Log.BeginScope("feature", "Checkout"))
    {
        try
        {
            throw new InvalidOperationException("Card declined");
        }
        catch (InvalidOperationException ex)
        {
            Log.Write(ex, "Payment failed", LogLevel.Error);
        }
    }
}

await Log.WriteAsync("Async log message");

await Log.FlushAsync();
Log.Shutdown();
// readme:step2:end
