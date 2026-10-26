#!/usr/bin/env bash
# Unpack a release zip (or use an existing folder) and run Lite or Full smoke.
#
# Usage:
#   bash eng/release-smoke/run-smoke.sh lite <Lite.zip|unpacked-dir>
#   bash eng/release-smoke/run-smoke.sh full <Full-*.zip|unpacked-dir> <linux-x64|win-x64>
#
# Full copies every file from the unpacked zip next to the smoke output so
# SqlClient/AWS/satellites load without a deps.json. Next: release-zips.yml.

set -euo pipefail

# Resolve repository root from this script.
# Prints the absolute repo path.
repo_root() {
  local here
  here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
  cd "$here/../.." && pwd
}

# Print an error and exit 1.
# Args: message words.
fail() {
  echo "release-smoke: $*" >&2
  exit 1
}

# Unpack a zip to a directory, or reuse a directory path.
# Args: zip-or-dir, destination directory.
# Prints the directory that contains ALog.dll (the zip root folder).
prepare_zip_dir() {
  local source="$1"
  local dest="$2"
  rm -rf "$dest"
  mkdir -p "$dest"
  if [ -d "$source" ]; then
    cp -a "$source"/. "$dest"/
  elif [ -f "$source" ]; then
    python3 - "$source" "$dest" <<'PY'
import sys, zipfile
from pathlib import Path
zpath, dest = Path(sys.argv[1]), Path(sys.argv[2])
with zipfile.ZipFile(zpath) as zf:
    zf.extractall(dest)
PY
  else
    fail "zip or directory not found: $source"
  fi

  local alog
  alog=$(find "$dest" -name 'ALog.dll' -print | head -n 1)
  if [ -z "$alog" ]; then
    fail "ALog.dll not found under $dest"
  fi
  dirname "$alog"
}

# Build and run the Lite smoke project against ZipDir.
# Args: repo, unpacked folder containing ALog.dll.
run_lite() {
  local repo="$1"
  local zip_dir="$2"
  local project="$repo/eng/release-smoke/Lite/LiteSmoke.csproj"
  dotnet build "$project" -c Release -p:ZipDir="$zip_dir" --nologo
  local out
  out="$repo/eng/release-smoke/Lite/bin/Release/net10.0"
  dotnet exec "$out/LiteSmoke.dll"
}

# Build and run the Full smoke project; copy zip payload beside the output.
# Args: repo, unpacked folder, rid.
run_full() {
  local repo="$1"
  local zip_dir="$2"
  local rid="$3"
  local project="$repo/eng/release-smoke/Full/FullSmoke.csproj"
  dotnet build "$project" -c Release -p:ZipDir="$zip_dir" --nologo
  local out
  out="$repo/eng/release-smoke/Full/bin/Release/net10.0"
  # Overlay zip DLLs and satellite folders so runtime probing finds SqlClient/AWS.
  cp -a "$zip_dir"/. "$out"/
  ALOG_RELEASE_SMOKE_RID="$rid" dotnet exec "$out/FullSmoke.dll" "$rid"
}

# CLI entry.
# Args: lite|full, zip-or-dir, [rid].
main() {
  local repo
  repo=$(repo_root)
  if [ "$#" -lt 2 ]; then
    echo "Usage: run-smoke.sh lite <zip-or-dir>" >&2
    echo "       run-smoke.sh full <zip-or-dir> <linux-x64|win-x64>" >&2
    exit 2
  fi
  local mode="$1"
  local source="$2"
  local work="${RUNNER_TEMP:-/tmp}/alog-release-smoke-$$"
  local zip_dir
  zip_dir=$(prepare_zip_dir "$source" "$work")
  echo "zip dir: $zip_dir"

  case "$mode" in
    lite)
      run_lite "$repo" "$zip_dir"
      ;;
    full)
      if [ "$#" -ne 3 ]; then
        fail "full smoke requires a RID argument"
      fi
      run_full "$repo" "$zip_dir" "$3"
      ;;
    *)
      fail "unknown mode: $mode"
      ;;
  esac
}

main "$@"
