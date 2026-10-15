# Changelog

## 0.2.1

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
