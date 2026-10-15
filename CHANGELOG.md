# Changelog

## 0.2.1

- Fixed: inner scope wins on duplicate keys; scopes are isolated across parallel tasks; dispose restores the correct parent
- Fixed: logging never throws into the application; errors go to SelfLog

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
