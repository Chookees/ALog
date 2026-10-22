#!/usr/bin/env bash
# Combines every eng/comparison-bench/results/<agent>-<ts>/raw.csv into eng/comparison-bench/results/merged-<YYYY-MM-DD>/ (merged.csv and summary.csv). See merge.py.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$HERE/merge.py" --results "${1:-${BENCH_RESULTS:-$HERE/results}}" "${@:2}"
