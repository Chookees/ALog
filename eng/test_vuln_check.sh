#!/usr/bin/env bash
# Tests the vulnerability-report text check without calling dotnet.
# The phrase that fails the gate is the one arc_spec 4.1 point 2 names.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=vuln-check.sh
source "$here/vuln-check.sh"

failures=0

pass() {
  printf 'ok %s\n' "$1"
}

# Args: label. A non-zero status from the previous command is a failure.
expect_fail() {
  local label="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    echo "FAIL ${label}: expected a non-zero status" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

expect_ok() {
  local label="$1"
  shift
  if ! "$@" >/dev/null 2>&1; then
    echo "FAIL ${label}" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

clean_report=$'Project `ALog` has no vulnerable packages\n'
dirty_report=$'Project `ALog` has the following vulnerable packages\n   > System.Security.Cryptography.Pkcs  9.0.4  High\n'

expect_ok "clean report" report_is_clean "$clean_report"
expect_fail "vulnerable sentence" report_is_clean "$dirty_report"
expect_fail "empty report" report_is_clean ""
expect_fail "whitespace report" report_is_clean $' \n\t'

if [ "$failures" -ne 0 ]; then
  echo "${failures} vuln-check tests failed" >&2
  exit 1
fi
echo "vuln-check tests passed"
