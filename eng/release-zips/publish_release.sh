#!/usr/bin/env bash
# Create a GitHub release for a version tag from CI artifacts of this run.
#
# Expects exactly three zip assets whose names are derived from the tag version.
# SHA256SUMS may appear in the notes text but is never uploaded as a fourth asset.
# Aborts when the release already exists (no --clobber). Uses gh + GITHUB_TOKEN.
#
# Usage:
#   bash eng/release-zips/publish_release.sh <tag-ref> <artifacts-dir>
#
# artifacts-dir must contain the three zips (and preferably SHA256SUMS).

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
  echo "publish_release: $*" >&2
  exit 1
}

# Derive version and required asset names; verify the artifact directory.
# Args: repo, tag ref, artifacts dir.
# Prints: version on the first line, then the three asset paths.
collect_assets() {
  local repo="$1"
  local tag_ref="$2"
  local artifacts="$3"
  python3 - "$repo" "$tag_ref" "$artifacts" <<'PY'
import sys
from pathlib import Path

repo = Path(sys.argv[1])
tag_ref = sys.argv[2]
artifacts = Path(sys.argv[3])
sys.path.insert(0, str(repo / "eng" / "release-zips"))
from release_lib import expected_asset_names, parse_tag_version

version = parse_tag_version(tag_ref)
print(version)
names = expected_asset_names(version)
paths = []
for name in names:
    matches = [p for p in artifacts.rglob(name)]
    if len(matches) != 1:
        raise SystemExit(
            f"expected exactly one {name} under {artifacts}, found {len(matches)}"
        )
    paths.append(matches[0])
extra = sorted(
    p.name for p in artifacts.rglob("*.zip") if p.name not in set(names)
)
if extra:
    raise SystemExit(f"unexpected zip assets: {', '.join(extra)}")
for path in paths:
    print(path)
PY
}

# Build release notes from CHANGELOG + asset table + SHA256SUMS text.
# Args: repo, version, paths to the three zips, optional SHA256SUMS path.
# Writes notes to the path printed on stdout.
write_notes() {
  local repo="$1"
  local version="$2"
  local lite="$3"
  local win="$4"
  local linux="$5"
  local sums="${6:-}"
  local notes
  notes=$(mktemp)
  python3 - "$repo" "$version" "$lite" "$win" "$linux" "$sums" "$notes" <<'PY'
import hashlib
import sys
from pathlib import Path

repo = Path(sys.argv[1])
version = sys.argv[2]
lite, win, linux = map(Path, sys.argv[3:6])
sums_arg = sys.argv[6]
notes_path = Path(sys.argv[7])
sys.path.insert(0, str(repo / "eng" / "release-zips"))
from release_lib import build_release_notes, extract_changelog_section, expected_asset_names

changelog = (repo / "CHANGELOG.md").read_text(encoding="utf-8")
section = extract_changelog_section(changelog, version)
names = expected_asset_names(version)
sizes = {
    names[0]: lite.stat().st_size,
    names[1]: win.stat().st_size,
    names[2]: linux.stat().st_size,
}

def digest(path: Path) -> str:
    h = hashlib.sha256()
    h.update(path.read_bytes())
    return h.hexdigest()

if sums_arg:
    sha_text = Path(sums_arg).read_text(encoding="utf-8")
else:
    sha_text = "".join(
        f"{digest(path)}  {name}\n"
        for name, path in zip(names, (lite, win, linux), strict=True)
    )
notes_path.write_text(
    build_release_notes(section, version, sizes, sha_text),
    encoding="utf-8",
    newline="\n",
)
print(notes_path)
PY
}

# Create the GitHub release or abort when it already exists.
# Args: tag ref, version, notes file, three zip paths.
create_release() {
  local tag_ref="$1"
  local version="$2"
  local notes="$3"
  local lite="$4"
  local win="$5"
  local linux="$6"

  if [ -z "${GITHUB_TOKEN:-}" ] && [ -z "${GH_TOKEN:-}" ]; then
    fail "GITHUB_TOKEN (or GH_TOKEN) is required"
  fi
  if ! command -v gh >/dev/null 2>&1; then
    fail "gh is not on PATH"
  fi

  local tag="$tag_ref"
  case "$tag" in
    refs/tags/*) tag="${tag#refs/tags/}" ;;
  esac

  if gh release view "$tag" >/dev/null 2>&1; then
    fail "release $tag already exists; refusing to overwrite (no --clobber)"
  fi

  # --verify-tag aborts when the tag is missing on the remote (does not create one).
  gh release create "$tag" \
    --verify-tag \
    --title "ALog v${version}" \
    --notes-file "$notes" \
    "$lite" "$win" "$linux"
  echo "publish_release: created $tag with 3 assets"
}

# CLI entry.
# Args: tag-ref, artifacts-dir.
main() {
  if [ "$#" -ne 2 ]; then
    echo "Usage: publish_release.sh <tag-ref> <artifacts-dir>" >&2
    exit 2
  fi
  local repo
  repo=$(repo_root)
  local tag_ref="$1"
  local artifacts="$2"
  if [ ! -d "$artifacts" ]; then
    fail "artifacts directory missing: $artifacts"
  fi

  local collected
  collected=$(collect_assets "$repo" "$tag_ref" "$artifacts")
  local version lite win linux
  version=$(printf '%s\n' "$collected" | sed -n '1p')
  lite=$(printf '%s\n' "$collected" | sed -n '2p')
  win=$(printf '%s\n' "$collected" | sed -n '3p')
  linux=$(printf '%s\n' "$collected" | sed -n '4p')

  local sums=""
  local sums_hits
  sums_hits=$(find "$artifacts" -name SHA256SUMS -print)
  local sums_count
  sums_count=$(printf '%s\n' "$sums_hits" | sed '/^$/d' | wc -l | tr -d ' ')
  if [ "$sums_count" -eq 1 ]; then
    sums=$(printf '%s\n' "$sums_hits" | sed '/^$/d' | head -n 1)
  fi

  local notes
  notes=$(write_notes "$repo" "$version" "$lite" "$win" "$linux" "$sums")
  create_release "$tag_ref" "$version" "$notes" "$lite" "$win" "$linux"
}

main "$@"
