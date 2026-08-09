[![Download ALog](https://a.fsdn.com/con/app/sf-download-button)](https://sourceforge.net/projects/alog/files/latest/download)
![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/chookees/alog/total)

# ALog – A Modern, Modular, Cross-Platform Logger for .NET 10+

**ALog** is a powerful and extensible logging framework built for .NET 10+.  
It is designed to be simple to use, highly configurable, and ready for modern development across platforms including Windows, Linux, and iOS.

---

## Features

- Intuitive API: `Log.Write(...)`, `Log.WriteAsync(...)`
- Fully async- and sync-capable (`UseAsync` / `AsyncEnabled`)
- Structured logging with scoped contextual data (`BeginScope(...)`)
- Exception logging included
- Formatter support (PlainText, JSON, or custom)
- Console and file writers (with optional color + rolling)
- Log level filtering
- Cross-platform compatible (Windows, Linux, iOS helpers)
- Fluent, builder-style configuration
- Background queue via `UseBackgroundQueue(...)` (`BackgroundQueueEnabled`)
- Deterministic shutdown: `FlushAsync` + `Log.Shutdown()`

---

## Installation

> ALog is currently under development. You can use it via project reference:

```bash
git clone https://github.com/yourusername/ALog.git
```

Reference `ALog.csproj` in your .NET 10+ project.

---

## Quick Start

### Step 1: Configure ALog

```csharp
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
```

### Step 2: Start Logging

```csharp
Log.Write("Application started");

using (Log.BeginScope("userId", 42))
{
    using (Log.BeginScope("feature", "Login"))
    {
        Log.Write("User successfully authenticated");
        Log.Write(new Exception("Test failure"), "Something went wrong", LogLevel.Error);
    }
}

await Log.WriteAsync("Async log message");

await Log.FlushAsync();
Log.Shutdown();
```

### Step 3: Background Queue (Optional)

For high-performance scenarios, enable the background queue:

```csharp
var config = new LoggerConfig()
    .AddWriter(new ConsoleLogWriter())
    .AddWriter(new FileLogWriter("logs/app.log"))
    .UseBackgroundQueue(enabled: true, capacity: 1000, batchSize: 10, flushInterval: TimeSpan.FromMilliseconds(100))
    .SetMinimumLevel(LogLevel.Debug);

Log.Init(config);

// Logs are queued and processed in background (BackgroundQueueEnabled == true)
Log.Write("This will be processed asynchronously");

await Log.FlushAsync();
Log.Shutdown();
```

### Step 4: Shipping Writers

```csharp
using ALog.Writers.Http;
using ALog.Writers.Sql;
using ALog.Writers.Cloud;

// HTTP Writer
var httpWriter = new HttpLogWriter(
    endpoint: "https://api.example.com/logs",
    method: HttpMethod.Post,
    headers: new Dictionary<string, string> { ["Authorization"] = "Bearer token" }
);

// SQL Writer (tableName: ^[A-Za-z_][A-Za-z0-9_]*$, max 128 chars)
var sqlWriter = new SqlLogWriter(
    connectionString: "Server=localhost;Database=Logs;Integrated Security=true;",
    tableName: "ApplicationLogs"
);

// Azure Application Insights (correct /v2/track payload; iKey in body)
var azureWriter = new AzureLogWriter(instrumentationKey: "your-instrumentation-key");

// AWS CloudWatch Logs (default AWS credential chain, or pass accessKey/secretKey / custom transport)
var awsWriter = new AwsCloudWatchWriter(
    logGroupName: "/aws/application/alog",
    logStreamName: "app-stream",
    region: "eu-central-1");

var config = new LoggerConfig()
    .AddWriter(httpWriter)
    .AddWriter(sqlWriter)
    .AddWriter(azureWriter)
    .AddWriter(awsWriter);

Log.Init(config);
```

---

## Limitations

- **SQL `tableName`** must match `^[A-Za-z_][A-Za-z0-9_]*$` (max 128). Schema-qualified or quoted names are rejected at construction.
- **Writer lifetime:** `AddWriter` transfers ownership to the engine; call `Log.Shutdown()` (or `Log.Reset()`) to dispose writers. Prefer `Shutdown` before a second `Init`.
- Injected `HttpClient` instances are **not** disposed by `HttpLogWriter` / `AzureLogWriter`. Injected `IAwsCloudWatchLogsTransport` is **not** disposed by `AwsCloudWatchWriter`.
- Cloud writers fail closed (errors to stderr) when credentials/endpoints reject the request.

---

## Platform-Specific Paths (Optional)

Built-in `IPlatformHelper` implementations resolve safe, writable log paths on **Windows**, **Linux**, and **iOS** only:

### Windows

```csharp
using ALog.Platform.Windows;

var logPath = new WindowsPlatformHelper().ResolveLogFilePath("logs/app.log");
```

### Linux

```csharp
using ALog.Platform.Linux;

var logPath = new LinuxPlatformHelper().ResolveLogFilePath("logs/app.log");
```

### iOS (MAUI / Xamarin)

```csharp
using ALog.Platform.iOS;

var logPath = new IOSPlatformHelper().ResolveLogFilePath("logs/app.log");
```

> You control the log location – ALog does not enforce platform helpers. They are optional and recommended for mobile or portable environments.

---

## Writers

| Writer                | Description                                                | Status        |
|-----------------------|------------------------------------------------------------|---------------|
| `ConsoleLogWriter`    | Outputs to console with optional color and formatting      | Shipping      |
| `FileLogWriter`       | Outputs to file with optional rolling and formatter support| Shipping      |
| `HttpLogWriter`       | Sends logs to HTTP endpoints (REST APIs, webhooks)         | Shipping      |
| `SqlLogWriter`        | Stores logs in SQL Server database                         | Shipping      |
| `AzureLogWriter`      | Application Insights `/v2/track` (iKey in body)             | Shipping      |
| `AwsCloudWatchWriter` | CloudWatch Logs via AWSSDK (or injected transport)         | Shipping      |

Namespaces: `ALog.Writers.Console`, `ALog.Writers.File`, `ALog.Writers.Http`, `ALog.Writers.Sql`, `ALog.Writers.Cloud`.

---

## Formatters

| Formatter           | Description                                                |
|---------------------|------------------------------------------------------------|
| `PlainTextFormatter`| Developer-friendly, single-line format (customizable time)|
| `JsonFormatter`     | Structured JSON output, ideal for logs ingestion tools     |

Namespace: `ALog.Formatters`. Config-level `SetFormatter` applies only to writers that do not already have a formatter (single format pass).

---

## Contextual Logging

Scoped logging adds temporary key-value pairs that are automatically removed when their scope ends:

```csharp
using (Log.BeginScope("sessionId", "abc123"))
{
    Log.Write("User clicked 'Buy'");
}
// sessionId is no longer attached here
```

> Works automatically with supported formatters like JSON or plain text.

---

## Roadmap

- [x] Scope-based logging (`using Log.BeginScope(...)`)
- [x] Channel-based async background log queue
- [x] Shipping writers (HTTP, SQL, Azure App Insights track, AWS CloudWatch Logs)
- [x] Core unit tests
- [x] Live SQL integration tests (Testcontainers)
- [ ] External config via JSON or environment
- [ ] NuGet package & logo

---

## Contributing

Contributions welcome! Fork the repository and submit a PR.

For ideas like new formatters or writers, feel free to open a discussion first.

---

## License

MIT © Artur Bobb / Chookees

---

## Maintainer

Built and maintained by **Artur Bobb / Chookees**
