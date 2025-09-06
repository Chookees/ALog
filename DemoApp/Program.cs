using ALog;
using ALog.Config;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Formatters;

internal class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("ALog Demo Application");
        Console.WriteLine("====================");
        Console.WriteLine("1. Basic Demo");
        Console.WriteLine("2. Extended Demo (Background Queue, HTTP, SQL, Cloud)");
        Console.WriteLine("Choose demo (1 or 2): ");

        var choice = Console.ReadLine();

        if (choice == "2")
        {
            await ExtendedDemo.RunAsync();
        }
        else
        {
            await RunBasicDemo();
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    private static async Task RunBasicDemo()
    {
        Console.WriteLine("\n=== Basic Demo ===");

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

        using (Log.BeginScope("userId", 123))
        {
            Log.Write("Inside scoped block");
        }

        Log.Write("User login successfull.", LogLevel.Info);

        Log.Write(new Exception("Testerror"), "When saving an error occurred.", LogLevel.Error);

        await Log.WriteAsync("Async-Logentry successfull written.");

        Log.ClearContext();

        Log.Write("Continued Logging Without Context.");
    }
}
