using ALog;
using ALog.Config;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Writers.Http;
using ALog.Writers.Sql;
using ALog.Writers.Cloud;
using ALog.Formatters;

namespace DemoApp;

public class ExtendedDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== ALog Extended Demo ===\n");

        // Demo 1: Background Queue
        await DemoBackgroundQueue();

        // Demo 2: HTTP Writer
        await DemoHttpWriter();

        // Demo 3: SQL Writer (commented out - requires SQL Server)
        // await DemoSqlWriter();

        // Demo 4: Cloud Writers
        await DemoCloudWriters();

        Console.WriteLine("\n=== Demo completed ===");
    }

    private static async Task DemoBackgroundQueue()
    {
        Console.WriteLine("1. Background Queue Demo");
        Console.WriteLine("========================");

        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true, formatter: new PlainTextFormatter("HH:mm:ss.fff")))
            .AddWriter(new FileLogWriter("logs/background-demo.log", new JsonFormatter(pretty: true)))
            .SetMinimumLevel(LogLevel.Debug)
            .UseBackgroundQueue(enabled: true, capacity: 100, batchSize: 5, flushInterval: TimeSpan.FromMilliseconds(500));

        Log.Init(config);

        // Generate many log messages quickly
        for (int i = 1; i <= 20; i++)
        {
            Log.Write($"Background queue message {i}", LogLevel.Info);
            await Log.WriteAsync($"Async background message {i}", LogLevel.Debug);
        }

        Console.WriteLine("Waiting for background queue to process...");
        await Task.Delay(2000);

        await Log.FlushAsync();
        Console.WriteLine("Background queue demo completed.\n");
    }

    private static async Task DemoHttpWriter()
    {
        Console.WriteLine("2. HTTP Writer Demo");
        Console.WriteLine("===================");

        // Note: This will fail unless you have a real HTTP endpoint
        var httpWriter = new HttpLogWriter(
            endpoint: "https://httpbin.org/post",
            method: HttpMethod.Post,
            headers: new Dictionary<string, string>
            {
                ["User-Agent"] = "ALog/1.0",
                ["X-Custom-Header"] = "Demo"
            },
            formatter: new JsonFormatter(pretty: true)
        );

        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true))
            .AddWriter(httpWriter)
            .SetMinimumLevel(LogLevel.Info);

        Log.Init(config);

        Log.Write("HTTP Writer test message");
        await Log.WriteAsync("Async HTTP message");

        using (Log.BeginScope("requestId", "req-123"))
        {
            Log.Write("HTTP request with context");
        }

        Console.WriteLine("HTTP Writer demo completed.\n");
    }

    private static async Task DemoSqlWriter()
    {
        Console.WriteLine("3. SQL Writer Demo");
        Console.WriteLine("==================");

        // Note: This requires a SQL Server instance
        var sqlWriter = new SqlLogWriter(
            connectionString: "Server=localhost;Database=Logs;Integrated Security=true;",
            tableName: "ApplicationLogs",
            autoCreateTable: true
        );

        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true))
            .AddWriter(sqlWriter)
            .SetMinimumLevel(LogLevel.Info);

        Log.Init(config);

        Log.Write("SQL Writer test message");
        await Log.WriteAsync("Async SQL message");

        using (Log.BeginScope("userId", 12345))
        {
            Log.Write("User action logged to database");
        }

        Console.WriteLine("SQL Writer demo completed.\n");
    }

    private static async Task DemoCloudWriters()
    {
        Console.WriteLine("4. Cloud Writers Demo");
        Console.WriteLine("=====================");

        // Azure Application Insights (requires instrumentation key)
        var azureWriter = new AzureLogWriter(
            instrumentationKey: "your-instrumentation-key-here",
            formatter: new JsonFormatter(pretty: false)
        );

        // AWS CloudWatch (requires AWS credentials)
        var awsWriter = new AwsCloudWatchWriter(
            logGroupName: "/aws/application/alog-demo",
            logStreamName: "demo-stream",
            region: "us-east-1"
        );

        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true))
            .AddWriter(azureWriter)
            .AddWriter(awsWriter)
            .SetMinimumLevel(LogLevel.Info);

        Log.Init(config);

        Log.Write("Cloud logging test message");
        await Log.WriteAsync("Async cloud message");

        using (Log.BeginScope("environment", "production"))
        {
            Log.Write("Production environment log");
        }

        Log.Write(new Exception("Test cloud exception"), "Exception logged to cloud services", LogLevel.Error);

        Console.WriteLine("Cloud Writers demo completed.\n");
    }
}
