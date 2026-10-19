#!/usr/bin/env bash
# CLK-03, U-CLK-01, U-CLK-04, and CLK-31-03. Core code must not read the wall
# clock. A test name must not claim a time it does not check.
# One hit is one line. The last line is clk-grep: ok, or the problem count.

set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root"

python3 - << 'PY'
"""Report wall-clock calls and test names that promise an unchecked time.

A hit is one stdout line. Success prints only "clk-grep: ok". Failure prints
each hit and then "clk-grep: N problem(s) found." and returns 1.
Comments and string literals are ignored. An allowlist entry suppresses one
known call when the path, line, and call all match.
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
TOKEN_ARG = re.compile(r"CancellationToken|\btoken\b|\bct\b", re.IGNORECASE)
TIMESPAN_ARG = re.compile(r"TimeSpan|\d")
MAX_LOOKAHEAD = 12
MAX_BODY = 400
MAX_FILE_LINES = 20000
ALLOWLIST = "eng/clk-grep-allowlist.txt"
CORE = Path("ALog/Core")

# (reported call, regex, kind). kind selects the argument rule.
CALLS: list[tuple[str, re.Pattern[str], str]] = [
    ("Task.Delay(", re.compile(r"(?<![\w])Task\.Delay\s*\("), "delay"),
    ("Thread.Sleep", re.compile(r"(?<![\w])Thread\.Sleep\s*\("), "always"),
    ("Stopwatch", re.compile(r"(?<![\w])Stopwatch\b"), "always"),
    ("Environment.TickCount64", re.compile(r"(?<![\w])Environment\.TickCount64\b"), "always"),
    ("Environment.TickCount", re.compile(r"(?<![\w])Environment\.TickCount(?!64)\b"), "always"),
    ("System.Threading.Timer", re.compile(r"(?<![\w])System\.Threading\.Timer\b"), "always"),
    ("System.Timers.Timer", re.compile(r"(?<![\w])System\.Timers\.Timer\b"), "always"),
    ("new Timer", re.compile(r"(?<![\w])new Timer\b"), "always"),
    ("CancellationTokenSource(", re.compile(r"(?<![\w])new CancellationTokenSource\s*\("), "ctor-timeout"),
    ("CancelAfter(", re.compile(r"(?<![\w])CancelAfter\s*\("), "always"),
    ("WaitOne(", re.compile(r"(?<![\w])WaitOne\s*\("), "any-arg"),
    ("WaitAny(", re.compile(r"(?<![\w])WaitAny\s*\("), "extra-arg"),
    ("WaitAll(", re.compile(r"(?<![\w])WaitAll\s*\("), "extra-arg"),
    (".Wait(", re.compile(r"\.Wait\s*\("), "wait-timeout"),
    ("WaitAsync(", re.compile(r"(?<![\w])WaitAsync\s*\("), "wait-async"),
    ("SpinWait.SpinUntil", re.compile(r"(?<![\w])SpinWait\.SpinUntil\b"), "always"),
    ("DateTimeOffset.UtcNow", re.compile(r"(?<![\w])DateTimeOffset\.UtcNow\b"), "always"),
    ("DateTimeOffset.Now", re.compile(r"(?<![\w])DateTimeOffset\.Now\b"), "always"),
    ("DateTime.UtcNow", re.compile(r"(?<![\w])DateTime\.UtcNow\b"), "always"),
    ("DateTime.Now", re.compile(r"(?<![\w])DateTime\.Now\b"), "always"),
    ("TimeProvider.System", re.compile(r"(?<![\w])TimeProvider\.System\b"), "always"),
]


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


def mask_non_code(text: str) -> str:
    """Replace comments and string literals with spaces. Newlines stay.

    Args:
        text: C# source.

    Returns:
        The same length as text. Code, including interpolation holes, remains.

    Raises:
        None.

    Side effects:
        None.
    """
    chars = list(text)
    i = 0
    n = len(chars)
    while i < n:
        if chars[i] == "/" and i + 1 < n and chars[i + 1] == "/":
            while i < n and chars[i] != "\n":
                chars[i] = " "
                i += 1
            continue
        if chars[i] == "/" and i + 1 < n and chars[i + 1] == "*":
            chars[i] = " "
            chars[i + 1] = " "
            i += 2
            while i + 1 < n and not (chars[i] == "*" and chars[i + 1] == "/"):
                if chars[i] != "\n":
                    chars[i] = " "
                i += 1
            if i < n:
                chars[i] = " "
                i += 1
            if i < n:
                chars[i] = " "
                i += 1
            continue
        if chars[i] == "'":
            i = mask_char(chars, i)
            continue
        if chars[i] == '"':
            i = mask_string(chars, i, False, False)
            continue
        if chars[i] == "@" and i + 1 < n and chars[i + 1] == '"':
            i = mask_string(chars, i + 1, True, False)
            continue
        if chars[i] == "$" and i + 1 < n and chars[i + 1] == '"':
            i = mask_string(chars, i + 1, False, True)
            continue
        if chars[i] == "$" and i + 2 < n and chars[i + 1] == "@" and chars[i + 2] == '"':
            chars[i] = " "
            i = mask_string(chars, i + 2, True, True)
            continue
        if chars[i] == "@" and i + 2 < n and chars[i + 1] == "$" and chars[i + 2] == '"':
            chars[i] = " "
            i = mask_string(chars, i + 2, True, True)
            continue
        i += 1
    return "".join(chars)


def mask_char(chars: list[str], i: int) -> int:
    """Blank one character literal.

    Args:
        chars: Source being masked.
        i: Index of the opening quote.

    Returns:
        The index after the literal.

    Raises:
        None.

    Side effects:
        Writes spaces into chars.
    """
    chars[i] = " "
    i += 1
    if i < len(chars) and chars[i] == "\\":
        chars[i] = " "
        i += 1
    if i < len(chars) and chars[i] != "\n":
        chars[i] = " "
        i += 1
    if i < len(chars) and chars[i] == "'":
        chars[i] = " "
        i += 1
    return i


def mask_string(chars: list[str], i: int, verbatim: bool, interpolated: bool) -> int:
    """Blank one string. Interpolation holes stay as code.

    Args:
        chars: Source being masked.
        i: Index of the opening quote.
        verbatim: True for an @ string.
        interpolated: True when holes are code.

    Returns:
        The index after the closing quote.

    Raises:
        None.

    Side effects:
        Writes spaces into chars.
    """
    chars[i] = " "
    i += 1
    n = len(chars)
    while i < n:
        if not verbatim and chars[i] == "\n":
            return i
        if interpolated and chars[i] == "{":
            if i + 1 < n and chars[i + 1] == "{":
                chars[i] = " "
                chars[i + 1] = " "
                i += 2
                continue
            chars[i] = " "
            i = mask_hole(chars, i + 1)
            continue
        if interpolated and chars[i] == "}" and i + 1 < n and chars[i + 1] == "}":
            chars[i] = " "
            chars[i + 1] = " "
            i += 2
            continue
        if chars[i] == '"':
            if verbatim and i + 1 < n and chars[i + 1] == '"':
                chars[i] = " "
                chars[i + 1] = " "
                i += 2
                continue
            chars[i] = " "
            return i + 1
        if not verbatim and chars[i] == "\\":
            chars[i] = " "
            i += 1
            if i < n and chars[i] != "\n":
                chars[i] = " "
                i += 1
            continue
        chars[i] = " "
        i += 1
    return i


def mask_hole(chars: list[str], i: int) -> int:
    """Leave an interpolation hole as code and blank its format tail.

    Args:
        chars: Source being masked.
        i: Index of the first character inside the hole.

    Returns:
        The index after the closing brace.

    Raises:
        None.

    Side effects:
        Writes spaces over the format suffix. Recurses into nested strings.
    """
    n = len(chars)
    depth = 1
    while i < n and depth > 0:
        if chars[i] == "{":
            depth += 1
            i += 1
            continue
        if chars[i] == "}":
            depth -= 1
            if depth == 0:
                chars[i] = " "
                return i + 1
            i += 1
            continue
        if chars[i] == ":" and depth == 1:
            chars[i] = " "
            i += 1
            while i < n and chars[i] != "}":
                if chars[i] != "\n":
                    chars[i] = " "
                i += 1
            continue
        if chars[i] == '"':
            i = mask_string(chars, i, False, False)
            continue
        if chars[i] == "/" and i + 1 < n and chars[i + 1] == "/":
            while i < n and chars[i] != "\n":
                chars[i] = " "
                i += 1
            continue
        i += 1
    return i


def collapse(masked: str) -> tuple[str, list[int]]:
    """Drop whitespace around dots and before parens. Other gaps become one space.

    Args:
        masked: Source with comments and strings blanked.

    Returns:
        The normalized text and the 1-based source line of each character.

    Raises:
        None.

    Side effects:
        None.
    """
    out: list[str] = []
    lines: list[int] = []
    line = 1
    i = 0
    n = len(masked)
    while i < n:
        if masked[i].isspace():
            start_line = line
            while i < n and masked[i].isspace():
                if masked[i] == "\n":
                    line += 1
                i += 1
            prev = out[-1] if out else ""
            nxt = masked[i] if i < n else ""
            if prev == "." or nxt in ".(":
                continue
            if not out or i >= n:
                continue
            out.append(" ")
            lines.append(start_line)
            continue
        out.append(masked[i])
        lines.append(line)
        i += 1
    return "".join(out), lines


def top_level_args(text: str, open_index: int) -> list[str]:
    """Split the argument list that begins at an opening parenthesis.

    Args:
        text: Normalized source.
        open_index: Index of '('.

    Returns:
        Trimmed top-level arguments. Empty when the call has none.
        Empty when the parenthesis is never closed.

    Raises:
        None.

    Side effects:
        None.
    """
    if open_index >= len(text) or text[open_index] != "(":
        return []
    depth_paren = 0
    depth_brack = 0
    depth_brace = 0
    start = open_index + 1
    args: list[str] = []
    i = open_index
    while i < len(text):
        char = text[i]
        if char == "(":
            depth_paren += 1
        elif char == ")":
            depth_paren -= 1
            if depth_paren == 0:
                piece = text[start:i].strip()
                if piece != "":
                    args.append(piece)
                return args
        elif char == "[":
            depth_brack += 1
        elif char == "]":
            depth_brack -= 1
        elif char == "{":
            depth_brace += 1
        elif char == "}":
            depth_brace -= 1
        elif char == "," and depth_paren == 1 and depth_brack == 0 and depth_brace == 0:
            piece = text[start:i].strip()
            if piece != "":
                args.append(piece)
            start = i + 1
        i += 1
    return []


def paren_at(text: str, match: re.Match[str]) -> int:
    """Return the index of the '(' that ends a call match.

    Args:
        text: Normalized source.
        match: A call match. The parenthesis is the last non-space of the match,
            or the next '(' when the pattern did not include one.

    Returns:
        The index, or -1 when there is no parenthesis.

    Raises:
        None.

    Side effects:
        None.
    """
    end = match.end() - 1
    while end >= match.start() and text[end] == " ":
        end -= 1
    if end >= match.start() and text[end] == "(":
        return end
    found = text.find("(", match.end())
    return found


def is_timeout(kind: str, args: list[str]) -> bool:
    """Return whether this call is a banned wall-clock use.

    Args:
        kind: Rule name from CALLS.
        args: Top-level arguments. May be empty.

    Returns:
        True when the call must be reported.

    Raises:
        None.

    Side effects:
        None.
    """
    if kind == "always":
        return True
    if kind == "delay":
        if len(args) >= 3:
            return False
        if len(args) == 2 and TOKEN_ARG.search(args[1]) is None:
            return False
        return True
    if kind == "ctor-timeout":
        return len(args) > 0
    if kind == "any-arg":
        return len(args) > 0
    if kind == "extra-arg":
        return len(args) >= 2
    if kind == "wait-timeout":
        if len(args) == 0:
            return False
        if len(args) == 1 and TOKEN_ARG.search(args[0]) is not None and TIMESPAN_ARG.search(args[0]) is None:
            return False
        return True
    if kind == "wait-async":
        return any(TIMESPAN_ARG.search(arg) is not None for arg in args)
    return False


def calls_in(text: str) -> list[tuple[int, str]]:
    """Find banned calls in one normalized source.

    Args:
        text: Normalized code. Comments and strings are already gone.

    Returns:
        Pairs of start index and reported call, longest match first on a tie,
        with overlaps dropped.

    Raises:
        None.

    Side effects:
        None.
    """
    found: list[tuple[int, int, str]] = []
    for call, pattern, kind in CALLS:
        for match in pattern.finditer(text):
            if kind == "always":
                found.append((match.start(), match.end(), call))
                continue
            open_index = paren_at(text, match)
            args = top_level_args(text, open_index) if open_index >= 0 else []
            if is_timeout(kind, args):
                found.append((match.start(), match.end(), call))
    found.sort(key=lambda item: (item[0], -(item[1] - item[0])))
    kept: list[tuple[int, str]] = []
    occupied_until = -1
    for start, end, call in found:
        if start < occupied_until:
            continue
        kept.append((start, call))
        occupied_until = end
    return kept


def wall_hits_text(relative: str, source: str, allowed: set[tuple[str, int, str]]) -> list[str]:
    """Return formatted wall-clock hits for one source text.

    Args:
        relative: Path used in the report.
        source: C# text.
        allowed: Hits to suppress. The set is updated with entries that matched.

    Returns:
        One line per hit that was not allowlisted.

    Raises:
        None.

    Side effects:
        Removes used keys from a copy is not done. The caller passes the live set
        and this function records uses by discarding them from `allowed` only
        when `record_use` is the same set the caller checks. Uses are recorded
        in `used`, which the caller passes as `allowed` and also inspects.
    """
    masked = mask_non_code(source)
    normalized, line_map = collapse(masked)
    hits: list[str] = []
    seen: set[tuple[int, str]] = set()
    for start, call in calls_in(normalized):
        line_no = line_map[start]
        key = (relative, line_no, call)
        if key in seen:
            continue
        seen.add(key)
        if key in allowed:
            allowed.remove(key)
            continue
        hits.append(wall_line(relative, line_no, call))
    return hits


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


def parse_allowlist(text: str) -> tuple[set[tuple[str, int, str]], list[str]]:
    """Parse path:line:call: reason entries.

    Args:
        text: Allowlist file text.

    Returns:
        The keys, and problem strings for malformed lines.

    Raises:
        None.

    Side effects:
        None.
    """
    keys: set[tuple[str, int, str]] = set()
    problems: list[str] = []
    for raw in text.splitlines():
        line = raw.strip()
        if line == "" or line.startswith("#"):
            continue
        path, separator, rest = line.partition(":")
        number_text, separator2, rest2 = rest.partition(":")
        call, separator3, reason = rest2.partition(":")
        if separator == "" or separator2 == "" or separator3 == "" or reason.strip() == "":
            problems.append("clk-grep: allowlist entry needs a reason: " + line)
            continue
        if not number_text.isdigit() or int(number_text) <= 0:
            problems.append("clk-grep: allowlist line is not a positive number: " + line)
            continue
        keys.add((path, int(number_text), call))
    return keys, problems


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
        not count as a use of a deadline constant.

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
    and Instant match inside a PascalCase name. A name that is the product
    deadline (ShutdownDeadline or NewWriteBudget) is allowed when the body uses
    that constant. A name that carries a number is allowed when the body checks
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


def name_hits_lines(relative: str, lines: list[str]) -> list[str]:
    """Return illegal test-name hits in one file.

    Args:
        relative: Repository-relative path.
        lines: File lines.

    Returns:
        One formatted line per illegal name.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
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


def core_files() -> list[Path]:
    """Every C# file under ALog/Core, in path order.

    Returns:
        Source files. Build output is skipped.

    Raises:
        None.

    Side effects:
        Reads the directory.
    """
    found: list[Path] = []
    for path in sorted(CORE.rglob("*.cs")):
        parts = path.parts
        if "bin" in parts or "obj" in parts:
            continue
        found.append(path)
    return found


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

    injected = "\n".join([
        "class Injected {",
        "    void M() {",
        "        Task.Delay(TimeSpan.FromSeconds(1));",
        "        Thread.Sleep(1);",
        "        var a = Stopwatch.StartNew();",
        "        var b = Environment.TickCount;",
        "        var c = Environment.TickCount64;",
        "        var d = new Timer(callback);",
        "        System.Threading.Timer e;",
        "        System.Timers.Timer f;",
        "        var g = new CancellationTokenSource(TimeSpan.FromSeconds(1));",
        "        var h = new CancellationTokenSource(5);",
        "        cts.CancelAfter(1);",
        "        handle.WaitOne(1);",
        "        WaitHandle.WaitAny(handles, 1);",
        "        WaitHandle.WaitAll(handles, 1);",
        "        task.Wait(1);",
        "        task.WaitAsync(TimeSpan.FromSeconds(1));",
        "        SpinWait.SpinUntil(() => false);",
        "        var n1 = DateTime.Now;",
        "        var n2 = DateTime.UtcNow;",
        "        var n3 = DateTimeOffset.Now;",
        "        var n4 = DateTimeOffset.UtcNow;",
        "        var p = TimeProvider.System;",
        "        Thread",
        "            .Sleep (2);",
        "    }",
        "}",
    ])
    required = [
        "Task.Delay(",
        "Thread.Sleep",
        "Stopwatch",
        "Environment.TickCount",
        "Environment.TickCount64",
        "new Timer",
        "System.Threading.Timer",
        "System.Timers.Timer",
        "CancellationTokenSource(",
        "CancelAfter(",
        "WaitOne(",
        "WaitAny(",
        "WaitAll(",
        ".Wait(",
        "WaitAsync(",
        "SpinWait.SpinUntil",
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "TimeProvider.System",
    ]
    allowed: set[tuple[str, int, str]] = set()
    hits = wall_hits_text("ALog/Core/SelfCheck.cs", injected, allowed)
    for call in required:
        needle = "wall-clock call (" + call + ")"
        if not any(needle in hit for hit in hits):
            raise SystemExit("clk-grep: self-check failed for " + call)
    if not any("Thread.Sleep" in hit and hit.startswith("ALog/Core/SelfCheck.cs:25:") for hit in hits):
        raise SystemExit("clk-grep: self-check failed for a call split over lines")

    quiet = "\n".join([
        "class Quiet {",
        "    void M() {",
        "        // Stopwatch stays in this comment",
        "        /* DateTime.Now */",
        '        var text = "DateTime.Now";',
        '        var system = "TimeProvider.System";',
        '        var whole = $"DateTime.Now";',
        "        WaitHandle.WaitAny(new WaitHandle[] { signaled.WaitHandle, wake.WaitHandle });",
        "        task.Wait();",
        "        handle.WaitOne();",
        "        var timer = new PeriodicTimer(interval, clock);",
        "        var cts = new CancellationTokenSource();",
        "        Task.Delay(span, timeProvider);",
        "        task.WaitAsync(cancellationToken);",
        "        _wakeCts = new CancellationTokenSource();",
        "    }",
        "}",
    ])
    quiet_hits = wall_hits_text("ALog/Core/Quiet.cs", quiet, set())
    if quiet_hits:
        raise SystemExit("clk-grep: self-check flagged a non-hit: " + quiet_hits[0])
    hole = 'var hole = $"now {DateTime.Now}";'
    hole_hits = wall_hits_text("ALog/Core/Hole.cs", hole, set())
    if len(hole_hits) != 1 or "DateTime.Now" not in hole_hits[0]:
        raise SystemExit("clk-grep: self-check missed DateTime.Now inside an interpolation")

    listed = {("ALog/Core/LoggerEngine.cs", 1, "DateTime.UtcNow")}
    suppressed = wall_hits_text("ALog/Core/LoggerEngine.cs", "Timestamp: DateTime.UtcNow,", listed)
    if suppressed or listed:
        raise SystemExit("clk-grep: self-check allowlist did not suppress DateTime.UtcNow")

    names = "\n".join([
        "[Fact]",
        "public void DropsImmediately() { }",
        "[Fact]",
        "public void UsesFastPath() { }",
        "[Fact]",
        "public void ReturnsQuickly() { }",
        "[Fact]",
        "public void FinishesInstant() { }",
        "[Fact]",
        "public void DoneWithin5() { }",
        "[Fact]",
        "public void Limit10Ms() { }",
        "[Fact]",
        "public void Wait1Second() { }",
    ])
    name_hits = name_hits_lines("ALog.Tests/SelfCheck.cs", names.splitlines())
    expected_names = [
        "DropsImmediately",
        "UsesFastPath",
        "ReturnsQuickly",
        "FinishesInstant",
        "DoneWithin5",
        "Limit10Ms",
        "Wait1Second",
    ]
    for name in expected_names:
        if not any("'" + name + "'" in hit for hit in name_hits):
            raise SystemExit("clk-grep: self-check failed for time word " + name)
    sample = wall_line("ALog/Core/BackgroundLogQueue.cs", 1, "Task.Delay(")
    if "wall-clock call (" + "Task.Delay(" not in sample:
        raise SystemExit("clk-grep: self-check failed for wall line")


def main() -> int:
    """Run the self-check and both scans.

    Returns:
        0 when the tree is clean. 1 when at least one hit was reported.

    Raises:
        SystemExit: The self-check or the allowlist fails.
        OSError: A file cannot be read.

    Side effects:
        Writes the report to stdout.
    """
    self_check()
    if not Path(ALLOWLIST).is_file():
        raise SystemExit("clk-grep: allowlist file is missing")
    allowed, problems = parse_allowlist(Path(ALLOWLIST).read_text(encoding="utf-8"))
    if problems:
        raise SystemExit(problems[0])
    if not allowed:
        raise SystemExit("clk-grep: allowlist is empty")
    original = set(allowed)
    hits: list[str] = []
    files = core_files()
    if not files:
        raise SystemExit("clk-grep: ALog/Core has no C# files")
    for path in files:
        relative = path.as_posix()
        source = Path(relative).read_text(encoding="utf-8")
        if source.count("\n") + 1 > MAX_FILE_LINES:
            raise SystemExit("clk-grep: " + relative + " exceeds the line cap")
        hits.extend(wall_hits_text(relative, source, allowed))
    unused = sorted(allowed)
    if unused:
        path, line_no, call = unused[0]
        raise SystemExit(
            "clk-grep: allowlist entry was not used (" + path + ":" + str(line_no) + ":" + call + ")"
        )
    if len(original) < 1:
        raise SystemExit("clk-grep: allowlist is empty")
    tests = Path("ALog.Tests")
    for path in sorted(tests.rglob("*.cs")):
        relative = path.relative_to(".").as_posix()
        hits.extend(name_hits_lines(relative, read_lines(relative)))
    if hits:
        print("\n".join(hits))
        print("clk-grep: " + str(len(hits)) + " problem(s) found.")
        return 1
    print("clk-grep: ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
PY