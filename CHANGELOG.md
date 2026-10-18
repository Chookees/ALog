# Changelog

## 0.2.1

- Breaking (dependency): `Microsoft.Data.SqlClient` moves from 6.0.2 to 7.1.1. If your app references `Microsoft.Data.SqlClient` directly, upgrade it to 7.x as well.
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
