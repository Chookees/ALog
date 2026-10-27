#!/usr/bin/env bash
# Create a GitHub release for a version tag from CI artifacts of this run.
#
# Expects exactly three zip assets whose names are derived from the tag version.
# SHA256SUMS may appear in the notes text but is never uploaded as a fourth asset.
# Aborts when the release already exists (no --clobber). Uses gh + GITHUB_TOKEN.
#
# Flow: create a --draft release (no assets) → upload zips → gh release edit
# --draft=false. OWNED_DRAFT is set only after our create succeeds and cleared
# immediately after publish edit succeeds. A TERM/INT/EXIT trap deletes the
# draft (gh release delete --yes, never --cleanup-tag) while OWNED_DRAFT is set.
# Concurrent "already exists" and other create failures never delete.
#
# Usage:
#   bash eng/release-zips/publish_release.sh <tag-ref> <artifacts-dir>
#
# artifacts-dir must contain the three zips (and preferably SHA256SUMS).

set -euo pipefail

# Set when this run successfully created a draft release; cleared after publish.
OWNED_DRAFT=0
OWNED_TAG=""

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

# Normalize a tag ref to the bare tag name (strip refs/tags/).
# Args: tag ref. Prints the bare tag.
bare_tag() {
  local tag="$1"
  case "$tag" in
    refs/tags/*) tag="${tag#refs/tags/}" ;;
  esac
  printf '%s\n' "$tag"
}

# Require that no GitHub release exists for the tag.
# Only a clear "release not found" from gh means absent; auth/network/other → fail.
# Args: bare tag name.
require_release_absent() {
  local tag="$1"
  local err
  local rc
  set +e
  err=$(gh release view "$tag" 2>&1)
  rc=$?
  set -e
  if [ "$rc" -eq 0 ]; then
    fail "error: release $tag already exists; refusing to overwrite (no --clobber)"
  fi
  if printf '%s\n' "$err" | grep -qi 'release not found'; then
    return 0
  fi
  fail "error: gh release view failed (not a clear 'release not found'): $err"
}

# Delete the draft this run owns. Never passes --cleanup-tag (tag stays).
# Args: bare tag name.
# Returns: 0 when delete succeeds; non-zero when gh delete fails.
delete_owned_draft() {
  local tag="$1"
  echo "publish_release: deleting owned draft $tag (keeping tag; no --cleanup-tag)" >&2
  gh release delete "$tag" --yes
}

# Roll back an owned draft after upload/edit failure or signal.
# Args: bare tag name, reason words for a successful delete path.
# On delete failure prints the exact FAILED message and exits 1 (never "rolled back").
rollback_owned_draft() {
  local tag="$1"
  shift
  local reason="$*"
  OWNED_DRAFT=0
  OWNED_TAG=""
  set +e
  delete_owned_draft "$tag"
  local del_rc=$?
  set -e
  if [ "$del_rc" -ne 0 ]; then
    fail "error: rollback FAILED for $tag, manual cleanup needed"
  fi
  fail "error: $reason; rolled back draft $tag, tag kept"
}

# TERM/INT/EXIT handler: delete owned draft if still marked, then exit non-zero on signals.
# No args; uses OWNED_DRAFT / OWNED_TAG.
on_owned_draft_trap() {
  local signal="${1:-EXIT}"
  if [ "${OWNED_DRAFT}" != "1" ] || [ -z "${OWNED_TAG}" ]; then
    if [ "$signal" = "TERM" ] || [ "$signal" = "INT" ]; then
      exit 143
    fi
    return 0
  fi
  local tag="$OWNED_TAG"
  OWNED_DRAFT=0
  OWNED_TAG=""
  set +e
  delete_owned_draft "$tag"
  local del_rc=$?
  set -e
  if [ "$del_rc" -ne 0 ]; then
    echo "publish_release: error: rollback FAILED for $tag, manual cleanup needed" >&2
    exit 1
  fi
  if [ "$signal" = "TERM" ] || [ "$signal" = "INT" ]; then
    echo "publish_release: error: interrupted ($signal); deleted owned draft $tag" >&2
    exit 143
  fi
}

# Install TERM/INT/EXIT traps that roll back an owned draft.
# No args.
install_owned_draft_traps() {
  trap 'on_owned_draft_trap TERM' TERM
  trap 'on_owned_draft_trap INT' INT
  trap 'on_owned_draft_trap EXIT' EXIT
}

# Create a draft release, upload assets, then publish (edit --draft=false).
# Args: tag ref, version, notes file, three zip paths.
create_release() {
  local tag_ref="$1"
  local version="$2"
  local notes="$3"
  local lite="$4"
  local win="$5"
  local linux="$6"

  if [ -z "${GITHUB_TOKEN:-}" ] && [ -z "${GH_TOKEN:-}" ]; then
    fail "error: GITHUB_TOKEN (or GH_TOKEN) is required"
  fi
  if ! command -v gh >/dev/null 2>&1; then
    fail "error: gh is not on PATH"
  fi

  local tag
  tag=$(bare_tag "$tag_ref")
  require_release_absent "$tag"
  install_owned_draft_traps

  # Draft create without assets. --verify-tag does not create a missing tag.
  local create_out
  local create_rc
  set +e
  create_out=$(
    gh release create "$tag" \
      --draft \
      --verify-tag \
      --title "ALog v${version}" \
      --notes-file "$notes" 2>&1
  )
  create_rc=$?
  set -e
  if [ "$create_rc" -ne 0 ]; then
    if printf '%s\n' "$create_out" | grep -qi 'already exists'; then
      fail "error: release $tag already exists (created concurrently?); not rolling back"
    fi
    printf '%s\n' "$create_out" >&2
    fail "error: gh release create failed for $tag (rc=$create_rc); not rolling back"
  fi
  if [ -n "$create_out" ]; then
    printf '%s\n' "$create_out"
  fi

  OWNED_DRAFT=1
  OWNED_TAG="$tag"

  local upload_out
  local upload_rc
  set +e
  upload_out=$(gh release upload "$tag" "$lite" "$win" "$linux" 2>&1)
  upload_rc=$?
  set -e
  if [ "$upload_rc" -ne 0 ]; then
    printf '%s\n' "$upload_out" >&2
    rollback_owned_draft "$tag" "gh release upload failed for $tag (rc=$upload_rc)"
  fi
  if [ -n "$upload_out" ]; then
    printf '%s\n' "$upload_out"
  fi

  local edit_out
  local edit_rc
  set +e
  edit_out=$(gh release edit "$tag" --draft=false 2>&1)
  edit_rc=$?
  set -e
  if [ "$edit_rc" -ne 0 ]; then
    printf '%s\n' "$edit_out" >&2
    rollback_owned_draft "$tag" "gh release edit --draft=false failed for $tag (rc=$edit_rc)"
  fi
  if [ -n "$edit_out" ]; then
    printf '%s\n' "$edit_out"
  fi

  # Disarm immediately after publish succeeds so EXIT/TERM cannot delete a live release.
  OWNED_DRAFT=0
  OWNED_TAG=""
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
    fail "error: artifacts directory missing: $artifacts"
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
