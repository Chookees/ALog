#!/usr/bin/env bash
# Locks the S4 text contracts: one digest pin in each owning file, no saved SQL
# password, timeouts on every job, and the smoke job's continue-on-error comment.
# Forbidden strings are built at runtime so this file does not contain them.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

failures=0

# Prints a passing check name. Args: label. Returns 0.
pass() {
  printf 'ok %s\n' "$1"
}

# Records a failure and keeps going. Args: label. Returns 0.
fail() {
  echo "FAIL $1" >&2
  failures=$((failures + 1))
}

# Prints file names that contain the fixed string, skipping build output.
# Args: the fixed string. Returns 0 even when nothing matches.
matching_files() {
  local needle="$1"
  grep -R -l -F \
    --exclude-dir=.git \
    --exclude-dir=bin \
    --exclude-dir=obj \
    --exclude-dir=TestResults \
    -- "$needle" . || true
}

# Requires the needle to be absent. Args: label, needle. Returns 0.
require_absent() {
  local label="$1"
  local needle="$2"
  local hits
  hits=$(matching_files "$needle")
  if [ -n "$hits" ]; then
    echo "FAIL ${label} found in:" >&2
    printf '%s\n' "$hits" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

# Requires the needle once in the file. Args: label, file, needle. Returns 0.
require_once() {
  local label="$1"
  local file="$2"
  local needle="$3"
  local count
  count=$(grep -o -F -- "$needle" "$file" | grep -c . || true)
  if [ "$count" -ne 1 ]; then
    echo "FAIL ${label}: count ${count} in ${file}" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

workflow=".github/workflows/dotnet.yml"
sql_test="ALog.Tests/SqlLiveIntegrationTests.cs"
marker=$(printf '%s%s' '@' 'sha256:')
password=$(printf '%s%s%s' 'Your_' 'strong_' 'Password123')
moving_tag=$(printf '%s%s' ':2022-' 'latest')
secret_assignment=$(printf '%s%s' 'MSSQL_SA_PASSWORD' ':')
comment='# bis A2a-65 (2a-2)'

require_absent "saved SQL password" "$password"
require_absent "moving SQL tag" "$moving_tag"
require_absent "SQL password assignment" "$secret_assignment"

pin_files=$(matching_files "$marker" | LC_ALL=C sort)
expected=$(printf '%s\n%s\n' "./${workflow}" "./${sql_test}" | LC_ALL=C sort)
if [ "$pin_files" != "$expected" ]; then
  echo "FAIL digest marker files:" >&2
  printf '%s\n' "$pin_files" >&2
  failures=$((failures + 1))
else
  pass "digest marker only in the two pin files"
fi
require_once "workflow digest" "$workflow" "$marker"
require_once "test digest" "$sql_test" "$marker"

workflow_digest=$(grep -o -E 'sha256:[0-9a-f]{64}' "$workflow")
test_digest=$(grep -o -E 'sha256:[0-9a-f]{64}' "$sql_test")
if [ "$workflow_digest" != "$test_digest" ] || [ -z "$workflow_digest" ]; then
  fail "workflow and test digests differ"
else
  pass "workflow and test digests match"
fi

job_count=$(awk '
  /^jobs:/ { in_jobs = 1; next }
  in_jobs && /^[^ ]/ { in_jobs = 0 }
  in_jobs && /^  [A-Za-z0-9_-]+:$/ { count++ }
  END { print count + 0 }
' "$workflow")
timeout_count=$(grep -c '^    timeout-minutes:' "$workflow" || true)
if [ "$job_count" -lt 1 ] || [ "$job_count" -ne "$timeout_count" ]; then
  fail "timeout count ${timeout_count} != job count ${job_count}"
else
  pass "every job has a timeout"
fi
if ! grep -F -q 'timeout-minutes: 30' "$workflow"; then
  fail "build job timeout is not 30"
else
  pass "build timeout 30"
fi
if ! grep -F -q -- '--blame-hang-timeout 5m' "$workflow"; then
  fail "hang timeout missing"
else
  pass "hang timeout"
fi
if ! grep -F -q -- '--blame-hang-dump-type mini' "$workflow"; then
  fail "hang dump type missing"
else
  pass "hang dump type"
fi
# Z1 turns the line gate on. Extensions lines are above 90 with SQL and without it.
if ! grep -F -q 'COVERAGE_ENFORCE: "true"' "$workflow"; then
  fail "coverage enforce is not true"
else
  pass "coverage enforce true"
fi
if grep -E -q 'COVERAGE_ENFORCE[=:][[:space:]]*"?false' "$workflow"; then
  fail "coverage enforce false is present"
else
  pass "coverage enforce false absent"
fi

continue_count=$(grep -c -F 'continue-on-error:' "$workflow" || true)
if [ "$continue_count" -ne 1 ]; then
  fail "continue-on-error count ${continue_count}"
else
  pass "one continue-on-error"
fi
if ! grep -F -q "continue-on-error: true ${comment}" "$workflow"; then
  fail "smoke comment missing"
else
  pass "smoke comment"
fi

consumer="eng/consumer-smoke/console/ALog.ConsumerSmoke.csproj"
if ! grep -F -q 'ALogSmokeFromFeed' "$consumer"; then
  fail "consumer smoke switch missing"
else
  pass "consumer smoke switch"
fi
if ! grep -F -q 'ProjectReference' "$consumer"; then
  fail "consumer project reference missing"
else
  pass "consumer project reference for the normal build"
fi
if ! grep -F -q 'ALog.ConsumerSmoke' ALog.sln; then
  fail "consumer is not in the solution"
else
  pass "consumer is in the solution"
fi
if ! grep -F -q 'ALOG_REQUIRE_SQL: "1"' "$workflow"; then
  fail "ALOG_REQUIRE_SQL is not set"
else
  pass "ALOG_REQUIRE_SQL=1"
fi
if ! grep -F -q 'smoke-file:' "$workflow" || ! grep -F -q 'smoke-console:' "$workflow"; then
  fail "smoke jobs missing"
else
  pass "smoke-file and smoke-console"
fi
if ! grep -F -q 'eng/coverage-exception-check.py' "$workflow"; then
  fail "coverage exception check missing"
else
  pass "coverage exception check"
fi
if ! grep -F -q 'GITHUB_EVENT_NAME: ${{ github.event_name }}' "$workflow"; then
  fail "coverage gate event name missing"
else
  pass "coverage gate reads the event name"
fi
if ! grep -F -q '/eng/consumer-smoke/console' .github/dependabot.yml; then
  fail "dependabot consumer directory missing"
else
  pass "dependabot consumer directory"
fi
ssh_comment='SSH.NET 2026.0.0: GHSA-q939-rpr3-3284 (CVE-2026-48798), GHSA-mggc-4xg6-vcxf (CVE-2026-85756) (High), transitiv über Testcontainers. Entfernen, sobald Testcontainers eine Version mit SSH.NET >= 2026.0.0 mitbringt.'
if ! grep -F -q "$ssh_comment" ALog.Tests/ALog.Tests.csproj; then
  fail "SSH.NET comment missing"
else
  pass "SSH.NET comment"
fi
if ! grep -F -q 'SMOKE-PACKAGE ALog' eng/smoke-assets-check.py; then
  fail "package proof line missing"
else
  pass "package proof line"
fi
if ! grep -F -q 'SMOKE-CONSOLE count=' eng/consumer-smoke.sh; then
  fail "console count line missing"
else
  pass "console count line"
fi

push_cmd=$(printf '%s %s' 'nuget' 'push')
if grep -R -n -F --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj -- "$push_cmd" .github >/dev/null; then
  fail "nuget push is in a workflow"
else
  pass "no nuget push"
fi

python3 - << 'PY'
import os
import sys
codes = ("NU1901", "NU1902", "NU1903", "NU1904")
properties = ("NoWarn", "WarningsNotAsErrors")
skipped = {".git", "bin", "obj", "TestResults"}
bad = []
seen_files = 0
seen_dirs = 0
for dirpath, dirnames, filenames in os.walk(".", followlinks=False):
    seen_dirs += 1
    if seen_dirs > 2000:
        sys.stderr.write("directory cap exceeded\n")
        sys.exit(1)
    dirnames[:] = [name for name in dirnames if name not in skipped]
    if len(dirnames) > 500:
        sys.stderr.write(f"{dirpath} has too many children\n")
        sys.exit(1)
    for name in filenames:
        if not (name.endswith(".csproj") or name.endswith(".props") or name == ".editorconfig"):
            continue
        seen_files += 1
        if seen_files > 500:
            sys.stderr.write("file cap exceeded\n")
            sys.exit(1)
        path = os.path.join(dirpath, name)
        with open(path, encoding="utf-8") as handle:
            for index, line in enumerate(handle, start=1):
                if index > 20000:
                    sys.stderr.write(f"{path} is too long\n")
                    sys.exit(1)
                if not any(prop in line for prop in properties):
                    continue
                if any(code in line for code in codes):
                    bad.append(f"{path}:{index}")
if bad:
    sys.stderr.write("audit codes suppressed:\n" + "\n".join(bad) + "\n")
    sys.exit(1)
PY
pass "no audit suppression"

if [ "$failures" -ne 0 ]; then
  echo "${failures} s4 grep tests failed" >&2
  exit 1
fi
echo "s4 grep tests passed"
