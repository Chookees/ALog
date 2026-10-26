# Changelog

## Unreleased

- Fixed: when a log file ends without a line ending (for example after a crash), the next writer starts a new line instead of appending to the leftover fragment.
- Changed: logging through the background queue is faster. Adding an event usually no longer takes a lock.
- Changed: each logger with a background queue now uses one dedicated thread.
- Changed: FileLogWriter writes simple events from PlainTextFormatter and JsonFormatter through a faster path. The written text stays the same.
- Added: on Windows, `File.ReadAllText` can read the log file while the logger is running: after `Flush`, when the flush interval passes with no new lines (not with `FileFlushInterval` 0), and after dispose. While lines are being written, the write handle stays open, also with `FileFlushInterval` 0. To read the file at any time, open it with `FileShare.ReadWrite`.
- Changed: message templates are cached, up to 1024 entries.

## 0.2.1

- Breaking (dependency): `Microsoft.Data.SqlClient` moves from 6.0.2 to 7.1.1. If your app references `Microsoft.Data.SqlClient` directly, upgrade it to 7.x as well.
- Changed: ALog is now licensed under the Apache License 2.0 instead of MIT. Versions up to and including 0.2.0 stay under the MIT License.
- Fixed: inner scope wins on duplicate keys; scopes are isolated across parallel tasks; dispose restores the correct parent
- Fixed: logging never throws into the application; errors go to SelfLog
- Fixed: a throwing filter drops the event and writes one SelfLog line, so a broken filter cannot leak the event. A throwing enricher is still skipped and the event is logged
- Fixed: scopes that are not key/value pairs (for example `BeginScope("Batch 7")`) are kept in a `Scope` context array, outer to inner. Key/value scopes still become properties and the inner value wins
- Fixed: every log path, including the Microsoft.Extensions.Logging `Log` and `IsEnabled` methods, returns before any work when the level is below the cached minimum. That minimum is the lowest level any current rule allows, including a category override below the global level, and is refreshed at init and when filter rules reload
- Added: `SelfLog.Enable(Action<string>)` and `SelfLog.Disable()`
- Added: `LogContext.Push(IEnumerable<KeyValuePair<string, object?>>)` and `LogContext.PushScope(object)`, both returning `IDisposable`
- Changed: the Microsoft.Extensions.Logging bridge uses the public `LogContext` API. `ALog.Extensions.Logging` depends on ALog `>= 0.2.1` (not an exact pin). Direct `LogContext` scopes are attached when they begin
- Fixed: `ALogLoggerProvider` implements `ISupportExternalScope` again, so the patch does not drop that interface. Factory scopes, including Activity TraceId, SpanId, and ParentId, are copied onto the event at emit time, after the level gate. `LogContext` scopes come first; external scopes follow
- Changed: Scopes from `ILogger.BeginScope` apply to MEL events; for `Log.*` use `LogContext.Push`
- Changed: `GetContext()` returns one shared empty dictionary when there is no scope. Each scope node stores its depth so the depth limit is a single comparison
- Fixed: ALog references `System.Security.Cryptography.Pkcs` 10.0.13 directly, so apps no longer get the vulnerable version that `Microsoft.Data.SqlClient` pulls in transitively (NU1902/NU1903 with `TreatWarningsAsErrors`).
- Fixed: `AzureLogWriter` and `HttpLogWriter` no longer deadlock when they log synchronously on a thread with a `SynchronizationContext` (UI thread, classic ASP.NET); the synchronous path uses `HttpClient.Send`.
- Fixed: ALog no longer resumes on the caller's `SynchronizationContext`, and `SqlLogWriter` has a truly synchronous path, so no synchronous write blocks on a task.
- Breaking (behavior): without the background queue, `FileLogWriter` writes synchronously on the calling thread (no `Task.Run`); `WriteAsync` completes inline.
- Breaking (behavior): synchronous writes through `AzureLogWriter` and `HttpLogWriter` use `HttpClient.Send`. If you pass an `HttpClient` whose handler does not support synchronous `Send`, those writes fail like any writer error; enable the background queue for such handlers.
- Breaking (behavior): ALog writes to the console only when `ALog:Console:Enabled` is `true`. Before, `AddALog` added its own console output next to the Microsoft console provider, so every line appeared twice.
- Fixed: `AddALog` matches message-template values by name, not by position, so ASP.NET Core and EF Core messages render correctly and keep the right property names.
- Fixed: `AddALog` no longer throws into the application when a log message cannot be formatted. ALog writes the event with its message template and reports ALOG0022.
- Fixed: `AddALog` stores `{@Order}` and `{$Order}` as the property `Order`, and keeps the property values when a message has no `{OriginalFormat}`.
- Fixed: with the background queue, a full queue no longer blocks the caller for 5 seconds and then drops events silently, and shutdown no longer loses queued events silently or writes to a disposed writer. Dropped and unwritten events are counted and reported (ALOG0070, ALOG0071).
- Breaking (behavior): a single value or a list where ALog expects a section (for example `"ALog": { "Console": true }`) now stops startup with an error that names the path. Before, ALog ignored it and wrote nothing.
- Fixed: `ConsoleLogWriter` and `FileLogWriter` write the line even when the `ToString()` of a property or scope value throws. That value shows `[capture failed: ExceptionType]`.
- Breaking (dependency): `AWSSDK.CloudWatchLogs` moves from 3.7.509.3 (AWS SDK for .NET V3, end of support since 2026-06-01) to 4.0.106 (V4). If your app references other `AWSSDK.*` packages, upgrade all of them to V4 together; otherwise restore fails with NU1107 (version conflict on `AWSSDK.Core`), or it succeeds with warning NU1608 and the app throws `MissingMethodException` at runtime.
- Fixed: `AwsCloudWatchWriter` now sends the message-template properties and the context with each event, not only the rendered message.

### Breaking (output)

- Plain text, console, and file output write exceptions as "Type: Message", followed by inner exceptions and the source line ("at Class.Method in File.cs:42"). The "EX:" label is gone; update log parsers that match it. `|` and `-->` in the message, the category, and context or property names and values are escaped as `\|` and `--\>`, line breaks as `\r` and `\n`, and a backslash is doubled only where it would otherwise read as one of these escapes (`C:\temp` stays as is), so each event stays one line.
- Escaping also covers the event message, the category, and context and property names, not only exception messages. An exception message longer than 500 characters is cut before escaping, and then ` [truncated]` is appended. Inner exceptions and aggregate entries follow the order of `AggregateException.Flatten()`; when the list stops at its limit, the suffix is `(+N+ more)`, and `(+N more)` means every entry was counted.
- Plain text shows exception type names in C# syntax (`Outer.NestedEx`, `GenEx<Int32>`) without assembly details, and lists only the entries of an aggregate, not their inner exceptions. JSON and HTTP keep `Type.FullName` in `type` and include each entry's inner exceptions.

### Added

- JSON and HTTP exception output add "source" (method, file, line) and "inner" (type, message, and source of each inner exception, up to 16).
- PlainTextFormatter(includeStackTrace: true) appends the full stack trace.
- `QueueFullMode`: `Drop` (default, as before) or `Block`. With `Block`, a full background queue makes the caller wait instead of dropping the event.
- `FileBufferSize` and `FileFlushInterval` control how `FileLogWriter` buffers lines.
- `FlushTimeout` sets how long `Flush`, `Dispose`, and `Shutdown` wait (0 to 60 s, default 5 s; previously fixed at 5 s).

### Changed

- `FileLogWriter` keeps the file open and writes buffered lines instead of opening and closing it for every event. By default, lines reach the file within 1 second; set `FileFlushInterval` to `0` for the previous per-event behavior.
- A log file must have a single writer process; give each process its own file. On Windows a second writer gets an error; on Linux and macOS it is not detected and lines from both processes can mix. Reading, tailing, and rotating the file from outside still work (rename or copy-and-truncate); after a rename or delete, ALog starts the new file within one second, or within `FileFlushInterval` if that is longer. Lines written to a deleted file before then are lost.

## 0.2.0

- Message templates (`Log.Write(LogLevel, template, args...)`) and `LogEvent.Properties`
- Enrichers: MachineName, ThreadId, ProcessId, Activity, PropertyEnricher
- `ALogOptions` + `LoggerConfigFactory` for JSON/env-friendly setup
- New package `ALog.Extensions.Logging` (MEL provider + configuration binding)
- Sample: `samples/ALog.AspNetSample`
- Benchmarks: `ALog.Benchmarks`
- Broader tests; disable parallelization for static `Log` façade safety
- Package split plan documented in `docs/PACKAGE_SPLIT.md`

## 0.1.0

- Initial .NET 10 library with writers, queue, tests, gap fixes
