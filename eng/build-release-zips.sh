#!/usr/bin/env bash
# Build ALog Lite / Full release zips with allowlists, version gates, and SHA256SUMS.
#
# Usage (from repo root, or any cwd):
#   bash eng/build-release-zips.sh lite
#   bash eng/build-release-zips.sh full linux-x64
#   bash eng/build-release-zips.sh full win-x64
#   bash eng/build-release-zips.sh all
#
# Environment:
#   RELEASE_ZIPS_OUT   Output directory (default: <repo>/artifacts/release-zips)
#   SOURCE_DATE_EPOCH  Fixed zip timestamps (default: HEAD committer unix time)
#
# The host project under eng/release-zips/host is not in ALog.sln.
# Next: eng/release-smoke, .github/workflows/release-zips.yml.

set -euo pipefail

# Resolve repository root from this script's location.
# Returns the absolute repo path on stdout.
repo_root() {
  local here
  here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
  cd "$here/.." && pwd
}

# Print a clear error and exit 1.
# Args: message words.
fail() {
  echo "build-release-zips: $*" >&2
  exit 1
}

# Require a command on PATH.
# Args: command name.
require_cmd() {
  local name="$1"
  if ! command -v "$name" >/dev/null 2>&1; then
    fail "$name is not on PATH"
  fi
}

# Read the shared package version from both library csproj files.
# Args: repo root. Prints version. Fails when the two differ.
read_package_version() {
  local repo="$1"
  python3 - "$repo" <<'PY'
import sys
from pathlib import Path
sys.path.insert(0, str(Path(sys.argv[1]) / "eng" / "release-zips"))
from release_lib import require_matching_package_versions
print(require_matching_package_versions(Path(sys.argv[1])))
PY
}

# Default SOURCE_DATE_EPOCH to the HEAD committer date when unset.
# Args: repo root. Prints an integer unix timestamp.
resolve_epoch() {
  local repo="$1"
  if [ -n "${SOURCE_DATE_EPOCH:-}" ]; then
    printf '%s\n' "$SOURCE_DATE_EPOCH"
    return 0
  fi
  git -C "$repo" log -1 --format=%ct
}

# Build the AssemblyVersions helper once and print its DLL path.
# Args: repo root. Build logs go to stderr so callers can capture only the path.
ensure_version_reader() {
  local repo="$1"
  local project="$repo/eng/release-zips/assembly_versions/AssemblyVersions.csproj"
  dotnet build "$project" -c Release --nologo -v q >&2
  local out
  out="$repo/eng/release-zips/assembly_versions/bin/Release/net10.0/AssemblyVersions.dll"
  if [ ! -f "$out" ]; then
    fail "AssemblyVersions.dll was not built at $out"
  fi
  printf '%s\n' "$out"
}

# Read FileVersion and InformationalVersion for one DLL.
# Args: version-reader DLL, target DLL path.
# Prints: FileVersion=...|InformationalVersion=...
read_assembly_versions() {
  local reader="$1"
  local target="$2"
  local line
  line=$(dotnet exec "$reader" "$target")
  printf '%s\n' "${line#*|}"
}

# Fail unless ALog / Extensions match the package version and Pkcs is new enough.
# Args: repo, version-reader DLL, package version, path to ALog.dll,
#       optional path to Extensions DLL, optional path to Pkcs DLL.
check_versions() {
  local repo="$1"
  local reader="$2"
  local expected="$3"
  local alog_dll="$4"
  local ext_dll="${5:-}"
  local pkcs_dll="${6:-}"

  python3 - "$repo" "$reader" "$expected" "$alog_dll" "$ext_dll" "$pkcs_dll" <<'PY'
import subprocess
import sys
from pathlib import Path

repo = Path(sys.argv[1])
reader, expected, alog, ext, pkcs = sys.argv[2:7]
sys.path.insert(0, str(repo / "eng" / "release-zips"))
from release_lib import (
    PKCS_MIN_VERSION,
    file_version_matches,
    informational_matches,
    version_at_least,
)


def parse(line: str) -> dict[str, str]:
    """Split one AssemblyVersions output line into a field map."""
    fields: dict[str, str] = {}
    for part in line.split("|"):
        if "=" in part:
            key, value = part.split("=", 1)
            fields[key] = value
    return fields


def check_lib(path: str, label: str) -> None:
    """Require FileVersion and InformationalVersion to match the package version."""
    out = subprocess.check_output(["dotnet", "exec", reader, path], text=True).strip()
    fields = parse(out)
    file_version = fields.get("FileVersion", "")
    informational = fields.get("InformationalVersion", "")
    if not file_version_matches(expected, file_version):
        raise SystemExit(
            f"{label} FileVersion {file_version!r} does not match package {expected}"
        )
    if not informational_matches(expected, informational):
        raise SystemExit(
            f"{label} InformationalVersion {informational!r} does not match package {expected}"
        )
    print(
        f"version ok {label}: FileVersion={file_version} InformationalVersion={informational}"
    )


check_lib(alog, "ALog.dll")
if ext:
    check_lib(ext, "ALog.Extensions.Logging.dll")
if pkcs:
    out = subprocess.check_output(["dotnet", "exec", reader, pkcs], text=True).strip()
    fields = parse(out)
    file_version = fields.get("FileVersion", "")
    if not version_at_least(file_version, PKCS_MIN_VERSION):
        raise SystemExit(
            f"System.Security.Cryptography.Pkcs FileVersion {file_version!r} is below 10.0.13"
        )
    print(f"version ok Pkcs: FileVersion={file_version}")
PY
}

# Reject a top-level SqlClient stub (≈93 KiB) after RID publish.
# Args: path to Microsoft.Data.SqlClient.dll.
require_real_sqlclient() {
  local dll="$1"
  python3 - "$dll" <<'PY'
import sys
from pathlib import Path

path = Path(sys.argv[1])
size = path.stat().st_size
minimum = 500_000
if size < minimum:
    raise SystemExit(
        f"Microsoft.Data.SqlClient.dll looks like the reference stub "
        f"({size} bytes; need >={minimum}). Use dotnet publish -r <rid>."
    )
print(f"SqlClient size ok: {size} bytes")
PY
}

# Write README.txt for one zip kind into a stage folder.
# Args: repo, version, kind, destination directory.
write_readme() {
  local repo="$1"
  local version="$2"
  local kind="$3"
  local dest="$4"
  python3 - "$repo" "$version" "$kind" "$dest" <<'PY'
import sys
from pathlib import Path
repo, version, kind, dest = sys.argv[1:5]
sys.path.insert(0, str(Path(repo) / "eng" / "release-zips"))
from release_lib import build_readme_text
allowlist_dir = Path(repo) / "eng" / "release-zips" / "allowlists"
text = build_readme_text(version, kind, allowlist_dir=allowlist_dir)
Path(dest, "README.txt").write_text(text, encoding="utf-8", newline="\n")
PY
}

# Copy LICENSE and NOTICE from the repo root into a stage folder.
# Args: repo, destination directory.
copy_license_files() {
  local repo="$1"
  local dest="$2"
  cp "$repo/LICENSE" "$dest/LICENSE"
  cp "$repo/NOTICE" "$dest/NOTICE"
}

# Stage the Lite zip contents (ALog.dll + license files + README).
# Args: repo, version, stage parent, version-reader DLL.
stage_lite() {
  local repo="$1"
  local version="$2"
  local stage_root="$3"
  local reader="$4"
  local folder="ALog-v${version}-Lite"
  local dest="$stage_root/$folder"
  rm -rf "$dest"
  mkdir -p "$dest"

  dotnet build "$repo/ALog/ALog.csproj" -c Release --nologo -v q
  local alog_dll="$repo/ALog/bin/Release/net10.0/ALog.dll"
  if [ ! -f "$alog_dll" ]; then
    fail "ALog.dll missing after Release build"
  fi
  cp "$alog_dll" "$dest/ALog.dll"
  copy_license_files "$repo" "$dest"
  write_readme "$repo" "$version" "Lite" "$dest"
  check_versions "$repo" "$reader" "$version" "$dest/ALog.dll"
}

# Fail when publish output ≠ allowlist payload or RID DLL hashes ≠ NuGet runtimes/.
# Args: repo, publish directory, allowlist path, rid.
check_publish_before_stage() {
  local repo="$1"
  local publish="$2"
  local allowlist="$3"
  local rid="$4"
  python3 - "$repo" "$publish" "$allowlist" "$rid" <<'PY'
import sys
from pathlib import Path

repo, publish, allowlist, rid = sys.argv[1:5]
sys.path.insert(0, str(Path(repo) / "eng" / "release-zips"))
from release_lib import check_full_publish_output

check_full_publish_output(Path(publish), Path(allowlist), rid)
print(f"publish allowlist + RID NuGet hash ok for {rid}")
PY
}

# Publish the host for one RID and stage Full zip contents (DLLs + language folders).
# Args: repo, version, rid (linux-x64|win-x64), stage parent, version-reader DLL.
stage_full() {
  local repo="$1"
  local version="$2"
  local rid="$3"
  local stage_root="$4"
  local reader="$5"
  local kind="Full-${rid}"
  local folder="ALog-v${version}-${kind}"
  local dest="$stage_root/$folder"
  local publish="$stage_root/publish-${rid}"
  local host_dir="$repo/eng/release-zips/host"
  rm -rf "$dest" "$publish"
  rm -rf "$host_dir/bin" "$host_dir/obj"
  mkdir -p "$dest" "$publish"

  dotnet publish "$host_dir/ReleaseZipHost.csproj" \
    -c Release -r "$rid" --self-contained false \
    -o "$publish" --nologo -v q

  # Compare full publish output to the allowlist BEFORE staging (BUG-51-2).
  # Exclusions: host files, *.pdb, *.deps.json, *.runtimeconfig.json only.
  # RID DLLs must SHA-256-match NuGet runtimes/<family>/lib|native assets.
  local allowlist="$repo/eng/release-zips/allowlists/${kind}.txt"
  check_publish_before_stage "$repo" "$publish" "$allowlist" "$rid"

  # Copy only allowlisted payload: top-level DLLs + SqlClient satellite folders.
  while IFS= read -r rel || [ -n "$rel" ]; do
    rel=${rel//$'\r'/}
    [ -z "$rel" ] && continue
    case "$rel" in
      LICENSE|NOTICE|README.txt) continue ;;
    esac
    local src="$publish/$rel"
    if [ ! -f "$src" ]; then
      fail "published output missing $rel for $rid"
    fi
    mkdir -p "$dest/$(dirname "$rel")"
    cp "$src" "$dest/$rel"
  done < "$allowlist"

  # Refuse host leftovers and stub SqlClient.
  if [ -e "$dest/ReleaseZipHost.dll" ] || [ -e "$dest/ReleaseZipHost.exe" ] || [ -e "$dest/ReleaseZipHost" ]; then
    fail "host binary leaked into $kind stage"
  fi
  if find "$dest" \( -name '*.pdb' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) | grep -q .; then
    fail "pdb/deps/runtimeconfig leaked into $kind stage"
  fi
  if [ -d "$dest/runtimes" ]; then
    fail "runtimes/ folder must not appear in $kind stage"
  fi
  require_real_sqlclient "$dest/Microsoft.Data.SqlClient.dll"
  copy_license_files "$repo" "$dest"
  write_readme "$repo" "$version" "$kind" "$dest"
  check_versions "$repo" "$reader" "$version" \
    "$dest/ALog.dll" \
    "$dest/ALog.Extensions.Logging.dll" \
    "$dest/System.Security.Cryptography.Pkcs.dll"
}

# Pack staged folders with make_zips.py.
# Args: repo, version, stage parent, out dir, epoch, kind words...
pack_kinds() {
  local repo="$1"
  local version="$2"
  local stage_root="$3"
  local out_dir="$4"
  local epoch="$5"
  shift 5
  local args=(
    python3 "$repo/eng/release-zips/make_zips.py"
    --repo "$repo"
    --stage "$stage_root"
    --out "$out_dir"
    --version "$version"
    --epoch "$epoch"
  )
  local kind
  for kind in "$@"; do
    args+=(--kind "$kind")
  done
  "${args[@]}"
}

# Print usage and exit 2.
usage() {
  cat >&2 <<'EOF'
Usage:
  eng/build-release-zips.sh lite
  eng/build-release-zips.sh full <linux-x64|win-x64>
  eng/build-release-zips.sh all
EOF
  exit 2
}

# Orchestrate staging and packing for the requested targets.
# Args: CLI args after script name.
main() {
  require_cmd dotnet
  require_cmd python3
  require_cmd git

  local repo
  repo=$(repo_root)
  local out_dir="${RELEASE_ZIPS_OUT:-$repo/artifacts/release-zips}"
  local stage_root="$out_dir/stage"
  local epoch
  epoch=$(resolve_epoch "$repo")
  local version
  version=$(read_package_version "$repo")
  echo "package version: $version"
  echo "SOURCE_DATE_EPOCH: $epoch"
  echo "output: $out_dir"

  # Clean host outputs and the stage tree so local/CI runs start from a known state.
  rm -rf "$stage_root"
  rm -rf "$repo/eng/release-zips/host/bin" "$repo/eng/release-zips/host/obj"
  mkdir -p "$out_dir" "$stage_root"
  local reader
  reader=$(ensure_version_reader "$repo")

  if [ "$#" -lt 1 ]; then
    usage
  fi

  local mode="$1"
  shift || true
  local kinds=()

  case "$mode" in
    lite)
      stage_lite "$repo" "$version" "$stage_root" "$reader"
      kinds+=(Lite)
      ;;
    full)
      if [ "$#" -ne 1 ]; then
        usage
      fi
      local rid="$1"
      case "$rid" in
        linux-x64|win-x64) ;;
        *) fail "unsupported RID: $rid" ;;
      esac
      stage_full "$repo" "$version" "$rid" "$stage_root" "$reader"
      kinds+=("Full-${rid}")
      ;;
    all)
      stage_lite "$repo" "$version" "$stage_root" "$reader"
      stage_full "$repo" "$version" "linux-x64" "$stage_root" "$reader"
      stage_full "$repo" "$version" "win-x64" "$stage_root" "$reader"
      kinds+=(Lite Full-linux-x64 Full-win-x64)
      ;;
    *)
      usage
      ;;
  esac

  pack_kinds "$repo" "$version" "$stage_root" "$out_dir" "$epoch" "${kinds[@]}"
  echo "SHA256SUMS:"
  cat "$out_dir/SHA256SUMS"
}

main "$@"
