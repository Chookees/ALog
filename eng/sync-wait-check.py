#!/usr/bin/env python3
"""Fail when a library sync-wait is not on the allowlist.

Scans ``ALog`` and ``ALog.Extensions.Logging`` for ``.Result``, ``.Wait(``,
``GetResult(``, ``WaitHandle.WaitAny`` / ``WaitAll`` / ``WaitOne`` calls
that pass no finite timeout, and ``WaitHandle.SignalAndWait`` without a
timeout. Every hit must match one line in ``eng/sync-wait-allowlist.txt``.
Every allowlist line must match exactly one hit and must name a timeout.
``FileLogWriter.cs`` must not call ``Task.Run``.
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
WAIT_HANDLE_RE = re.compile(
    r"\bWaitHandle\s*\.\s*Wait(Any|All|One)\s*\(",
    re.MULTILINE,
)
SIGNAL_AND_WAIT_RE = re.compile(
    r"\bWaitHandle\s*\.\s*SignalAndWait\s*\(",
    re.MULTILINE,
)
INFINITE_TIMEOUT_RE = re.compile(
    r"\bTimeout\.Infinite(?:TimeSpan)?\b|(?<![\w.])-1\b",
)
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


def wait_handle_lacks_timeout(text: str) -> bool:
    """True when a WaitHandle wait in ``text`` has no finite timeout argument.

    Args:
        text: One source line, or a short span that may contain a newline between
            ``WaitHandle`` and ``.WaitAny`` / ``WaitAll`` / ``WaitOne``.

    Returns:
        True when ``WaitAny`` or ``WaitAll`` has only the handles, or passes
        ``Timeout.Infinite``, ``Timeout.InfiniteTimeSpan``, or ``-1`` as the
        timeout; or when ``WaitOne`` has an empty argument list or those same
        infinite values. A call that passes a finite timeout is false.

    Raises:
        None.

    Side effects:
        None.
    """
    for match in WAIT_HANDLE_RE.finditer(text):
        kind = match.group(1)
        open_paren = match.end() - 1
        if kind == "WaitOne":
            if not _call_has_argument(text, open_paren):
                return True
            if _timeout_argument_is_infinite(text, open_paren):
                return True
        elif not _call_has_timeout_argument(text, open_paren):
            return True
        elif _timeout_argument_is_infinite(text, open_paren):
            return True
    return False


def signal_and_wait_lacks_timeout(text: str) -> bool:
    """True when ``WaitHandle.SignalAndWait`` is called without a timeout.

    Args:
        text: One source line, or a short span that may contain a newline between
            ``WaitHandle`` and ``.SignalAndWait``.

    Returns:
        True when the call has fewer than three top-level arguments (the
        two-handle form). A call that passes a timeout after the handles is false.

    Raises:
        None.

    Side effects:
        None.
    """
    for match in SIGNAL_AND_WAIT_RE.finditer(text):
        open_paren = match.end() - 1
        if not _call_has_timeout_argument(text, open_paren):
            return True
        if _timeout_argument_is_infinite(text, open_paren):
            return True
    return False


def _call_has_argument(text: str, open_paren: int) -> bool:
    """True when the call at ``open_paren`` has any argument.

    Args:
        text: Source text that contains the call.
        open_paren: Index of the opening parenthesis.

    Returns:
        True when a non-space character appears before the matching close paren.

    Raises:
        None.

    Side effects:
        None.
    """
    paren = 0
    for index in range(open_paren, len(text)):
        char = text[index]
        if char == "(":
            paren += 1
        elif char == ")":
            paren -= 1
            if paren == 0:
                return False
        elif paren == 1 and not char.isspace():
            return True
    return False


def _call_has_timeout_argument(text: str, open_paren: int) -> bool:
    """True when a multi-argument wait call passes an argument after the handles.

    Args:
        text: Source text that contains the call.
        open_paren: Index of the opening parenthesis.

    Returns:
        True when a comma sits at the call's own argument depth. Commas inside
        the handles array do not count. For ``SignalAndWait``, the first comma
        separates the two handles; a second top-level comma is the timeout.

    Raises:
        None.

    Side effects:
        None.
    """
    paren = 0
    bracket = 0
    brace = 0
    top_commas = 0
    is_signal = False
    # Look back a short distance for SignalAndWait so the first comma is not
    # treated as the timeout separator.
    prefix = text[max(0, open_paren - 40) : open_paren + 1]
    if "SignalAndWait" in prefix:
        is_signal = True
    for index in range(open_paren, len(text)):
        char = text[index]
        if char == "(":
            paren += 1
        elif char == ")":
            paren -= 1
            if paren == 0:
                return False
        elif char == "[":
            bracket += 1
        elif char == "]":
            bracket -= 1
        elif char == "{":
            brace += 1
        elif char == "}":
            brace -= 1
        elif char == "," and paren == 1 and bracket == 0 and brace == 0:
            top_commas += 1
            if is_signal:
                if top_commas >= 2:
                    return True
            else:
                return True
    return False


def _timeout_argument_is_infinite(text: str, open_paren: int) -> bool:
    """True when the call's timeout argument is an infinite wait.

    Args:
        text: Source text that contains the call.
        open_paren: Index of the opening parenthesis.

    Returns:
        True when the text after the last top-level comma and before the matching
        close paren is only ``Timeout.Infinite``, ``Timeout.InfiniteTimeSpan``,
        or ``-1`` (with optional whitespace). False when there is no timeout
        argument or the timeout is finite.

    Raises:
        None.

    Side effects:
        None.
    """
    paren = 0
    bracket = 0
    brace = 0
    last_comma = -1
    close = -1
    for index in range(open_paren, len(text)):
        char = text[index]
        if char == "(":
            paren += 1
        elif char == ")":
            paren -= 1
            if paren == 0:
                close = index
                break
        elif char == "[":
            bracket += 1
        elif char == "]":
            bracket -= 1
        elif char == "{":
            brace += 1
        elif char == "}":
            brace -= 1
        elif char == "," and paren == 1 and bracket == 0 and brace == 0:
            last_comma = index
    if last_comma < 0 or close < 0:
        return False
    argument = text[last_comma + 1 : close].strip()
    if argument == "":
        return False
    return INFINITE_TIMEOUT_RE.fullmatch(argument) is not None


def _line_number_at(text: str, index: int) -> int:
    """1-based line number of ``index`` in ``text``.

    Args:
        text: Full file text.
        index: Character offset. Not negative.

    Returns:
        The line number that contains ``index``.

    Raises:
        None.

    Side effects:
        None.
    """
    return text.count("\n", 0, index) + 1


def _line_text(lines: list[str], line_no: int) -> str:
    """Return the source line at a 1-based number.

    Args:
        lines: File lines without separators.
        line_no: 1-based line number.

    Returns:
        The line text, or empty when out of range.

    Raises:
        None.

    Side effects:
        None.
    """
    if line_no < 1 or line_no > len(lines):
        return ""
    return lines[line_no - 1]


def scan_hits(root: Path) -> tuple[list[tuple[str, str]], list[str]]:
    """Find sync-wait lines in the two libraries.

    Args:
        root: Repository root.

    Returns:
        ``(relative path, line text)`` hits, and problems for files that are
        too large to trust. A ``WaitHandle`` call split across lines is reported
        on the line that contains the method name.

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
        marked: set[int] = set()
        for line_index, line in enumerate(lines):
            if len(line) > MAX_LINE_CHARS:
                problems.append(f"{relative} has a line longer than {MAX_LINE_CHARS} characters.")
                marked.clear()
                break
            if HIT_RE.search(line):
                hits.append((relative, line))
                marked.add(line_index)

        # WaitHandle / SignalAndWait may span lines: "WaitHandle\n    .WaitAny(...)".
        for match in WAIT_HANDLE_RE.finditer(text):
            start = match.start()
            # Extend far enough to cover a typical argument list on following lines.
            span_end = min(len(text), match.end() + 400)
            span = text[start:span_end]
            if not wait_handle_lacks_timeout(span):
                continue
            line_no = _line_number_at(text, match.start())
            # Prefer the line that holds ".Wait..." when WaitHandle sat alone above.
            method_line = _line_number_at(text, match.end() - 1)
            report_no = method_line
            report = _line_text(lines, report_no)
            if report_no - 1 in marked:
                continue
            hits.append((relative, report if report else span.splitlines()[0]))
            marked.add(report_no - 1)

        for match in SIGNAL_AND_WAIT_RE.finditer(text):
            start = match.start()
            span_end = min(len(text), match.end() + 400)
            span = text[start:span_end]
            if not signal_and_wait_lacks_timeout(span):
                continue
            report_no = _line_number_at(text, match.end() - 1)
            if report_no - 1 in marked:
                continue
            report = _line_text(lines, report_no)
            hits.append((relative, report if report else span.splitlines()[0]))
            marked.add(report_no - 1)
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
    """Return allowlist entries that do not match exactly one current hit.

    Args:
        hits: Path and line pairs from ``scan_hits``.
        entries: Parsed allowlist.

    Returns:
        One problem per entry whose hit count is not exactly one. The message
        names the path, pattern, and hit count.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    for path, pattern, _reason in entries:
        count = 0
        for hit_path, line in hits:
            if hit_path == path and pattern.search(line):
                count += 1
        if count != 1:
            problems.append(
                f"allowlist entry must match exactly one hit (found {count}): "
                f"{path}: {pattern.pattern}"
            )
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
