#!/usr/bin/env bash
# ALog comparison harness, process runner (arc §8c BEN-06/07). Linux; needs bash, flock, git, dotnet.
#
#   ./run.sh [runs=5] [agent-name]
#
# Writes eng/comparison-bench/results/<agent>-<UTC timestamp>/ with raw.csv (fixed BEN-07 columns), details.jsonl, env.json, run.log
# and, for full-size runs, bdn/ (one BenchmarkDotNet run, BEN-05/07).
# Environment (all optional):
#   AlogSrc=<ALog repo root>    default: two levels up (the repo when this lives in eng/comparison-bench)
#   DOTNET=<dotnet binary>      default: dotnet on PATH, else ~/.dotnet/dotnet
#   BENCH_N_SCALE=0.001         scale every N (smoke); BENCH_N=1000 fixes N for every scenario (CI smoke, BEN-09)
#   BENCH_BDN=0|1               BenchmarkDotNet after the process runs; default 1 for full size, 0 when scaled
#   BENCH_SEED=<int>            library order seed (default random, always recorded)
#   BENCH_LIBS / BENCH_SCENARIOS  comma lists to restrict the matrix (default all)
#   BENCH_RESULTS=<dir>         results root (default eng/comparison-bench/results). When BENCH_N is set and this is empty, eng/comparison-bench/results/_smoke.
#   BENCH_LOCK=<file>           machine-wide lock (default /tmp/alog-comparison-bench.lock)
#   BENCH_CELL_TIMEOUT_S        per-cell hang guard (default 1800 s, 30 s for smoke sizes)
#   BENCH_MUTANT_FULL_MODE      proof switch for the ALog async queue mode (Block when unset)
set -euo pipefail

# PID of another process that holds an flock on the lock file. Empty when it cannot be named.
# Prefers /proc/locks (FLOCK row, same inode). Skips this shell: it has the file open but does not hold the lock.
lock_holder_pid() {
  local lock="$1"
  local ino="" kind="" pid="" devino="" token=""
  ino="$(stat -c '%i' "$lock" 2>/dev/null || true)"
  if [[ -n "$ino" && -r /proc/locks ]]; then
    while read -r _ kind _ _ pid devino _; do
      [[ "$kind" == "FLOCK" ]] || continue
      [[ "$devino" == *":$ino" ]] || continue
      [[ "$pid" =~ ^[0-9]+$ ]] || continue
      [[ "$pid" == "$$" ]] && continue
      printf '%s\n' "$pid"
      return 0
    done < /proc/locks
  fi
  if command -v fuser >/dev/null 2>&1; then
    local text=""
    text="$(fuser "$lock" 2>&1 || true)"
    text="${text//"$lock"/ }"
    for token in $text; do
      pid="${token//[^0-9]/}"
      [[ "$pid" =~ ^[0-9]+$ ]] || continue
      [[ "$pid" == "$$" ]] && continue
      printf '%s\n' "$pid"
      return 0
    done
  fi
  return 0
}

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
  pid="$(lock_holder_pid "$LOCK")"
  if [[ -z "$pid" ]]; then
    echo "Another benchmark run holds $LOCK (PID unknown). Wait until it finishes, then start again." >&2
  else
    echo "Another benchmark run holds $LOCK (PID $pid). Wait until it finishes, then start again." >&2
  fi
  exit 3
fi
echo "$AGENT pid $$ since $(date -u +%Y-%m-%dT%H:%M:%SZ) in $HERE" > "$LOCK.owner"
trap 'rm -f "$LOCK.owner"' EXIT

export TZ=UTC DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# A fixed N is a smoke run. Keep it out of the raw-run tree unless the caller chose a directory.
if [[ -n "${BENCH_N:-}" && -z "${BENCH_RESULTS:-}" ]]; then
  export BENCH_RESULTS="$HERE/results/_smoke"
fi
TS="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="${BENCH_RESULTS:-$HERE/results}/$AGENT-$TS"
mkdir -p "$OUT"
LOG="$OUT/run.log"

echo "building (Release, AlogSrc=$AlogSrc)" | tee "$LOG" 9>&-
# Close fd 9 in every dotnet child so a build server cannot keep the lock after this script exits.
"$DOTNET" build "$HERE/ALog.Comparison.csproj" -c Release -nologo -v q --disable-build-servers -p:AlogSrc="$AlogSrc" 9>&- 2>&1 | tee -a "$LOG" 9>&-
BIN="$HERE/bin/Release/net10.0/ALog.Comparison.dll"

export BENCH_SDK="$("$DOTNET" --version 9>&- 2>/dev/null || echo unknown)"
export BENCH_ALOG_SHA="$(git -C "$AlogSrc" rev-parse HEAD 2>/dev/null || echo unknown)"
export BENCH_ALOG_DIRTY="$( [[ -n "$(git -C "$AlogSrc" status --porcelain --untracked-files=no 2>/dev/null)" ]] && echo true || echo false)"
export BENCH_HARNESS_SHA="$(git -C "$HERE" rev-parse HEAD 2>/dev/null || echo not-in-git)"
export BENCH_FS_TYPE="$(stat -f -c %T "$OUT" 2>/dev/null || echo unknown)"

SEED_ARGS=()
if [[ -n "${BENCH_SEED:-}" ]]; then SEED_ARGS=(--seed "$BENCH_SEED"); fi
set +e
"$DOTNET" "$BIN" run --runs "$RUNS" --agent "$AGENT" --out "$OUT" "${SEED_ARGS[@]}" 9>&- 2>&1 | tee -a "$LOG" 9>&-
STATUS=${PIPESTATUS[0]}
set -e

SCALED=0
if [[ -n "${BENCH_N:-}" ]] || [[ -n "${BENCH_N_SCALE:-}" && "${BENCH_N_SCALE}" != "1" ]]; then SCALED=1; fi
BDN="${BENCH_BDN:-$([[ $SCALED == 1 ]] && echo 0 || echo 1)}"
if [[ "$BDN" == "1" ]]; then
  echo "BenchmarkDotNet run" | tee -a "$LOG"
  # BenchmarkDotNet finds ALog.Comparison.csproj from the working directory, so it runs from here.
  (cd "$HERE" && "$DOTNET" "$BIN" micro --filter '*' --artifacts "$OUT/bdn" --exporters csv github json 9>&- 2>&1 | tee -a "$LOG" 9>&-)
fi

echo "done: $OUT (exit $STATUS; 1 = at least one invalid cell, see details.jsonl)" | tee -a "$LOG"
exit "$STATUS"
