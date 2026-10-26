# ALog – A Modern, Modular, Cross-Platform Logger for .NET 10+
[![CI](https://img.shields.io/github/actions/workflow/status/Chookees/ALog/dotnet.yml?branch=master&label=CI)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![Coverage](https://raw.githubusercontent.com/Chookees/ALog/badges/coverage-ALog.svg)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue)](https://github.com/Chookees/ALog/blob/master/LICENSE) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) [![GitHub downloads](https://img.shields.io/github/downloads/chookees/alog/total)](https://github.com/Chookees/ALog/releases) 
[![XERJ Corpus Hub contributor](https://hub.xerj.org/contributors/Chookees/badge.svg)](https://hub.xerj.org/contributors/Chookees)
[![Download ALog](https://a.fsdn.com/con/app/sf-download-button)](https://sourceforge.net/projects/alog/files/latest/download)

**ALog** is a powerful and extensible logging framework built for .NET 10+.  
It is designed to be simple to use, highly configurable, and ready for modern development across platforms including Windows, Linux, and iOS.

---

## Features

- Intuitive API: `Log.Write(...)`, `Log.WriteAsync(...)`
- Message templates: `Log.Write(LogLevel.Info, "User {UserId} logged in", userId)`
- Enrichers (`MachineName`, `ThreadId`, `ProcessId`, `Activity`, custom)
- JSON/env options via `ALogOptions` / `LoggerConfigFactory`
- `Microsoft.Extensions.Logging` provider (`ALog.Extensions.Logging`) + ASP.NET sample
- Formatters, scopes, background queue (drops and counts events when full, or applies backpressure with `QueueFullMode = Block`), `FlushAsync` / `Shutdown`
- Writers: Console, File, HTTP, SQL, Azure App Insights, AWS CloudWatch
- Benchmarks: `ALog.Benchmarks`

---

## Installation

```bash
dotnet add package ALog
dotnet add package ALog.Extensions.Logging
```

Source: https://github.com/Chookees/ALog · Optional future sink split: [`docs/PACKAGE_SPLIT.md`](https://github.com/Chookees/ALog/blob/master/docs/PACKAGE_SPLIT.md)

### ASP.NET Core

```csharp
using ALog.Extensions.Logging;

builder.Logging.ClearProviders();
builder.Logging.AddALog(builder.Configuration); // binds "ALog" section
```

Turn on ALog's console in `appsettings.json`:
```json
{ "ALog": { "Console": { "Enabled": true } } }
```

ALog reads its settings, including MinimumLevel, once at startup. Changes while the app runs have no effect; restart the app.

See `samples/ALog.AspNetSample`.

---

## Console output

ALog writes to the console only when `ALog:Console:Enabled` is `true`. With `AddALog`, choose one console: keep the Microsoft console provider and leave ALog's console off, or call `builder.Logging.ClearProviders()` and set `ALog:Console:Enabled` to `true` (as in `samples/ALog.AspNetSample`). With both, every line appears twice.

An exception shares that line. The form is `Type: Message`, then inner exceptions, then the source frame. Types in `System.*` use the short name; every other type keeps its namespace. A thrown exception names the application frame:

```
[20:55:19] [Error] Payment failed | InvalidOperationException: Card declined | at CheckoutService.Pay in CheckoutService.cs:42 | ctx: orderId=17
```

`|` and `-->` in the message, the category, and context or property names and values are escaped as `\|` and `--\>`, line breaks as `\r` and `\n`, and a backslash is doubled only where it would otherwise read as one of these escapes (`C:\temp` stays as is), so each event stays one line. `PlainTextFormatter(includeStackTrace: true)` appends the full stack trace indented by four spaces. The default is off.

`PlainTextFormatter(includeStackTrace: true)` calls .NET's `Exception.ToString()`, and a self-referencing exception built via reflection can overflow inside that call.

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
```

### Example output

Step 1 and Step 2 together are a complete program: [`samples/ALog.QuickstartSample`](https://github.com/Chookees/ALog/tree/master/samples/ALog.QuickstartSample). Running it prints this to the console:

![Console output of the Quick Start sample from Step 1 and Step 2](https://raw.githubusercontent.com/Chookees/ALog/master/docs/images/quickstart-console.png)

The first entry and the error entry from `logs/app.log` of the same run:

```json
{
  "timestamp": "2026-10-22T03:43:09.9681404Z",
  "level": "Info",
  "message": "Application started",
  "properties": {},
  "context": {}
}
{
  "timestamp": "2026-10-22T03:43:10.0425193Z",
  "level": "Error",
  "message": "Payment failed",
  "exception": {
    "type": "System.InvalidOperationException",
    "message": "Card declined",
    "stackTrace": "   at Program.\u003CMain\u003E$(String[] args) in /_/Program.cs:line 33",
    "source": {
      "method": "Program.Main",
      "file": "Program.cs",
      "line": 33
    }
  },
  "properties": {},
  "context": {
    "feature": "Checkout",
    "orderId": 17
  }
}
```

Both come from a real run of the sample, unedited.

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

When the queue is full, ALog drops the new event by default and counts it (SelfLog ALOG0070). To make the caller wait instead, set `QueueFullMode = Block` (in JSON: `"ALog": { "QueueFullMode": "Block" }`). Even with `Block`, a call from the queue's own writer thread or after shutdown has started is dropped and counted, so logging can never deadlock.

If the log file can't be reopened, ALog retries at most once per second and reports it once a minute (SelfLog ALOG0073). It keeps up to 4 MiB of lines for the file, with or without a background queue, and drops and counts the rest (SelfLog ALOG0070). Events still in the background queue are limited by the queue capacity, not by this limit.

#### Queue, file buffer, and shutdown options

- `QueueFullMode`: `Drop` (default) drops and counts new events while the queue is full. `Block` makes the caller wait until there is room.
- `FileBufferSize`: how much `FileLogWriter` collects before it writes to the file. Default 64 KiB, 4 KiB to 16 MiB.
- `FileFlushInterval`: how often buffered lines are written even if the buffer is not full. Default 1 s, 0 to 60 s. `0` writes every event right away.
- `FlushTimeout`: how long `Flush`, `Dispose`, and `Shutdown` wait for queued events. Default 5 s, 0 to 60 s. Events still unwritten after that are counted (ALOG0071). On every OS, `Flush` closes the write handle and reopens the file for reading so readers can open it, which makes each call slower. That matters most on Windows, where `File.ReadAllText` needs that reopen. Don't call `Flush` after every line.
- Each log file has one writer process: give each process its own file. Other programs can read, tail, and rotate it. On Windows, `File.ReadAllText` asks for `FileShare.Read`, so it can open the file only after `Flush`, when the flush interval passes with no new lines (not with `FileFlushInterval` 0), or after dispose. To read while lines are being written, open the file with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`.

If the process crashes, the file can miss up to one `FileFlushInterval` or one `FileBufferSize` of lines, whichever comes first. Set `FileFlushInterval` to `0` if every line must reach the operating system immediately.

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

`AwsCloudWatchWriter` uses the AWS SDK for .NET V4 (`AWSSDK.CloudWatchLogs` 4.0.106). If your app references other `AWSSDK.*` packages, use their V4 versions too. V3 packages next to it make restore fail with NU1107 (version conflict on `AWSSDK.Core`), or restore succeeds with warning NU1608 and the app throws `MissingMethodException` at runtime.

---

## Limitations

- **SQL `tableName`** must match `^[A-Za-z_][A-Za-z0-9_]*$` (max 128). Schema-qualified or quoted names are rejected at construction.
- **Writer lifetime:** `AddWriter` transfers ownership to the engine; call `Log.Shutdown()` (or `Log.Reset()`) to dispose writers. Prefer `Shutdown` before a second `Init`.
- Injected `HttpClient` instances are **not** disposed by `HttpLogWriter` / `AzureLogWriter`. Injected `IAwsCloudWatchLogsTransport` is **not** disposed by `AwsCloudWatchWriter`.
- Cloud writers fail closed (errors to stderr) when credentials/endpoints reject the request.

## Observations during testing

- After the log file has opened successfully, a later write that fails (for example disk full or an I/O error) is reported only on stderr. That line is lost and is still counted as written. Watch free disk space and stderr, and keep logs on a volume with headroom.
- If the log file is renamed or moved while the writer still holds its write handle, and every later reopen fails (for example something else now occupies the path), further lines are lost with only stderr output and no SelfLog event. Rotate with tools that leave the path writable, or call `Flush` / let the flush interval pass before external rotation, and watch stderr.
- If the very first open fails (for example a read-only directory), each event tries one open and writes `Write failed` to stderr, with no SelfLog event, and the queue counts the event as written. Make sure the directory is writable at startup (for example via a platform helper path) and watch stderr.
- `FlushAsync` can rarely return before the event logged just before it is in the file (about 1 in 2000 calls in measurement, and more often on the first `FlushAsync` in a fresh process). Use `Flush()` or `Shutdown()` when you must read the file right afterwards.

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

Without the background queue, the file writer writes on the calling thread, so a slow disk or network share slows down the caller. Enable the background queue to decouple callers from file I/O.

If you pass your own `HttpClient` to `HttpLogWriter` or `AzureLogWriter`, its handler must support synchronous `Send`, or enable the background queue. Use `AwsCloudWatchWriter` with the background queue: the AWS SDK has no synchronous API.

---

## Formatters

| Formatter           | Description                                                |
|---------------------|------------------------------------------------------------|
| `PlainTextFormatter`| One line (customizable time). Exceptions read `Type: Message`, with inner exceptions and `at Class.Method in File.cs:42`. `includeStackTrace: true` appends the full stack. |
| `JsonFormatter`     | Structured JSON. `exception` keeps `type`, `message`, and `stackTrace`, and adds `source` (`method`, `file`, `line`) and `inner` (up to 16). |

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
- [x] Core unit tests + live SQL integration tests
- [x] External config via JSON / environment (`ALog` section)
- [x] MEL provider + ASP.NET sample
- [x] Message templates + enrichers
- [x] Benchmarks project
- [ ] Modular sink packages (see `docs/PACKAGE_SPLIT.md`)
- [ ] NuGet logo / package icon

---

## Migrating from 0.2.0

### C2-Console

- Breaking (behavior): ALog writes to the console only when `ALog:Console:Enabled` is `true`. Before, `AddALog` added its own console output next to the Microsoft console provider, so every line appeared twice.

### AWS SDK V4

- Breaking (dependency): `AWSSDK.CloudWatchLogs` moves from 3.7.509.3 (AWS SDK for .NET V3, end of support since 2026-06-01) to 4.0.106 (V4). If your app references other `AWSSDK.*` packages, upgrade all of them to V4 together; otherwise restore fails with NU1107 (version conflict on `AWSSDK.Core`), or it succeeds with warning NU1608 and the app throws `MissingMethodException` at runtime.

---

## Contributing

Contributions welcome! Fork the repository and submit a PR.

To see why a test was skipped, run `dotnet test --logger "console;verbosity=normal"`.

For ideas like new formatters or writers, feel free to open a discussion first.

---

## License

Apache License 2.0 © Artur Bobb. See [LICENSE](https://github.com/Chookees/ALog/blob/master/LICENSE) and [NOTICE](https://github.com/Chookees/ALog/blob/master/NOTICE). Versions up to and including 0.2.0 were released under the MIT License.

---

## Maintainer

Built and maintained by **Artur Bobb / Chookees**
