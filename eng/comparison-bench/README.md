# ALog.Comparison

Comparison harness for ALog, NLog, Serilog and log4net.
It writes the same events with every library into a file, measures each cell in a fresh process, checks that
every event reached the file, and stores the raw values. It does not decide winners; that evaluation is separate.

The project is not packable and is not part of the ALog packages. Only this project references NLog, Serilog
and log4net.

The ranked speed tests are `tests/RankedSpeedTests.cs`. They run only when `ALOG_SPEED=1` and are not part of required CI.
Each scenario uses the median of 3 runs. ALog async may be up to 5% slower than the fastest valid competitor; the output states the reason.
Fails if ALog async is slower than the fastest valid competitor in any scenario, or if a competitor's output is invalid.

## Quick start

```bash
# from eng/comparison-bench in the ALog repo, one run at a time per machine
./run.sh 5 <your-name>                 # 5 process runs + 1 BenchmarkDotNet run -> eng/comparison-bench/results/<your-name>-<UTC>/
./merge.sh                             # all results/*/raw.csv -> eng/comparison-bench/results/merged-<UTC date>/merged.csv + summary.csv

# smoke (tiny N, no BenchmarkDotNet, about 10 s). BENCH_N_SCALE does not change the results directory.
BENCH_N_SCALE=0.001 BENCH_RESULTS=results/_smoke ./run.sh 1 smoke
# CI smoke (N = 1000 for every scenario). BENCH_N with no BENCH_RESULTS writes under eng/comparison-bench/results/_smoke/.
BENCH_N=1000 ./run.sh 1 ci
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
| `Verify.cs` | file check: every record is UTC and in order, exact event count, fields in three samples |
| `HarnessChecks.cs` | `verify-self` and the Serilog time-zone matrix |
| `Runner.cs` | process runner: shuffled order, child processes, `raw.csv`, `details.jsonl`, `env.json` |
| `Micro.cs` | BenchmarkDotNet benchmarks |
| `Program.cs` | command line: `run`, `cell`, `micro`, `versions`, `verify-self`, `tz-matrix` |
| `tests/` | ranked speed tests (`ALOG_SPEED=1` only) and the harness self-checks |
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
capacity 10,000 for every library with a queue (`Libs.QueueCapacity`). NLog, Serilog and ALog wait when the
queue is full (`QueueFullMode.Block` for ALog). A lost event still makes the cell invalid. Flush and dispose
are inside the timed span.

| Mode | Configuration | Sources |
|---|---|---|
| ALog `async` | `LoggerEngine` + `FileLogWriter` (file kept open, 64 KiB buffer, flush every 1 s), `UseBackgroundQueue(capacity 10000, batch 10, 100 ms)`, `QueueFullMode.Block`, `FlushTimeout` 5 s; Close flushes once, then disposes; `PlainTextFormatter("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")` (standard exception format) / `JsonFormatter` | README "Step 3: Background Queue" https://github.com/Chookees/ALog#step-3-background-queue-optional |
| ALog `sync` | the same writer without the queue (writes on the caller thread); extra row, **not ranked** | same |
| NLog `async` | `FileTarget(KeepFileOpen=true, AutoFlush=false)` in `AsyncTargetWrapper(QueueLimit=10000, OverflowAction=Block)`; `${exception}` with its default format; `JsonLayout` for T7 | https://github.com/NLog/NLog/wiki/Exception-Layout-Renderer, https://github.com/NLog/NLog/wiki/Performance ("File Logging Performance"), https://github.com/NLog/NLog/wiki/AsyncWrapper-target, https://github.com/NLog/NLog/wiki/File-target, https://github.com/NLog/NLog/wiki/JsonLayout |
| Serilog `async` | `WriteTo.Async(bufferSize: 10000, blockWhenFull: true)` → `File(buffered: true)`, `ExpressionTemplate` with `UtcDateTime(@t)` for plain and JSON (Serilog.Expressions, bench project only) | https://github.com/serilog/serilog-sinks-async/blob/dev/README.md, https://github.com/serilog/serilog-sinks-file/blob/dev/README.md#performance, https://github.com/serilog/serilog/wiki/Formatting-Output |
| log4net `buffered` | `BufferingForwardingAppender(lossy=false, Fix=Partial, bufferSize 512 default)` → `FileAppender(ImmediateFlush=false, ExclusiveLock)`, standard `%exception`; flush + repository shutdown at the end | https://logging.apache.org/log4net/manual/configuration/appenders/bufferingforwardingappender.html, https://logging.apache.org/log4net/manual/configuration/appenders/fileappender.html, `ImmediateFlush`: https://github.com/apache/logging-log4net/blob/master/src/log4net/Appender/TextWriterAppender.cs |

Deviations from defaults, recorded in `details.jsonl` (`notes`) and `env.json` (`libraries`):

- **ALog category.** The static `Log` class has no category. The adapter uses the public
  `LoggerEngine.LogStructured(category, …)` for every call so the line carries `[Bench]` like the others.
- **ALog plain line.** For T2 the plain formatter also appends `| props: UserId=…, Ip=…`. This is ALog's format.
- **Serilog file sink.** The default `buffered: false` flushes every event; set to `true` so it does not.
- **NLog file target.** `AutoFlush=false` so the target does not flush per batch.
- **log4net `Fix=Partial`.** The default `All` captures location info per event; the log4net page recommends `Partial`.
- **Serilog timestamp.** Plain and JSON use `ExpressionTemplate` and `UtcDateTime(@t)` from Serilog.Expressions,
  so the stamp is UTC (`Z`) in any time zone. That package is a dependency of this bench project only.

## Fairness decisions (2026-10-22)

1. **Same capacity for every library: 10,000, and a full queue waits.** NLog `AsyncTargetWrapper(QueueLimit=10000,
   OverflowAction=Block)`, Serilog `WriteTo.Async(bufferSize: 10000, blockWhenFull: true)`, ALog
   `UseBackgroundQueue(capacity 10000)` with `QueueFullMode.Block`, all from the one constant `Libs.QueueCapacity`.
   log4net's `BufferingForwardingAppender` stays as it is (documented default `bufferSize` 512, lossy=false): it is a
   synchronous batch buffer on the caller thread, not a queue, so a capacity does not apply. A full ALog queue waits.
   A lost event still makes the cell invalid.
2. **Close flushes once.** ALog `Close` calls `FlushAsync` once, then `Dispose`. Both use `FlushTimeout` (default 5 s).
3. **ALog `FileLogWriter` keeps the file open.** The buffer is 64 KiB and the file is flushed at least every 1 s
   (an interval of 0 would write every event; this harness keeps 1 s).
4. **No per-event flush for the competitors** (Serilog `buffered: true`, NLog `AutoFlush=false`, log4net
   `ImmediateFlush=false`) is OK. ALog keeps the file open and flushes on the 64 KiB buffer, on the 1 s interval,
   and on flush, rotation, and dispose.
5. **T3: every library writes its standard exception format, as users get it.** ALog: `PlainTextFormatter`
   default (one-line form with the inner chain and the source frame, no forced stack). NLog: `${exception}`
   without format (default `ToString,Data`). Serilog: `{@x}` in the `ExpressionTemplate` (`ToString()`), the
   same text `{Exception}` writes. log4net: `%exception` (`ToString()`). The check requires all three exception types of the chain
   in the record; stack frames are not required.
6. **log4net T2 = n/a.** log4net has no message templates or named properties; `ILog.InfoFormat` formats with
   `String.Format` only. T7 stays n/a.
7. **ALog sync stays as an extra row, not ranked.** `details.jsonl` and `env.json` carry `ranked: false`;
   `merge.py` writes `ranked=no` and leaves it out of `rank_total_ms`. The evaluation uses only
   `ranked=yes` rows. `raw.csv` keeps the fixed columns; the row is identified by `framework=ALog,mode=sync`.
8. **Results location.** Raw runs stay `eng/comparison-bench/results/<agent>-<UTC>/` (`BENCH_RESULTS` overrides).
   A run started with `BENCH_N` and no `BENCH_RESULTS` writes under `eng/comparison-bench/results/_smoke/` (git-ignored).
   `./merge.sh` writes `eng/comparison-bench/results/merged-<YYYY-MM-DD>/` (`merged.csv` and `summary.csv`, UTC date).
   `--out` overrides that directory.
9. **BenchmarkDotNet** keeps fixed invocation counts per class (needed for `IterationSetup`).
10. **Serilog timestamps** are UTC (`Z`) via `UtcDateTime(@t)`, including when the process time zone is not UTC.

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
| `valid` | `true`, `false` (check failed, crash, hang guard, or the ALog async queue mode in `env.json` is not Block) or `n/a` |

After the timed span the child checks the file: the number of records must equal N + warm-up (T5: 1),
every record must contain the scenario's message, and the first, a middle and the last record must contain the
timestamp, level, category, message, values (T2/T7) or all three exception types of the chain (T3). Any
miss sets `valid=false`; the reason is in `details.jsonl`. `BENCH_MUTANT_DROP=1` skips one event to prove that
a lost event makes the cell invalid. `BENCH_MUTANT_FULL_MODE=Drop` builds the ALog queue with Drop.
`env.json` records the mode read back from that configuration (`QueueFullMode=Drop`), not the environment variable.
Every ALog async cell of that run is then invalid, and `run.sh` exits with
status 1. Unset, or `Block`, records `QueueFullMode=Block`. Any other value is stored as Drop, so the record is
`QueueFullMode=Drop` and the run is invalid. The reason names the mode from `env.json`, or `missing` when that record is absent.

The order of the libraries is shuffled per run and scenario with a seed (`BENCH_SEED`, otherwise random). The
seed is in `env.json` and every `details.jsonl` line. A cell that runs longer than the hang guard
(`BENCH_CELL_TIMEOUT_S`, default 1800 s, 30 s for smoke sizes) is killed and recorded as invalid.

## Result directory

`eng/comparison-bench/results/<agent>-<yyyyMMddTHHmmssZ>/`:

- `raw.csv`: fixed columns
  `runner,run,framework,version,scenario,mode,n,total_ms,cpu_ms,cpu_pct,peak_ws_mb,alloc_bytes_per_op,mean_ns_per_op,events_per_s,valid`
- `details.jsonl`: one JSON object per cell with every value above plus records, file bytes, write time,
  GC counts, VmHWM, order, seed, configuration notes and the reason for `valid=false`/`n/a`
- `env.json`: machine, CPU model, logical cores, cgroup CPU limit, RAM, OS, kernel, .NET SDK and runtime, GC mode,
  load average at start and end, file system type, ALog SHA and dirty flag, harness SHA, versions of all packages,
  seed, scale, `DOTNET_*`/`BENCH_*` variables, scenario and library tables. The ALog async library row records
  `queue_full_mode` as `QueueFullMode=Block`. The ALog sync row records `n/a (no queue)`.
- `run.log`: build and console output
- `bdn/`: BenchmarkDotNet reports (full-size runs only)

## BenchmarkDotNet

`run.sh` runs it once after the process runs when N is not scaled (`BENCH_BDN=1` forces it, `0` skips it). By
hand: `dotnet bin/Release/net10.0/ALog.Comparison.dll micro --filter '*'` from this directory.

One benchmark class per scenario (T1, T2, T3, T4, T5, T7), parameter `Lib` over all supporting libraries, default
job, `MemoryDiagnoser` and `ThreadingDiagnoser`. Init runs in `IterationSetup`, flush and dispose in
`IterationCleanup`; only the write is measured. `IterationSetup` requires a fixed invocation count, so each class
sets `[InvocationCount]` (T1/T2/T7 10,000, T3 5,000, T4 1 × 8 threads × 1,250 events, T5 1,000,000). No
iteration writes more than 10,000 events. ALog uses `QueueFullMode.Block`, so a full queue waits and an
iteration cannot drop an event. T3 logs a pre-thrown chain, T4 runs 8 parallel writers per invocation and reports per event.

## Lock

`run.sh` takes `flock` on `/tmp/alog-comparison-bench.lock` (`BENCH_LOCK`) for the whole run and fails at once
if another run holds the lock. The message names that process id, or says the PID is unknown when it cannot be
determined. Runs on one machine always run one after another. Build and benchmark processes do not keep the lock
open after `run.sh` exits.

## Merge

`./merge.sh [results-dir] [--out dir] [--include-underscore]` reads every `*/raw.csv` (directories starting with
`_`, such as `_smoke`, only with `--include-underscore`) and writes `merged.csv` and `summary.csv` into
`eng/comparison-bench/results/merged-<YYYY-MM-DD>/` (UTC date; `--out` overrides). An ALog async row is marked invalid unless that
run's `env.json` records `QueueFullMode=Block` for `alog-async`. The files are:

- `merged.csv`: all rows plus `source` (the run directory)
- `summary.csv`: valid rows only; `scope=agent` per runner × framework × mode × scenario and `scope=overall`
  per framework × mode × scenario; count, mean, sample stddev, min and max of every metric; invalid and n/a
  rows are counted, not averaged; `ranked` (ALog sync = `no`) and `rank_total_ms` (1 = lowest mean total time
  among the ranked rows with valid values of the same scope, runner and scenario)

## Licenses and names

NLog (BSD-3-Clause), Serilog, Serilog.Sinks.File, Serilog.Sinks.Async, Serilog.Expressions and log4net
(Apache-2.0) are referenced only by this benchmark project and are not redistributed. BenchmarkDotNet (MIT) is a
build-time and run-time dependency of this project only. Product names are used only to identify the libraries.
