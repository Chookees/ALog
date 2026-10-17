#!/usr/bin/env python3
"""Fail when a library sync-wait is not on the allowlist.

Scans ``ALog`` and ``ALog.Extensions.Logging`` for ``.Result``, ``.Wait(``,
and ``GetResult(``. Every hit must match one line in
``eng/sync-wait-allowlist.txt``. Every allowlist line must match a hit and
must name a timeout. ``FileLogWriter.cs`` must not call ``Task.Run``.
Next file: ``eng/sync-wait-allowlist.txt``.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

LIBRARIES = ("ALog", "ALog.Extensions.Logging")
ALLOWLIST_NAME = "eng/sync-wait-allowlist.txt"
FILE_WRITER = "ALog/Writers/File/FileLogWriter.cs"
HIT_RE = re.compile(r"\.Result\b|\.Wait\(|GetResult\(")
TASK_RUN_RE = re.compile(r"\bTask\.Run\b")
MAX_ALLOWLIST_LINES = 50
MAX_FILES = 500
MAX_LINES = 20000
MAX_LINE_CHARS = 4000
MIN_REASON = 20


def read_text(path: Path) -> str:
    """Read one UTF-8 text file.

    Args:
        path: File to read.

    Returns:
        The file text.

    Raises:
        OSError: The file cannot be read.

    Side effects:
        Reads the filesystem.
    """
    return path.read_text(encoding="utf-8")


def parse_allowlist(text: str) -> tuple[list[tuple[str, re.Pattern[str], str]], list[str]]:
    """Parse allowlist lines into path, pattern, and reason.

    Args:
        text: Full allowlist text. Blank lines and ``#`` comments are ignored.

    Returns:
        Entries and problem strings. A bad line is a problem and is skipped.
        At most 50 content lines are accepted.

    Raises:
        None.

    Side effects:
        None.
    """
    entries: list[tuple[str, re.Pattern[str], str]] = []
    problems: list[str] = []
    seen = 0
    for raw in text.splitlines():
        line = raw.strip()
        if line == "" or line.startswith("#"):
            continue
        seen += 1
        if seen > MAX_ALLOWLIST_LINES:
            problems.append("allowlist has more than 50 entries.")
            break
        path, separator, rest = line.partition(":")
        pattern_text, reason_separator, reason = rest.partition(" # ")
        if separator == "" or reason_separator == "" or path == "" or pattern_text == "":
            problems.append(f"allowlist line is not path:pattern # reason: {line}")
            continue
        if len(reason) < MIN_REASON or "Timeout:" not in reason:
            problems.append(f"allowlist reason needs a Timeout: note: {path}")
            continue
        try:
            pattern = re.compile(pattern_text)
        except re.error as ex:
            problems.append(f"allowlist pattern is not a regex ({path}): {ex}")
            continue
        entries.append((path, pattern, reason))
    return entries, problems


def iter_source_files(root: Path) -> list[Path]:
    """List library C# files, skipping build output.

    Args:
        root: Repository root.

    Returns:
        Up to 500 ``.cs`` files under the two libraries.

    Raises:
        None.

    Side effects:
        Reads the filesystem.
    """
    found: list[Path] = []
    for name in LIBRARIES:
        base = root / name
        if not base.is_dir():
            continue
        for path in base.rglob("*.cs"):
            if len(found) >= MAX_FILES:
                return found
            parts = path.parts
            if "bin" in parts or "obj" in parts:
                continue
            found.append(path)
    return found


def scan_hits(root: Path) -> tuple[list[tuple[str, str]], list[str]]:
    """Find sync-wait lines in the two libraries.

    Args:
        root: Repository root.

    Returns:
        ``(relative path, line text)`` hits, and problems for files that are
        too large to trust.

    Raises:
        OSError: A source file cannot be read.

    Side effects:
        Reads the filesystem.
    """
    hits: list[tuple[str, str]] = []
    problems: list[str] = []
    for path in iter_source_files(root):
        text = read_text(path)
        lines = text.splitlines()
        if len(lines) > MAX_LINES:
            problems.append(f"{path} has more than {MAX_LINES} lines.")
            continue
        relative = path.relative_to(root).as_posix()
        for line in lines:
            if len(line) > MAX_LINE_CHARS:
                problems.append(f"{relative} has a line longer than {MAX_LINE_CHARS} characters.")
                break
            if HIT_RE.search(line):
                hits.append((relative, line))
    return hits, problems


def unmatched(hits: list[tuple[str, str]], entries: list[tuple[str, re.Pattern[str], str]]) -> list[str]:
    """Return hits that no allowlist entry covers.

    Args:
        hits: Path and line pairs from ``scan_hits``.
        entries: Parsed allowlist.

    Returns:
        One problem per uncovered hit. At most 50.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    for path, line in hits:
        if len(problems) >= MAX_ALLOWLIST_LINES:
            break
        covered = False
        for entry_path, pattern, _reason in entries:
            if entry_path == path and pattern.search(line):
                covered = True
                break
        if not covered:
            problems.append(f"sync-wait is not allowlisted: {path}: {line.strip()}")
    return problems


def unused_entries(
    hits: list[tuple[str, str]],
    entries: list[tuple[str, re.Pattern[str], str]],
) -> list[str]:
    """Return allowlist entries that match no current hit.

    Args:
        hits: Path and line pairs from ``scan_hits``.
        entries: Parsed allowlist.

    Returns:
        One problem per stale entry.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    for path, pattern, _reason in entries:
        matched = False
        for hit_path, line in hits:
            if hit_path == path and pattern.search(line):
                matched = True
                break
        if not matched:
            problems.append(f"allowlist entry matches nothing: {path}")
    return problems


def file_writer_task_run(root: Path) -> list[str]:
    """Return a problem when the file writer still hops to the thread pool.

    Args:
        root: Repository root.

    Returns:
        A one-item list when ``Task.Run`` appears, or when the file is missing.
        Empty when the writer stays inline.

    Raises:
        OSError: The file exists but cannot be read.

    Side effects:
        Reads the filesystem.
    """
    path = root / FILE_WRITER
    if not path.is_file():
        return [f"{FILE_WRITER} is missing."]
    if TASK_RUN_RE.search(read_text(path)):
        return [f"{FILE_WRITER} still calls Task.Run."]
    return []


def evaluate(root: Path) -> list[str]:
    """Run the sync-wait gate for one repository root.

    Args:
        root: Directory that contains ``eng/sync-wait-allowlist.txt`` and the libraries.

    Returns:
        Problem strings. Empty when the gate passes.

    Raises:
        OSError: The allowlist or a source file cannot be read.

    Side effects:
        Reads the filesystem.
    """
    allowlist_path = root / ALLOWLIST_NAME
    if not allowlist_path.is_file():
        return [f"{ALLOWLIST_NAME} is missing."]
    entries, problems = parse_allowlist(read_text(allowlist_path))
    hits, scan_problems = scan_hits(root)
    problems.extend(scan_problems)
    problems.extend(unmatched(hits, entries))
    problems.extend(unused_entries(hits, entries))
    problems.extend(file_writer_task_run(root))
    return problems


def main(argv: list[str]) -> int:
    """Check the repository that contains this script.

    Args:
        argv: Unused. The script takes no arguments.

    Returns:
        0 when the gate passes, 1 when it fails.

    Raises:
        OSError: A required file cannot be read.

    Side effects:
        Writes the result to stdout or stderr.
    """
    del argv
    root = Path(__file__).resolve().parent.parent
    problems = evaluate(root)
    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        return 1
    print("sync-wait check passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
