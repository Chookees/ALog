#!/usr/bin/env bash
# Tag-push guard for release-zips publish.
#
# Aborts when:
#   1) the tagged commit is not an ancestor of origin/master, or
#   2) the tag version (without leading v) differs from <Version> in both
#      ALog.csproj and ALog.Extensions.Logging.csproj.
#
# Requires a checkout with enough history to fetch origin/master.
# Usage: bash eng/release-zips/guard_tag.sh <tag-ref> [commit-sha]
# Next: .github/workflows/release-zips.yml publish job.

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
  echo "guard_tag: $*" >&2
  exit 1
}

# Ensure the tagged commit is on origin/master.
# Args: repo, commit sha.
require_on_master() {
  local repo="$1"
  local sha="$2"
  git -C "$repo" fetch --no-tags origin master
  if ! git -C "$repo" merge-base --is-ancestor "$sha" origin/master; then
    fail "tagged commit $sha is not an ancestor of origin/master"
  fi
  echo "guard: $sha is on origin/master"
}

# Ensure the tag version equals both package <Version> values.
# Args: repo, tag ref.
require_version_match() {
  local repo="$1"
  local tag_ref="$2"
  python3 - "$repo" "$tag_ref" <<'PY'
import sys
from pathlib import Path

repo = Path(sys.argv[1])
tag_ref = sys.argv[2]
sys.path.insert(0, str(repo / "eng" / "release-zips"))
from release_lib import parse_tag_version, require_matching_package_versions

tag_version = parse_tag_version(tag_ref)
package_version = require_matching_package_versions(repo)
if tag_version != package_version:
    raise SystemExit(
        f"tag version {tag_version} does not match package Version {package_version}"
    )
print(f"guard: tag {tag_version} matches package Version")
PY
}

# CLI entry.
# Args: tag-ref, optional commit sha (defaults to HEAD).
main() {
  if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    echo "Usage: guard_tag.sh <tag-ref> [commit-sha]" >&2
    exit 2
  fi
  local repo
  repo=$(repo_root)
  local tag_ref="$1"
  local sha="${2:-}"
  if [ -z "$sha" ]; then
    sha=$(git -C "$repo" rev-parse HEAD)
  fi
  require_on_master "$repo" "$sha"
  require_version_match "$repo" "$tag_ref"
}

main "$@"
