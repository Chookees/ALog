#!/usr/bin/env bash
# The base-assembly helper must fail closed when the revision does not exist.
# A missing assembly is a red BB-09 job, not a skip.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

dest=$(mktemp -d)
if bash eng/build-base-assemblies.sh does-not-exist-bb09 "$dest" >/tmp/base-asm-miss.txt 2>&1; then
  echo "FAIL missing revision was accepted" >&2
  exit 1
fi
if [ -e "$dest/ALog.dll" ]; then
  echo "FAIL missing revision wrote ALog.dll" >&2
  exit 1
fi
echo "ok missing base revision is red"
echo "build base assembly tests passed"
