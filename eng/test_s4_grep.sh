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

# Empty runs the repository sweep. Set when --m1-fail-lines names one file.
m1_fail_lines_file=""
if [ "${1:-}" = "--m1-fail-lines" ]; then
  if [ "$#" -ne 2 ]; then
    echo "FAIL M1-02 --m1-fail-lines needs one file" >&2
    exit 2
  fi
  m1_fail_lines_file="$2"
fi

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

# The line-number mode prints pins for one file and does not sweep the repo.
if [ -z "$m1_fail_lines_file" ]; then

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
    "`ALog:Console:Enabled` to `true` (as in `samples/ALog.AspNetSample`). With both, every line appears twice."
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

python3 - << 'PY'
"""B1-01 and the README badge line.

The badge job's condition, needs, and permissions are checked from parsed
YAML, not by counting lines. The workflow default stays exactly contents
read, and no other job grants write. Both READMEs carry CI, Coverage,
License, and .NET 10 in that order directly under the H1. The repository
README also keeps GitHub downloads.
"""

import importlib.util
import sys
from pathlib import Path

WORKFLOW = ".github/workflows/dotnet.yml"
ACTIONS = "https://github.com/Chookees/ALog/actions/workflows/dotnet.yml"
LICENSE = "https://github.com/Chookees/ALog/blob/master/LICENSE"
CI_IMAGE = "https://img.shields.io/github/actions/workflow/status/Chookees/ALog/dotnet.yml?branch=master&label=CI"
NET_IMAGE = "https://img.shields.io/badge/.NET-10-512BD4"
DOWNLOADS = "https://img.shields.io/github/downloads/chookees/alog/total"
RELEASES = "https://github.com/Chookees/ALog/releases"
SOURCEFORGE = (
    "[![Download ALog](https://a.fsdn.com/con/app/sf-download-button)]"
    "(https://sourceforge.net/projects/alog/files/latest/download)"
)
ROOT_HEADING = "# ALog – A Modern, Modular, Cross-Platform Logger for .NET 10+"
EXT_HEADING = "# ALog.Extensions.Logging"


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


def assert_order(line, markers, label):
    """Require each marker to appear, left to right.

    Args:
        line: One README line.
        markers: Substrings in the required order. At most 10.
        label: Name used in the failure.

    Returns:
        None.

    Raises:
        SystemExit: A marker is missing or out of order.
    """
    if len(markers) > 10:
        fail("too many badge markers")
    position = -1
    for marker in markers:
        found = line.find(marker)
        if found < 0:
            fail(f"{label} is missing {marker}")
        if found <= position:
            fail(f"{label} has {marker} out of order")
        position = found


def line_under(text, heading):
    """Return the line immediately under a heading.

    Args:
        text: Markdown source.
        heading: Full heading line.

    Returns:
        The following line.

    Raises:
        SystemExit: The heading is missing or is the last line.
    """
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line == heading:
            if index + 1 >= len(lines):
                fail(f"no line under {heading}")
            return lines[index + 1]
    fail(f"missing heading {heading}")
    return ""


def next_nonempty(text, heading):
    """Return the first non-empty line after the line under a heading.

    Args:
        text: Markdown source.
        heading: Full heading line.

    Returns:
        That line.

    Raises:
        SystemExit: No such line exists.
    """
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line != heading:
            continue
        for follower in lines[index + 2:]:
            if follower.strip() != "":
                return follower
        fail(f"no paragraph under {heading}")
    fail(f"missing heading {heading}")
    return ""


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


def badge_markers(coverage_file):
    """Return the shared badge markers plus one coverage file.

    Args:
        coverage_file: SVG file name on the badges branch.

    Returns:
        Markers in README order, without the GitHub downloads badge.
    """
    coverage = (
        "[![Coverage](https://raw.githubusercontent.com/Chookees/ALog/badges/"
        + coverage_file
        + ")]("
        + ACTIONS
        + ")"
    )
    return [
        "[![CI](" + CI_IMAGE + ")](" + ACTIONS + ")",
        coverage,
        "[![License: MIT](https://img.shields.io/badge/License-MIT-blue)](" + LICENSE + ")",
        "![.NET 10](" + NET_IMAGE + ")",
    ]


def check_readmes():
    """Lock the badge line on both READMEs.

    Returns:
        None.

    Raises:
        SystemExit: Order, alt text, link, or a forbidden badge is wrong.
    """
    root = read_text("README.md")
    extensions = read_text("ALog.Extensions.Logging/README.md")
    root_badges = line_under(root, ROOT_HEADING)
    ext_badges = line_under(extensions, EXT_HEADING)
    root_markers = badge_markers("coverage-ALog.svg")
    root_markers.append("[![GitHub downloads](" + DOWNLOADS + ")](" + RELEASES + ")")
    assert_order(root_badges, root_markers, "root README")
    assert_order(ext_badges, badge_markers("coverage-ALog.Extensions.Logging.svg"), "extensions README")
    if "[![.NET 10]" in root_badges or "[![.NET 10]" in ext_badges:
        fail(".NET 10 badge must not be a link")
    if "GitHub downloads" in ext_badges or "sourceforge" in extensions.lower():
        fail("extensions README has a repository-only badge")
    if "coverage-ALog.Extensions.Logging.svg" in root_badges:
        fail("root README points at the extensions coverage badge")
    if "coverage-ALog.svg)" in ext_badges:
        fail("extensions README points at the ALog coverage badge")
    if next_nonempty(root, ROOT_HEADING) != SOURCEFORGE:
        fail("SourceForge button is not under the badge line")
    if "GitHub Downloads (all assets, all releases)" in root:
        fail("old GitHub downloads alt text remains")
    for label, text in (("root", root), ("extensions", extensions)):
        if "img.shields.io/nuget" in text or "codecov.io" in text or "gist.github" in text:
            fail(f"{label} README links a service that is not in the badge set")
        if "[![NuGet" in text or "NuGet downloads" in text:
            fail(f"{label} README has a NuGet badge")
    changelog = read_text("CHANGELOG.md")
    if "coverage-ALog.svg" in changelog or "img.shields.io" in changelog:
        fail("CHANGELOG mentions badges")


check_workflow()
check_readmes()
PY
python3 eng/test_badge_workflow.py
pass "B1 badge job and README badges"

fi

# M1-02. Reports which install pin a line uses.
# Args: one line. Prints --version, -v, PackageReference, or VersionOverride
# when the line names ALog in any case and pins a version. Prints nothing otherwise.
# Returns 0. A verbosity -v that is not followed by a digit is not a pin.
# --prerelease is not --version. VersionOverride is its own form.
# Include accepts either quote, spaces around =, and ALog in any case.
m1_readme_version_hit() {
  local line="$1"
  if ! printf '%s\n' "$line" | grep -Eiq 'alog'; then
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eiq -- '--version[[:space:]]+[0-9]'; then
    printf '%s\n' '--version'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eiq -- '--version=[[:space:]]*[0-9]'; then
    printf '%s\n' '--version'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eiq -- '--version[[:space:]]+\[[[:space:]]*[0-9]'; then
    printf '%s\n' '--version'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eq 'dotnet add package' \
      && printf '%s\n' "$line" | grep -Eq '(^|[[:space:]])-v[[:space:]]+[0-9]'; then
    printf '%s\n' '-v'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eq 'Install-Package' \
      && printf '%s\n' "$line" | grep -Eiq -- '(^|[[:space:]])-version[[:space:]]+[0-9]'; then
    printf '%s\n' 'Install-Package'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eq '<PackageReference([[:space:]]|>)' \
      && printf '%s\n' "$line" | grep -Eq 'Include[[:space:]]*=[[:space:]]*['"'"'"][Aa][Ll][Oo][Gg][^'"'"'"]*' \
      && printf '%s\n' "$line" | grep -Eq 'Version[[:space:]]*=[[:space:]]*["'"'"'][0-9]'; then
    printf '%s\n' 'PackageReference'
    return 0
  fi
  if printf '%s\n' "$line" | grep -Eq 'VersionOverride[[:space:]]*=[[:space:]]*["'"'"'][0-9]'; then
    printf '%s\n' 'VersionOverride'
    return 0
  fi
  return 0
}

# M1-02. Scans one README for install pins.
# Args: path. Prints one "reason<TAB>line" per hit, at most 2000 lines.
# Returns 0. A file past the cap is a failure and the rest of that file is skipped.
m1_scan_readme() {
  local file="$1"
  local number=0
  local line reason
  while IFS= read -r line || [ -n "$line" ]; do
    number=$((number + 1))
    if [ "$number" -gt 2000 ]; then
      fail "M1-02 ${file} has more than 2000 lines"
      return 0
    fi
    reason=$(m1_readme_version_hit "$line")
    if [ -n "$reason" ]; then
      printf '%s\t%s\n' "$reason" "$line"
    fi
  done < "$file"
}

# M1-02. Prints one fail line per pin: file, line number, then the line text.
# Args: path. Prints "FAIL M1-02 pin in <file>:<number>: <text>" to stdout.
# Returns 0 when the file has no pin, 1 when it has a pin or passes 2000 lines.
# The 2000 cap stops the loop. The line text is not split again.
# A trailing CR from a CRLF README is not part of the reported text.
m1_print_fail_lines() {
  local file="$1"
  local number=0
  local line reason text
  local found=0
  while IFS= read -r line || [ -n "$line" ]; do
    number=$((number + 1))
    if [ "$number" -gt 2000 ]; then
      echo "FAIL M1-02 ${file} has more than 2000 lines" >&2
      return 1
    fi
    reason=$(m1_readme_version_hit "$line")
    if [ -n "$reason" ]; then
      text="${line%$'\r'}"
      printf 'FAIL M1-02 pin in %s:%s: %s\n' "$file" "$number" "$text"
      found=1
    fi
  done < "$file"
  if [ "$found" -eq 0 ]; then
    return 0
  fi
  return 1
}

# M1-02. Counts hits of one reason in scan output.
# Args: scan output, reason. Prints a number. Returns 0.
m1_reason_count() {
  local hits="$1"
  local reason="$2"
  printf '%s\n' "$hits" | grep -c "^${reason}"$'\t' || true
}

# M1-02. Requires the three injected install lines to be detected, and a
# verbosity -v on an ALog line to be left alone.
# Returns 0.
m1_expect_injected_versions() {
  local dir file hits version_count short_count package_count
  dir=$(mktemp -d)
  file="${dir}/README.md"
  printf '%s\n' \
    'dotnet add package ALog --version 1.0.0' \
    'dotnet add package ALog.Extensions.Logging -v 0.2.0' \
    '<PackageReference Include="ALog" Version="0.2.0" />' \
    'Run ALog tests with dotnet test -v n' \
    > "$file"
  hits=$(m1_scan_readme "$file")
  rm -rf "$dir"
  version_count=$(m1_reason_count "$hits" '--version')
  short_count=$(m1_reason_count "$hits" '-v')
  package_count=$(m1_reason_count "$hits" 'PackageReference')
  if [ "$version_count" -ne 1 ] || [ "$short_count" -ne 1 ] || [ "$package_count" -ne 1 ]; then
    fail "M1-02 negative forms --version=${version_count} -v=${short_count} PackageReference=${package_count}"
    printf '%s\n' "$hits" >&2
    return 0
  fi
  if printf '%s\n' "$hits" | grep -F 'dotnet test -v n' >/dev/null; then
    fail "M1-02 treats dotnet test -v as an install pin"
    return 0
  fi
  pass "M1-02 negative --version, -v, and PackageReference"
}

# M1-02. Legacy pins still fire. The four control lines do not.
# Attribute order, a tab, a double space, the extensions package, and an
# ALog.csproj line are pins. Returns 0.
m1_expect_legacy_and_controls() {
  local dir file hits count
  dir=$(mktemp -d)
  file="${dir}/README.md"
  {
    printf '%s\n' '<PackageReference Version="0.2.0" Include="ALog" />'
    printf '%s\n' '<PackageReference Include="ALog.Extensions.Logging" Version="0.2.0" />'
    printf '%s\n' '<PackageReference Include="ALog"  Version="0.2.0" />'
    printf '<PackageReference Include="ALog"\tVersion="0.2.0" />\n'
    printf '%s\n' 'dotnet add ALog.csproj package Foo --version 1.0.0'
  } > "$file"
  hits=$(m1_scan_readme "$file")
  rm -rf "$dir"
  count=$(printf '%s\n' "$hits" | grep -c . || true)
  if [ "$count" -ne 5 ]; then
    fail "M1-02 legacy pins count ${count}"
    printf '%s\n' "$hits" >&2
    return 0
  fi
  dir=$(mktemp -d)
  file="${dir}/README.md"
  printf '%s\n' \
    'dotnet add package ALog --prerelease' \
    'dotnet build ALog.sln -v q' \
    'dotnet test ALog.Tests -v n' \
    '<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />' \
    > "$file"
  hits=$(m1_scan_readme "$file")
  rm -rf "$dir"
  if [ -n "$hits" ]; then
    fail "M1-02 control line was pinned"
    printf '%s\n' "$hits" >&2
    return 0
  fi
  pass "M1-02 legacy pins and control lines"
}

# M1-02. Each G10 form fails on line 2 and the fail line names that number.
# Eight forms. The loop stops at 8. Returns 0.
m1_expect_g10_line_numbers() {
  local dir file output status line expected i
  local forms=(
    "dotnet add package ALog --version=0.2.0"
    "dotnet add package ALog --Version 0.2.0"
    "dotnet add package ALog --version [0.2.0]"
    "<PackageReference Include=\"ALog\" Version='0.2.0' />"
    "<PackageReference Include=\"ALog\" Version = \"0.2.0\" />"
    "<PackageReference Include=\"ALog\" VersionOverride=\"0.2.0\" />"
    "dotnet add package alog --version 0.2.0"
    "Install-Package ALog -Version 0.2.0"
  )
  if [ "${#forms[@]}" -ne 8 ]; then
    fail "M1-02 G10 form count ${#forms[@]}"
    return 0
  fi
  dir=$(mktemp -d)
  i=0
  for line in "${forms[@]}"; do
    i=$((i + 1))
    if [ "$i" -gt 8 ]; then
      fail "M1-02 G10 loop passed 8"
      rm -rf "$dir"
      return 0
    fi
    file="${dir}/README.md"
    printf 'header\n%s\n' "$line" > "$file"
    status=0
    output=$(m1_print_fail_lines "$file") || status=$?
    expected="FAIL M1-02 pin in ${file}:2: ${line}"
    if [ "$status" -ne 1 ] || [ "$output" != "$expected" ]; then
      fail "M1-02 G10 form ${i} line number"
      printf 'expected %s\nactual %s\nstatus %s\n' "$expected" "$output" "$status" >&2
      rm -rf "$dir"
      return 0
    fi
  done
  rm -rf "$dir"
  pass "M1-02 G10 eight forms name line 2"
}

# M1-32-05. Each flexible Include pin fails on line 2. Five lines. The loop stops at 5.
# Returns 0. A case-sensitive double-quoted Include misses these lines.
m1_expect_include_forms() {
  local dir file output status line expected i
  local forms=(
    "<PackageReference Include='ALog' Version='0.2.0' />"
    "<PackageReference Include = \"ALog\" Version = \"0.2.0\" />"
    "<PackageReference Include=\"alog\" Version=\"0.2.0\" />"
    "<PackageReference Include=\"ALOG\" Version=\"0.2.0\" />"
    "<PackageReference Include='ALog' Version=\"0.2.0\" />"
  )
  if [ "${#forms[@]}" -ne 5 ]; then
    fail "M1-02 Include form count ${#forms[@]}"
    return 0
  fi
  dir=$(mktemp -d)
  i=0
  for line in "${forms[@]}"; do
    i=$((i + 1))
    if [ "$i" -gt 5 ]; then
      fail "M1-02 Include loop passed 5"
      rm -rf "$dir"
      return 0
    fi
    file="${dir}/README.md"
    printf 'header\n%s\n' "$line" > "$file"
    status=0
    output=$(m1_print_fail_lines "$file") || status=$?
    expected="FAIL M1-02 pin in ${file}:2: ${line}"
    if [ "$status" -ne 1 ] || [ "$output" != "$expected" ]; then
      fail "M1-02 Include form ${i} line number"
      printf 'expected %s\nactual %s\nstatus %s\n' "$expected" "$output" "$status" >&2
      rm -rf "$dir"
      return 0
    fi
  done
  rm -rf "$dir"
  pass "M1-02 Include quotes, spaces, and case name line 2"
}

# M1-02. Every README*.md outside build output must be free of install pins.
# At most 20 files are accepted. Returns 0.
# A pin prints "FAIL M1-02 pin in <file>:<number>: <text>" on stderr.
m1_check_readme_versions() {
  local files=()
  local file found=0
  while IFS= read -r file; do
    files+=("$file")
  done < <(find . -type f -name 'README*.md' \
    -not -path './.git/*' \
    -not -path '*/bin/*' \
    -not -path '*/obj/*' \
    -not -path '*/TestResults/*' | sort)
  if [ "${#files[@]}" -eq 0 ] || [ "${#files[@]}" -gt 20 ]; then
    fail "M1-02 README count ${#files[@]} is outside 1..20"
    return 0
  fi
  for file in "${files[@]}"; do
    if m1_print_fail_lines "$file" >&2; then
      continue
    fi
    failures=$((failures + 1))
    found=1
  done
  if [ "$found" -eq 0 ]; then
    pass "M1-02 README install snippets name no ALog version"
  fi
  m1_expect_injected_versions
  m1_expect_legacy_and_controls
  m1_expect_g10_line_numbers
  m1_expect_include_forms
}

# Line-number mode: one file, then exit. The repository sweep is skipped above.
if [ -n "$m1_fail_lines_file" ]; then
  if [ ! -f "$m1_fail_lines_file" ]; then
    echo "FAIL M1-02 missing ${m1_fail_lines_file}" >&2
    exit 2
  fi
  if m1_print_fail_lines "$m1_fail_lines_file"; then
    exit 0
  fi
  exit 1
fi

m1_check_readme_versions

if [ "$failures" -ne 0 ]; then
  echo "${failures} s4 grep tests failed" >&2
  exit 1
fi
echo "s4 grep tests passed"
