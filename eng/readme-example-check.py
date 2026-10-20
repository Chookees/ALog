#!/usr/bin/env python3
# Screenshot recording (DOC-05):
# Command: dotnet run --project samples/ALog.QuickstartSample
# Terminal: XTerm(390) on Xvfb, background black, foreground white.
# Font: DejaVu Sans Mono, size 16.
# Window size: 1330x166 pixels, xterm geometry 102x6.
"""Keep the Quick Start README example tied to one real run.

The sample, the screenshot text, and the JSON excerpt are allowed to change
only together. This script is the CI check for that rule. It does not start
the sample: the caller passes the run's stdout transcript and ``logs/app.log``.
"""

from __future__ import annotations

import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

# The only wall-clock limit in this file. A git command that hangs stops here.
GUARD_SECONDS = 30

# 300 KB as 300 KiB. The captured PNG is far below either 300000 or 307200.
MAX_PNG_BYTES = 300 * 1024
MIN_PNG_WIDTH = 900
MAX_PNG_WIDTH = 1400
MAX_TEXT_BYTES = 2_000_000
MAX_JSON_VALUES = 100
MAX_TRACKED_PATHS = 10000
MAX_PROBLEMS = 20
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
ISO_TIMESTAMP = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z")
CLOCK_TIMESTAMP = re.compile(r"\d{2}:\d{2}:\d{2}")
TIMESTAMP_TOKEN = "<timestamp>"
# Absolute tree URL: nuget.org renders this README and does not resolve a relative link.
EXAMPLE_INTRO = (
    "Step 1 and Step 2 together are a complete program: "
    "[`samples/ALog.QuickstartSample`]"
    "(https://github.com/Chookees/ALog/tree/master/samples/ALog.QuickstartSample). "
    "Running it prints this to the console:"
)
IMAGE_LINE = (
    "![Console output of the Quick Start sample from Step 1 and Step 2]"
    "(https://raw.githubusercontent.com/Chookees/ALog/master/docs/images/quickstart-console.png)"
)
EXCERPT_INTRO = (
    "The first entry and the error entry from `logs/app.log` of the same run:"
)
EXAMPLE_OUTRO = "Both come from a real run of the sample, unedited."
STEP_HEADINGS = (
    ("step1", "### Step 1: Configure ALog"),
    ("step2", "### Step 2: Start Logging"),
)
CONSOLE_PNG = "docs/images/quickstart-console.png"
CONSOLE_TXT = "docs/images/quickstart-console.txt"
HEADER_SCRIPT = "eng/readme-example-check.py"
END_OF_OUTPUT = "<end of output>"
END_OF_FILE = "<end of file>"
END_OF_BLOCK = "<end of block>"
SAMPLE_PROGRAM = "samples/ALog.QuickstartSample/Program.cs"
MAX_COMPARE_LINES = MAX_TEXT_BYTES


def normalize_newlines(text: str) -> str:
    """Return text with CR LF and bare CR turned into LF.

    The README and the sample are compared after this change only, because a
    Windows checkout must not look like a code edit. The JSON excerpt and the
    console transcript do not use this helper.

    Args:
        text: File text. At most two million characters are meaningful.

    Returns:
        The same characters with normalized line endings.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(text) > MAX_TEXT_BYTES:
        return text[:MAX_TEXT_BYTES]
    return text.replace("\r\n", "\n").replace("\r", "\n")


def normalize_timestamps(text: str) -> str:
    """Replace ISO-8601 and ``HH:mm:ss`` timestamps with one placeholder.

    ISO-8601 is replaced first. The clock inside ``2026-10-20T20:55:19.4758886Z``
    is part of that timestamp, matching ``DateTime`` format ``o`` from the file
    writer and ``HH:mm:ss`` from Step 1. No other character is changed, so a
    hand edit stays visible.

    Args:
        text: Excerpt, log, transcript, or console text.

    Returns:
        The text with both timestamp forms replaced by ``<timestamp>``.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(text) > MAX_TEXT_BYTES:
        text = text[:MAX_TEXT_BYTES]
    without_iso = ISO_TIMESTAMP.sub(TIMESTAMP_TOKEN, text)
    return CLOCK_TIMESTAMP.sub(TIMESTAMP_TOKEN, without_iso)


def read_exact(path: Path, limit: int) -> tuple[str | None, str | None]:
    """Read one UTF-8 file without translating line endings.

    Args:
        path: File to read.
        limit: Maximum size in bytes. Larger files are rejected.

    Returns:
        ``(text, None)`` on success, or ``(None, problem)`` when the file is
        missing, too large, or not UTF-8.

    Raises:
        None.

    Side effects:
        Reads the file.
    """
    if limit < 1:
        return None, "read limit must be positive."
    if not path.is_file():
        return None, f"{path.name} is missing."
    size = path.stat().st_size
    if size > limit:
        return None, f"{path.name} is larger than {limit} bytes."
    try:
        data = path.read_bytes()
    except OSError:
        return None, f"{path.name} could not be read."
    if len(data) > limit:
        return None, f"{path.name} is larger than {limit} bytes."
    try:
        return data.decode("utf-8"), None
    except UnicodeError:
        return None, f"{path.name} is not UTF-8."


def git_env() -> dict[str, str]:
    """Return the process environment without a redirected git directory.

    A caller that exported ``GIT_DIR`` would make ``git log`` inspect the wrong
    repository. Dropping those variables keeps the check on ``--root``.

    Returns:
        A copy of the environment of at most 500 entries.

    Raises:
        None.

    Side effects:
        None.
    """
    skipped = {"GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"}
    copied: dict[str, str] = {}
    for key, value in os.environ.items():
        if len(copied) >= 500:
            break
        if key in skipped:
            continue
        copied[key] = value
    return copied


def run_git(root: Path, args: list[str]) -> tuple[str | None, str | None]:
    """Run one git command in the repository and capture stdout.

    Args:
        root: Repository root.
        args: Arguments after ``git``, at most 12.

    Returns:
        ``(stdout, None)`` on exit 0, or ``(None, problem)`` otherwise.

    Raises:
        None. A hang is stopped by the 30 s guard.

    Side effects:
        Reads git metadata. Does not write the index.
    """
    if len(args) < 1 or len(args) > 12:
        return None, "git argument list is outside 1..12."
    command = [
        "git",
        "-c",
        "core.hooksPath=/dev/null",
        "-c",
        "user.name=Readme Check",
        "-c",
        "user.email=readme-check@example.test",
        *args,
    ]
    try:
        result = subprocess.run(
            command,
            cwd=root,
            env=git_env(),
            capture_output=True,
            text=True,
            timeout=GUARD_SECONDS,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None, "git exceeded the 30 s guard or could not start."
    if result.returncode != 0:
        return None, "git command failed."
    return result.stdout, None


def last_commit(root: Path, relative: str) -> tuple[str | None, str | None]:
    """Return the commit that last changed one path.

    Args:
        root: Repository root.
        relative: Path relative to the root, as ``git log --`` expects it.

    Returns:
        ``(sha, None)`` when ``git log -1 --format=%H`` prints one hash, or
        ``(None, problem)`` when history does not name the file.

    Raises:
        None.

    Side effects:
        Reads git history.
    """
    if relative == "" or len(relative) > 200:
        return None, "git path is empty or too long."
    output, problem = run_git(root, ["log", "-1", "--format=%H", "--", relative])
    if problem is not None or output is None:
        return None, problem or "git log failed."
    sha = output.strip()
    if len(sha) != 40:
        return None, f"git log did not find a commit for {relative}."
    index = 0
    while index < 40:
        if sha[index] not in "0123456789abcdef":
            return None, f"git log hash for {relative} is not hexadecimal."
        index += 1
    return sha, None


def png_broken_message(found: str) -> str:
    """Return the U-DOC-06 line for a missing or broken screenshot file.

    Args:
        found: ``file missing``, ``no PNG signature``, or ``no IHDR chunk``.

    Returns:
        The full FAIL line. Callers print it unchanged.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL {CONSOLE_PNG}: expected a PNG file, found {found}. "
        f"Retake the screenshot as described in the header of {HEADER_SCRIPT}."
    )


def png_limits_message(width: int, size: int) -> str:
    """Return the U-DOC-06 line for a PNG outside the width or size limit.

    ``size // 1024`` is the KB figure because the accepted limit is
    ``300 * 1024`` bytes. A file of that many bytes is 300 KB and fails.

    Args:
        width: IHDR width in pixels.
        size: File size in bytes. Negative sizes are reported as 0 KB.

    Returns:
        The full FAIL line. Callers print it unchanged.

    Raises:
        None.

    Side effects:
        None.
    """
    kb = size // 1024
    if kb < 0:
        kb = 0
    return (
        f"FAIL {CONSOLE_PNG}: expected 900 to 1400 px wide and under 300 KB, "
        f"found {width} px, {kb} KB. Retake the screenshot with a fitting "
        "window size; crop only, do not scale or recompress."
    )


def png_problem(path: Path) -> str | None:
    """Validate the screenshot's signature, IHDR, width, and size.

    A missing file, a bad signature, and a missing IHDR are three separate
    U-DOC-06 texts. Width and size are checked only after those, so a large
    non-PNG is reported as broken rather than as the wrong dimensions.

    Args:
        path: ``docs/images/quickstart-console.png``.

    Returns:
        A U-DOC-06 FAIL line, or None when the PNG is inside the uiux limits.

    Raises:
        None.

    Side effects:
        Reads the first 24 bytes and the file size.
    """
    if not path.is_file():
        return png_broken_message("file missing")
    try:
        size = path.stat().st_size
        with path.open("rb") as handle:
            header = handle.read(24)
    except OSError:
        return png_broken_message("file missing")
    if len(header) < 8 or header[:8] != PNG_SIGNATURE:
        return png_broken_message("no PNG signature")
    if len(header) < 24:
        return png_broken_message("no IHDR chunk")
    length = int.from_bytes(header[8:12], "big")
    if header[12:16] != b"IHDR" or length != 13:
        return png_broken_message("no IHDR chunk")
    width = int.from_bytes(header[16:20], "big")
    if width < MIN_PNG_WIDTH or width > MAX_PNG_WIDTH or size >= MAX_PNG_BYTES:
        return png_limits_message(width, size)
    return None


def line_at(text: str, offset: int) -> int:
    """Return the 1-based line number that contains one character offset.

    Args:
        text: The file text. Line breaks are ``\\n``.
        offset: Character index. Values outside the text use the nearest end.

    Returns:
        The line number. Offset 0 is line 1.

    Raises:
        None.

    Side effects:
        None.
    """
    if offset < 0:
        offset = 0
    if offset > len(text):
        offset = len(text)
    return text.count("\n", 0, offset) + 1


def heading_region(text: str, heading: str) -> tuple[int, str]:
    """Return one README heading and the text before the next heading.

    A missing heading is an empty region. Callers then report zero code
    blocks with the U-DOC-07 count, instead of a separate missing-heading sentence.

    Args:
        text: Normalized README text.
        heading: Full heading line, including the leading ``###``.

    Returns:
        The heading's offset and its section, not including the next heading.

    Raises:
        None.

    Side effects:
        None.
    """
    start = text.find(heading)
    if start < 0:
        return 0, ""
    next_heading = text.find("\n### ", start + len(heading))
    if next_heading < 0:
        return start, text[start:]
    return start, text[start:next_heading]


def fenced_blocks(region: str, language: str) -> list[tuple[int, str]]:
    """Return every closed fence of one language inside a heading section.

    An opener without a closing fence is not counted. The walk stops at 20
    blocks so a repeated fence cannot run without a bound.

    Args:
        region: Heading section from ``heading_region``.
        language: ``csharp`` or ``json``, without the fence marks.

    Returns:
        Pairs of body offset within ``region`` and body text. The body
        includes the newline that precedes the closing fence.

    Raises:
        None.

    Side effects:
        None.
    """
    opener = "```" + language + "\n"
    blocks: list[tuple[int, str]] = []
    search = 0
    while search < len(region) and len(blocks) < 20:
        open_at = region.find(opener, search)
        if open_at < 0:
            break
        body_at = open_at + len(opener)
        close_at = region.find("\n```", body_at)
        if close_at < 0:
            break
        blocks.append((body_at, region[body_at : close_at + 1]))
        search = close_at + len("\n```")
    return blocks


def count_token(text: str, token: str) -> int:
    """Count non-overlapping copies of one marker, at most 100.

    Args:
        text: ``Program.cs`` text.
        token: The marker text, such as ``// readme:step1:begin``.

    Returns:
        The count, capped at 100.

    Raises:
        None.

    Side effects:
        None.
    """
    count = 0
    start = 0
    while count < 100:
        found = text.find(token, start)
        if found < 0:
            return count
        count += 1
        start = found + len(token)
    return count


def marker_status(program: str, step: int) -> tuple[int, int, bool]:
    """Count one step's begin and end markers and record reversed order.

    ``end before begin`` is true only when both markers exist and the first
    end appears earlier in the file than the first begin.

    Args:
        program: Normalized ``Program.cs`` text.
        step: 1 or 2.

    Returns:
        Begin count, end count, and whether end comes before begin.

    Raises:
        None.

    Side effects:
        None.
    """
    begin = f"// readme:step{step}:begin"
    end = f"// readme:step{step}:end"
    begin_at = program.find(begin)
    end_at = program.find(end)
    end_before = begin_at >= 0 and end_at >= 0 and end_at < begin_at
    return count_token(program, begin), count_token(program, end), end_before


def marker_body(program: str, step: int) -> tuple[int, str] | None:
    """Return the sample text between one step's first begin and first end.

    Args:
        program: Normalized ``Program.cs`` text.
        step: 1 or 2. The caller has already required one begin and one end
            in the right order.

    Returns:
        The body's offset and text, or None when the pair cannot be sliced.

    Raises:
        None.

    Side effects:
        None.
    """
    begin = f"// readme:step{step}:begin"
    end = f"// readme:step{step}:end"
    start = program.find(begin)
    if start < 0:
        return None
    body_at = start + len(begin)
    if body_at < len(program) and program[body_at] == "\n":
        body_at += 1
    finish = program.find(end, body_at)
    if finish < 0:
        return None
    return body_at, program[body_at:finish]


def marker_message(step: int, begins: int, ends: int, end_before: bool) -> str:
    """Return the U-DOC-07 line for a missing or reversed step marker.

    Args:
        step: 1 or 2.
        begins: How many begin markers were found, at most 100.
        ends: How many end markers were found, at most 100.
        end_before: When true, append ``, end before begin``.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    extra = ", end before begin" if end_before else ""
    return (
        f"FAIL {SAMPLE_PROGRAM}: expected exactly one // readme:step{step}:begin "
        f"followed by one // readme:step{step}:end, found {begins} begin, {ends} end{extra}. "
        f"Put the Step {step} code block from README.md between these two markers."
    )


def readme_block_message(step: int, count: int) -> str:
    """Return the U-DOC-07 line when a Step heading has not exactly one C# block.

    Args:
        step: 1 or 2.
        count: Closed ```csharp fences under that heading, at most 20.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL README.md: expected one C# code block under ### Step {step}, found {count}. "
        f"Restore the Step {step} code block; the sample copies it."
    )


def code_diff_message(
    readme_line: int,
    step: int,
    program_line: int,
    expected: str,
    found: str,
) -> str:
    """Return the U-DOC-07 line for the first differing code line.

    Args:
        readme_line: 1-based line in README.md.
        step: 1 or 2.
        program_line: 1-based line in ``Program.cs``.
        expected: The README line, or ``<end of block>`` when the README ended.
        found: The sample line, or ``<end of block>`` when the sample ended.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL README.md:{readme_line}: Step {step} code block differs from "
        f"{SAMPLE_PROGRAM}:{program_line} "
        f"(between // readme:step{step}:begin and end): "
        f'expected "{expected}", found "{found}". '
        "Change README and sample in the same commit so both blocks match byte for byte."
    )


def content_lines(body: str) -> list[str]:
    """Split a code block into lines without the fence's trailing break.

    Both blocks normally end with the newline that sits in front of the
    closing fence or the end marker. Dropping that one empty split item keeps
    it from looking like a blank line. The walk is capped at the text limit.

    Args:
        body: Text between the opener and the closer.

    Returns:
        The lines. An empty body has no lines.

    Raises:
        None.

    Side effects:
        None.
    """
    if body == "":
        return []
    lines = body.split("\n")
    if len(lines) > MAX_COMPARE_LINES:
        lines = lines[:MAX_COMPARE_LINES]
    if lines and lines[-1] == "":
        lines = lines[:-1]
    return lines


def block_line_diff(
    readme: str,
    readme_at: int,
    readme_body: str,
    program: str,
    program_at: int,
    program_body: str,
    step: int,
) -> str | None:
    """Return the first line where one step's README block and sample differ.

    A side that has no line at the difference is ``<end of block>``. The
    comparison uses the newline-normalized bodies, so a Windows checkout is
    not a code edit. Only this first line is reported.

    Args:
        readme: Full README, used for line numbers.
        readme_at: Offset of the README block body.
        readme_body: README block body.
        program: Full ``Program.cs``, used for line numbers.
        program_at: Offset of the sample block body.
        program_body: Sample block body.
        step: 1 or 2.

    Returns:
        The U-DOC-07 FAIL line, or None when the bodies are the same text.

    Raises:
        None.

    Side effects:
        None.
    """
    expected_lines = content_lines(readme_body)
    found_lines = content_lines(program_body)
    readme_line0 = line_at(readme, readme_at)
    program_line0 = line_at(program, program_at)
    count = len(expected_lines)
    if len(found_lines) > count:
        count = len(found_lines)
    index = 0
    while index < count:
        expected = expected_lines[index] if index < len(expected_lines) else None
        found = found_lines[index] if index < len(found_lines) else None
        if expected != found:
            return code_diff_message(
                readme_line0 + index,
                step,
                program_line0 + index,
                END_OF_BLOCK if expected is None else expected,
                END_OF_BLOCK if found is None else found,
            )
        index += 1
    if readme_body == program_body:
        return None
    readme_tail = "" if readme_body.endswith("\n") else END_OF_BLOCK
    program_tail = "" if program_body.endswith("\n") else END_OF_BLOCK
    return code_diff_message(
        readme_line0 + len(expected_lines),
        step,
        program_line0 + len(found_lines),
        readme_tail,
        program_tail,
    )


def one_step_problem(readme: str, program: str, heading: str, step: int) -> str | None:
    """Check one Quick Start step and return its first U-DOC-07 problem.

    A missing C# block is reported before markers, and markers are reported
    before a line difference. Later steps are the caller's concern.

    Args:
        readme: Normalized README text.
        program: Normalized ``Program.cs`` text.
        heading: The step heading line.
        step: 1 or 2.

    Returns:
        The FAIL line, or None when this step's block matches the sample.

    Raises:
        None.

    Side effects:
        None.
    """
    start, region = heading_region(readme, heading)
    blocks = fenced_blocks(region, "csharp")
    if len(blocks) != 1:
        return readme_block_message(step, len(blocks))
    begins, ends, end_before = marker_status(program, step)
    if begins != 1 or ends != 1 or end_before:
        return marker_message(step, begins, ends, end_before)
    located = marker_body(program, step)
    if located is None:
        return marker_message(step, begins, ends, end_before)
    program_at, program_body = located
    body_at, readme_body = blocks[0]
    return block_line_diff(
        readme,
        start + body_at,
        readme_body,
        program,
        program_at,
        program_body,
        step,
    )


def code_problems(readme: str, program: str) -> list[str]:
    """Compare Step 1 and Step 2 with the sample and return the first mismatch.

    Args:
        readme: Normalized README text.
        program: Normalized ``Program.cs`` text.

    Returns:
        A one-item list with the first U-DOC-07 code line, or an empty list
        when both blocks match the markers.

    Raises:
        None.

    Side effects:
        None.
    """
    index = 0
    while index < len(STEP_HEADINGS):
        _name, heading = STEP_HEADINGS[index]
        problem = one_step_problem(readme, program, heading, index + 1)
        if problem is not None:
            return [problem]
        index += 1
    return []


def wording_problem(readme: str) -> str | None:
    """Require the Example output heading, sentences, image, and position.

    The sample link in the sentence above the image must be the absolute
    GitHub tree URL. nuget.org shows the packed README and does not resolve
    a relative sample link, so a relative URL is a failed check.

    Args:
        readme: Normalized README text.

    Returns:
        A problem when the section is missing, moved, or reworded.

    Raises:
        None.

    Side effects:
        None.
    """
    step2 = readme.find("### Step 2: Start Logging")
    example = readme.find("### Example output")
    step3 = readme.find("### Step 3: Background Queue (Optional)")
    if step2 < 0 or example < 0 or step3 < 0 or not (step2 < example < step3):
        return "### Example output must sit between Step 2 and Step 3."
    required = (EXAMPLE_INTRO, IMAGE_LINE, EXCERPT_INTRO, EXAMPLE_OUTRO)
    index = 0
    while index < len(required):
        if required[index] not in readme:
            return "README Example output wording does not match the required text."
        index += 1
    return None


def split_json_values(text: str) -> tuple[list[tuple[int, str]], bool]:
    """Split concatenated JSON values without reformatting them.

    Args:
        text: A README json fence or a ``logs/app.log`` body.

    Returns:
        Each complete value as ``(offset, raw text)``, and True when the text
        ended in the middle of a value or held more than 100 values.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(text) > MAX_TEXT_BYTES:
        return [], True
    decoder = json.JSONDecoder()
    values: list[tuple[int, str]] = []
    index = 0
    length = len(text)
    while index < length:
        while index < length and text[index].isspace():
            index += 1
        if index >= length:
            break
        if len(values) >= MAX_JSON_VALUES:
            return values, True
        try:
            _parsed, end = decoder.raw_decode(text, index)
        except json.JSONDecodeError:
            return values, True
        if end <= index:
            return values, True
        values.append((index, text[index:end]))
        index = end
    return values, False


def entry_quote(text: str) -> str:
    """Return the first 120 characters of one normalized entry, then an ellipsis.

    The ellipsis is always present, including when the entry is shorter than
    120 characters. Timestamps must already be replaced.

    Args:
        text: Normalized JSON text of one entry, or of the fence when no
            complete entry could be read.

    Returns:
        At most 120 characters followed by ``…``.

    Raises:
        None.

    Side effects:
        None.
    """
    return text[:120] + "…"


def json_block_message(count: int) -> str:
    """Return the U-DOC-07 line when Example output has not exactly one json block.

    Args:
        count: Closed ```json fences under the heading, at most 20.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "FAIL README.md: expected one json code block under ### Example output, "
        f"found {count}. Restore the excerpt from logs/app.log of a sample run."
    )


def missing_entry_message(line: int, entry: int, quote: str) -> str:
    """Return the U-DOC-07 line for an excerpt entry the run log does not contain.

    Args:
        line: 1-based README line where the entry starts.
        entry: 1-based entry number inside the json block.
        quote: First 120 normalized characters plus the ellipsis.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL README.md:{line}: JSON excerpt entry {entry} not found as a complete "
        "entry in logs/app.log of the sample run (timestamps normalized): "
        f'"{quote}". Run dotnet run --project samples/ALog.QuickstartSample and '
        "copy the first entry and the error entry from logs/app.log unedited, each one whole."
    )


def order_message(line: int, entry: int) -> str:
    """Return the U-DOC-07 line when an excerpt entry appears too early in the log.

    Args:
        line: 1-based README line where this entry starts.
        entry: 1-based entry number. The previous entry is ``entry - 1``.

    Returns:
        The full FAIL line.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL README.md:{line}: JSON excerpt entry {entry} comes before entry {entry - 1} "
        "in logs/app.log, expected the order of the log. "
        "List the entries in the order they appear in logs/app.log."
    )


def find_entry(entries: list[str], wanted: str, start: int) -> int:
    """Find one normalized log entry at or after a position.

    Args:
        entries: Normalized log entries. At most 100 are scanned.
        wanted: Normalized excerpt entry.
        start: First index that may count. Negative values start at 0.

    Returns:
        The index, or -1 when no later entry matches.

    Raises:
        None.

    Side effects:
        None.
    """
    index = start
    if index < 0:
        index = 0
    while index < len(entries) and index < MAX_JSON_VALUES:
        if entries[index] == wanted:
            return index
        index += 1
    return -1


def excerpt_mismatch(
    readme: str,
    fence_at: int,
    fence: str,
    excerpt: list[tuple[int, str]],
    excerpt_bad: bool,
    normalized_log: list[str],
) -> str | None:
    """Return the first excerpt entry that is missing or out of log order.

    Entries between the chosen ones may be absent. A match is the whole
    normalized value, so part of an entry does not count. The search continues
    after the previous match; an earlier hit is the order failure.

    Args:
        readme: Normalized README, used for line numbers.
        fence_at: Offset of the json body in ``readme``.
        fence: Json body, quoted when it holds no complete entry.
        excerpt: Complete values and their offsets inside ``fence``.
        excerpt_bad: True when another value was cut off after ``excerpt``.
        normalized_log: Complete log entries with timestamps replaced.

    Returns:
        The U-DOC-07 FAIL line, or None when every excerpt entry is an
        in-order complete log entry.

    Raises:
        None.

    Side effects:
        None.
    """
    position = 0
    index = 0
    while index < len(excerpt):
        offset, raw = excerpt[index]
        wanted = normalize_timestamps(raw)
        found_at = find_entry(normalized_log, wanted, position)
        if found_at >= 0:
            position = found_at + 1
            index += 1
            continue
        line = line_at(readme, fence_at + offset)
        entry = index + 1
        if find_entry(normalized_log, wanted, 0) >= 0:
            return order_message(line, entry)
        return missing_entry_message(line, entry, entry_quote(wanted))
    if excerpt_bad or len(excerpt) < 1:
        return missing_entry_message(
            line_at(readme, fence_at),
            len(excerpt) + 1,
            entry_quote(normalize_timestamps(fence)),
        )
    return None


def excerpt_problem(readme: str, log_text: str) -> str | None:
    """Require the Example output json block to be complete log entries, in order.

    Args:
        readme: Normalized README text.
        log_text: Exact ``logs/app.log`` text from the run.

    Returns:
        The first U-DOC-07 excerpt line, or None when the block matches.

    Raises:
        None.

    Side effects:
        None.
    """
    start, region = heading_region(readme, "### Example output")
    blocks = fenced_blocks(region, "json")
    if len(blocks) != 1:
        return json_block_message(len(blocks))
    body_at, fence = blocks[0]
    excerpt, excerpt_bad = split_json_values(fence)
    logged, _log_bad = split_json_values(log_text)
    normalized_log = [normalize_timestamps(raw) for _offset, raw in logged]
    return excerpt_mismatch(
        readme,
        start + body_at,
        fence,
        excerpt,
        excerpt_bad,
        normalized_log,
    )


def stdout_fail_message(line_number: int, expected: str, found: str) -> str:
    """Return the U-DOC-06 line for the first console mismatch.

    Args:
        line_number: 1-based line in the TXT after timestamp replacement.
        expected: That TXT line, or ``<end of file>`` when the TXT ended.
        found: The sample line, or ``<end of output>`` when the sample ended.

    Returns:
        The full FAIL line. Callers print it unchanged.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL {CONSOLE_TXT}:{line_number}: expected \"{expected}\", "
        f"found \"{found}\" in the sample output. "
        "The console output changed, so the screenshot is out of date: "
        "run dotnet run --project samples/ALog.QuickstartSample, "
        f"retake {CONSOLE_PNG} as described in the header of {HEADER_SCRIPT}, "
        "and commit it together with the new quickstart-console.txt."
    )


def first_console_mismatch(expected_text: str, found_text: str) -> str:
    """Name the first LF line that differs after timestamp replacement.

    The walk stops at the first difference. A side with no line at that
    index is the end marker, not an empty string. No other character is
    changed. The loop is capped at the text read limit.

    Args:
        expected_text: Normalized ``quickstart-console.txt``.
        found_text: Normalized sample transcript. Known to differ.

    Returns:
        The U-DOC-06 FAIL line for that first difference.

    Raises:
        None.

    Side effects:
        None.
    """
    expected_lines = expected_text.split("\n")
    found_lines = found_text.split("\n")
    if len(expected_lines) > MAX_COMPARE_LINES:
        expected_lines = expected_lines[:MAX_COMPARE_LINES]
    if len(found_lines) > MAX_COMPARE_LINES:
        found_lines = found_lines[:MAX_COMPARE_LINES]
    count = len(expected_lines)
    if len(found_lines) > count:
        count = len(found_lines)
    index = 0
    while index < count:
        expected_line = expected_lines[index] if index < len(expected_lines) else None
        found_line = found_lines[index] if index < len(found_lines) else None
        if expected_line != found_line:
            shown_expected = END_OF_FILE if expected_line is None else expected_line
            shown_found = END_OF_OUTPUT if found_line is None else found_line
            return stdout_fail_message(index + 1, shown_expected, shown_found)
        index += 1
    return stdout_fail_message(1, END_OF_FILE, END_OF_OUTPUT)


def stdout_problem(stdout_text: str, console_text: str) -> str | None:
    """Compare the run transcript with the committed console text.

    Args:
        stdout_text: Prompt, command, and process stdout from the run.
        console_text: ``docs/images/quickstart-console.txt``.

    Returns:
        None when the texts match after timestamp replacement. Otherwise the
        U-DOC-06 line for the first differing line.

    Raises:
        None.

    Side effects:
        None.
    """
    expected_text = normalize_timestamps(console_text)
    found_text = normalize_timestamps(stdout_text)
    if expected_text == found_text:
        return None
    return first_console_mismatch(expected_text, found_text)


def tracked_log_message(path: str) -> str:
    """Return the U-DOC-07 line for one tracked file under ``logs/``.

    Args:
        path: Path as ``git ls-files`` prints it, using ``/`` separators.

    Returns:
        The full FAIL line, including the ``git rm --cached`` command.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        f"FAIL {path}: expected no tracked files under logs/, found this one in git. "
        f"Remove it with git rm --cached {path}; .gitignore already ignores logs/."
    )


def tracked_log_problems(root: Path) -> list[str]:
    """List every git-tracked file under ``logs/`` or ``Logs/``.

    One FAIL line is returned per file, in ``git ls-files`` order. The walk
    stops after 10000 index paths or 20 tracked log files.

    Args:
        root: Repository root.

    Returns:
        The U-DOC-07 lines. Empty when nothing under ``logs/`` is tracked.
        A git failure is returned as that command's own problem.

    Raises:
        None.

    Side effects:
        Reads the git index.
    """
    output, problem = run_git(root, ["ls-files", "-z"])
    if problem is not None or output is None:
        return [problem or "git command failed."]
    found: list[str] = []
    seen = 0
    for path in output.split("\0"):
        if path == "":
            continue
        seen += 1
        if seen > MAX_TRACKED_PATHS or len(found) >= MAX_PROBLEMS:
            break
        parts = path.split("/")
        index = 0
        while index < len(parts) - 1 and index < 20:
            if parts[index] in ("logs", "Logs"):
                found.append(tracked_log_message(path))
                break
            index += 1
    return found


def same_commit_problem(root: Path) -> str | None:
    """Require the PNG and the TXT to share one last-changed commit.

    Args:
        root: Repository root.

    Returns:
        None when ``git log -1 --format=%H -- <file>`` is the same hash for
        both screenshot files. Otherwise the U-DOC-06 line with both 7-character hashes.

    Raises:
        None.

    Side effects:
        Reads git history.
    """
    png, png_problem_text = last_commit(root, "docs/images/quickstart-console.png")
    txt, txt_problem_text = last_commit(root, "docs/images/quickstart-console.txt")
    if png_problem_text is not None:
        return png_problem_text
    if txt_problem_text is not None:
        return txt_problem_text
    if png != txt:
        return (
            f"FAIL {CONSOLE_PNG} and quickstart-console.txt: "
            "expected both last changed in the same commit, "
            f"found png {png[:7]}, txt {txt[:7]}. "
            "Retake the screenshot from the current output and commit the PNG together with the TXT."
        )
    return None


def append_problem(problems: list[str], problem: str | None) -> None:
    """Keep at most 20 problems so a broken tree cannot flood the log.

    Args:
        problems: Collected problems. Mutated when there is room.
        problem: None is ignored.

    Returns:
        None.

    Raises:
        None.

    Side effects:
        Appends to ``problems``.
    """
    if problem is None or len(problems) >= MAX_PROBLEMS:
        return
    problems.append(problem)


def check_repository(root: Path, stdout_path: Path, log_path: Path) -> list[str]:
    """Run every README example comparison for one repository and one run.

    Args:
        root: Repository root that contains the README, the sample, and the image.
        stdout_path: Console transcript from the run being checked.
        log_path: ``logs/app.log`` from that same run.

    Returns:
        Problems. Empty means the unchanged example is still true.

    Raises:
        None.

    Side effects:
        Reads the repository and the two run files. Reads git history.
    """
    problems: list[str] = []
    if not root.is_dir():
        return ["repository root is missing."]
    readme_text, readme_error = read_exact(root / "README.md", MAX_TEXT_BYTES)
    program_text, program_error = read_exact(
        root / "samples/ALog.QuickstartSample/Program.cs",
        MAX_TEXT_BYTES,
    )
    console_text, console_error = read_exact(
        root / "docs/images/quickstart-console.txt",
        MAX_TEXT_BYTES,
    )
    stdout_text, stdout_error = read_exact(stdout_path, MAX_TEXT_BYTES)
    log_text, log_error = read_exact(log_path, MAX_TEXT_BYTES)
    append_problem(problems, readme_error)
    append_problem(problems, program_error)
    append_problem(problems, console_error)
    append_problem(problems, stdout_error)
    append_problem(problems, log_error)
    append_problem(problems, png_problem(root / "docs/images/quickstart-console.png"))
    append_problem(problems, same_commit_problem(root))
    for problem in tracked_log_problems(root):
        append_problem(problems, problem)
    if readme_text is None or program_text is None:
        return problems
    readme = normalize_newlines(readme_text)
    program = normalize_newlines(program_text)
    append_problem(problems, wording_problem(readme))
    for problem in code_problems(readme, program):
        append_problem(problems, problem)
    if log_text is not None:
        append_problem(problems, excerpt_problem(readme, log_text))
    if stdout_text is not None and console_text is not None:
        append_problem(problems, stdout_problem(stdout_text, console_text))
    return problems


def png_chunk(tag: bytes, payload: bytes) -> bytes:
    """Build one PNG chunk, including its length and CRC.

    Args:
        tag: Four-byte chunk type.
        payload: Chunk data. At most one million bytes.

    Returns:
        The encoded chunk.

    Raises:
        ValueError: The tag or the payload is the wrong size.

    Side effects:
        None.
    """
    if len(tag) != 4 or len(payload) > 1_000_000:
        raise ValueError("png chunk is not encodable.")
    crc = zlib.crc32(tag + payload) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", crc)


def encode_png(width: int, height: int) -> bytes:
    """Encode a black RGB PNG so a self-test can choose an illegal size.

    Args:
        width: Pixel width from 1 to 4000.
        height: Pixel height from 1 to 8.

    Returns:
        A PNG with a valid signature and a 13-byte IHDR.

    Raises:
        ValueError: A dimension is outside the encoder's bounds.

    Side effects:
        None.
    """
    if width < 1 or width > 4000 or height < 1 or height > 8:
        raise ValueError("png fixture dimensions are out of range.")
    row = b"\x00" + (b"\x00\x00\x00" * width)
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    encoded = PNG_SIGNATURE + png_chunk(b"IHDR", ihdr)
    encoded += png_chunk(b"IDAT", zlib.compress(row * height, 9))
    encoded += png_chunk(b"IEND", b"")
    return encoded


def write_fixture_png(path: Path, width: int) -> None:
    """Write a small valid RGB PNG for the self-test.

    Args:
        path: Destination. Parent directories must exist.
        width: Image width. Must be from 900 to 1400.

    Returns:
        None.

    Raises:
        ValueError: The width is outside the check's accepted range.

    Side effects:
        Writes the file.
    """
    if width < MIN_PNG_WIDTH or width > MAX_PNG_WIDTH:
        raise ValueError("fixture png width is outside 900-1400.")
    path.write_bytes(encode_png(width, 2))


def fixture_entry(timestamp: str, level: str, message: str) -> str:
    """Build one pretty JSON object the self-test can match exactly.

    Args:
        timestamp: ISO-8601 text placed in the timestamp field.
        level: Level name.
        message: Message text. Must not contain quotes.

    Returns:
        One JSON object with LF line endings and no trailing newline.

    Raises:
        ValueError: The message contains a quote or a newline.

    Side effects:
        None.
    """
    if "\"" in message or "\n" in message or len(message) > 80:
        raise ValueError("fixture message is not a single plain line.")
    return (
        "{\n"
        f"  \"timestamp\": \"{timestamp}\",\n"
        f"  \"level\": \"{level}\",\n"
        f"  \"message\": \"{message}\"\n"
        "}"
    )


def fixture_readme() -> str:
    """Return the fixture README, including an excerpt with its own timestamps.

    Returns:
        README text whose csharp blocks match ``fixture_program`` and whose
        JSON uses different timestamps from the run log.

    Raises:
        ValueError: A fixture message was rejected.

    Side effects:
        None.
    """
    shown_first = fixture_entry(
        "2026-10-20T20:55:19.4758886Z", "Info", "Application started"
    )
    shown_error = fixture_entry(
        "2026-10-20T20:55:19.5318101Z", "Error", "Something went wrong"
    )
    return (
        "### Step 1: Configure ALog\n\n"
        "```csharp\n"
        "using Sample;\n"
        "\n"
        "var ready = 1;\n"
        "```\n\n"
        "### Step 2: Start Logging\n\n"
        "```csharp\n"
        "var ran = 2;\n"
        "```\n\n"
        "### Example output\n\n"
        f"{EXAMPLE_INTRO}\n\n"
        f"{IMAGE_LINE}\n\n"
        f"{EXCERPT_INTRO}\n\n"
        "```json\n"
        f"{shown_first}\n"
        f"{shown_error}\n"
        "```\n\n"
        f"{EXAMPLE_OUTRO}\n\n"
        "### Step 3: Background Queue (Optional)\n"
    )


def fixture_program() -> str:
    """Return the fixture ``Program.cs``, markers included.

    Returns:
        The sample whose marker bodies are the README csharp blocks.

    Raises:
        None.

    Side effects:
        None.
    """
    return (
        "// readme:step1:begin\n"
        "using Sample;\n"
        "\n"
        "var ready = 1;\n"
        "// readme:step1:end\n"
        "\n"
        "// readme:step2:begin\n"
        "var ran = 2;\n"
        "// readme:step2:end\n"
    )


def write_fixture_tree(root: Path) -> None:
    """Write the README, sample, PNG, and console text for one fixture.

    Args:
        root: Repository root. Created here, and must not already exist.

    Returns:
        None.

    Raises:
        ValueError: A fixture message was rejected.
        FileExistsError: ``root`` already exists.

    Side effects:
        Creates the sample and image directories and writes four tracked files.
    """
    root.mkdir(parents=True, exist_ok=False)
    sample = root / "samples" / "ALog.QuickstartSample"
    images = root / "docs" / "images"
    sample.mkdir(parents=True)
    images.mkdir(parents=True)
    (sample / "Program.cs").write_text(fixture_program(), encoding="utf-8")
    (root / "README.md").write_text(fixture_readme(), encoding="utf-8")
    console = (
        "$ dotnet run --project samples/ALog.QuickstartSample\n"
        "[20:55:19] [Info] Application started\n"
        "[11:22:33] [Error] Something went wrong\n"
    )
    (images / "quickstart-console.txt").write_text(console, encoding="utf-8")
    write_fixture_png(images / "quickstart-console.png", 1000)


def write_fixture_run(root: Path) -> tuple[Path, Path]:
    """Write the untracked transcript and log that make the fixture green.

    Args:
        root: Fixture root that already contains the README files.

    Returns:
        Paths of the transcript and the log. The log includes a middle entry
        the excerpt does not show, with different timestamps from the README.

    Raises:
        ValueError: A fixture message was rejected.

    Side effects:
        Writes ``run-stdout.txt`` and ``run-log.txt``.
    """
    first = fixture_entry("2026-10-20T01:02:03.0000000Z", "Info", "Application started")
    middle = fixture_entry("2026-10-20T01:02:04.0000000Z", "Info", "middle entry")
    error = fixture_entry("2026-10-20T01:02:05.0000000Z", "Error", "Something went wrong")
    stdout = (
        "$ dotnet run --project samples/ALog.QuickstartSample\n"
        "[01:02:03] [Info] Application started\n"
        "[04:05:06] [Error] Something went wrong\n"
    )
    stdout_path = root / "run-stdout.txt"
    log_path = root / "run-log.txt"
    stdout_path.write_text(stdout, encoding="utf-8")
    log_path.write_text(first + "\n" + middle + "\n" + error + "\n", encoding="utf-8")
    return stdout_path, log_path


def commit_fixture(root: Path) -> None:
    """Commit the PNG and the TXT together so the unchanged fixture is green.

    Args:
        root: Fixture repository root. ``git init`` has not been run yet.

    Returns:
        None.

    Raises:
        RuntimeError: git init, add, or commit failed.

    Side effects:
        Creates one commit. The run files stay untracked.
    """
    init_out, init_problem = run_git(root, ["init", "-b", "master"])
    if init_problem is not None:
        raise RuntimeError(init_problem)
    del init_out
    add_out, add_problem = run_git(
        root,
        [
            "add",
            "README.md",
            "samples/ALog.QuickstartSample/Program.cs",
            "docs/images/quickstart-console.png",
            "docs/images/quickstart-console.txt",
        ],
    )
    if add_problem is not None:
        raise RuntimeError(add_problem)
    del add_out
    commit_out, commit_problem = run_git(root, ["commit", "-m", "fixture example"])
    if commit_problem is not None:
        raise RuntimeError(commit_problem)
    del commit_out


def write_fixture(root: Path) -> tuple[Path, Path]:
    """Create a git repository whose unchanged example check is green.

    Args:
        root: Empty directory that becomes the repository.

    Returns:
        Paths of the run transcript and the run log.

    Raises:
        RuntimeError: git could not create the commit.
        ValueError: A fixture string was rejected.

    Side effects:
        Writes files and creates one git commit containing the PNG and the TXT.
    """
    write_fixture_tree(root)
    stdout_path, log_path = write_fixture_run(root)
    commit_fixture(root)
    return stdout_path, log_path


def report_case(label: str, problems: list[str], phrase: str) -> int:
    """Print one self-test result.

    Args:
        label: Case name.
        problems: Check output. Empty is green.
        phrase: Text that must appear when the case is expected to be red.
            Empty means the case is expected to be green.

    Returns:
        0 when the color matches, 1 otherwise.

    Raises:
        None.

    Side effects:
        Writes one line to stdout or stderr.
    """
    text = "\n".join(problems)
    if phrase == "":
        if problems:
            sys.stderr.write(f"FAIL {label}: expected green: {text}\n")
            return 1
        sys.stdout.write(f"ok {label}\n")
        return 0
    if problems and phrase in text:
        sys.stdout.write(f"ok {label}\n")
        return 0
    sys.stderr.write(f"FAIL {label}: expected red containing {phrase!r}: {text}\n")
    return 1


def report_exact(label: str, problems: list[str], expected: str) -> int:
    """Require one red case to be exactly one U-DOC-06 line.

    Args:
        label: Case name.
        problems: Check output.
        expected: The full failure line, with runtime fields already filled.

    Returns:
        0 when that line is the only problem, 1 otherwise.

    Raises:
        None.

    Side effects:
        Writes one line to stdout or stderr.
    """
    if problems == [expected]:
        sys.stdout.write(f"ok {label}\n")
        return 0
    sys.stderr.write(f"FAIL {label}: expected exact {expected!r}: {problems!r}\n")
    return 1


def report_exact_many(label: str, problems: list[str], expected: list[str]) -> int:
    """Require one red case to be exactly these U-DOC-07 lines, in order.

    Args:
        label: Case name.
        problems: Check output.
        expected: The full failure lines, with runtime fields already filled.

    Returns:
        0 when the lists are equal, 1 otherwise.

    Raises:
        None.

    Side effects:
        Writes one line to stdout or stderr.
    """
    if problems == expected:
        sys.stdout.write(f"ok {label}\n")
        return 0
    sys.stderr.write(f"FAIL {label}: expected exact {expected!r}: {problems!r}\n")
    return 1


def self_test() -> int:
    """Prove the U-DOC-06 and U-DOC-07 red texts and the unchanged green case.

    Each red case starts from a fresh fixture so one mutation cannot hide
    another. The green case is that fixture before any mutation. The code,
    excerpt, tracked-log, stdout, commit, PNG, and size cases require the
    full failure line, not a fragment.

    Returns:
        0 when every case has the required color, 1 otherwise.

    Raises:
        None. Fixture setup errors are reported as a failed case.

    Side effects:
        Creates and deletes a temporary directory. Runs git there.
    """
    failures = 0
    try:
        with tempfile.TemporaryDirectory(prefix="readme-check-") as tmp_name:
            tmp = Path(tmp_name)
            failures += case_unchanged(tmp / "green")
            failures += case_missing_png(tmp / "missing")
            failures += case_bad_signature(tmp / "signature")
            failures += case_no_ihdr(tmp / "ihdr")
            failures += case_png_narrow(tmp / "narrow")
            failures += case_png_too_large(tmp / "large")
            failures += case_txt_differs(tmp / "txt")
            failures += case_stdout_shorter(tmp / "short-out")
            failures += case_txt_shorter(tmp / "short-txt")
            failures += case_excerpt_edited(tmp / "excerpt")
            failures += case_excerpt_order(tmp / "order")
            failures += case_json_missing(tmp / "json")
            failures += case_code_differs(tmp / "code")
            failures += case_code_shorter(tmp / "code-short")
            failures += case_marker_missing(tmp / "marker")
            failures += case_marker_reversed(tmp / "reversed")
            failures += case_readme_block_missing(tmp / "csharp")
            failures += case_tracked_logs(tmp / "tracked")
            failures += case_different_commits(tmp / "commits")
    except (OSError, RuntimeError, ValueError) as exc:
        sys.stderr.write(f"FAIL self-test setup: {exc}\n")
        return 1
    if failures != 0:
        sys.stderr.write(f"{failures} readme-example-check self-tests failed\n")
        return 1
    sys.stdout.write("readme-example-check self-test passed\n")
    return 0


def case_unchanged(root: Path) -> int:
    """The fixture before mutation must be green.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check reports no problems.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository.
    """
    stdout_path, log_path = write_fixture(root)
    return report_case("unchanged", check_repository(root, stdout_path, log_path), "")


def case_missing_png(root: Path) -> int:
    """A missing PNG must be red.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check names the missing PNG.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and deletes the PNG.
    """
    stdout_path, log_path = write_fixture(root)
    (root / "docs/images/quickstart-console.png").unlink()
    return report_exact(
        "missing png",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.png: expected a PNG file, found file missing. "
        "Retake the screenshot as described in the header of eng/readme-example-check.py.",
    )


def case_bad_signature(root: Path) -> int:
    """A PNG with a broken signature must be red.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check names the signature.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and overwrites the PNG's first byte.
    """
    stdout_path, log_path = write_fixture(root)
    png = root / "docs/images/quickstart-console.png"
    data = bytearray(png.read_bytes())
    data[0] = ord("X")
    png.write_bytes(data)
    return report_exact(
        "broken signature",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.png: expected a PNG file, found no PNG signature. "
        "Retake the screenshot as described in the header of eng/readme-example-check.py.",
    )


def case_no_ihdr(root: Path) -> int:
    """A signature without an IHDR chunk must use the IHDR failure text.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem is the missing-IHDR line.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and replaces the PNG with a header only.
    """
    stdout_path, log_path = write_fixture(root)
    png = root / "docs/images/quickstart-console.png"
    png.write_bytes(PNG_SIGNATURE + b"\x00\x00\x00\x00IEND")
    return report_exact(
        "no ihdr",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.png: expected a PNG file, found no IHDR chunk. "
        "Retake the screenshot as described in the header of eng/readme-example-check.py.",
    )


def case_png_narrow(root: Path) -> int:
    """A PNG narrower than 900 px must use the dimension failure text.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem quotes 800 px and the file's KB size.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and replaces the PNG.
    """
    stdout_path, log_path = write_fixture(root)
    png = root / "docs/images/quickstart-console.png"
    png.write_bytes(encode_png(800, 2))
    kb = png.stat().st_size // 1024
    return report_exact(
        "png too narrow",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.png: expected 900 to 1400 px wide and under 300 KB, "
        f"found 800 px, {kb} KB. Retake the screenshot with a fitting window size; "
        "crop only, do not scale or recompress.",
    )


def case_png_too_large(root: Path) -> int:
    """A PNG of 300 KiB must use the size failure text.

    Padding keeps the IHDR, so the failure is the size and not a broken file.
    300 times 1024 bytes is 300 KB under the check's integer KB figure.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says 1000 px and 300 KB.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and pads the PNG to the size limit.
    """
    stdout_path, log_path = write_fixture(root)
    png = root / "docs/images/quickstart-console.png"
    data = png.read_bytes()
    if len(data) >= MAX_PNG_BYTES:
        raise RuntimeError("fixture png is already at the size limit.")
    png.write_bytes(data + (b"\x00" * (MAX_PNG_BYTES - len(data))))
    return report_exact(
        "png too large",
        check_repository(root, stdout_path, log_path),
        (
            "FAIL docs/images/quickstart-console.png: expected 900 to 1400 px "
            "wide and under 300 KB, found 1000 px, 300 KB. Retake the screenshot "
            "with a fitting window size; crop only, do not scale or recompress."
        ),
    )


def case_txt_differs(root: Path) -> int:
    """A changed console line must name that line and no later line.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem is line 2 with both normalized texts.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and edits one TXT line.
    """
    stdout_path, log_path = write_fixture(root)
    txt = root / "docs/images/quickstart-console.txt"
    txt.write_text(
        txt.read_text(encoding="utf-8").replace("Application started", "Application changed", 1),
        encoding="utf-8",
    )
    return report_exact(
        "txt line differs",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.txt:2: "
        'expected "[<timestamp>] [Info] Application changed", '
        'found "[<timestamp>] [Info] Application started" in the sample output. '
        "The console output changed, so the screenshot is out of date: "
        "run dotnet run --project samples/ALog.QuickstartSample, "
        "retake docs/images/quickstart-console.png as described in the header of "
        "eng/readme-example-check.py, and commit it together with the new quickstart-console.txt.",
    )


def case_stdout_shorter(root: Path) -> int:
    """A transcript that ends early must say end of output on that line.

    Args:
        root: Directory for this case.

    Returns:
        0 when line 3 expected the error line and found end of output.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and drops the transcript's last line.
    """
    stdout_path, log_path = write_fixture(root)
    parts = stdout_path.read_text(encoding="utf-8").split("\n")
    if len(parts) < 4:
        raise RuntimeError("fixture transcript is too short.")
    stdout_path.write_text(parts[0] + "\n" + parts[1], encoding="utf-8")
    return report_exact(
        "stdout shorter",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.txt:3: "
        'expected "[<timestamp>] [Error] Something went wrong", '
        'found "<end of output>" in the sample output. '
        "The console output changed, so the screenshot is out of date: "
        "run dotnet run --project samples/ALog.QuickstartSample, "
        "retake docs/images/quickstart-console.png as described in the header of "
        "eng/readme-example-check.py, and commit it together with the new quickstart-console.txt.",
    )


def case_txt_shorter(root: Path) -> int:
    """A TXT that ends early must say end of file on that line.

    Args:
        root: Directory for this case.

    Returns:
        0 when line 3 expected end of file and found the error line.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and drops the TXT's last line.
    """
    stdout_path, log_path = write_fixture(root)
    txt = root / "docs/images/quickstart-console.txt"
    parts = txt.read_text(encoding="utf-8").split("\n")
    if len(parts) < 4:
        raise RuntimeError("fixture console text is too short.")
    txt.write_text(parts[0] + "\n" + parts[1], encoding="utf-8")
    return report_exact(
        "txt shorter",
        check_repository(root, stdout_path, log_path),
        "FAIL docs/images/quickstart-console.txt:3: "
        'expected "<end of file>", '
        'found "[<timestamp>] [Error] Something went wrong" in the sample output. '
        "The console output changed, so the screenshot is out of date: "
        "run dotnet run --project samples/ALog.QuickstartSample, "
        "retake docs/images/quickstart-console.png as described in the header of "
        "eng/readme-example-check.py, and commit it together with the new quickstart-console.txt.",
    )


def case_excerpt_edited(root: Path) -> int:
    """A hand-edited JSON excerpt must be red.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check rejects the excerpt.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and edits the README JSON.
    """
    stdout_path, log_path = write_fixture(root)
    readme = root / "README.md"
    readme.write_text(
        readme.read_text(encoding="utf-8").replace(
            "Application started",
            "Application edited",
            1,
        ),
        encoding="utf-8",
    )
    quote = (
        "{\n"
        "  \"timestamp\": \"<timestamp>\",\n"
        "  \"level\": \"Info\",\n"
        "  \"message\": \"Application edited\"\n"
        "}…"
    )
    return report_exact(
        "excerpt hand-edited",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md:24: JSON excerpt entry 1 not found as a complete entry "
        "in logs/app.log of the sample run (timestamps normalized): "
        f'"{quote}". Run dotnet run --project samples/ALog.QuickstartSample and '
        "copy the first entry and the error entry from logs/app.log unedited, each one whole.",
    )


def case_excerpt_order(root: Path) -> int:
    """An excerpt that reverses two log entries must name that order.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says entry 2 comes before entry 1.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and swaps the two README JSON entries.
    """
    stdout_path, log_path = write_fixture(root)
    readme = root / "README.md"
    text = readme.read_text(encoding="utf-8")
    first = fixture_entry("2026-10-20T20:55:19.4758886Z", "Info", "Application started")
    error = fixture_entry("2026-10-20T20:55:19.5318101Z", "Error", "Something went wrong")
    swapped = text.replace(first + "\n" + error, error + "\n" + first, 1)
    if swapped == text:
        raise RuntimeError("fixture excerpt could not be reversed.")
    readme.write_text(swapped, encoding="utf-8")
    return report_exact(
        "excerpt order",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md:29: JSON excerpt entry 2 comes before entry 1 in logs/app.log, "
        "expected the order of the log. List the entries in the order they appear in logs/app.log.",
    )


def case_json_missing(root: Path) -> int:
    """Example output with no json fence must use the missing-block line.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says zero json blocks were found.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and deletes the json fence.
    """
    stdout_path, log_path = write_fixture(root)
    readme = root / "README.md"
    text = readme.read_text(encoding="utf-8")
    start = text.find("```json\n")
    finish = text.find("\n```", start)
    if start < 0 or finish < 0:
        raise RuntimeError("fixture json fence is missing.")
    readme.write_text(text[:start] + text[finish + len("\n```") :], encoding="utf-8")
    return report_exact(
        "json block missing",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md: expected one json code block under ### Example output, found 0. "
        "Restore the excerpt from logs/app.log of a sample run.",
    )


def case_code_differs(root: Path) -> int:
    """Sample code that drifts from the README block must be red.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check names the marker mismatch.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and edits ``Program.cs``.
    """
    stdout_path, log_path = write_fixture(root)
    program = root / "samples/ALog.QuickstartSample/Program.cs"
    program.write_text(
        program.read_text(encoding="utf-8").replace("var ready = 1;", "var ready = 9;", 1),
        encoding="utf-8",
    )
    return report_exact(
        "readme code differs",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md:6: Step 1 code block differs from "
        "samples/ALog.QuickstartSample/Program.cs:4 "
        "(between // readme:step1:begin and end): "
        'expected "var ready = 1;", found "var ready = 9;". '
        "Change README and sample in the same commit so both blocks match byte for byte.",
    )


def case_code_shorter(root: Path) -> int:
    """A sample block that ends early must say end of block on that line.

    Args:
        root: Directory for this case.

    Returns:
        0 when line 6 of the README is missing from the sample block.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and drops one sample line.
    """
    stdout_path, log_path = write_fixture(root)
    program = root / "samples/ALog.QuickstartSample/Program.cs"
    text = program.read_text(encoding="utf-8")
    shorter = text.replace("var ready = 1;\n", "", 1)
    if shorter == text:
        raise RuntimeError("fixture sample line could not be removed.")
    program.write_text(shorter, encoding="utf-8")
    return report_exact(
        "code shorter",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md:6: Step 1 code block differs from "
        "samples/ALog.QuickstartSample/Program.cs:4 "
        "(between // readme:step1:begin and end): "
        'expected "var ready = 1;", found "<end of block>". '
        "Change README and sample in the same commit so both blocks match byte for byte.",
    )


def case_marker_missing(root: Path) -> int:
    """A step with no begin marker must use the marker-count line.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says Step 1 has 0 begin and 1 end.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and deletes the Step 1 begin marker.
    """
    stdout_path, log_path = write_fixture(root)
    program = root / "samples/ALog.QuickstartSample/Program.cs"
    text = program.read_text(encoding="utf-8")
    program.write_text(text.replace("// readme:step1:begin\n", "", 1), encoding="utf-8")
    return report_exact(
        "marker missing",
        check_repository(root, stdout_path, log_path),
        "FAIL samples/ALog.QuickstartSample/Program.cs: expected exactly one "
        "// readme:step1:begin followed by one // readme:step1:end, found 0 begin, 1 end. "
        "Put the Step 1 code block from README.md between these two markers.",
    )


def case_marker_reversed(root: Path) -> int:
    """An end marker placed before begin must append the order clause.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says end comes before begin.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and swaps the Step 1 markers.
    """
    stdout_path, log_path = write_fixture(root)
    program = root / "samples/ALog.QuickstartSample/Program.cs"
    text = program.read_text(encoding="utf-8")
    swapped = text.replace("// readme:step1:begin", "// readme:step1:END", 1)
    swapped = swapped.replace("// readme:step1:end", "// readme:step1:begin", 1)
    swapped = swapped.replace("// readme:step1:END", "// readme:step1:end", 1)
    program.write_text(swapped, encoding="utf-8")
    return report_exact(
        "end before begin",
        check_repository(root, stdout_path, log_path),
        "FAIL samples/ALog.QuickstartSample/Program.cs: expected exactly one "
        "// readme:step1:begin followed by one // readme:step1:end, "
        "found 1 begin, 1 end, end before begin. "
        "Put the Step 1 code block from README.md between these two markers.",
    )


def case_readme_block_missing(root: Path) -> int:
    """A Step heading with no C# fence must use the missing-block line.

    Args:
        root: Directory for this case.

    Returns:
        0 when the only problem says Step 1 has zero C# blocks.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and deletes the Step 1 fence.
    """
    stdout_path, log_path = write_fixture(root)
    readme = root / "README.md"
    text = readme.read_text(encoding="utf-8")
    start = text.find("```csharp\n")
    finish = text.find("\n```", start)
    if start < 0 or finish < 0:
        raise RuntimeError("fixture csharp fence is missing.")
    readme.write_text(text[:start] + text[finish + len("\n```") :], encoding="utf-8")
    return report_exact(
        "readme block missing",
        check_repository(root, stdout_path, log_path),
        "FAIL README.md: expected one C# code block under ### Step 1, found 0. "
        "Restore the Step 1 code block; the sample copies it.",
    )


def case_tracked_logs(root: Path) -> int:
    """Two tracked files under logs/ must produce two exact lines.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check lists both paths and no other problem.

    Raises:
        OSError, RuntimeError, ValueError: The fixture or ``git add`` failed.

    Side effects:
        Writes a temporary repository and stages two files under ``logs/``.
    """
    stdout_path, log_path = write_fixture(root)
    logged = root / "logs"
    logged.mkdir()
    (logged / "app.log").write_text("one\n", encoding="utf-8")
    (logged / "other.log").write_text("two\n", encoding="utf-8")
    added, add_problem = run_git(root, ["add", "logs/app.log", "logs/other.log"])
    if add_problem is not None:
        raise RuntimeError(add_problem)
    del added
    return report_exact_many(
        "tracked logs",
        check_repository(root, stdout_path, log_path),
        [
            "FAIL logs/app.log: expected no tracked files under logs/, found this one in git. "
            "Remove it with git rm --cached logs/app.log; .gitignore already ignores logs/.",
            "FAIL logs/other.log: expected no tracked files under logs/, found this one in git. "
            "Remove it with git rm --cached logs/other.log; .gitignore already ignores logs/.",
        ],
    )


def case_different_commits(root: Path) -> int:
    """A TXT commit that is not the PNG commit must be red.

    The replacement timestamp still normalizes to the run transcript, so the
    failure is the commit pair and not the console text.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check names the different commits.

    Raises:
        OSError, RuntimeError, ValueError: The fixture or the second commit failed.

    Side effects:
        Writes a temporary repository and adds one commit that changes only the TXT.
    """
    stdout_path, log_path = write_fixture(root)
    txt = root / "docs/images/quickstart-console.txt"
    txt.write_text(
        txt.read_text(encoding="utf-8").replace("[20:55:19]", "[04:05:06]", 1),
        encoding="utf-8",
    )
    added, add_problem = run_git(root, ["add", "docs/images/quickstart-console.txt"])
    if add_problem is not None:
        raise RuntimeError(add_problem)
    del added
    committed, commit_problem = run_git(root, ["commit", "-m", "txt only"])
    if commit_problem is not None:
        raise RuntimeError(commit_problem)
    del committed
    png_sha, png_error = last_commit(root, "docs/images/quickstart-console.png")
    txt_sha, txt_error = last_commit(root, "docs/images/quickstart-console.txt")
    if png_error is not None or txt_error is not None or png_sha is None or txt_sha is None:
        raise RuntimeError(png_error or txt_error or "commit hash missing")
    if png_sha == txt_sha:
        raise RuntimeError("fixture commits did not diverge.")
    return report_exact(
        "png and txt in different commits",
        check_repository(root, stdout_path, log_path),
        (
            "FAIL docs/images/quickstart-console.png and quickstart-console.txt: "
            "expected both last changed in the same commit, "
            f"found png {png_sha[:7]}, txt {txt_sha[:7]}. "
            "Retake the screenshot from the current output and commit the PNG together with the TXT."
        ),
    )


def main(argv: list[str]) -> int:
    """Dispatch ``--self-test`` or one repository check.

    Args:
        argv: Arguments after the program name.

    Returns:
        0 when the check is green, 1 when it is red or the usage is wrong.

    Raises:
        None.

    Side effects:
        Reads the repository or writes a temporary self-test tree. Writes the
        result to stdout and stderr.
    """
    if argv == ["--self-test"]:
        return self_test()
    root = Path(".")
    stdout_path: Path | None = None
    log_path: Path | None = None
    index = 0
    while index < len(argv) and index < 12:
        arg = argv[index]
        if arg not in ("--root", "--stdout", "--log") or index + 1 >= len(argv):
            sys.stderr.write(
                "usage: eng/readme-example-check.py --self-test\n"
                "       eng/readme-example-check.py --stdout PATH --log PATH [--root PATH]\n"
            )
            return 1
        value = argv[index + 1]
        if arg == "--root":
            root = Path(value)
        elif arg == "--stdout":
            stdout_path = Path(value)
        else:
            log_path = Path(value)
        index += 2
    if stdout_path is None or log_path is None:
        sys.stderr.write(
            "usage: eng/readme-example-check.py --stdout PATH --log PATH [--root PATH]\n"
        )
        return 1
    problems = check_repository(root.resolve(), stdout_path, log_path)
    if problems:
        for problem in problems:
            sys.stderr.write(problem + "\n")
        return 1
    sys.stdout.write("readme-example-check: ok\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
