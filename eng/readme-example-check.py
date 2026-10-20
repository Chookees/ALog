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
SCREENSHOT_RERECORD = "The screenshot must be re-recorded."


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


def png_problem(path: Path) -> str | None:
    """Validate the screenshot's signature, IHDR, width, and size.

    Args:
        path: ``docs/images/quickstart-console.png``.

    Returns:
        A problem string, or None when the PNG is inside the uiux limits.

    Raises:
        None.

    Side effects:
        Reads the file when it is under 300 KB.
    """
    if not path.is_file():
        return "docs/images/quickstart-console.png is missing."
    size = path.stat().st_size
    if size < 24:
        return "docs/images/quickstart-console.png is too small to be a PNG."
    if size >= MAX_PNG_BYTES:
        return "docs/images/quickstart-console.png is not under 300 KB."
    try:
        data = path.read_bytes()
    except OSError:
        return "docs/images/quickstart-console.png could not be read."
    if len(data) != size or len(data) < 24:
        return "docs/images/quickstart-console.png could not be read."
    if data[:8] != PNG_SIGNATURE:
        return "docs/images/quickstart-console.png PNG signature is not valid."
    length = int.from_bytes(data[8:12], "big")
    if data[12:16] != b"IHDR" or length != 13:
        return "docs/images/quickstart-console.png IHDR chunk is missing."
    width = int.from_bytes(data[16:20], "big")
    height = int.from_bytes(data[20:24], "big")
    if height < 1 or width < MIN_PNG_WIDTH or width > MAX_PNG_WIDTH:
        return (
            "docs/images/quickstart-console.png width "
            f"{width} px is outside 900-1400."
        )
    return None


def fenced_csharp(text: str, heading: str) -> tuple[str | None, str | None]:
    """Return the first csharp fence under one README heading.

    Args:
        text: README text with LF line endings.
        heading: Heading line, including the leading ``###``.

    Returns:
        ``(body, None)`` including the newline after the last code line, or
        ``(None, problem)`` when the heading or the fence is missing.

    Raises:
        None.

    Side effects:
        None.
    """
    start = text.find(heading)
    if start < 0:
        return None, f"README is missing {heading}."
    next_heading = text.find("\n### ", start + len(heading))
    region = text[start:] if next_heading < 0 else text[start:next_heading]
    opener = "```csharp\n"
    open_at = region.find(opener)
    if open_at < 0:
        return None, f"README is missing a csharp block under {heading}."
    body_at = open_at + len(opener)
    close_at = region.find("\n```", body_at)
    if close_at < 0:
        return None, f"README csharp block under {heading} is not closed."
    return region[body_at : close_at + 1], None


def marked_region(text: str, name: str) -> tuple[str | None, str | None]:
    """Return the sample text between one pair of README markers.

    Args:
        text: ``Program.cs`` with LF line endings.
        name: ``step1`` or ``step2``.

    Returns:
        ``(body, None)`` or ``(None, problem)`` when a marker is missing.

    Raises:
        None.

    Side effects:
        None.
    """
    if name not in ("step1", "step2"):
        return None, "marker name must be step1 or step2."
    begin = f"// readme:{name}:begin\n"
    end = f"// readme:{name}:end"
    start = text.find(begin)
    if start < 0:
        return None, f"Program.cs is missing {begin.strip()}."
    body_at = start + len(begin)
    finish = text.find(end, body_at)
    if finish < 0:
        return None, f"Program.cs is missing {end}."
    return text[body_at:finish], None


def code_problems(readme: str, program: str) -> list[str]:
    """Compare both README csharp blocks with the sample markers.

    Args:
        readme: Normalized README text.
        program: Normalized ``Program.cs`` text.

    Returns:
        One problem per mismatched or missing block. Empty when both blocks
        are byte-identical to the markers.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    index = 0
    while index < len(STEP_HEADINGS):
        name, heading = STEP_HEADINGS[index]
        readme_body, readme_problem = fenced_csharp(readme, heading)
        sample_body, sample_problem = marked_region(program, name)
        if readme_problem is not None:
            problems.append(readme_problem)
        elif sample_problem is not None:
            problems.append(sample_problem)
        elif readme_body != sample_body:
            problems.append(
                f"README {name} block is not byte-identical to the sample markers."
            )
        index += 1
    return problems


def outside_marker_problem(program: str) -> str | None:
    """Reject a log call that is not inside a README marker.

    The markers already contain Step 1 and Step 2. Anything outside them is
    only allowed to make that text compile, so another ``Log.`` call would be
    a second example the README does not show.

    Args:
        program: Normalized ``Program.cs`` text.

    Returns:
        A problem when ``Log.`` survives outside the markers, else None.

    Raises:
        None.

    Side effects:
        None.
    """
    remaining = program
    index = 0
    while index < 2:
        name = STEP_HEADINGS[index][0]
        begin = f"// readme:{name}:begin"
        end = f"// readme:{name}:end"
        start = remaining.find(begin)
        finish = remaining.find(end, start if start >= 0 else 0)
        if start < 0 or finish < 0:
            return "Program.cs markers could not be removed."
        finish_end = finish + len(end)
        if finish_end < len(remaining) and remaining[finish_end] == "\n":
            finish_end += 1
        remaining = remaining[:start] + remaining[finish_end:]
        index += 1
    if "Log." in remaining:
        return "Program.cs has a log call outside the README markers."
    return None


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


def split_json_values(text: str) -> tuple[list[str], str | None]:
    """Split concatenated JSON values without reformatting them.

    Args:
        text: A README json fence or a ``logs/app.log`` body.

    Returns:
        The raw text of each complete value, in order, or a problem when a
        value is cut off or there are more than 100.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(text) > MAX_TEXT_BYTES:
        return [], "JSON text is larger than the read limit."
    decoder = json.JSONDecoder()
    values: list[str] = []
    index = 0
    length = len(text)
    while index < length:
        while index < length and text[index].isspace():
            index += 1
        if index >= length:
            break
        if len(values) >= MAX_JSON_VALUES:
            return [], "JSON text has more than 100 values."
        try:
            _parsed, end = decoder.raw_decode(text, index)
        except json.JSONDecodeError:
            return [], "JSON text is not a sequence of complete values."
        if end <= index:
            return [], "JSON decoder did not advance."
        values.append(text[index:end])
        index = end
    return values, None


def json_fence(readme: str) -> tuple[str | None, str | None]:
    """Return the Example output json fence body.

    Args:
        readme: Normalized README text.

    Returns:
        ``(body, None)`` or ``(None, problem)``.

    Raises:
        None.

    Side effects:
        None.
    """
    heading = readme.find("### Example output")
    if heading < 0:
        return None, "README is missing ### Example output."
    next_heading = readme.find("\n### ", heading + len("### Example output"))
    region = readme[heading:] if next_heading < 0 else readme[heading:next_heading]
    opener = "```json\n"
    open_at = region.find(opener)
    if open_at < 0:
        return None, "README Example output is missing a json block."
    body_at = open_at + len(opener)
    close_at = region.find("\n```", body_at)
    if close_at < 0:
        return None, "README json block is not closed."
    return region[body_at:close_at], None


def excerpt_problem(readme: str, log_text: str) -> str | None:
    """Require every README JSON value to be a full log entry, in order.

    Entries between the chosen ones may be absent. A partial entry, a reorder,
    or any edit other than the timestamp placeholder is a failure.

    Args:
        readme: Normalized README text.
        log_text: Exact ``logs/app.log`` text from the run.

    Returns:
        A problem, or None when the excerpt is a subsequence of the log.

    Raises:
        None.

    Side effects:
        None.
    """
    fence, fence_problem = json_fence(readme)
    if fence_problem is not None or fence is None:
        return fence_problem or "README json block is missing."
    excerpt, excerpt_error = split_json_values(fence)
    logged, log_error = split_json_values(log_text)
    if excerpt_error is not None:
        return "README JSON excerpt is not a sequence of complete values."
    if log_error is not None:
        return "The run log is not a sequence of complete values."
    if len(excerpt) < 1:
        return "README JSON excerpt has no entry."
    normalized_excerpt = [normalize_timestamps(item) for item in excerpt]
    normalized_log = [normalize_timestamps(item) for item in logged]
    if not is_subsequence(normalized_excerpt, normalized_log):
        return (
            "README JSON excerpt is not a verbatim subsequence of complete "
            "log entries after timestamp normalization."
        )
    return None


def is_subsequence(need: list[str], have: list[str]) -> bool:
    """Return whether every needed entry appears in the log, in order.

    Args:
        need: Normalized excerpt entries. One to 100.
        have: Normalized log entries. At most 100.

    Returns:
        True when ``need`` is an in-order subsequence of ``have``.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(need) < 1 or len(need) > MAX_JSON_VALUES or len(have) > MAX_JSON_VALUES:
        return False
    position = 0
    for item in need:
        found = False
        scan = position
        while scan < len(have):
            if have[scan] == item:
                position = scan + 1
                found = True
                break
            scan += 1
        if not found:
            return False
    return True


def stdout_problem(stdout_text: str, console_text: str) -> str | None:
    """Compare the run transcript with the committed console text.

    Args:
        stdout_text: Prompt, command, and process stdout from the run.
        console_text: ``docs/images/quickstart-console.txt``.

    Returns:
        None when the texts match after timestamp replacement. Otherwise a
        problem that tells the reader the screenshot must be re-recorded.

    Raises:
        None.

    Side effects:
        None.
    """
    if normalize_timestamps(stdout_text) == normalize_timestamps(console_text):
        return None
    return (
        "docs/images/quickstart-console.txt does not match the run stdout. "
        + SCREENSHOT_RERECORD
    )


def tracked_log_problem(root: Path) -> str | None:
    """Fail when git tracks anything under ``logs/`` or ``Logs/``.

    Args:
        root: Repository root.

    Returns:
        A problem naming the tracked path, or None when ``git ls-files`` shows
        no such path.

    Raises:
        None.

    Side effects:
        Reads the git index.
    """
    output, problem = run_git(root, ["ls-files", "-z"])
    if problem is not None or output is None:
        return problem or "git ls-files failed."
    paths = output.split("\0")
    seen = 0
    for path in paths:
        if path == "":
            continue
        seen += 1
        if seen > MAX_TRACKED_PATHS:
            return "git ls-files returned more than 10000 paths."
        parts = path.split("/")
        index = 0
        while index < len(parts) - 1 and index < 20:
            if parts[index] in ("logs", "Logs"):
                return f"A file under logs/ is tracked: {path}"
            index += 1
    return None


def same_commit_problem(root: Path) -> str | None:
    """Require the PNG and the TXT to share one last-changed commit.

    Args:
        root: Repository root.

    Returns:
        None when ``git log -1 --format=%H -- <file>`` is the same hash for
        both screenshot files.

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
            "docs/images/quickstart-console.png and "
            "docs/images/quickstart-console.txt were last changed in "
            "different commits."
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
    append_problem(problems, tracked_log_problem(root))
    if readme_text is None or program_text is None:
        return problems
    readme = normalize_newlines(readme_text)
    program = normalize_newlines(program_text)
    append_problem(problems, wording_problem(readme))
    for problem in code_problems(readme, program):
        append_problem(problems, problem)
    append_problem(problems, outside_marker_problem(program))
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
    height = 2
    row = b"\x00" + (b"\x00\x00\x00" * width)
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    encoded = PNG_SIGNATURE + png_chunk(b"IHDR", ihdr)
    encoded += png_chunk(b"IDAT", zlib.compress(row + row, 9))
    encoded += png_chunk(b"IEND", b"")
    path.write_bytes(encoded)


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


def self_test() -> int:
    """Prove the six red cases and the unchanged green case.

    Each red case starts from a fresh fixture so one mutation cannot hide
    another. The green case is that fixture before any mutation.

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
            failures += case_txt_differs(tmp / "txt")
            failures += case_excerpt_edited(tmp / "excerpt")
            failures += case_code_differs(tmp / "code")
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
    return report_case(
        "missing png",
        check_repository(root, stdout_path, log_path),
        "quickstart-console.png is missing",
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
    return report_case(
        "broken signature",
        check_repository(root, stdout_path, log_path),
        "PNG signature is not valid",
    )


def case_txt_differs(root: Path) -> int:
    """A console text file that is not the run transcript must be red.

    Args:
        root: Directory for this case.

    Returns:
        0 when the check says the screenshot must be re-recorded.

    Raises:
        OSError, RuntimeError, ValueError: The fixture could not be created.

    Side effects:
        Writes a temporary repository and edits the TXT.
    """
    stdout_path, log_path = write_fixture(root)
    txt = root / "docs/images/quickstart-console.txt"
    txt.write_text(txt.read_text(encoding="utf-8") + "extra line\n", encoding="utf-8")
    return report_case(
        "txt differs",
        check_repository(root, stdout_path, log_path),
        SCREENSHOT_RERECORD,
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
    return report_case(
        "excerpt hand-edited",
        check_repository(root, stdout_path, log_path),
        "complete log entries",
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
    return report_case(
        "readme code differs",
        check_repository(root, stdout_path, log_path),
        "not byte-identical",
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
    return report_case(
        "png and txt in different commits",
        check_repository(root, stdout_path, log_path),
        "different commits",
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
