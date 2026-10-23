#!/usr/bin/env bash
# Locks the S4 text contracts: one digest pin in each owning file, no saved SQL
# password, and timeouts on every job. C2-Console (A2a-65) removed the
# smoke-console continue-on-error exception, so no workflow may set that key.
# C2-03 keeps the coverage gate in the workflow only.
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
retired_comment=$(printf '%s%s' '# bis A2a-65 ' '(2a-2)')

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

# A2a-65: smoke-console is a normal job. No workflow may keep continue-on-error.
if grep -R -n -F --include='*.yml' 'continue-on-error:' .github/workflows; then
  fail "continue-on-error remains"
else
  pass "no continue-on-error"
fi
if grep -R -n -F --include='*.yml' -- "$retired_comment" .github/workflows; then
  fail "retired smoke-console exception comment"
else
  pass "retired smoke-console exception comment is gone"
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

python3 - << 'PY'
"""C2-03 checks over the working tree.

The coverage gate and COVERAGE_ENFORCE stay in the workflow. Project files and
test classes must not call or set them.
"""

import os
import sys

GATE = "coverage-gate"
ENFORCE = "COVERAGE_ENFORCE"
SKIP_DIRS = {".git", "bin", "obj", "TestResults"}
MAX_DIRS = 2000
MAX_FILES = 500
MAX_LINES = 20000


def fail(message):
    """Print one failure and stop the process.

    Args:
        message: What was wrong. Empty is rejected by the caller, not here.

    Side effects: writes stderr and exits 1. Does not return.
    """
    sys.stderr.write(message + "\n")
    sys.exit(1)


def read_text(path):
    """Read a UTF-8 text file with a line cap.

    Args:
        path: File to read. Missing files raise.

    Returns:
        The file text, including newlines.

    Raises:
        SystemExit: The file has more than MAX_LINES lines.
    """
    with open(path, encoding="utf-8") as handle:
        lines = []
        for index, line in enumerate(handle, start=1):
            if index > MAX_LINES:
                fail(f"{path} is too long")
            lines.append(line)
    return "".join(lines)


def walk_names():
    """Yield (directory, filename) pairs under the repo, skipping build output.

    Returns:
        Pairs in walk order. At most MAX_FILES files are visited.

    Raises:
        SystemExit: A directory cap, a child cap, or the file cap is exceeded.
    """
    seen_dirs = 0
    seen_files = 0
    for dirpath, dirnames, filenames in os.walk(".", followlinks=False):
        seen_dirs += 1
        if seen_dirs > MAX_DIRS:
            fail("directory cap exceeded")
        dirnames[:] = [name for name in dirnames if name not in SKIP_DIRS]
        if len(dirnames) > 500:
            fail(f"{dirpath} has too many children")
        for name in filenames:
            seen_files += 1
            if seen_files > MAX_FILES:
                fail("file cap exceeded")
            yield dirpath, name


def is_msbuild(name):
    """Return whether the file name is a project, props, or targets file.

    Args:
        name: Base name, not a path.

    Returns:
        True for the three MSBuild extensions C2-03 names.
    """
    return name.endswith(".csproj") or name.endswith(".props") or name.endswith(".targets")


def is_test_source(path, text):
    """Return whether the file holds a test class.

    Args:
        path: Used only to require a C# file.
        text: Full source. A Fact or Theory attribute marks a test class.

    Returns:
        True when the file is C# and contains a test attribute.
    """
    if not path.endswith(".cs"):
        return False
    return "[Fact" in text or "[Theory" in text


def assert_gate_stays_out(path, text):
    """Fail when a project file or test class mentions the coverage gate.

    Args:
        path: Printed in the failure. Not read again.
        text: Full file text.

    Returns:
        None when the file is clean.

    Raises:
        SystemExit: The file contains the gate script name or COVERAGE_ENFORCE.
    """
    if GATE in text or ENFORCE in text:
        fail(f"{path} calls or sets the coverage gate")


def check_projects_and_tests():
    """Walk project files and test classes for the coverage gate.

    Returns:
        None when every checked file is clean.

    Raises:
        SystemExit: A checked file mentions the gate, or the walk exceeds a cap.
    """
    checked = 0
    for dirpath, name in walk_names():
        path = os.path.join(dirpath, name)
        if is_msbuild(name):
            assert_gate_stays_out(path, read_text(path))
            checked += 1
            continue
        if not name.endswith(".cs"):
            continue
        text = read_text(path)
        if is_test_source(path, text):
            assert_gate_stays_out(path, text)
            checked += 1
    if checked < 1:
        fail("no project or test file was checked")


check_projects_and_tests()
PY
pass "coverage gate stays in CI"

python3 - << 'PY'
"""B1-01 badge job.

The badge job's condition, needs, and permissions are checked from parsed
YAML, not by counting lines. The workflow default stays exactly contents
read, and no other job grants write.
"""

import importlib.util
import sys
from pathlib import Path

WORKFLOW = ".github/workflows/dotnet.yml"


def fail(message):
    """Print one failure and stop.

    Args:
        message: What was wrong.

    Side effects: writes stderr and exits 1. Does not return.
    """
    sys.stderr.write(message + "\n")
    sys.exit(1)


def read_text(path):
    """Read a UTF-8 file with a line cap.

    Args:
        path: File to read.

    Returns:
        The file text.

    Raises:
        SystemExit: The file has more than 20000 lines.
    """
    with open(path, encoding="utf-8") as handle:
        lines = []
        for index, line in enumerate(handle, start=1):
            if index > 20000:
                fail(f"{path} is too long")
            lines.append(line)
    return "".join(lines)


def is_job_header(line):
    """Return whether a line starts a workflow job.

    Args:
        line: One workflow line, without the trailing newline.

    Returns:
        True for a two-space key such as ``  build:``.
    """
    if not line.startswith("  ") or line.startswith("   "):
        return False
    name = line.strip()
    if not name.endswith(":"):
        return False
    key = name[:-1]
    return key != "" and " " not in key


def split_jobs(text):
    """Split a workflow into the preamble and one body per job.

    Args:
        text: Full workflow YAML.

    Returns:
        The text before ``jobs:``, then a map of job name to body text.

    Raises:
        SystemExit: The jobs section is missing or a job is malformed.
    """
    lines = text.splitlines()
    jobs_at = None
    for index, line in enumerate(lines):
        if line == "jobs:":
            jobs_at = index
            break
    if jobs_at is None:
        fail("jobs section is missing")
    preamble = "\n".join(lines[:jobs_at])
    jobs = {}
    current = None
    body = []
    for line in lines[jobs_at + 1:]:
        if is_job_header(line):
            if current is not None:
                jobs[current] = "\n".join(body)
            current = line.strip()[:-1]
            body = []
            continue
        if current is None:
            fail(f"unexpected line before the first job: {line}")
        if line and not line.startswith(" ") and not line.startswith("#"):
            fail(f"job {current} ended at {line}")
        body.append(line)
        if len(body) > 2000:
            fail(f"job {current} is too long")
    if current is None:
        fail("workflow has no jobs")
    jobs[current] = "\n".join(body)
    if len(jobs) > 20:
        fail("workflow has more than 20 jobs")
    return preamble, jobs


def load_badge_checker():
    """Load the structural B1-01 checker.

    Returns:
        The badge_workflow_check module.

    Raises:
        SystemExit: The checker cannot be loaded.
    """
    path = "eng/badge_workflow_check.py"
    spec = importlib.util.spec_from_file_location("badge_workflow_check", path)
    if spec is None or spec.loader is None:
        fail("badge workflow checker is missing")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def check_workflow():
    """Lock B1-01 from parsed YAML, plus the trigger text.

    Returns:
        None.

    Raises:
        SystemExit: The badge job, its permissions, or the trigger is wrong.
    """
    checker = load_badge_checker()
    try:
        document = checker.load_workflow(Path(WORKFLOW))
    except (OSError, ValueError) as error:
        fail(f"workflow cannot be parsed: {error}")
    for problem in checker.check_badge_workflow(document):
        fail(problem)
    text = read_text(WORKFLOW)
    preamble, _jobs = split_jobs(text)
    if "badges" in preamble:
        fail("workflow triggers mention badges")
    if preamble.count('branches: [ "master" ]') != 2:
        fail("push and pull_request must both target only master")
    if "--force" in text or "push -f" in text:
        fail("workflow contains a force push")


check_workflow()
PY
python3 eng/test_badge_workflow.py
pass "B1 badge job"

if [ "$failures" -ne 0 ]; then
  echo "${failures} s4 grep tests failed" >&2
  exit 1
fi
echo "s4 grep tests passed"
