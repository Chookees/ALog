using ALog;
using ALog.Config;

internal class Program
{
    public static async Task Main(string[] args)
    {
        var consoleWriter = new ConsoleLogWriter(
            useColors: true,
            formatter: new PlainTextFormatter("HH:mm:ss")
        );

        var fileWriter = new FileLogWriter(
            filePath: "logs/app.log",
            formatter: new JsonFormatter(pretty: true),
            maxFileSizeInBytes: 1024 * 1024 // 1 MB
        );

        var config = new LoggerConfig()
            .AddWriter(consoleWriter)
            .AddWriter(fileWriter)
            .SetMinimumLevel(LogLevel.Debug);

        Log.Init(config);

        Log.Write("System started.");
        Log.WithContext("UserId", 42);
        Log.WithContext("Feature", "Demo");

        Log.Write("User login successfull.", LogLevel.Info);

        Log.Write(new Exception("Testerror"), "When saving an error occurred.", LogLevel.Error);

        await Log.WriteAsync("Async-Logentry successfull written.");

        Log.ClearContext();

        Log.Write("Continued Logging Without Context.");
        Console.ReadKey();
    }
}
