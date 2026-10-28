# ALog – A Modern, Modular, Cross-Platform Logger for .NET 10+
[![CI](https://img.shields.io/github/actions/workflow/status/Chookees/ALog/dotnet.yml?branch=master&label=CI)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![Coverage](https://raw.githubusercontent.com/Chookees/ALog/badges/coverage-ALog.svg)](https://github.com/Chookees/ALog/actions/workflows/dotnet.yml) [![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue)](https://github.com/Chookees/ALog/blob/master/LICENSE) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) [![GitHub downloads](https://img.shields.io/github/downloads/chookees/alog/total)](https://github.com/Chookees/ALog/releases) 
[![XERJ Corpus Hub contributor](https://hub.xerj.org/contributors/Chookees/badge.svg)](https://hub.xerj.org/contributors/Chookees)

**ALog** is a logging framework for .NET 10+ with a small façade (`Log.Write` / `Log.WriteAsync`), message templates, scopes, and enrichers.

It ships console, file (size rotation), HTTP, SQL, Azure Application Insights, and AWS CloudWatch writers, plus a `Microsoft.Extensions.Logging` provider (`ALog.Extensions.Logging`) that binds the `ALog` configuration section.

An optional background queue can drop and count events when full (`QueueFullMode.Drop`, default) or apply backpressure (`QueueFullMode.Block`). Internal failures go to `SelfLog` so logging does not throw into the application.

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

## How ALog compares

How ALog's main package compares with the three most used .NET logging libraries. Links go to each project on GitHub.

| | ALog | [Serilog](https://github.com/serilog/serilog) | [NLog](https://github.com/NLog/NLog) | [log4net](https://github.com/apache/logging-log4net) |
|---|---|---|---|---|
| License | Apache-2.0 | Apache-2.0 | BSD-3-Clause | Apache-2.0 |
| Message templates (structured events) | ✔️ | ✔️ | ✔️ | —[^1] |
| Console and file output | ✔️ | [`Serilog.Sinks.Console`](https://www.nuget.org/packages/Serilog.Sinks.Console), [`Serilog.Sinks.File`](https://www.nuget.org/packages/Serilog.Sinks.File) | ✔️ | ✔️ |
| SQL Server writer | ✔️ | [`Serilog.Sinks.MSSqlServer`](https://www.nuget.org/packages/Serilog.Sinks.MSSqlServer)[^2] | [`NLog.Database`](https://www.nuget.org/packages/NLog.Database) | ✔️ |
| HTTP writer | ✔️ | [`Serilog.Sinks.Http`](https://www.nuget.org/packages/Serilog.Sinks.Http)[^2] | [`NLog.Targets.WebService`](https://www.nuget.org/packages/NLog.Targets.WebService) | —[^3] |
| Azure Application Insights writer | ✔️ | [`Serilog.Sinks.ApplicationInsights`](https://www.nuget.org/packages/Serilog.Sinks.ApplicationInsights)[^2] | [`Microsoft.ApplicationInsights.NLogTarget`](https://www.nuget.org/packages/Microsoft.ApplicationInsights.NLogTarget)[^4] | —[^5] |
| AWS CloudWatch writer | ✔️ | [`Serilog.Sinks.AwsCloudWatch`](https://www.nuget.org/packages/Serilog.Sinks.AwsCloudWatch)[^2] | [`AWS.Logger.NLog`](https://www.nuget.org/packages/AWS.Logger.NLog)[^4] | [`AWS.Logger.Log4net`](https://www.nuget.org/packages/AWS.Logger.Log4net)[^6] |
| Background queue that drops and counts, or blocks, when full | ✔️ | [`Serilog.Sinks.Async`](https://www.nuget.org/packages/Serilog.Sinks.Async) | ✔️ | —[^7] |
| Microsoft.Extensions.Logging provider | ✔️ ([separate project in this repo](https://github.com/Chookees/ALog/tree/master/ALog.Extensions.Logging)) | [`Serilog.Extensions.Logging`](https://www.nuget.org/packages/Serilog.Extensions.Logging) | [`NLog.Extensions.Logging`](https://www.nuget.org/packages/NLog.Extensions.Logging) | [`Microsoft.Extensions.Logging.Log4Net.AspNetCore`](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Log4Net.AspNetCore)[^6] |
| JSON configuration | ✔️ (Console and File only, [see Configuration](#configuration)) | [`Serilog.Settings.Configuration`](https://www.nuget.org/packages/Serilog.Settings.Configuration) | [`NLog.Extensions.Logging`](https://www.nuget.org/packages/NLog.Extensions.Logging) | —[^8] |
| Scopes and enrichers | ✔️ | ✔️ | ✔️ | ✔️ |
| Size-based file rotation | ✔️ | [`Serilog.Sinks.File`](https://www.nuget.org/packages/Serilog.Sinks.File) | ✔️ | ✔️ |
| Time-based file rolling | —[^9] | [`Serilog.Sinks.File`](https://www.nuget.org/packages/Serilog.Sinks.File) | ✔️ | ✔️ |
| Automatic deletion of old log files | —[^10] | [`Serilog.Sinks.File`](https://www.nuget.org/packages/Serilog.Sinks.File) | ✔️ | ✔️ |
| XML configuration | —[^11] | [`Serilog.Settings.AppSettings`](https://www.nuget.org/packages/Serilog.Settings.AppSettings) | ✔️ | ✔️ |
| Email output | —[^12] | [`Serilog.Sinks.Email`](https://www.nuget.org/packages/Serilog.Sinks.Email) | [`NLog.MailKit`](https://www.nuget.org/packages/NLog.MailKit) | ✔️ |
| Windows Event Log output | —[^12] | [`Serilog.Sinks.EventLog`](https://www.nuget.org/packages/Serilog.Sinks.EventLog) | [`NLog.WindowsEventLog`](https://www.nuget.org/packages/NLog.WindowsEventLog) | ✔️ (.NET Framework only) |
| Syslog output | —[^12] | [`Serilog.Sinks.SyslogMessages`](https://www.nuget.org/packages/Serilog.Sinks.SyslogMessages)[^2] | [`NLog.Targets.Network`](https://www.nuget.org/packages/NLog.Targets.Network) | ✔️ |

Checked on 2026-10-27 against Serilog 4.4.0, NLog 6.2.2, and log4net 3.5.0 (latest stable on NuGet). Community packages are marked in the footnotes.

[^1]: log4net has no message templates (structured events) in its main package or a maintained package, see [ILog](https://github.com/apache/logging-log4net/blob/master/src/log4net/ILog.cs).
[^2]: Community package, not maintained by the Serilog project.
[^3]: log4net has no HTTP writer in its main package or a maintained package, see [Appenders](https://logging.apache.org/log4net/manual/configuration/appenders.html).
[^4]: Community package, not maintained by the NLog project.
[^5]: log4net has no maintained Application Insights output; Microsoft.ApplicationInsights.Log4NetAppender is out of support, see [Migrate Application Insights Classic API SDKs to OpenTelemetry](https://learn.microsoft.com/en-us/azure/azure-monitor/app/migrate-to-opentelemetry).
[^6]: Community package, not maintained by the log4net project.
[^7]: log4net has no background queue that drops and counts, or blocks, when full in its main package or a maintained package, see [BufferingForwardingAppender](https://logging.apache.org/log4net/manual/configuration/appenders/bufferingforwardingappender.html).
[^8]: log4net has no JSON configuration in its main package or a maintained package, see [Configuration](https://logging.apache.org/log4net/manual/configuration.html).
[^9]: ALog rotates files by size only, see [Size rotation](#size-rotation).
[^10]: ALog keeps rotated files until you delete them, see [Size rotation](#size-rotation).
[^11]: ALog reads JSON configuration only, see [Configuration](#configuration).
[^12]: Not among ALog's writers, see [Writers](#writers).

### Performance

Time and memory per event when writing to a file, with each library's own async or buffered file setup. Lower is better in every row.

| | ALog | Serilog | NLog | log4net |
|---|---|---|---|---|
| Plain message, 1M events (ns per event) | 600 | 1,450 | 1,020 | 1,400 |
| Message template, 1M events (ns per event) | 1,050 | 1,800 | 1,320 | n/a[^13] |
| JSON output, 1M events (ns per event) | 1,300 | 5,450 | 1,700 | n/a[^14] |
| Exception with 3-level chain, 100k events (ns per event) | 22,400 | 30,700 | 28,800 | 31,800 |
| 8 threads, 1M events (ns per event) | 720 | 2,540 | 1,360 | 1,690 |
| Cold start to first event written (ms) | 26.0 | 128 | 102 | 94.8 |
| Memory allocated per event, plain message (bytes) | 152 | 680 | 193 | 923 |
| Peak working set, plain message (MB) | 61.0 | 71.3 | 70.9 | 71.0 |

ALog is behind NLog in four measurements: memory per event with message templates (632 B vs. 457 B, and 639 B vs. 263 B in BenchmarkDotNet), memory per event with exceptions (16,400 B vs. 13,300 B), and the BenchmarkDotNet time for logging an exception that was already thrown (197 ns vs. 64.8 ns).

Measured on 2026-10-27 with ALog 0.2.1, Serilog 4.4.0 (Serilog.Sinks.File 7.0.0), NLog 6.2.2, and log4net 3.5.0 on .NET 10.0.14, GitHub Actions ubuntu-24.04 (AMD EPYC 7763, 4 cores), mean of 5 runs. Every row shown here also held in 9 further runs on a separate Linux machine, with ALog at least 5% ahead of each library in every environment, all on commit b3daa7c. Harness, method, and all results, including where ALog is behind: [the harness at b3daa7c](https://github.com/Chookees/ALog/tree/b3daa7c77a743809fa09854aa760d81896b618b9/eng/comparison-bench) and [RESULTS.md](https://github.com/Chookees/ALog/blob/c1e1d5e2e9af424c7246430bb7d23047ccf8455a/eng/comparison-bench/RESULTS.md).

[^13]: log4net has no message templates or named properties.
[^14]: log4net has no built-in JSON layout.

---

## Installation

```bash
dotnet add package ALog
dotnet add package ALog.Extensions.Logging
```

Release zips are on the [Releases](https://github.com/Chookees/ALog/releases) page: Lite (`ALog-v0.2.1-Lite.zip`) covers Console, File, HTTP and Azure Application Insights on any platform (HTTP and Azure Application Insights only when configured in code; JSON config still supports only Console and File); Full is per RID (`ALog-v0.2.1-Full-win-x64.zip`, `ALog-v0.2.1-Full-linux-x64.zip`). For other platforms, or for SQL/AWS outside those RIDs, use NuGet.

Source: https://github.com/Chookees/ALog · Optional future sink split: [`docs/PACKAGE_SPLIT.md`](https://github.com/Chookees/ALog/blob/master/docs/PACKAGE_SPLIT.md)

### Hello World

```csharp
using ALog;
using ALog.Config;
using ALog.Writers.Console;

Log.Init(new LoggerConfig().AddWriter(new ConsoleLogWriter()));
Log.Write("Hello");
```

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

With `AddALog`, choose one console: keep the Microsoft console provider and leave ALog's console off, or call `builder.Logging.ClearProviders()` and set `ALog:Console:Enabled` to `true` (as in the `ALog.AspNetSample` project in [`DemoApp/AspNet`](https://github.com/Chookees/ALog/tree/master/DemoApp/AspNet)). With both, every line appears twice.

ALog reads its settings, including MinimumLevel, once at startup. Changes while the app runs have no effect; restart the app.

Inject `ILogger<T>` as usual:

```csharp
using Microsoft.Extensions.Logging;

public sealed class OrdersController(ILogger<OrdersController> logger)
{
    public void Place(int id) =>
        logger.LogInformation("Order {OrderId}", id);
}
```

With `"Formatter": "json"` (and `JsonPretty` false), that event is one NDJSON line shaped like `JsonFormatter` output (null fields omitted):

```json
{"timestamp":"2026-10-26T21:15:55.0000000Z","level":"Info","message":"Order 17","messageTemplate":"Order {OrderId}","category":"OrdersController","properties":{"OrderId":17},"context":{}}
```

See the `ALog.AspNetSample` project in [`DemoApp/AspNet`](https://github.com/Chookees/ALog/tree/master/DemoApp/AspNet).

### Configuration

Complete `appsettings.json` keys bound from `ALogOptions` (section name `ALog`):

```json
{
  "ALog": {
    "MinimumLevel": "Info",
    "AsyncEnabled": true,
    "BackgroundQueueEnabled": false,
    "BackgroundQueueCapacity": 1000,
    "BackgroundQueueBatchSize": 10,
    "BackgroundQueueFlushIntervalMs": 100,
    "QueueFullMode": "Drop",
    "FileBufferSize": 65536,
    "FileFlushInterval": "00:00:01",
    "FlushTimeout": "00:00:05",
    "Enrichers": [ "MachineName", "ThreadId", "ProcessId", "Activity" ],
    "Formatter": "json",
    "JsonPretty": false,
    "Console": {
      "Enabled": true,
      "UseColors": true
    },
    "File": {
      "Enabled": true,
      "Path": "logs/app.log",
      "MaxFileSizeInBytes": 1048576
    }
  }
}
```

Environment variables use the same section with `__` nesting (for example `ALog__MinimumLevel`, `ALog__Console__Enabled`, `ALog__File__Path`).

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
    .AddWriter(new FileLogWriter("logs/app.log", new JsonFormatter(pretty: false), maxFileSizeInBytes: 1_048_576)) // 1 MB
    .SetMinimumLevel(LogLevel.Debug);

Log.Init(config);
```

Use `JsonFormatter(pretty: false)` for files (NDJSON: one JSON object per line). `pretty: true` is for display only; a file of pretty-printed objects is not valid JSON or JSON Lines.

### Step 2: Start Logging

The exception overload takes the level last (`Log.Write(ex, "msg", LogLevel.Error)`); the template overload takes the level first (`Log.Write(LogLevel.Info, "User {UserId} logged in", 42)`).

```csharp
Log.Write("Application started");

using (Log.BeginScope("userId", 42))
{
    using (Log.BeginScope("feature", "Login"))
    {
        Log.Write(LogLevel.Info, "User {UserId} logged in", 42);
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

Step 1 and Step 2 together are a complete program: the `ALog.QuickstartSample` project in [`DemoApp/Quickstart`](https://github.com/Chookees/ALog/tree/master/DemoApp/Quickstart). Running it prints this to the console:

```
[22:48:09] [Info] Application started
[22:48:09] [Info] User 42 logged in | props: UserId=42 | ctx: feature=Login, userId=42
[22:48:09] [Error] Payment failed | InvalidOperationException: Card declined | at Program.Main in Program.cs:33 | ctx: feature=Checkout, orderId=17
[22:48:09] [Info] Async log message
```

![Console output of the Quick Start sample from Step 1 and Step 2](https://raw.githubusercontent.com/Chookees/ALog/master/docs/images/quickstart-console.png)

`logs/app.log` of the same run (`JsonFormatter(pretty: false)`, NDJSON):

```
{"timestamp":"2026-10-26T22:48:09.3570731Z","level":"Info","message":"Application started","properties":{},"context":{}}
{"timestamp":"2026-10-26T22:48:09.3928381Z","level":"Info","message":"User 42 logged in","messageTemplate":"User {UserId} logged in","properties":{"UserId":42},"context":{"feature":"Login","userId":42}}
{"timestamp":"2026-10-26T22:48:09.3940362Z","level":"Error","message":"Payment failed","exception":{"type":"System.InvalidOperationException","message":"Card declined","stackTrace":"   at Program.\u003CMain\u003E$(String[] args) in /_/Program.cs:line 33","source":{"method":"Program.Main","file":"Program.cs","line":33}},"properties":{},"context":{"feature":"Checkout","orderId":17}}
{"timestamp":"2026-10-26T22:48:09.4468376Z","level":"Info","message":"Async log message","properties":{},"context":{}}
```

The console output and these log lines come from a real run of the sample, unedited.

Pretty form of the template line (display only — do not write a file this way):

```json
{
  "timestamp": "2026-10-26T21:38:51.9798504Z",
  "level": "Info",
  "message": "User 42 logged in",
  "messageTemplate": "User {UserId} logged in",
  "properties": {
    "UserId": 42
  },
  "context": {
    "feature": "Login",
    "userId": 42
  }
}
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

When the queue is full, ALog drops the new event by default and counts it (SelfLog ALOG0070). To make the caller wait instead, set `QueueFullMode = Block` (in JSON: `"ALog": { "QueueFullMode": "Block" }`). Even with `Block`, a call from the queue's own writer thread or after shutdown has started is dropped and counted, so logging can never deadlock.

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

AWS SDK V4 notes: see [Migrating from 0.2.0](#aws-sdk-v4).

---

## File writer: reliability & operations

If the log file can't be reopened, ALog retries at most once per second and reports it once a minute (SelfLog ALOG0073). It keeps up to 4 MiB of lines for the file, with or without a background queue, and drops and counts the rest (SelfLog ALOG0070). Events still in the background queue are limited by the queue capacity, not by this limit.

| Option | Default | Range | Meaning |
|--------|---------|-------|---------|
| `QueueFullMode` | `Drop` | `Drop`, `Block` | Full background queue: drop and count, or make the caller wait |
| `FileBufferSize` | 64 KiB (`65536`) | 4 KiB … 16 MiB (`4096` … `16777216`) | Bytes `FileLogWriter` collects before writing |
| `FileFlushInterval` | 1 s | 0 … 60 s | How often a partial buffer is written; `0` writes every event |
| `FlushTimeout` | 5 s | 0 … 60 s | How long `Flush`, `Dispose`, and `Shutdown` wait for queued events; leftovers counted as ALOG0071 |

- On every OS, `Flush` closes the write handle and reopens the file for reading so readers can open it, which makes each call slower. That matters most on Windows, where `File.ReadAllText` needs that reopen. Don't call `Flush` after every line.
- Each log file has one writer process: give each process its own file. Other programs can read, tail, and rotate it. On Windows, `File.ReadAllText` asks for `FileShare.Read`, so it can open the file only after `Flush`, when the flush interval passes with no new lines (not with `FileFlushInterval` 0), or after dispose. To read while lines are being written, open the file with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`.
- If the process crashes, the file can miss up to one `FileFlushInterval` or one `FileBufferSize` of lines, whichever comes first. Set `FileFlushInterval` to `0` if every line must reach the operating system immediately.

### Size rotation

When `maxFileSizeInBytes` is set and the logical file length is already at least that value, ALog renames the open file to `{name}_{yyyyMMdd_HHmmss}{ext}` (UTC stamp) in the same directory and opens a new file at the original path. A same-second rename overwrites that stamped path; older stamped files are not deleted, so retention is unbounded unless you prune them externally.

---

## Diagnostics (SelfLog)

`SelfLog` reports internal failures so logging can continue without throwing into the app. By default it writes one line to stderr. `SelfLog.Enable` replaces that target; `SelfLog.Disable` stops output.

```csharp
SelfLog.Enable(line => Console.Error.WriteLine(line));
// ...
SelfLog.Disable();
```

Catalog codes (some lines include the code text; others are identified only in source comments):

| Code | Meaning |
|------|---------|
| ALOG0022 | A `Microsoft.Extensions.Logging` message could not be formatted; ALog still writes the event with the message template only (once per category and exception type, up to 256 pairs) |
| ALOG0070 | Events dropped: background queue full, or file reopen hold reached the 4 MiB / line limit (or a line could not be formatted while held) |
| ALOG0071 | Queued or held events were not written before dispose / shutdown deadline |
| ALOG0072 | First event after shutdown/dispose closed the queue or file; later closed drops are counted, not reported again |
| ALOG0073 | Log file could not be reopened; ALog retries at most once per second and reports at most once per minute |
| ALOG0105 | A queue/file option was changed after startup seal; the new value was ignored (restart to apply) |

---

<a id="limitations"></a>
## Known issues and limitations

### SQL `tableName`

- **Symptom:** Construction rejects the name.
- **When it happens:** Name is not `^[A-Za-z_][A-Za-z0-9_]*$` or longer than 128 characters; schema-qualified or quoted names.
- **Workaround:** Use a simple identifier that matches the pattern.

### Writer lifetime

- **Symptom:** Writers stop or are disposed when you did not expect it.
- **When it happens:** `AddWriter` transfers ownership to the engine.
- **Workaround:** Call `Log.Shutdown()` (or `Log.Reset()`) to dispose writers. Prefer `Shutdown` before a second `Init`.

### Injected clients and transports

- **Symptom:** Your `HttpClient` or AWS transport is still alive after ALog shuts down (or the reverse expectation).
- **When it happens:** You injected `HttpClient` into `HttpLogWriter` / `AzureLogWriter`, or `IAwsCloudWatchLogsTransport` into `AwsCloudWatchWriter`.
- **Workaround:** Dispose those injections yourself; ALog does not dispose them.

### Cloud writers fail closed

- **Symptom:** Cloud sink errors appear on stderr and in SelfLog (`[ALog] {WriterName} failed: …`); the app keeps running.
- **When it happens:** Credentials or endpoints reject the request, or (Lite) Microsoft.Data.SqlClient is missing on the first `SqlLogWriter` write / table create.
- **Workaround:** Fix credentials/endpoints or deploy the optional dependency; watch stderr / SelfLog.

<a id="observations-during-testing"></a>
### Silent loss: flush I/O failure (ENOSPC / EIO)

- **Symptom:** Buffered lines of a failed flush are gone; stderr gets one message per failed flush; those events still count as written.
- **When it happens:** After the log file opened successfully, a later flush fails (for example disk full or I/O error).
- **Workaround:** Watch free disk space and stderr; keep logs on a volume with headroom.

### Silent loss: rename with failing reopen

- **Symptom:** Further lines are lost with only stderr output and no SelfLog event.
- **When it happens:** The log file is renamed or moved while the writer still holds its write handle, and every later reopen fails (for example something else now occupies the path).
- **Workaround — before external rotation:**
  1. `await Log.FlushAsync()` (it can return early; see FlushAsync early return below).
  2. Wait one more `FileFlushInterval` (skip this wait when `FileFlushInterval` is `0`).
  3. With `FileFlushInterval` 0 the write handle stays open, so waiting does not help: keep the `FileLogWriter` instance you pass to `AddWriter` and call its `Flush()` after `await Log.FlushAsync()`, before rotating. `FileLogWriter.Flush()` alone does not drain the background queue. Or rotate with tools that leave the path writable. Watch stderr.

### Silent loss: first open fails

- **Symptom:** Each event tries one open and writes `Write failed` to stderr, with no SelfLog event; the queue counts the event as written.
- **When it happens:** The very first open fails (for example a read-only directory).
- **Workaround:** Make sure the directory is writable at startup (see [Platform-Specific Paths](#platform-specific-paths-optional)) and watch stderr.

### Silent loss: `FlushAsync` returns early

- **Symptom:** The file is missing the last event(s) even though `await Log.FlushAsync()` completed.
- **When it happens:** About 1 in 2000 calls, and up to about 1 in 3 on the first call in a fresh process.
- **Workaround:** Use `Log.Shutdown()` when you must read the file right afterwards.

---

## Platform-Specific Paths (Optional)

Built-in `IPlatformHelper` implementations resolve safe, writable log paths on **Windows**, **Linux**, and **iOS** only:

| Platform | Type | Namespace |
|----------|------|-----------|
| Windows | `WindowsPlatformHelper` | `ALog.Platform.Windows` |
| Linux | `LinuxPlatformHelper` | `ALog.Platform.Linux` |
| iOS (MAUI / Xamarin) | `IOSPlatformHelper` | `ALog.Platform.iOS` |

```csharp
using ALog.Platform.Windows; // or .Linux / .iOS

var logPath = new WindowsPlatformHelper().ResolveLogFilePath("logs/app.log");
```

> You control the log location – ALog does not enforce platform helpers. They are optional and recommended for mobile or portable environments.

---

## Writers

| Writer                | Description                                                |
|-----------------------|------------------------------------------------------------|
| `ConsoleLogWriter`    | Outputs to console with optional color and formatting      |
| `FileLogWriter`       | Outputs to file with optional rolling and formatter support|
| `HttpLogWriter`       | Sends logs to HTTP endpoints (REST APIs, webhooks)         |
| `SqlLogWriter`        | Stores logs in SQL Server database                         |
| `AzureLogWriter`      | Application Insights `/v2/track` (iKey in body)             |
| `AwsCloudWatchWriter` | CloudWatch Logs via AWSSDK (or injected transport)         |

Namespaces: `ALog.Writers.Console`, `ALog.Writers.File`, `ALog.Writers.Http`, `ALog.Writers.Sql`, `ALog.Writers.Cloud`.

Without the background queue, the file writer writes on the calling thread, so a slow disk or network share slows down the caller. Enable the background queue to decouple callers from file I/O.

If you pass your own `HttpClient` to `HttpLogWriter` or `AzureLogWriter`, its handler must support synchronous `Send`, or enable the background queue. Use `AwsCloudWatchWriter` with the background queue: the AWS SDK has no synchronous API.

---

## Formatters

| Formatter           | Description                                                |
|---------------------|------------------------------------------------------------|
| `PlainTextFormatter`| One line (customizable time). Exceptions read `Type: Message`, with inner exceptions and `at Class.Method in File.cs:42`. `includeStackTrace: true` appends the full stack. |
| `JsonFormatter`     | Structured JSON. `exception` keeps `type`, `message`, and `stackTrace`, and adds `source` (`method`, `file`, `line`) and `inner` (up to 16). Compact (`pretty: false`) is one line per event (NDJSON-friendly); `pretty: true` indents for display. |

Namespace: `ALog.Formatters`. Config-level `SetFormatter` applies only to writers that do not already have a formatter (single format pass).

<a id="console-output"></a>
### Console and plain-text line shape

ALog writes to the console only when `ALog:Console:Enabled` is `true` (or you add `ConsoleLogWriter` in code). An exception shares that line. The form is `Type: Message`, then inner exceptions, then the source frame. Types in `System.*` use the short name; every other type keeps its namespace. A thrown exception names the application frame:

```
[20:55:19] [Error] Payment failed | InvalidOperationException: Card declined | at CheckoutService.Pay in CheckoutService.cs:42 | ctx: orderId=17
```

`|` and `-->` in the message, the category, and context or property names and values are escaped as `\|` and `--\>`, line breaks as `\r` and `\n`, and a backslash is doubled only where it would otherwise read as one of these escapes (`C:\temp` stays as is), so each event stays one line. `PlainTextFormatter(includeStackTrace: true)` appends the full stack trace indented by four spaces. The default is off.

`PlainTextFormatter(includeStackTrace: true)` calls .NET's `Exception.ToString()`, and a self-referencing exception built via reflection can overflow inside that call.

---

## Contextual Logging

`Log.BeginScope` adds temporary key-value pairs removed when the scope ends (see [Quick Start Step 2](#step-2-start-logging)). Supported formatters include them in plain text and JSON.

---

## Roadmap

- [ ] Modular sink packages (see `docs/PACKAGE_SPLIT.md`)
- [ ] NuGet logo / package icon

---

## Migrating from 0.2.0

### Console output (breaking)

- Breaking (behavior): ALog writes to the console only when `ALog:Console:Enabled` is `true`. Before, `AddALog` added its own console output next to the Microsoft console provider, so every line appeared twice.

### AWS SDK V4

- Breaking (dependency): `AWSSDK.CloudWatchLogs` moves from 3.7.509.3 (AWS SDK for .NET V3, end of support since 2026-06-01) to 4.0.106 (V4). If your app references other `AWSSDK.*` packages, upgrade all of them to V4 together; otherwise restore fails with NU1107 (version conflict on `AWSSDK.Core`), or it succeeds with warning NU1608 and the app throws `MissingMethodException` at runtime.

---

## Contributing

Contributions welcome! Fork the repository and submit a PR.

```bash
dotnet build ALog.sln -c Release
dotnet test ALog.sln -c Release --no-build
dotnet format whitespace ALog.sln --verify-no-changes
```

To see why a test was skipped, run `dotnet test --logger "console;verbosity=normal"`.

For ideas like new formatters or writers, feel free to open a discussion first.

---

## License

Apache License 2.0 © Artur Bobb. See [LICENSE](https://github.com/Chookees/ALog/blob/master/LICENSE) and [NOTICE](https://github.com/Chookees/ALog/blob/master/NOTICE). Versions up to and including 0.2.0 were released under the MIT License.

---

## Maintainer

Built and maintained by **Artur Bobb / Chookees**
