#!/usr/bin/env bash
# CLK-03, U-CLK-01, and U-CLK-04. Queue and engine dispose code must not read
# the wall clock. A test name must not claim a time it does not check.
# One hit is one line. The last line is clk-grep: ok, or the problem count.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

python3 - << 'PY'
"""Report wall-clock calls and test names that promise an unchecked time.

A hit is one stdout line. Success prints only "clk-grep: ok". Failure prints
each hit and then "clk-grep: N problem(s) found." and returns 1.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

FACT = re.compile(r"^\s*\[(Fact|Theory)\b")
METHOD = re.compile(r"\b(?:Task|void|ValueTask)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(")
TIME_NAME = re.compile(r"Within\d|(?<![A-Za-z])Ms\b|(?<![A-Za-z])Second")
SPEED_NAME = re.compile(r"Immediately|Fast|Quickly|Instant")
NUMBERED = re.compile(r"Within(\d+)|(?<![A-Za-z])(\d+)[Mm]s\b")
BOUND = re.compile(r"TimeSpan|FromMilliseconds|FromSeconds|Assert\.(True|InRange|Equal)")
QUEUE_CALL = re.compile(
    r"Stopwatch|DateTimeOffset\.UtcNow|DateTimeOffset\.Now|DateTime\.UtcNow|"
    r"DateTime\.Now|\.Wait\(|Task\.Delay\(|new PeriodicTimer\([^,)]*\)"
)
ENGINE_CALL = re.compile(r"Stopwatch|\.Wait\(|Task\.Delay\(")
MAX_LOOKAHEAD = 12
MAX_BODY = 400
MAX_FILE_LINES = 20000
QUEUE_FILE = "ALog/Core/BackgroundLogQueue.cs"
ENGINE_FILE = "ALog/Core/LoggerEngine.cs"


def wall_line(path: str, line_no: int, call: str) -> str:
    """Format one wall-clock hit.

    Args:
        path: Repository-relative path. Not empty.
        line_no: 1-based line. Greater than zero.
        call: Matched call text. Not empty.

    Returns:
        The required one-line report.

    Raises:
        ValueError: An argument is empty or the line is not positive.

    Side effects:
        None.
    """
    if path == "" or call == "":
        raise ValueError("path and call are required")
    if line_no <= 0:
        raise ValueError("line number must be positive")
    return (
        f"{path}:{line_no}: wall-clock call ({call}) in queue code. "
        "Use the injected TimeProvider instead."
    )


def name_line(path: str, line_no: int, name: str) -> str:
    """Format one test-name hit.

    Args:
        path: Repository-relative path. Not empty.
        line_no: 1-based signature line. Greater than zero.
        name: Test method name. Not empty.

    Returns:
        The required one-line report.

    Raises:
        ValueError: An argument is empty or the line is not positive.

    Side effects:
        None.
    """
    if path == "" or name == "":
        raise ValueError("path and name are required")
    if line_no <= 0:
        raise ValueError("line number must be positive")
    return (
        f"{path}:{line_no}: test name '{name}' promises a time the test does not check. "
        "Rename it to say what it asserts."
    )


def read_lines(relative: str) -> list[str]:
    """Read a repository file, bounded at MAX_FILE_LINES.

    Args:
        relative: Path from the repository root. Not empty.

    Returns:
        The lines, without line endings.

    Raises:
        OSError: The file cannot be read.
        ValueError: The path is empty or the file is longer than the cap.

    Side effects:
        Reads the file.
    """
    if relative == "":
        raise ValueError("path is required")
    lines = Path(relative).read_text(encoding="utf-8").splitlines()
    if len(lines) > MAX_FILE_LINES:
        raise ValueError(relative + " exceeds the line cap")
    return lines


def wall_hits(relative: str, pattern: re.Pattern[str]) -> list[str]:
    """Return one formatted hit for each wall-clock match.

    Args:
        relative: Repository-relative source file.
        pattern: Calls that are illegal in that file.

    Returns:
        Hits in line order. Empty when the file is clean.

    Raises:
        OSError: The file cannot be read.
        ValueError: The file is longer than the cap.

    Side effects:
        Reads the file.
    """
    hits: list[str] = []
    for index, line in enumerate(read_lines(relative), start=1):
        for match in pattern.finditer(line):
            hits.append(wall_line(relative, index, match.group(0)))
    return hits


def method_at(lines: list[str], start: int) -> tuple[str, int] | None:
    """Return the method name and signature line after a fact attribute.

    Args:
        lines: File lines.
        start: Index of the attribute line.

    Returns:
        Name and 0-based signature index, or None when no signature appears
        within 12 lines.

    Raises:
        None.

    Side effects:
        None.
    """
    end = min(len(lines), start + MAX_LOOKAHEAD)
    for index in range(start + 1, end):
        found = METHOD.search(lines[index])
        if found:
            return found.group(1), index
    return None


def method_body(lines: list[str], start: int) -> str:
    """Return the text after the signature through the line before the next fact.

    Args:
        lines: File lines.
        start: Index of the attribute line.

    Returns:
        At most 400 lines. The signature is left out, so the method name does
        not count as a use of a deadline constant. The search stops at the next
        fact so a later test cannot satisfy this test's bound.

    Raises:
        None.

    Side effects:
        None.
    """
    found = method_at(lines, start)
    if found is None:
        return ""
    signature = found[1]
    chunk: list[str] = []
    end = min(len(lines), signature + 1 + MAX_BODY)
    for index in range(signature + 1, end):
        if FACT.match(lines[index]):
            break
        chunk.append(lines[index])
    return "\n".join(chunk)


def violation(name: str, body: str) -> bool:
    """Return whether a name claims a time the body does not check.

    Args:
        name: Test method name.
        body: Method text, not including later tests.

    Returns:
        True when the name is illegal.

    Raises:
        None.

    Side effects:
        None.

    Within<digits>, a trailing Ms, or Second, are time claims. A letter before
    Second, as in SecondWriter, does not match. Immediately, Fast, Quickly,
    and Instant match inside a PascalCase name, so DropsImmediately and
    FastWriter are claims. A name that is the product deadline
    (ShutdownDeadline or NewWriteBudget) is allowed when the body uses that
    constant. A name that carries a number is allowed when the body checks
    that number as a bound. The four speed words have no number, so they stay
    flagged.
    """
    speed = SPEED_NAME.search(name) is not None
    if TIME_NAME.search(name) is None and not speed:
        return False
    if "ShutdownDeadline" in name and "ShutdownDeadline" in body:
        return False
    if "NewWriteBudget" in name and "NewWriteBudget" in body:
        return False
    if speed:
        return True
    numbers = [item[0] or item[1] for item in NUMBERED.findall(name)]
    if numbers:
        if any(number in body and BOUND.search(body) for number in numbers):
            return False
        return True
    return True


def name_hits(root: Path) -> list[str]:
    """Scan ALog.Tests for illegal test names.

    Args:
        root: Repository root.

    Returns:
        One formatted line per illegal name, in path order.

    Raises:
        OSError: A test file cannot be read.
        ValueError: A test file exceeds the line cap.

    Side effects:
        Reads the test tree.
    """
    problems: list[str] = []
    tests = root / "ALog.Tests"
    for path in sorted(tests.rglob("*.cs")):
        relative = path.relative_to(root).as_posix()
        lines = read_lines(relative)
        for index, line in enumerate(lines):
            if FACT.match(line) is None:
                continue
            found = method_at(lines, index)
            if found is None:
                continue
            name, signature = found
            if violation(name, method_body(lines, index)):
                problems.append(name_line(relative, signature + 1, name))
    return problems


def self_check() -> None:
    """Fail the process when a sample is classified wrongly.

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
        ("FullQueue_DropsImmediately_ReportsAlog0070", "Assert.Equal(900, queue.FullDropCount);", True),
        ("DropsImmediatelyWithin10", "Assert.True(elapsed < TimeSpan.FromMilliseconds(10));", True),
        ("FullQueue_DropsWithoutWaiting_ReportsAlog0070", "Assert.Equal(900, queue.FullDropCount);", False),
        ("Shutdown_SlowWriter_ReportsRemainder_FastWriter_ReportsNothing", "Assert.Empty(lines);", True),
        ("Queue_DropsQuickly", "Assert.Equal(1, dropped);", True),
        ("Write_ReturnsInstant", "Assert.True(done);", True),
    ]
    for name, body, expect_violation in samples:
        actual = violation(name, body)
        if actual != expect_violation:
            raise SystemExit("clk-grep: self-check failed for " + name)
    if QUEUE_CALL.findall("new PeriodicTimer(interval, clock)") != []:
        raise SystemExit("clk-grep: self-check failed for two-argument PeriodicTimer")
    if QUEUE_CALL.findall("new PeriodicTimer(interval)") != ["new PeriodicTimer(interval)"]:
        raise SystemExit("clk-grep: self-check failed for one-argument PeriodicTimer")
    if ENGINE_CALL.findall("DateTime.UtcNow") != []:
        raise SystemExit("clk-grep: self-check failed for engine timestamp")
    if QUEUE_CALL.findall("Task.Delay(") != ["Task.Delay("]:
        raise SystemExit("clk-grep: self-check failed for Task.Delay")
    sample = wall_line(QUEUE_FILE, 1, "Task.Delay(")
    needle = "wall-clock call (" + "Task.Delay("
    if needle not in sample:
        raise SystemExit("clk-grep: self-check failed for wall line")


def main() -> int:
    """Run the self-check and both scans.

    Returns:
        0 when the tree is clean. 1 when at least one hit was reported.

    Raises:
        SystemExit: The self-check fails.
        OSError: A file cannot be read.

    Side effects:
        Writes the report to stdout.
    """
    self_check()
    problems = wall_hits(QUEUE_FILE, QUEUE_CALL)
    problems.extend(wall_hits(ENGINE_FILE, ENGINE_CALL))
    problems.extend(name_hits(Path(".")))
    if problems:
        print("\n".join(problems))
        print("clk-grep: " + str(len(problems)) + " problem(s) found.")
        return 1
    print("clk-grep: ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
PY
