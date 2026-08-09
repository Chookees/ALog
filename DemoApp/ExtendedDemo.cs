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

        await DemoBackgroundQueue();
        await DemoHttpWriter();
        // await DemoSqlWriter(); // requires SQL Server; tableName must match ^[A-Za-z_][A-Za-z0-9_]*$
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

        for (int i = 1; i <= 20; i++)
        {
            Log.Write($"Background queue message {i}", LogLevel.Info);
            await Log.WriteAsync($"Async background message {i}", LogLevel.Debug);
        }

        Console.WriteLine("Flushing background queue...");
        await Log.FlushAsync();
        Log.Shutdown();
        Console.WriteLine("Background queue demo completed.\n");
    }

    private static async Task DemoHttpWriter()
    {
        Console.WriteLine("2. HTTP Writer Demo");
        Console.WriteLine("===================");

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

        await Log.FlushAsync();
        Log.Shutdown();
        Console.WriteLine("HTTP Writer demo completed.\n");
    }

    private static async Task DemoSqlWriter()
    {
        Console.WriteLine("3. SQL Writer Demo");
        Console.WriteLine("==================");

        // tableName must match ^[A-Za-z_][A-Za-z0-9_]*$ (max 128)
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

        await Log.FlushAsync();
        Log.Shutdown();
        Console.WriteLine("SQL Writer demo completed.\n");
    }

    private static async Task DemoCloudWriters()
    {
        Console.WriteLine("4. Cloud Writers (shipping)");
        Console.WriteLine("===========================");
        Console.WriteLine("Uses transport/HTTP clients. Without real credentials/endpoints, writes fail closed to stderr.\n");

        // Demo with injectable AWS transport (no live AWS call).
        var demoTransport = new DemoAwsTransport();
        var azureHandler = new DemoHttpHandler();
        using var azureClient = new HttpClient(azureHandler);

        var azureWriter = new AzureLogWriter(
            instrumentationKey: "00000000-0000-0000-0000-000000000000",
            httpClient: azureClient,
            formatter: new JsonFormatter(pretty: false)
        );

        var awsWriter = new AwsCloudWatchWriter(
            logGroupName: "/aws/application/alog-demo",
            logStreamName: "demo-stream",
            transport: demoTransport
        );

        var config = new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: true))
            .AddWriter(azureWriter)
            .AddWriter(awsWriter)
            .SetMinimumLevel(LogLevel.Info);

        Log.Init(config);

        Log.Write("Cloud logging test message");
        await Log.WriteAsync("Async cloud message");

        await Log.FlushAsync();
        Log.Shutdown();

        Console.WriteLine($"Azure POSTs captured: {azureHandler.Count}");
        Console.WriteLine($"AWS PutLogEvent calls: {demoTransport.Count}");
        Console.WriteLine("Cloud writers demo completed.\n");
    }

    private sealed class DemoAwsTransport : IAwsCloudWatchLogsTransport
    {
        public int Count { get; private set; }

        public Task PutLogEventAsync(
            string logGroupName,
            string logStreamName,
            string message,
            long timestampUnixMs,
            CancellationToken cancellationToken = default)
        {
            Count++;
            Console.WriteLine($"[DemoAwsTransport] {logGroupName}/{logStreamName}: {message}");
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class DemoHttpHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"itemsReceived\":1,\"itemsAccepted\":1}")
            });
        }
    }
}
