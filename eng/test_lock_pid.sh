#!/usr/bin/env bash
# The benchmark lock names its holder from /proc/locks by inode. fuser is not used.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
run="$root/eng/comparison-bench/run.sh"

if grep -n 'fuser' "$run"; then
  echo "lock-pid: run.sh still mentions fuser" >&2
  exit 1
fi

fn=$(sed -n '/^lock_holder_pid() {/,/^}$/p' "$run")
if [[ -z "$fn" ]]; then
  echo "lock-pid: lock_holder_pid is missing" >&2
  exit 1
fi
eval "$fn"

if ! grep -F 'Another benchmark run holds $LOCK (PID unknown). Wait until it finishes, then start again.' "$run" >/dev/null; then
  echo "lock-pid: PID unknown message missing" >&2
  exit 1
fi
if ! grep -F 'Another benchmark run holds $LOCK (PID $pid). Wait until it finishes, then start again.' "$run" >/dev/null; then
  echo "lock-pid: PID message missing" >&2
  exit 1
fi

lock=$(mktemp)
holder=""
cleanup() {
  if [[ -n "$holder" ]]; then
    kill "$holder" 2>/dev/null || true
    wait "$holder" 2>/dev/null || true
  fi
  rm -f "$lock"
}
trap cleanup EXIT

empty="$(lock_holder_pid "$lock")"
if [[ -n "$empty" ]]; then
  echo "lock-pid: unlocked file reported PID $empty" >&2
  exit 1
fi

flock -n "$lock" -c 'sleep 30' &
holder=$!

found=""
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
  found="$(lock_holder_pid "$lock")"
  if [[ -n "$found" ]]; then
    break
  fi
  sleep 0.05
done

if [[ -z "$found" ]]; then
  echo "lock-pid: held lock was not in /proc/locks" >&2
  exit 1
fi
if [[ "$found" == "$$" ]]; then
  echo "lock-pid: reported this shell" >&2
  exit 1
fi
if [[ "$found" != "$holder" ]]; then
  echo "lock-pid: reported $found, holder is $holder" >&2
  exit 1
fi

echo "lock-pid: ok"
