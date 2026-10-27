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

# Require a Full zip file name to contain Full-<rid> so a linux zip cannot
# be smoked on the win path (and vice versa).
# Args: zip-or-dir source, rid.
require_zip_name_matches_rid() {
  local source="$1"
  local rid="$2"
  if [ -d "$source" ]; then
    # Unpacked folder: require the folder name (or a parent) to contain Full-<rid>.
    local base
    base=$(basename "$source")
    case "$base" in
      *Full-"${rid}"*) return 0 ;;
    esac
    fail "unpacked dir name does not match RID ${rid}: ${base}"
  fi
  local name
  name=$(basename "$source")
  case "$name" in
    *Full-"${rid}"*.zip) return 0 ;;
  esac
  fail "zip name does not match RID ${rid}: ${name}"
}

# Require SNI presence/absence for the Full RID.
# Args: unpacked zip dir, rid.
require_sni_for_rid() {
  local zip_dir="$1"
  local rid="$2"
  local sni="$zip_dir/Microsoft.Data.SqlClient.SNI.dll"
  if [ "$rid" = "win-x64" ]; then
    if [ ! -f "$sni" ]; then
      fail "win-x64 Full zip missing Microsoft.Data.SqlClient.SNI.dll"
    fi
    echo "release-smoke: SNI present for win-x64"
  elif [ "$rid" = "linux-x64" ]; then
    if [ -f "$sni" ]; then
      fail "linux-x64 Full zip must not contain Microsoft.Data.SqlClient.SNI.dll"
    fi
    echo "release-smoke: SNI absent for linux-x64"
  else
    fail "unsupported RID: $rid"
  fi
}

# Compile the Hello World snippet from the zip README.txt against ALog.dll in ZipDir.
# Args: repo, unpacked folder containing ALog.dll and README.txt.
compile_readme_hello() {
  local repo="$1"
  local zip_dir="$2"
  local work
  work=$(mktemp -d "${TMPDIR:-/tmp}/alog-readme-hello.XXXXXX")
  python3 - "$repo" "$zip_dir" "$work" <<'PY'
import sys
from pathlib import Path

repo, zip_dir, work = map(Path, sys.argv[1:4])
sys.path.insert(0, str(repo / "eng" / "release-zips"))
from release_lib import extract_hello_world_snippet

readme = (zip_dir / "README.txt").read_text(encoding="utf-8")
snippet = extract_hello_world_snippet(readme)
(work / "Program.cs").write_text(snippet, encoding="utf-8", newline="\n")
(work / "HelloFromReadme.csproj").write_text(
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="ALog">
      <HintPath>"""
    + str(zip_dir / "ALog.dll").replace("\\", "/")
    + """</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>
""",
    encoding="utf-8",
    newline="\n",
)
print(f"readme-hello snippet written under {work}")
PY
  dotnet build "$work/HelloFromReadme.csproj" -c Release --nologo
  echo "README-HELLO compile ok"
}

# Build and run the Lite smoke project against ZipDir.
# Args: repo, unpacked folder containing ALog.dll.
run_lite() {
  local repo="$1"
  local zip_dir="$2"
  local project="$repo/eng/release-smoke/Lite/LiteSmoke.csproj"
  compile_readme_hello "$repo" "$zip_dir"
  dotnet build "$project" -c Release -p:ZipDir="$zip_dir" --nologo
  local out
  out="$repo/eng/release-smoke/Lite/bin/Release/net10.0"
  dotnet exec "$out/LiteSmoke.dll"
}

# Build and run the Full smoke project; copy zip payload beside the output.
# Args: repo, unpacked folder, rid, original zip-or-dir source.
run_full() {
  local repo="$1"
  local zip_dir="$2"
  local rid="$3"
  local source="$4"
  require_zip_name_matches_rid "$source" "$rid"
  require_sni_for_rid "$zip_dir" "$rid"
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
  # shellcheck disable=SC2064
  trap 'rm -rf "$work"' EXIT
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
      run_full "$repo" "$zip_dir" "$3" "$source"
      ;;
    *)
      fail "unknown mode: $mode"
      ;;
  esac
}

main "$@"
