# ALog.Comparison

Comparison harness for ALog, NLog, Serilog and log4net.
It writes the same events with every library into a file, measures each cell in a fresh process, checks that
every event reached the file, and stores the raw values. It does not decide winners; that evaluation is separate.

The project is not packable and is not part of the ALog packages. Only this project references NLog, Serilog
and log4net.

## Quick start

```bash
# from bench/ALog.Comparison in the ALog repo, one run at a time per machine
./run.sh 5 <your-name>                 # 5 process runs + 1 BenchmarkDotNet run -> results/<your-name>-<UTC>/
./merge.sh                             # all results/*/raw.csv -> results/merged.csv + results/summary.csv

# smoke (tiny N, no BenchmarkDotNet, about 10 s)
BENCH_N_SCALE=0.001 BENCH_RESULTS=results/_smoke ./run.sh 1 smoke
# CI smoke (N = 1000 for every scenario)
BENCH_N=1000 BENCH_RESULTS=results/_smoke ./run.sh 1 ci
```

Outside the repo, point the build at an ALog checkout: `AlogSrc=/path/to/ALog ./run.sh 1 smoke`
(or `dotnet build -p:AlogSrc=/path/to/ALog`). Default: `../../ALog/ALog.csproj`.

Requirements: Linux, bash, `flock` (util-linux), git, Python 3 (merge only), the .NET SDK from `global.json`.

## Files

| File | Purpose |
|---|---|
| `ALog.Comparison.csproj` | net10.0 exe, `IsPackable=false`, pinned package versions, ALog as ProjectReference (`$(AlogSrc)`) |
| `Scenarios.cs` | T1 to T7: counts, messages, values, category, 3-level exception chain |
| `Adapters.cs` | one adapter per library, configuration with documentation links |
| `Cell.cs` | one cell in one process: warm-up, timed run incl. flush + dispose, metrics, check |
| `Verify.cs` | file check: exact event count, fields in the first, a middle and the last event |
| `Runner.cs` | process runner: shuffled order, child processes, `raw.csv`, `details.jsonl`, `env.json` |
| `Micro.cs` | BenchmarkDotNet benchmarks |
| `Program.cs` | command line: `run`, `cell`, `micro`, `versions` |
| `run.sh` | build, lock, process runs, BenchmarkDotNet run |
| `merge.sh`, `merge.py` | merge all result directories, per-agent and overall statistics |

## Scenarios

All scenarios write to one file, Release build, .NET 10, category `Bench`.

| Id | N | What one call writes |
|---|---|---|
| T1 | 1,000,000 | Info `Application event processed` |
| T2 | 1,000,000 | Info `User {UserId} logged in from {Ip}` with (index, `192.168.1.10`); needs named properties |
| T7 | 1,000,000 | T2 with the library's JSON formatter |
| T3 | 100,000 | Error `Payment failed` with a thrown and caught 3-level chain (`InvalidOperationException` → `ArgumentException` → `IOException`); the throw happens inside the timed loop for every library |
| T4 | 8 × 125,000 | T1 from 8 threads started together |
| T5 | 10,000,000 | Info calls below the minimum level Warn, then one Warn sentinel so an empty file cannot pass |
| T6 | 1 | cold process: init + first event + flush + dispose (init is timed only here) |

Warm-up: 10,000 calls of the same shape before the stopwatch starts (T3: 10,000 throws), then a
drain so the queue is empty. T6 has no warm-up. The warm-up events stay in the file and are counted.

`BENCH_N_SCALE` multiplies every N and the warm-up; `BENCH_N` fixes N for every scenario except T6
(warm-up = min(N, 10,000)).

## Library configuration

Same fields in every line: timestamp ISO-8601 UTC with milliseconds, level, category, message, values or the
exception in the library's standard format (see the decisions below), `\n` line endings, UTF-8. Every run uses a
fresh directory inside the result directory (same local disk), deleted after each cell. No rotation. Queue
capacity 10,000 for every library with a queue (`Libs.QueueCapacity`). NLog and Serilog block when full; ALog has
no Block mode yet, so a drop makes the ALog cell invalid. Flush and dispose are inside the timed span.

| Mode | Configuration | Sources |
|---|---|---|
| ALog `async` | `LoggerEngine` + `FileLogWriter`, `UseBackgroundQueue(capacity 10000, batch 10, 100 ms)`, full mode: drop (ALog has no Block mode in this version); `PlainTextFormatter("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")` (standard exception format) / `JsonFormatter` | README "Step 3: Background Queue" https://github.com/Chookees/ALog#step-3-background-queue-optional |
| ALog `sync` | the same without the queue (writes on the caller thread); extra row, **not ranked** | same |
| NLog `async` | `FileTarget(KeepFileOpen=true, AutoFlush=false)` in `AsyncTargetWrapper(QueueLimit=10000, OverflowAction=Block)`; `${exception}` with its default format; `JsonLayout` for T7 | https://github.com/NLog/NLog/wiki/Exception-Layout-Renderer, https://github.com/NLog/NLog/wiki/Performance ("File Logging Performance"), https://github.com/NLog/NLog/wiki/AsyncWrapper-target, https://github.com/NLog/NLog/wiki/File-target, https://github.com/NLog/NLog/wiki/JsonLayout |
| Serilog `async` | `WriteTo.Async(bufferSize: 10000, blockWhenFull: true)` → `File(buffered: true)`, `{NewLine}{Exception}` as in the default output template; `Serilog.Formatting.Json.JsonFormatter(renderMessage: true)` for T7 | https://github.com/serilog/serilog-sinks-async/blob/dev/README.md, https://github.com/serilog/serilog-sinks-file/blob/dev/README.md#performance, https://github.com/serilog/serilog/wiki/Formatting-Output |
| log4net `buffered` | `BufferingForwardingAppender(lossy=false, Fix=Partial, bufferSize 512 default)` → `FileAppender(ImmediateFlush=false, ExclusiveLock)`, standard `%exception`; flush + repository shutdown at the end | https://logging.apache.org/log4net/manual/configuration/appenders/bufferingforwardingappender.html, https://logging.apache.org/log4net/manual/configuration/appenders/fileappender.html, `ImmediateFlush`: https://github.com/apache/logging-log4net/blob/master/src/log4net/Appender/TextWriterAppender.cs |

Deviations from defaults, recorded in `details.jsonl` (`notes`) and `env.json` (`libraries`):

- **ALog category.** The static `Log` class has no category. The adapter uses the public
  `LoggerEngine.LogStructured(category, …)` for every call so the line carries `[Bench]` like the others.
- **ALog plain line.** For T2 the plain formatter also appends `| props: UserId=…, Ip=…`. This is ALog's format.
- **Serilog file sink.** The default `buffered: false` flushes every event; set to `true` so it does not.
- **NLog file target.** `AutoFlush=false` so the target does not flush per batch.
- **log4net `Fix=Partial`.** The default `All` captures location info per event; the log4net page recommends `Partial`.
- **Serilog JSON timestamp.** `JsonFormatter` writes the local offset; `run.sh` and the runner set `TZ=UTC`,
  so the value ends in `+00:00`.

## Fairness decisions (2026-10-22)

1. **Same capacity for every library: 10,000, blocking.** NLog `AsyncTargetWrapper(QueueLimit=10000,
   OverflowAction=Block)`, Serilog `WriteTo.Async(bufferSize: 10000, blockWhenFull: true)`, ALog
   `UseBackgroundQueue(capacity 10000)`, all from the one constant `Libs.QueueCapacity`. log4net's
   `BufferingForwardingAppender` stays as it is (documented default `bufferSize` 512, lossy=false): it is a
   synchronous batch buffer on the caller thread, not a queue, so a capacity does not apply. ALog has no Block mode
   yet: a full queue drops the event (ALOG0070), and a cell with a drop fails the event count and is **invalid. That
   is intended** until a Block mode is added. The single switch for that is `AlogLib.FullMode` in `Adapters.cs`
   plus the marked line in `AlogLib.Init`: when that mode exists, set its Block option there (with the API name it
   introduces) and update `FullMode`. No such option exists in ALog 0.2.1, and the harness does not invent one.
2. **Flush retry stays.** ALog's `FlushAsync`/`Dispose` wait at most 5 s; `Close` repeats `FlushAsync` until one
   call returns before that deadline, then disposes. Obsolete once a Block mode exists; remove it together with that change.
3. **ALog `FileLogWriter` opens and closes the file per event** (`File.AppendAllText`). Nothing to change in this version.
4. **No per-event flush for the competitors** (Serilog `buffered: true`, NLog `AutoFlush=false`, log4net
   `ImmediateFlush=false`) is OK. The same applies to ALog once it has a Block mode.
5. **T3: every library writes its standard exception format, as users get it.** ALog: `PlainTextFormatter`
   default (EXF one-line form with the inner chain and the source frame, no forced stack). NLog: `${exception}`
   without format (default `ToString,Data`). Serilog: `{Exception}` as in the default output template
   (`ToString()`). log4net: `%exception` (`ToString()`). The check requires all three exception types of the chain
   in the record; stack frames are not required.
6. **log4net T2 = n/a.** log4net has no message templates or named properties; `ILog.InfoFormat` formats with
   `String.Format` only. T7 stays n/a.
7. **ALog sync stays as an extra row, not ranked.** `details.jsonl` and `env.json` carry `ranked: false`;
   `merge.py` writes `ranked=no` and leaves it out of `rank_total_ms`. The evaluation uses only
   `ranked=yes` rows. `raw.csv` keeps the fixed columns; the row is identified by `framework=ALog,mode=sync`.
8. **Results location** stays `results/<agent>-<UTC>/` inside this directory (`BENCH_RESULTS` overrides).
9. **BenchmarkDotNet** keeps fixed invocation counts per class (needed for `IterationSetup`).
10. **Serilog JSON timestamps** end in `+00:00` (TZ=UTC) instead of `Z`.

## n/a cells

| Cell | Reason |
|---|---|
| log4net T2 | No message templates or named properties. `ILog.InfoFormat` formats with `String.Format` only (https://github.com/apache/logging-log4net/blob/master/src/log4net/ILog.cs, checked 2026-10-22, log4net 3.5.0). Decided 2026-10-22. |
| log4net T7 | No built-in JSON layout. The log4net layout page lists `JsonLayout` only "via the external project log4net.Ext.Json" (https://logging.apache.org/log4net/manual/configuration/layouts.html, checked 2026-10-22, log4net 3.5.0). No third-party substitutes. |

n/a cells appear in `raw.csv` with `valid=n/a` and empty metrics.

## Measurement

`run.sh` starts one child process per cell. The child initialises the library (not timed, except T6), runs the
warm-up, drains, forces a GC, then times the N calls plus flush and dispose with a `Stopwatch`.

| Value | Source |
|---|---|
| `total_ms` | first measured call until flush + dispose returned |
| `cpu_ms` | `Process.TotalProcessorTime` delta (user + sys, all threads) |
| `cpu_pct` | `cpu_ms / total_ms × 100`; can exceed 100 on several cores |
| `peak_ws_mb` | `Process.PeakWorkingSet64` (whole process lifetime); `VmHWM` from `/proc/self/status` is in `details.jsonl` |
| `alloc_bytes_per_op` | `GC.GetTotalAllocatedBytes(true)` delta / N (all threads) |
| `mean_ns_per_op` | `total_ms × 10⁶ / N` |
| `events_per_s` | `N / total_s` |
| `valid` | `true`, `false` (check failed, crash or hang guard) or `n/a` |

After the timed span the child checks the file: the number of records must equal N + warm-up (T5: 1),
every record must contain the scenario's message, and the first, a middle and the last record must contain the
timestamp, level, category, message, values (T2/T7) or all three exception types of the chain (T3). Any
miss sets `valid=false`; the reason is in `details.jsonl`. `BENCH_MUTANT_DROP=1` skips one event to prove that
a lost event makes the cell invalid.

The order of the libraries is shuffled per run and scenario with a seed (`BENCH_SEED`, otherwise random). The
seed is in `env.json` and every `details.jsonl` line. A cell that runs longer than the hang guard
(`BENCH_CELL_TIMEOUT_S`, default 1800 s, 30 s for smoke sizes) is killed and recorded as invalid.

## Result directory

`results/<agent>-<yyyyMMddTHHmmssZ>/`:

- `raw.csv`: fixed columns
  `runner,run,framework,version,scenario,mode,n,total_ms,cpu_ms,cpu_pct,peak_ws_mb,alloc_bytes_per_op,mean_ns_per_op,events_per_s,valid`
- `details.jsonl`: one JSON object per cell with every value above plus records, file bytes, write time,
  GC counts, VmHWM, order, seed, configuration notes and the reason for `valid=false`/`n/a`
- `env.json`: machine, CPU model, logical cores, cgroup CPU limit, RAM, OS, kernel, .NET SDK and runtime, GC mode,
  load average at start and end, file system type, ALog SHA and dirty flag, harness SHA, versions of all packages,
  seed, scale, `DOTNET_*`/`BENCH_*` variables, scenario and library tables
- `run.log`: build and console output
- `bdn/`: BenchmarkDotNet reports (full-size runs only)

## BenchmarkDotNet

`run.sh` runs it once after the process runs when N is not scaled (`BENCH_BDN=1` forces it, `0` skips it). By
hand: `dotnet bin/Release/net10.0/ALog.Comparison.dll micro --filter '*'` from this directory.

One benchmark class per scenario (T1, T2, T3, T4, T5, T7), parameter `Lib` over all supporting libraries, default
job, `MemoryDiagnoser` and `ThreadingDiagnoser`. Init runs in `IterationSetup`, flush and dispose in
`IterationCleanup`; only the write is measured. `IterationSetup` requires a fixed invocation count, so each class
sets `[InvocationCount]` (T1/T2/T7 10,000, T3 5,000, T4 1 × 8 threads × 1,250 events, T5 1,000,000). No
iteration writes more than 10,000 events, so ALog's queue (capacity 10,000, no Block mode in this version) cannot drop
inside an iteration. T3 logs a pre-thrown chain, T4 runs 8 parallel writers per invocation and reports per event.

## Lock

`run.sh` takes `flock` on `/tmp/alog-comparison-bench.lock` (`BENCH_LOCK`) for the whole run and fails at once
with the current holder if another run is active. Runs on one machine always run one after another.

## Merge

`./merge.sh [results-dir] [--out dir] [--include-underscore]` reads every `*/raw.csv` (directories starting with
`_`, such as `_smoke`, only with `--include-underscore`) and writes:

- `merged.csv`: all rows plus `source` (the run directory)
- `summary.csv`: valid rows only; `scope=agent` per runner × framework × mode × scenario and `scope=overall`
  per framework × mode × scenario; count, mean, sample stddev, min and max of every metric; invalid and n/a
  rows are counted, not averaged; `ranked` (ALog sync = `no`) and `rank_total_ms` (1 = lowest mean total time
  among the ranked rows with valid values of the same scope, runner and scenario)

## Licenses and names

NLog (BSD-3-Clause), Serilog, Serilog.Sinks.File, Serilog.Sinks.Async and log4net (Apache-2.0) are referenced
only by this benchmark project and are not redistributed. BenchmarkDotNet (MIT) is a build-time and run-time
dependency of this project only. Product names are used only to identify the libraries.
