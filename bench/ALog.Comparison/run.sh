#!/usr/bin/env bash
# ALog comparison harness, process runner (arc §8c BEN-06/07). Linux; needs bash, flock, git, dotnet.
#
#   ./run.sh [runs=5] [agent-name]
#
# Writes results/<agent>-<UTC timestamp>/ with raw.csv (fixed BEN-07 columns), details.jsonl, env.json, run.log
# and, for full-size runs, bdn/ (one BenchmarkDotNet run, BEN-05/07).
# Environment (all optional):
#   AlogSrc=<ALog repo root>    default: two levels up (the repo when this lives in bench/ALog.Comparison)
#   DOTNET=<dotnet binary>      default: dotnet on PATH, else ~/.dotnet/dotnet
#   BENCH_N_SCALE=0.001         scale every N (smoke); BENCH_N=1000 fixes N for every scenario (CI smoke, BEN-09)
#   BENCH_BDN=0|1               BenchmarkDotNet after the process runs; default 1 for full size, 0 when scaled
#   BENCH_SEED=<int>            library order seed (default random, always recorded)
#   BENCH_LIBS / BENCH_SCENARIOS  comma lists to restrict the matrix (default all)
#   BENCH_RESULTS=<dir>         results root (default ./results)
#   BENCH_LOCK=<file>           machine-wide lock (default /tmp/alog-comparison-bench.lock)
#   BENCH_CELL_TIMEOUT_S        per-cell hang guard (default 1800 s, 30 s for smoke sizes)
set -euo pipefail

RUNS="${1:-5}"
AGENT="${2:-${USER:-unknown}}"
if ! [[ "$RUNS" =~ ^[1-9][0-9]*$ ]]; then echo "runs must be a positive integer, got '$RUNS'" >&2; exit 2; fi
if ! [[ "$AGENT" =~ ^[A-Za-z0-9._-]+$ ]]; then echo "agent name may contain only A-Z a-z 0-9 . _ -, got '$AGENT'" >&2; exit 2; fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -z "${DOTNET:-}" ]]; then
  if command -v dotnet >/dev/null 2>&1; then DOTNET=dotnet; else DOTNET="$HOME/.dotnet/dotnet"; fi
fi
export AlogSrc="$(cd "${AlogSrc:-$HERE/../..}" && pwd)"
if [[ ! -f "$AlogSrc/ALog/ALog.csproj" ]]; then echo "ALog not found: $AlogSrc/ALog/ALog.csproj (set AlogSrc=<ALog repo root>)" >&2; exit 2; fi
command -v flock >/dev/null 2>&1 || { echo "flock is required (util-linux)" >&2; exit 2; }

# One run per machine at a time (BEN-07: parallel box runs invalidate each other). Fail fast and name the holder.
LOCK="${BENCH_LOCK:-/tmp/alog-comparison-bench.lock}"
exec 9>>"$LOCK"
if ! flock -n 9; then
  echo "another benchmark run holds $LOCK: $(cat "$LOCK.owner" 2>/dev/null || echo unknown). Run again when it is done; never in parallel." >&2
  exit 3
fi
echo "$AGENT pid $$ since $(date -u +%Y-%m-%dT%H:%M:%SZ) in $HERE" > "$LOCK.owner"
trap 'rm -f "$LOCK.owner"' EXIT

export TZ=UTC DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
TS="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="${BENCH_RESULTS:-$HERE/results}/$AGENT-$TS"
mkdir -p "$OUT"
LOG="$OUT/run.log"

echo "building (Release, AlogSrc=$AlogSrc)" | tee "$LOG"
"$DOTNET" build "$HERE/ALog.Comparison.csproj" -c Release -nologo -v q -p:AlogSrc="$AlogSrc" 2>&1 | tee -a "$LOG"
BIN="$HERE/bin/Release/net10.0/ALog.Comparison.dll"

export BENCH_SDK="$("$DOTNET" --version 2>/dev/null || echo unknown)"
export BENCH_ALOG_SHA="$(git -C "$AlogSrc" rev-parse HEAD 2>/dev/null || echo unknown)"
export BENCH_ALOG_DIRTY="$( [[ -n "$(git -C "$AlogSrc" status --porcelain --untracked-files=no 2>/dev/null)" ]] && echo true || echo false)"
export BENCH_HARNESS_SHA="$(git -C "$HERE" rev-parse HEAD 2>/dev/null || echo not-in-git)"
export BENCH_FS_TYPE="$(stat -f -c %T "$OUT" 2>/dev/null || echo unknown)"

SEED_ARGS=()
if [[ -n "${BENCH_SEED:-}" ]]; then SEED_ARGS=(--seed "$BENCH_SEED"); fi
set +e
"$DOTNET" "$BIN" run --runs "$RUNS" --agent "$AGENT" --out "$OUT" "${SEED_ARGS[@]}" 2>&1 | tee -a "$LOG"
STATUS=${PIPESTATUS[0]}
set -e

SCALED=0
if [[ -n "${BENCH_N:-}" ]] || [[ -n "${BENCH_N_SCALE:-}" && "${BENCH_N_SCALE}" != "1" ]]; then SCALED=1; fi
BDN="${BENCH_BDN:-$([[ $SCALED == 1 ]] && echo 0 || echo 1)}"
if [[ "$BDN" == "1" ]]; then
  echo "BenchmarkDotNet run" | tee -a "$LOG"
  # BenchmarkDotNet finds ALog.Comparison.csproj from the working directory, so it runs from here.
  (cd "$HERE" && "$DOTNET" "$BIN" micro --filter '*' --artifacts "$OUT/bdn" --exporters csv github json 2>&1 | tee -a "$LOG")
fi

echo "done: $OUT (exit $STATUS; 1 = at least one invalid cell, see details.jsonl)" | tee -a "$LOG"
exit "$STATUS"
