#!/usr/bin/env bash
# CLK-03 and U-CLK-01. The queue and the engine dispose path must not read the
# wall clock. A test name must not claim a time it does not check.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

failures=0

# Prints a passing check. Args: label.
pass() {
  printf 'ok %s\n' "$1"
}

# Records a failure. Args: message.
fail() {
  echo "FAIL $1" >&2
  failures=$((failures + 1))
}

queue="ALog/Core/BackgroundLogQueue.cs"
engine="ALog/Core/LoggerEngine.cs"

# Args: label, file, extended regex. A hit fails.
reject() {
  local label="$1"
  local file="$2"
  local pattern="$3"
  local hits
  hits=$(grep -n -E "$pattern" "$file" || true)
  if [ -n "$hits" ]; then
    echo "FAIL ${label}" >&2
    printf '%s\n' "$hits" >&2
    failures=$((failures + 1))
    return 0
  fi
  pass "$label"
}

reject "queue wall clock" "$queue" 'Stopwatch|DateTime\.UtcNow|DateTime\.Now|DateTimeOffset\.UtcNow|DateTimeOffset\.Now|\.Wait\('
reject "queue delay without a provider" "$queue" 'Task\.Delay\('
reject "queue one-argument periodic timer" "$queue" 'new PeriodicTimer\([^,)]*\)'
reject "engine dispose wall wait" "$engine" 'Stopwatch|\.Wait\(|Task\.Delay\('

python3 - << 'PY'
"""Fail when a test method name claims a time the body does not check.

Within<digits>, a trailing Ms, or Second, are time claims. A name that is the
product deadline (ShutdownDeadline or NewWriteBudget) is allowed when the body
uses that constant. A Within<digits> or <digits>ms name is allowed when the
body checks that number as a bound. ReportsASecondLine and SecondWriter do not
match, because a letter precedes Second.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

FACT = re.compile(r"^\s*\[(Fact|Theory)\b")
METHOD = re.compile(r"\b(?:Task|void|ValueTask)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")
TIME_NAME = re.compile(r"Within\d|(?<![A-Za-z])Ms\b|(?<![A-Za-z])Second")
NUMBERED = re.compile(r"Within(\d+)|(?<![A-Za-z])(\d+)[Mm]s\b")
BOUND = re.compile(r"TimeSpan|FromMilliseconds|FromSeconds|Assert\.(True|InRange|Equal)")
MAX_LOOKAHEAD = 12
MAX_BODY = 400


def method_name(lines: list[str], start: int) -> str | None:
    """Return the method name that follows a fact attribute.

    Args:
        lines: File lines.
        start: Index of the attribute line.

    Returns:
        The method name, or None when no signature appears within 12 lines.

    Raises:
        None.

    Side effects:
        None.
    """
    end = min(len(lines), start + MAX_LOOKAHEAD)
    for index in range(start + 1, end):
        found = METHOD.search(lines[index])
        if found:
            return found.group(1)
    return None


def method_body(lines: list[str], start: int) -> str:
    """Return the text from the signature through the line before the next fact.

    Args:
        lines: File lines.
        start: Index of the attribute line.

    Returns:
        At most 400 lines. The search stops at the next fact so a later test
        cannot satisfy this test's bound.

    Raises:
        None.

    Side effects:
        None.
    """
    chunk: list[str] = []
    end = min(len(lines), start + 1 + MAX_BODY)
    for index in range(start + 1, end):
        if index != start + 1 and FACT.match(lines[index]):
            break
        chunk.append(lines[index])
    return "\n".join(chunk)


def violation(name: str, body: str) -> str | None:
    """Return why a name is illegal, or None when it is allowed.

    Args:
        name: Test method name.
        body: Method text, not including later tests.

    Returns:
        A short reason, or None.

    Raises:
        None.

    Side effects:
        None.
    """
    if TIME_NAME.search(name) is None:
        return None
    if "ShutdownDeadline" in name and "ShutdownDeadline" in body:
        return None
    if "NewWriteBudget" in name and "NewWriteBudget" in body:
        return None
    numbers = [item[0] or item[1] for item in NUMBERED.findall(name)]
    if numbers:
        if any(number in body and BOUND.search(body) for number in numbers):
            return None
        return "names a duration the body does not check as a bound"
    return "names a time the body does not check as a bound"


def scan(root: Path) -> list[str]:
    """Scan ALog.Tests for illegal test names.

    Args:
        root: Repository root.

    Returns:
        Failure lines. Empty when every name is allowed.

    Raises:
        OSError: A test file cannot be read.

    Side effects:
        Reads the test tree.
    """
    problems: list[str] = []
    tests = root / "ALog.Tests"
    for path in sorted(tests.rglob("*.cs")):
        lines = path.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            if FACT.match(line) is None:
                continue
            name = method_name(lines, index)
            if name is None:
                continue
            reason = violation(name, method_body(lines, index))
            if reason is not None:
                problems.append(f"{path}:{index + 1} {name} {reason}")
    return problems


def self_check() -> None:
    """Fail the process when the name rule accepts or rejects the wrong sample.

    Raises:
        SystemExit: A sample was classified wrongly.

    Side effects:
        None on success.
    """
    samples = [
        ("WriteAsync_FullQueue_CompletesWithin10ms", "Assert.True(pending.IsCompletedSuccessfully);", True),
        ("WriteAsync_FullQueue_CompletesWithin10ms", "Assert.True(elapsed < TimeSpan.FromMilliseconds(10));", False),
        ("WriteAsync_FullQueue_CompletesSynchronously", "Assert.True(pending.IsCompletedSuccessfully);", False),
        ("DifferentExceptionType_ReportsASecondLine", "Assert.Equal(2, lines.Count);", False),
        ("Batch_ThrowOnEvent3_ContinuesAndSecondWriterGetsAll", "Assert.Equal(10, other.Events.Count);", False),
        ("Flush_BlockedWriter_ReturnsInsideShutdownDeadline_WritesNoAlog0071", "BackgroundLogQueue.ShutdownDeadline", False),
        ("WaitWithin5000_OnShutdownDeadline", "no constant here", True),
        ("WaitWithin5000_OnShutdownDeadline", "BackgroundLogQueue.ShutdownDeadline", False),
    ]
    for name, body, expect_violation in samples:
        actual = violation(name, body) is not None
        if actual != expect_violation:
            raise SystemExit(f"name rule mismatch for {name}")


def main() -> int:
    """Run the self-check and the tree scan.

    Returns:
        0 when every name is allowed. 1 when a test name claims an unchecked time.

    Raises:
        SystemExit: The self-check fails.
        OSError: A test file cannot be read.

    Side effects:
        Writes failures to stderr.
    """
    self_check()
    problems = scan(Path("."))
    if problems:
        print("FAIL test name claims a time", file=sys.stderr)
        print("\n".join(problems), file=sys.stderr)
        return 1
    print("ok test names")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
PY

if [ "$failures" -ne 0 ]; then
  echo "FAIL clk grep ${failures}" >&2
  exit 1
fi

echo "ok clk grep"
