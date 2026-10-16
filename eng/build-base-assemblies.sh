#!/usr/bin/env bash
# Builds the Release assemblies of ALog and ALog.Extensions.Logging at a git
# revision and copies them into a directory. BB-09 diffs those assemblies
# against the head build. A missing revision, a failed build, or a missing
# dll exits 1. The check is never skipped.

set -euo pipefail

# Removes the temporary worktree. Args: repo root, worktree path.
cleanup_worktree() {
  local root="$1"
  local src="$2"
  if [ -d "$src" ]; then
    git -C "$root" worktree remove --force "$src" >/dev/null 2>&1 || true
  fi
}

# Args: git ref, destination directory. The destination is created.
main() {
  local ref="${1:-}"
  local dest="${2:-}"
  if [ -z "$ref" ] || [ -z "$dest" ]; then
    echo "usage: eng/build-base-assemblies.sh <ref> <dest>" >&2
    return 1
  fi
  local root
  root=$(cd "$(dirname "$0")/.." && pwd)
  if ! git -C "$root" rev-parse --verify "${ref}^{commit}" >/dev/null 2>&1; then
    echo "base revision is missing: ${ref}" >&2
    return 1
  fi
  mkdir -p "$dest"
  local work src
  work=$(mktemp -d)
  src="$work/src"
  # shellcheck disable=SC2064
  trap "cleanup_worktree '$root' '$src'; rm -rf '$work'" EXIT
  git -C "$root" worktree add --detach "$src" "$ref"
  # The base tree has no global.json. Pin the same SDK as the head build so a
  # runner with several SDKs cannot compile the base assemblies with another one.
  if [ ! -f "$root/global.json" ]; then
    echo "global.json is missing; refusing to build base assemblies with an unpinned SDK." >&2
    return 1
  fi
  cp "$root/global.json" "$src/global.json"
  dotnet build "$src/ALog.Extensions.Logging/ALog.Extensions.Logging.csproj" -c Release --nologo
  local alog ext
  alog="$src/ALog/bin/Release/net10.0/ALog.dll"
  ext="$src/ALog.Extensions.Logging/bin/Release/net10.0/ALog.Extensions.Logging.dll"
  if [ ! -s "$alog" ] || [ ! -s "$ext" ]; then
    echo "base assemblies were not produced for ${ref}." >&2
    return 1
  fi
  cp "$alog" "$dest/ALog.dll"
  cp "$ext" "$dest/ALog.Extensions.Logging.dll"
  if [ ! -s "$dest/ALog.dll" ] || [ ! -s "$dest/ALog.Extensions.Logging.dll" ]; then
    echo "base assemblies could not be copied." >&2
    return 1
  fi
  echo "base assemblies copied to ${dest}"
}

main "$@"
