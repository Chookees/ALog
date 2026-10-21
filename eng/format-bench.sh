#!/usr/bin/env bash
# Runs FormatBenchmarks against this branch or against master 0dd4a89.
# Master has no FormatBenchmarks file, so the current harness is copied into a detached worktree.
# The library under test is the worktree's ALog project, not the branch.
set -euo pipefail

mode="${1:?master or branch}"
out="${2:?artifacts directory}"
filter="${3:?benchmark filter}"
root="$(cd "$(dirname "$0")/.." && pwd)"
master="0dd4a8966cbc4d5274829822889b0f814b4ed7c6"

if [ "$mode" = "master" ]; then
  dest="${RUNNER_TEMP:-/tmp}/alog-master"
  if [ ! -d "$dest" ]; then
    git -C "$root" worktree add --detach "$dest" "$master"
    cp "$root/ALog.Benchmarks/FormatBenchmarks.cs" "$dest/ALog.Benchmarks/FormatBenchmarks.cs"
    cp "$root/ALog.Benchmarks/Program.cs" "$dest/ALog.Benchmarks/Program.cs"
  fi
  project="$dest/ALog.Benchmarks/ALog.Benchmarks.csproj"
elif [ "$mode" = "branch" ]; then
  project="$root/ALog.Benchmarks/ALog.Benchmarks.csproj"
else
  echo "mode must be master or branch" >&2
  exit 1
fi

# One --filter option, then each pattern. Repeating --filter is rejected by BenchmarkDotNet.
filter_args=(--filter)
IFS='|' read -r -a patterns <<< "$filter"
for pattern in "${patterns[@]}"; do
  if [ -z "$pattern" ]; then
    echo "empty filter pattern" >&2
    exit 1
  fi
  filter_args+=("$pattern")
done

mkdir -p "$out"
dotnet run -c Release --project "$project" -- "${filter_args[@]}" --artifacts "$out"
shopt -s nullglob
reports=("$out"/results/*-report-github.md)
if [ "${#reports[@]}" -eq 0 ]; then
  echo "no github report in $out" >&2
  exit 1
fi
