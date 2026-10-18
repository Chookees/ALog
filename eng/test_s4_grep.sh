#!/usr/bin/env bash
# Locks the S4 text contracts: one digest pin in each owning file, no saved SQL
# password, and timeouts on every job. C2-Console (A2a-65) removed the
# smoke-console continue-on-error exception, so no workflow may set that key.
# C2-03 keeps the coverage gate in the workflow only. U-C2-03 checks README fences.
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
"""C2-03 and U-C2 checks over the working tree.

The coverage gate and COVERAGE_ENFORCE stay in the workflow. Project files and
test classes must not call or set them. README fences that call AddALog must
import ALog.Extensions.Logging, and the ASP.NET Core sample starts with that using.
"""

import os
import sys

GATE = "coverage-gate"
ENFORCE = "COVERAGE_ENFORCE"
USING = "using ALog.Extensions.Logging;"
CONSOLE_TEXT = (
    "ALog writes to the console only when `ALog:Console:Enabled` is `true`. "
    "With `AddALog`, choose one console: keep the Microsoft console provider and "
    "leave ALog's console off, or call `builder.Logging.ClearProviders()` and set "
    "`ALog:Console:Enabled` to `true` (as in the Quickstart). With both, every line appears twice."
)
CHANGELOG_LINE = (
    "- Breaking (behavior): ALog writes to the console only when `ALog:Console:Enabled` is `true`. "
    "Before, `AddALog` added its own console output next to the Microsoft console provider, "
    "so every line appeared twice."
)
SKIP_DIRS = {".git", "bin", "obj", "TestResults"}
MAX_DIRS = 2000
MAX_FILES = 500
MAX_LINES = 20000
MAX_READMES = 20


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


def fenced_blocks(text, path):
    """Return the inner text of each fenced block.

    Args:
        text: Markdown source.
        path: Used in the error when a fence is not closed.

    Returns:
        A list of block bodies, without the fence lines. Order is source order.

    Raises:
        SystemExit: A fence has no closing line.
    """
    blocks = []
    current = None
    for line in text.splitlines():
        if line.startswith("```"):
            if current is None:
                current = []
            else:
                blocks.append("\n".join(current))
                current = None
            continue
        if current is not None:
            if len(current) > MAX_LINES:
                fail(f"{path} fence is too long")
            current.append(line)
    if current is not None:
        fail(f"{path} has an unclosed fence")
    return blocks


def section_body(text, heading):
    """Return the body under one heading, until the next heading of the same rank.

    Args:
        text: Markdown source.
        heading: Full heading line, including the leading hash marks.

    Returns:
        The body, or None when the heading is missing. The heading line itself
        is not included.
    """
    lines = text.splitlines()
    rank = len(heading) - len(heading.lstrip("#"))
    start = None
    for index, line in enumerate(lines):
        if line.strip() == heading:
            start = index + 1
            break
    if start is None:
        return None
    body = []
    for line in lines[start:]:
        stripped = line.strip()
        if stripped.startswith("#") and not stripped.startswith("#" * (rank + 1)):
            mark = len(stripped) - len(stripped.lstrip("#"))
            if mark <= rank and stripped[mark:mark + 1] == " ":
                break
        body.append(line)
    return "\n".join(body)


def first_code_line(block):
    """Return the first non-empty line of a fence body.

    Args:
        block: Fence body. Empty yields an empty string.

    Returns:
        The line with surrounding spaces removed, or empty when the block is blank.
    """
    for line in block.splitlines():
        if line.strip():
            return line.strip()
    return ""


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


def check_readmes():
    """Check AddALog fences, the ASP.NET Core using, and the C2 texts.

    Returns:
        None when the texts and fences match.

    Raises:
        SystemExit: A required line is missing, duplicated, or a fence lacks the using.
    """
    readme_paths = []
    for dirpath, name in walk_names():
        if name != "README.md":
            continue
        readme_paths.append(os.path.join(dirpath, name))
        if len(readme_paths) > MAX_READMES:
            fail("too many README files")
    if "./README.md" not in readme_paths and "README.md" not in readme_paths:
        fail("root README.md is missing")
    root = "README.md"
    root_text = read_text(root)
    if root_text.count(CONSOLE_TEXT) != 1:
        fail(f"console output text count is {root_text.count(CONSOLE_TEXT)}")
    migrating = section_body(root_text, "## Migrating from 0.2.0")
    if migrating is None:
        fail("migrating section is missing")
    if "C2-Console" not in migrating or CHANGELOG_LINE not in migrating:
        fail("migrating section is missing the C2-Console line")
    installation = section_body(root_text, "## Installation")
    if installation is None:
        fail("installation section is missing")
    aspnet = section_body(installation, "### ASP.NET Core")
    if aspnet is None:
        fail("ASP.NET Core section is missing")
    aspnet_blocks = fenced_blocks(aspnet, "README.md ASP.NET Core")
    if not aspnet_blocks:
        fail("ASP.NET Core section has no code block")
    if first_code_line(aspnet_blocks[0]) != USING:
        fail("ASP.NET Core code block does not start with the ALog using")
    for path in readme_paths:
        blocks = fenced_blocks(read_text(path), path)
        for block in blocks:
            if "AddALog" not in block:
                continue
            if USING not in block:
                fail(f"{path} has an AddALog block without the ALog using")
    changelog = read_text("CHANGELOG.md")
    if changelog.count(CHANGELOG_LINE) != 1:
        fail(f"changelog line count is {changelog.count(CHANGELOG_LINE)}")


check_projects_and_tests()
check_readmes()
PY
pass "coverage gate stays in CI and README AddALog blocks import ALog"

if [ "$failures" -ne 0 ]; then
  echo "${failures} s4 grep tests failed" >&2
  exit 1
fi
echo "s4 grep tests passed"
