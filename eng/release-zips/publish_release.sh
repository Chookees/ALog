#!/usr/bin/env bash
# Create a GitHub release for a version tag from CI artifacts of this run.
#
# Expects exactly three zip assets whose names are derived from the tag version.
# SHA256SUMS may appear in the notes text but is never uploaded as a fourth asset.
# Aborts when the release already exists (no --clobber). Uses gh + GITHUB_TOKEN.
#
# Flow: create a --draft release (no assets) → upload zips → gh release edit
# --draft=false. OWNED_DRAFT is set only after our create succeeds and cleared
# immediately after publish edit succeeds. A TERM/INT/EXIT trap rolls back while
# OWNED_DRAFT is set, but only after `gh release view --json isDraft` prints
# true (never delete a published release). A draft can remain only after
# SIGKILL, runner loss, or a signal right after create. Concurrent "already
# exists" and other create failures never delete.
#
# Usage:
#   bash eng/release-zips/publish_release.sh <tag-ref> <artifacts-dir>
#
# artifacts-dir must contain the three zips (and preferably SHA256SUMS).

set -euo pipefail

# Set when this run successfully created a draft release; cleared after publish.
OWNED_DRAFT=0
OWNED_TAG=""
# mktemp notes path from write_notes; removed by EXIT / signal traps.
NOTES_FILE=""

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

# Remove the write_notes mktemp file when set.
# No args; uses NOTES_FILE.
cleanup_notes_file() {
  if [ -n "${NOTES_FILE:-}" ]; then
    rm -f "$NOTES_FILE"
    NOTES_FILE=""
  fi
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
# Args: repo, version, three zip paths, optional SHA256SUMS path, notes out path.
# Writes the notes file in place (caller owns mktemp / NOTES_FILE / cleanup).
write_notes() {
  local repo="$1"
  local version="$2"
  local lite="$3"
  local win="$4"
  local linux="$5"
  local sums="${6:-}"
  local notes="$7"
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

# Trim CR and leading/trailing whitespace from one isDraft token.
# Args: raw stdout text. Prints the trimmed token with no trailing newline logic
# beyond printf %s.
trim_is_draft_token() {
  local out="$1"
  out="${out//$'\r'/}"
  out="${out#"${out%%[![:space:]]*}"}"
  out="${out%"${out##*[![:space:]]}"}"
  printf '%s' "$out"
}

# Read isDraft for a tag via gh. Prints exactly true or false on success.
# Args: bare tag name.
# Returns: 0 when trimmed stdout is exactly true or false; non-zero on gh
# failure or unexpected stdout. stderr is logged separately and never mixed
# into the parsed value. Keeps errexit off across non-zero returns.
query_release_is_draft() {
  local tag="$1"
  local out
  local rc
  local err_file
  err_file="${TMPDIR:-/tmp}/publish-isdraft-err.$$"
  set +e
  # stdout only into out; stderr to a side file then the script log.
  out=$(gh release view "$tag" --json isDraft -q .isDraft 2>"$err_file")
  rc=$?
  if [ -s "$err_file" ]; then
    cat "$err_file" >&2
  fi
  rm -f "$err_file"
  if [ "$rc" -ne 0 ]; then
    return 1
  fi
  out=$(trim_is_draft_token "$out")
  case "$out" in
    true|false)
      printf '%s\n' "$out"
      return 0
      ;;
  esac
  return 1
}

# Delete an owned draft only when gh reports isDraft=true.
# Args: bare tag name.
# Returns:
#   0 — deleted successfully
#   1 — isDraft=false (already published; delete skipped)
#   2 — view failed or unexpected (could not read draft state)
#   3 — delete failed after isDraft=true
# Errexit stays off so a non-zero return does not abort the caller mid-case.
# Only an exact trimmed ``true`` triggers delete; never garbage/empty/null.
try_rollback_delete() {
  local tag="$1"
  local draft
  local q_rc
  local del_rc
  set +e
  draft=$(query_release_is_draft "$tag")
  q_rc=$?
  if [ "$q_rc" -ne 0 ]; then
    return 2
  fi
  if [ "$draft" = "true" ]; then
    delete_owned_draft "$tag"
    del_rc=$?
    if [ "$del_rc" -ne 0 ]; then
      return 3
    fi
    return 0
  fi
  if [ "$draft" = "false" ]; then
    echo "publish_release: $tag is already published, rollback skipped" >&2
    return 1
  fi
  return 2
}

# Roll back an owned draft after upload/edit failure.
# Args: bare tag name, reason words for a successful delete path.
# Always checks isDraft before delete. On isDraft=false stays red without delete.
rollback_owned_draft() {
  local tag="$1"
  shift
  local reason="$*"
  local rb_rc
  OWNED_DRAFT=0
  OWNED_TAG=""
  set +e
  try_rollback_delete "$tag"
  rb_rc=$?
  set -e
  case "$rb_rc" in
    0)
      fail "error: $reason; rolled back draft $tag, tag kept"
      ;;
    1)
      fail "error: $reason"
      ;;
    2)
      fail "error: rollback FAILED for $tag, manual cleanup needed (could not read draft state)"
      ;;
    *)
      fail "error: rollback FAILED for $tag, manual cleanup needed"
      ;;
  esac
}

# TERM/INT/EXIT handler: roll back owned draft only when still a draft.
# Args: signal name (TERM, INT, or EXIT). Uses OWNED_DRAFT / OWNED_TAG.
# Always removes NOTES_FILE. EXIT preserves a prior non-zero exit status.
# TERM/INT callers must ignore nested TERM/INT before invoking this function.
on_owned_draft_trap() {
  # Capture before any other command; `local` would reset $?.
  local prev_rc=$?
  local signal="${1:-EXIT}"
  if [ "${OWNED_DRAFT}" != "1" ] || [ -z "${OWNED_TAG}" ]; then
    cleanup_notes_file
    if [ "$signal" = "TERM" ] || [ "$signal" = "INT" ]; then
      exit 143
    fi
    if [ "$prev_rc" -ne 0 ]; then
      exit "$prev_rc"
    fi
    return 0
  fi
  local tag="$OWNED_TAG"
  local rb_rc
  OWNED_DRAFT=0
  OWNED_TAG=""
  set +e
  try_rollback_delete "$tag"
  rb_rc=$?
  set -e
  cleanup_notes_file
  case "$rb_rc" in
    0)
      if [ "$signal" = "TERM" ] || [ "$signal" = "INT" ]; then
        echo "publish_release: error: interrupted ($signal); deleted owned draft $tag" >&2
        exit 143
      fi
      return 0
      ;;
    1)
      if [ "$signal" = "TERM" ] || [ "$signal" = "INT" ]; then
        exit 143
      fi
      if [ "$prev_rc" -ne 0 ]; then
        exit "$prev_rc"
      fi
      return 0
      ;;
    2)
      echo "publish_release: error: rollback FAILED for $tag, manual cleanup needed (could not read draft state)" >&2
      exit 1
      ;;
    *)
      echo "publish_release: error: rollback FAILED for $tag, manual cleanup needed" >&2
      exit 1
      ;;
  esac
}

# TERM trap entry: ignore further TERM/INT, then roll back if needed.
# No args.
on_term_trap() {
  trap '' TERM INT
  on_owned_draft_trap TERM
}

# INT trap entry: ignore further TERM/INT, then roll back if needed.
# No args.
on_int_trap() {
  trap '' TERM INT
  on_owned_draft_trap INT
}

# Install TERM/INT/EXIT traps that roll back an owned draft (isDraft-gated).
# No args. Exports PUBLISH_RELEASE_PID so helpers can signal this process.
install_owned_draft_traps() {
  export PUBLISH_RELEASE_PID=$$
  trap 'on_term_trap' TERM
  trap 'on_int_trap' INT
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

  # Install notes cleanup BEFORE mktemp so SIGTERM during write_notes still
  # removes the file. NOTES_FILE is set in this shell (not a $(…) subshell).
  trap 'rc=$?; cleanup_notes_file; exit "$rc"' EXIT
  NOTES_FILE=$(mktemp)
  write_notes "$repo" "$version" "$lite" "$win" "$linux" "$sums" "$NOTES_FILE"
  create_release "$tag_ref" "$version" "$NOTES_FILE" "$lite" "$win" "$linux"
}

main "$@"
