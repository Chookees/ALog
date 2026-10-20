# ALog – Gaps and Problems

Analysis date: 2026-08-09  
Scope: solution, projects, source, CI, packaging, APIs, security, tests, .NET 8→10 readiness  
Severity levels: **extraordinary** > **high** > **medium** > **low**

---

## Extraordinary

### G-E01 — Solution does not build (CS0102)
**Potential:** Blocks all consumers, CI, packaging, and migration work.  
**Evidence:** `LoggerConfig` defines both property `UseBackgroundQueue` and method `UseBackgroundQueue(...)` (`ALog/Config/LoggerConfig.cs`). `dotnet build ALog.sln` fails with CS0102.  
**Impact:** Library is unusable from source as checked in.

---

## High

### G-H01 — No automated test project or coverage
**Potential:** Regressions invisible; README roadmap lists “Full unit test coverage” unchecked; CI `dotnet test` reports success with zero tests.  
**Evidence:** Solution contains only `ALog` + `DemoApp`; no `*Test*` projects.

### G-H02 — CI workflow mismatched and pinned to .NET 8
**Potential:** False confidence, broken PR signal after TFM change, wrong tooling for a class library.  
**Evidence:** `.github/workflows/dotnet-desktop.yml` is a WPF/desktop MSBuild template (`setup-msbuild`, signing comments) with `dotnet-version: 8.0.x`.

### G-H03 — Target frameworks still on `net8.0`
**Potential:** Blocks .NET 10 adoption; docs claim “.NET 8+” while packaging/CI stay on 8.  
**Evidence:** `ALog/ALog.csproj`, `DemoApp/DemoApp.csproj` use `<TargetFramework>net8.0</TargetFramework>`.

### G-H04 — `SqlLogWriter` uses legacy `System.Data.SqlClient` without package reference
**Potential:** Build/runtime failure after other errors clear; package is deprecated; insecure/unmaintained path on modern .NET.  
**Evidence:** `using System.Data.SqlClient` in `ALog/Writers/Sql/SqlLogWriter.cs`; no `PackageReference` in `ALog.csproj`.

### G-H05 — SQL identifier injection via table name
**Potential:** Attacker-controlled `tableName` can alter SQL (`INSERT` / `CREATE TABLE` string interpolation).  
**Evidence:** `$"INSERT INTO [{_tableName}]"` and create-table SQL in `SqlLogWriter.cs`.

### G-H06 — `AwsCloudWatchWriter` is a non-functional stub presented as a real writer
**Potential:** Production log loss; silent false sense of cloud shipping (writes to console only).  
**Evidence:** `AwsCloudWatchClient.PutLogEventInternal` TODO; console placeholder only.  
**Status (post LIM-002):** Fixed — ships via AWSSDK / `IAwsCloudWatchLogsTransport`.

### G-H07 — `AzureLogWriter` sends incorrect Application Insights payloads
**Potential:** Failed ingestion / broken telemetry while appearing to “write”.  
**Evidence:** Sets `Content-Encoding: gzip` without compressing body; uses `X-API-Key` with instrumentation key; incomplete track protocol vs App Insights expectations.  
**Status (post LIM-002):** Fixed — correct `/v2/track` MessageData with `iKey` in body.

### G-H08 — Disposable writers never disposed by engine / interface
**Potential:** `HttpClient` and other resources leak; `ILogWriter` lacks `IDisposable` while concrete writers implement `Dispose`.  
**Evidence:** `HttpLogWriter`, `AzureLogWriter`, `AwsCloudWatchWriter` have `Dispose`; `LoggerEngine.Dispose` only disposes background queue; `ILogWriter` has no `IDisposable`.

### G-H09 — `FileLogWriter` is not safe under concurrent writers
**Potential:** Corrupted logs, lost lines, failed rotation under background queue / multi-thread use.  
**Evidence:** Unsynchronized `AppendAllText` / `File.Move` in `FileLogWriter.cs`; background queue dispatches concurrently per writer batch path.

### G-H10 — Config-level formatter double-formats messages
**Potential:** Unreadable / nested-formatted logs when both `LoggerConfig.SetFormatter` and writer formatters are used.  
**Evidence:** `LoggerEngine.Format` replaces `Message` with formatter output; writers format `LogEvent` again.

---

## Medium

### G-M01 — Incomplete NuGet packaging metadata
**Potential:** Hard to publish; weak package identity.  
**Evidence:** `ALog.csproj` has Title/Authors/Description/PackageReadmeFile but missing Version, PackageId, License, RepositoryUrl, PackageLicenseExpression, etc. Roadmap: “NuGet package & logo” unchecked.

### G-M02 — Platform coverage vs README claims
**Potential:** Misleading docs for Android/macOS.  
**Evidence:** README mentions Android and (in csproj description) macOS; only Windows/Linux/iOS helpers exist.

### G-M03 — `BackgroundLogQueue.FlushAsync` is unreliable
**Potential:** Shutdown log loss.  
**Evidence:** Polls `Reader.Count` then fixed `Delay(50)` without coordinating with processor completion.

### G-M04 — HTTP-based writers dispose injected `HttpClient`
**Potential:** Breaks shared `HttpClient` lifetime (sockets / DI).  
**Evidence:** `HttpLogWriter` / `AzureLogWriter` dispose `_httpClient` unconditionally.

### G-M05 — `AsyncEnabled` configuration is unused
**Potential:** Dead API surface; callers believe sync/async is toggled.  
**Evidence:** `LoggerConfig.AsyncEnabled` / `UseAsync` never read by `LoggerEngine`.

### G-M06 — Static `Log` facade is hard to reset/test
**Potential:** Cross-test contamination; no public shutdown/replace.  
**Evidence:** `Log` holds private static `_logger`; no `Reset`/`Shutdown`; `FlushAsync` only.

### G-M07 — Duplicate type names via façade wrappers
**Potential:** Ambiguous references (`ALog.ConsoleLogWriter` vs `ALog.Writers.Console.ConsoleLogWriter`) and maintenance cost.  
**Evidence:** `ALog/Exports/LogExports.cs` subclasses writers/formatters into root namespace.

### G-M08 — Empty `WriterFactory` / incomplete internal seams
**Potential:** Dead code; unfinished extension point.  
**Evidence:** `ALog/Internal/WriterFactory.cs` is TBD-only.

---

## Low

### G-L01 — README clone URL is a placeholder (`yourusername`)
### G-L02 — Author / maintainer name inconsistency (resolved)
Resolved in the Apache 2.0 PR: csproj, nuspec, NOTICE and README all name Artur Bobb.
### G-L03 — Typos in comments/demo (`Fassade`, `successfull`, `Testerror`, gitignore `secrects`)
### G-L04 — `MinimumLevelFilter` overlaps `SetMinimumLevel` with little documentation of composition
### G-L05 — No `global.json` to pin SDK for contributors
### G-L06 — DemoApp interactive `ReadKey` hinders non-interactive runs/CI smoke

---

## Severity counts

| Severity | Count |
|---|---|
| Extraordinary | 1 |
| High | 10 |
| Medium | 8 |
| Low | 6 |
| **Total** | **25** |

## Fix policy (this run)

- **Must fix:** extraordinary, high, medium (`G-E*`, `G-H*`, `G-M*`)
- **Document only:** low (`G-L*`), unless required for .NET 10 migration or trivially coupled to a higher fix
- **Migration:** all projects and CI → .NET 10 (`net10.0`)
