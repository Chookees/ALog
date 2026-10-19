#!/usr/bin/env python3
"""Check RS0030 from compiler SARIF, not from a hand scan of suppressions.

CI builds each product project with ``-p:ErrorLog=<file>.sarif,version=2.1``.
This script reads every RS0030 result, including suppressed ones, and requires
an in-source suppression whose justification is one of the five allowlist texts
on that file and member. NoWarn and WarningsNotAsErrors come from
``dotnet msbuild -getProperty``. When no SARIF path is passed, the script
builds both product projects with ``--no-incremental`` so a later run on the
same tree still compiles and writes a fresh ErrorLog. Success prints
``rs0030-justification: ok``.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import tempfile
import urllib.parse
from pathlib import Path


PRAGMA = (
    'RS0030 exception needs [SuppressMessage(..., Justification = "why and when it is removed")], not #pragma.'
)
NO_JUST = "RS0030 exception has no Justification. Say why the wall clock is needed here."
NOT_MEMBER = (
    "RS0030 exception is not one of the five allowed members."
    " Remove the suppression and use the TimeProvider from the options; the allowlist is fixed at five entries."
)
BLOCK = "RS0030 exception member must be one expression. Only PutWithinTransportTimeout may have a block body."
UNCONDITIONAL = "RS0030 exception must be [SuppressMessage] on an allowed member, not UnconditionalSuppressMessage."
NOWARN = "RS0030 must not be listed in NoWarn or WarningsNotAsErrors."
SEVERITY = "RS0030 severity must stay an error."
DEFAULT = "ProductionClock.Default may only be the default clock on a public constructor."
DERIVE = "product code must not derive from TimeProvider."
UNSUPPRESSED = (
    "RS0030 is not suppressed in source."
    " Remove the banned call and use the TimeProvider from the options;"
    " NoWarn, #pragma and .editorconfig are not allowed."
)
SUMMARY_OK = "rs0030-justification: ok"
SEVERITY_LINE = "dotnet_diagnostic.RS0030.severity = error"
PRODUCT_GLOBS = ("[ALog/**.cs]", "[ALog.Extensions.Logging/**.cs]")

MODIFIERS = {
    "public", "private", "protected", "internal", "static", "sealed", "virtual",
    "override", "async", "extern", "new", "unsafe", "partial", "readonly",
    "volatile", "required", "file", "abstract", "ref",
}
TYPE_WORDS = {"class", "struct", "record", "interface", "enum", "namespace"}
PRAGMA_RE = re.compile(r"^\s*#\s*pragma\s+warning\s+disable\b(.*)$", re.IGNORECASE)
DERIVE_RE = re.compile(r"\b(?:class|struct|record)\s+\w+([^;{]*)")
COMPILE_RE = re.compile(r'<Compile\s+[^>]*Include\s*=\s*"([^"]+)"')
SKIP_DIRS = {"bin", "obj", ".git"}


class Allowed:
    """One of the five RS0030 exceptions."""

    def __init__(self, file: str, member: str, justification: str, block: bool) -> None:
        self.file = file
        self.member = member
        self.justification = justification
        self.block = block


class Member:
    """A method or property span in masked source."""

    def __init__(self, name: str, start: int, end: int, block: bool) -> None:
        self.name = name
        self.start = start
        self.end = end
        self.block = block


ALLOWED = [
    Allowed(
        "ALog/Core/ProductionClock.cs",
        "Default",
        "Default clock when the app sets no TimeProvider; tests inject a FakeTimeProvider.",
        False,
    ),
    Allowed(
        "ALog/Core/LoggerEngine.cs",
        "EventTimestamp",
        "Event timestamp is wall-clock time by design; it is log data, not a deadline.",
        False,
    ),
    Allowed(
        "ALog/Writers/Cloud/AwsCloudWatchWriter.cs",
        "PutWithinTransportTimeout",
        "CloudWatch put uses a 100 s transport timeout, not a shutdown or flush deadline; removed in AWS V4.",
        True,
    ),
    Allowed(
        "ALog/Writers/File/FileLogWriter.cs",
        "RotationStamp",
        "Rotation file name, not a shutdown or flush deadline; removed in P1.",
        False,
    ),
    Allowed(
        "ALog/Writers/Sql/SqlLogWriter.cs",
        "CreatedAtStamp",
        "CreatedAt column, not a shutdown or flush deadline; removed in P1.",
        False,
    ),
]


def main(argv: list[str]) -> int:
    """Run the self-test, then the repository scan unless ``--cases`` is set.

    Args:
        argv: Arguments after the program name.

    Returns:
        0 when the tree is allowed. 1 when the tree has a problem. 2 when the self-test fails.

    Raises:
        OSError: A file or dotnet cannot be started.

    Side effects:
        Builds fixture projects for the self-test. May build the product projects
        when SARIF paths are not passed. Writes the report to stdout.
    """
    cases_only = False
    sarif_paths: list[str] = []
    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg == "--cases":
            cases_only = True
        elif arg == "--sarif":
            index += 1
            if index >= len(argv):
                print("rs0030-justification: --sarif needs a path", file=sys.stderr)
                return 2
            sarif_paths.append(argv[index])
        else:
            print("rs0030-justification: unknown argument " + arg, file=sys.stderr)
            return 2
        index += 1

    repo = find_repo()
    rows = case_results(repo)
    if not cases_match(rows):
        print("rs0030-justification: self-check failed", file=sys.stderr)
        for row in rows:
            print(row[0], file=sys.stderr)
            for line in row[1]:
                print(line, file=sys.stderr)
        return 2

    if cases_only:
        print_cases(rows)
        return 0

    problems = scan_repo(repo, sarif_paths)
    return report(problems)


def print_cases(rows: list[tuple[str, list[str]]]) -> None:
    """Print the S01 case table."""
    number = 1
    for name, lines in rows:
        label = "S" + str(number).zfill(2) + " " + name
        number += 1
        if len(lines) == 0:
            print(label + ": pass")
            continue
        for line in lines:
            print(label + ": " + line)


def report(problems: list[str]) -> int:
    """Print the repository result.

    Fully identical lines are printed once. Two lines that differ both stay.
    Dropping a duplicate does not turn a red result into a pass.

    Args:
        problems: Problem lines. Empty when the tree is allowed.

    Returns:
        0 when no line remains. 1 when any line remains.
    """
    code, lines = finish(problems)
    if code == 0:
        print(SUMMARY_OK)
        return 0
    for line in lines:
        print(line)
    print("rs0030-justification: " + str(len(lines)) + " problem(s) found.")
    return code


def unique_lines(problems: list[str]) -> list[str]:
    """Keep the first copy of each fully identical line.

    Args:
        problems: Problem lines in discovery order.

    Returns:
        Those lines with a later character-for-character duplicate removed.
        A line that differs by path, line number, or text stays.
    """
    kept: list[str] = []
    seen: set[str] = set()
    for line in problems:
        if line in seen:
            continue
        seen.add(line)
        kept.append(line)
    return kept


def finish(problems: list[str]) -> tuple[int, list[str]]:
    """Deduplicate and choose the exit code report would use.

    Args:
        problems: Problem lines, possibly with duplicates.

    Returns:
        ``(0, [])`` when no line remains. ``(1, lines)`` when any line remains.
    """
    lines = unique_lines(problems)
    if len(lines) == 0:
        return 0, []
    return 1, lines


def red_lines(problems: list[str]) -> list[str]:
    """Return the lines a red report prints.

    Args:
        problems: Problem lines, possibly with duplicates.

    Returns:
        The deduplicated lines when the exit code is 1.
        ``exit 0`` when nothing remains, so a case that must stay red fails.
    """
    code, lines = finish(problems)
    if code != 1:
        return ["exit " + str(code)]
    return lines


def find_repo() -> Path:
    """Find the directory that contains ALog.sln.

    Returns:
        The repository root.

    Raises:
        SystemExit: The walk from this file does not find the solution.
    """
    for parent in Path(__file__).resolve().parents:
        if (parent / "ALog.sln").is_file():
            return parent
    raise SystemExit("ALog.sln was not found.")


def hit(path: str, line: int, message: str) -> str:
    """Format one problem line."""
    return path.replace("\\", "/") + ":" + str(line) + ": " + message


def contains_id(body: str) -> bool:
    """True when RS0030 is its own token in body."""
    for index in range(0, len(body) - 5):
        if body[index:index + 6].lower() != "rs0030":
            continue
        before = index == 0 or not body[index - 1].isalnum()
        after = index + 6 >= len(body) or not body[index + 6].isalnum()
        if before and after:
            return True
    return False


def mask_source(text: str) -> str:
    """Replace comments and strings with spaces. Newlines stay so indexes match."""
    chars = list(text)
    index = 0
    count = len(chars)
    while index < count:
        char = chars[index]
        nxt = chars[index + 1] if index + 1 < count else ""
        if char == "/" and nxt == "/":
            while index < count and chars[index] != "\n":
                chars[index] = " "
                index += 1
            continue
        if char == "/" and nxt == "*":
            chars[index] = " "
            chars[index + 1] = " "
            index += 2
            while index + 1 < count and not (chars[index] == "*" and chars[index + 1] == "/"):
                if chars[index] != "\n":
                    chars[index] = " "
                index += 1
            if index < count:
                chars[index] = " "
            if index + 1 < count:
                chars[index + 1] = " "
                index += 2
            else:
                index += 1
            continue
        if char == "@" and nxt == '"':
            chars[index] = " "
            chars[index + 1] = " "
            index += 2
            while index < count:
                if chars[index] == '"':
                    if index + 1 < count and chars[index + 1] == '"':
                        chars[index] = " "
                        chars[index + 1] = " "
                        index += 2
                        continue
                    chars[index] = " "
                    index += 1
                    break
                if chars[index] != "\n":
                    chars[index] = " "
                index += 1
            continue
        if char == '"':
            chars[index] = " "
            index += 1
            while index < count and chars[index] != '"':
                if chars[index] == "\\" and index + 1 < count:
                    chars[index] = " "
                    if chars[index + 1] != "\n":
                        chars[index + 1] = " "
                    index += 2
                    continue
                if chars[index] != "\n":
                    chars[index] = " "
                index += 1
            if index < count:
                chars[index] = " "
                index += 1
            continue
        if char == "'":
            chars[index] = " "
            index += 1
            while index < count and chars[index] != "'":
                if chars[index] != "\n":
                    chars[index] = " "
                index += 1
            if index < count:
                chars[index] = " "
                index += 1
            continue
        index += 1
    return "".join(chars)


def line_of(text: str, index: int) -> int:
    """Return the 1-based line containing index."""
    return text.count("\n", 0, index) + 1


def skip_ws(text: str, index: int) -> int:
    """Skip spaces and newlines."""
    while index < len(text) and text[index] in " \t\r\n":
        index += 1
    return index


def read_ident(text: str, index: int) -> tuple[str, int]:
    """Read one identifier starting at index."""
    start = index
    index += 1
    while index < len(text) and (text[index].isalnum() or text[index] == "_"):
        index += 1
    return text[start:index], index


def match_pair(text: str, index: int, open_ch: str, close_ch: str) -> int:
    """Return the index after the matching closer. index points at the opener."""
    depth = 0
    while index < len(text):
        char = text[index]
        if char == open_ch:
            depth += 1
        elif char == close_ch:
            depth -= 1
            if depth == 0:
                return index + 1
        index += 1
    return index


def find_semicolon(text: str, index: int) -> int:
    """Return the index after the semicolon that ends an expression body."""
    paren = 0
    while index < len(text):
        char = text[index]
        if char == "(":
            paren += 1
        elif char == ")":
            paren -= 1
        elif char == ";" and paren == 0:
            return index + 1
        if char == "{" and paren == 0:
            return index
        index += 1
    return index


def try_parse_member(text: str, index: int) -> tuple[Member | None, int, tuple[int, int] | None]:
    """Parse one type-body declaration.

    Args:
        text: Masked source.
        index: Start of the declaration.

    Returns:
        The member or None, the index to resume at, and a public constructor
        initializer span ``(colon, brace)`` when the declaration has one.
    """
    start = index
    tokens: list[str] = []
    while index < len(text):
        index = skip_ws(text, index)
        if index >= len(text):
            return None, start + 1, None
        char = text[index]
        if char.isalpha() or char == "_":
            word, index = read_ident(text, index)
            if word in TYPE_WORDS:
                return None, start + 1, None
            tokens.append(word)
            continue
        if char == "<":
            index = match_pair(text, index, "<", ">")
            continue
        if char == "(":
            if len(tokens) == 0:
                return None, start + 1, None
            name = tokens[-1]
            end_paren = match_pair(text, index, "(", ")")
            cursor = skip_ws(text, end_paren)
            if text.startswith("where", cursor) and (cursor + 5 >= len(text) or not text[cursor + 5].isalnum()):
                while cursor < len(text) and text[cursor] not in "{=":
                    cursor += 1
            init = None
            names = [token for token in tokens if token not in MODIFIERS]
            if cursor < len(text) and text[cursor] == ":" and len(names) == 1 and "public" in tokens:
                colon = cursor
                while cursor < len(text) and text[cursor] != "{":
                    cursor += 1
                init = (colon, cursor)
            if cursor < len(text) and text[cursor] == "{":
                end = match_pair(text, cursor, "{", "}")
                return Member(name, start, end, True), end, init
            if text.startswith("=>", cursor):
                end = find_semicolon(text, cursor)
                return Member(name, start, end, False), end, init
            return None, end_paren, None
        if char == "=" and index + 1 < len(text) and text[index + 1] == ">":
            if len(tokens) == 0:
                return None, start + 1, None
            end = find_semicolon(text, index)
            return Member(tokens[-1], start, end, False), end, None
        if char == "{":
            if len(tokens) == 0:
                return None, start + 1, None
            end = match_pair(text, index, "{", "}")
            return Member(tokens[-1], start, end, True), end, None
        if char == ";":
            return None, index + 1, None
        return None, start + 1, None
    return None, start + 1, None


def structure(text: str) -> tuple[list[Member], list[tuple[int, int]]]:
    """Find members and public constructor initializer spans.

    Args:
        text: C# source.

    Returns:
        Members, and ``(colon_index, brace_index)`` spans of public constructor initializers.
    """
    masked = mask_source(text)
    members: list[Member] = []
    inits: list[tuple[int, int]] = []
    frames = ["file"]
    pending_type = False
    index = 0
    count = len(masked)
    while index < count:
        if frames[-1] == "type" and masked[index] == "[":
            index = match_pair(masked, index, "[", "]")
            continue
        if masked[index].isalpha() or masked[index] == "_":
            if frames[-1] in ("file", "type"):
                word, after = read_ident(masked, index)
                if word in TYPE_WORDS:
                    pending_type = True
                    index = after
                    continue
            if frames[-1] == "type":
                member, nxt, init = try_parse_member(masked, index)
                if member is not None:
                    members.append(member)
                if init is not None:
                    inits.append(init)
                index = nxt if nxt > index else index + 1
                continue
        if masked[index] == "{":
            frames.append("type" if pending_type else "block")
            pending_type = False
            index += 1
            continue
        if masked[index] == "}":
            if len(frames) > 1:
                frames.pop()
            index += 1
            continue
        if masked[index] == ";":
            pending_type = False
        index += 1
    return members, inits


def member_at(text: str, line: int) -> Member | None:
    """Return the member whose span contains line. None when the line is outside every member."""
    found = None
    for member in structure(text)[0]:
        start_line = line_of(text, member.start)
        end_line = line_of(text, max(member.end - 1, member.start))
        if start_line <= line <= end_line:
            found = member
    return found


def default_problems(path: str, text: str, composition: set[str]) -> list[str]:
    """Report ProductionClock.Default uses that are not a listed constructor initializer.

    Args:
        path: Path printed in each hit.
        text: Source text.
        composition: Repository-relative files that may pass the default clock.

    Returns:
        One line per extra use.
    """
    masked = mask_source(text)
    _members, inits = structure(text)
    allowed_file = path.replace("\\", "/") in composition
    problems = []
    needle = "ProductionClock.Default"
    start = 0
    while True:
        index = masked.find(needle, start)
        if index < 0:
            break
        start = index + len(needle)
        inside = False
        for begin, end in inits:
            if begin <= index < end:
                inside = True
                break
        if allowed_file and inside:
            continue
        problems.append(hit(path, line_of(text, index), DEFAULT))
    return problems


def pragma_problems(path: str, text: str) -> list[str]:
    """Report a disable pragma that is bare or names RS0030 in any letter case."""
    problems = []
    for number, raw in enumerate(text.splitlines(), 1):
        match = PRAGMA_RE.match(raw)
        if match is None:
            continue
        rest = match.group(1)
        comment = rest.find("//")
        if comment >= 0:
            rest = rest[:comment]
        if rest.strip() == "" or contains_id(rest):
            problems.append(hit(path, number, PRAGMA))
    return problems


def derive_problems(path: str, text: str) -> list[str]:
    """Report a type that derives from System.TimeProvider."""
    problems = []
    masked = mask_source(text)
    for number, line in enumerate(masked.splitlines(), 1):
        match = DERIVE_RE.search(line)
        if match is None or ":" not in match.group(1):
            continue
        bases = match.group(1).split(":", 1)[1]
        for part in bases.split(","):
            name = part.strip().replace(" ", "")
            if name in ("TimeProvider", "System.TimeProvider", "global::System.TimeProvider"):
                problems.append(hit(path, number, DERIVE))
    return problems


def editorconfig_problems(path: str, text: str, require_product: bool) -> list[str]:
    """Report an RS0030 config line that is not severity = error.

    Args:
        path: Path printed in each hit.
        text: Editorconfig or globalconfig text.
        require_product: True when both product globs must set the diagnostic to error.

    Returns:
        Problem lines. Comments are ignored.
    """
    problems = []
    seen = {glob: False for glob in PRODUCT_GLOBS}
    header = ""
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if line.startswith("[") and line.endswith("]"):
            header = line
            continue
        if line.startswith("#") or line.startswith(";"):
            continue
        if "RS0030" not in line:
            continue
        compact = " ".join(line.split())
        if compact == SEVERITY_LINE:
            if header in seen:
                seen[header] = True
            continue
        problems.append(hit(path, number, SEVERITY))
    if not require_product:
        return problems
    for glob, present in seen.items():
        if not present:
            problems.append(hit(path, 1, SEVERITY_LINE + " is missing for " + glob + "."))
    return problems


def property_problems(path: str, project: Path) -> list[str]:
    """Report RS0030 in the evaluated NoWarn or WarningsNotAsErrors properties.

    Args:
        path: Path printed in each hit.
        project: Project file passed to msbuild.

    Returns:
        One line when either property lists RS0030. WarningsAsErrors is not read.

    Side effects:
        Runs ``dotnet msbuild -getProperty``.
    """
    problems = []
    for prop in ("NoWarn", "WarningsNotAsErrors"):
        completed = subprocess.run(
            ["dotnet", "msbuild", str(project), "-getProperty:" + prop, "-nologo"],
            text=True,
            capture_output=True,
            check=False,
        )
        if completed.returncode != 0:
            problems.append(hit(path, 1, "dotnet msbuild -getProperty:" + prop + " failed."))
            continue
        if contains_id(completed.stdout):
            problems.append(hit(path, 1, NOWARN))
    return problems


def load_composition(repo: Path) -> set[str]:
    """Read the composition-point file list.

    Args:
        repo: Repository root.

    Returns:
        Repository-relative paths. Blank lines and comments are skipped.

    Raises:
        FileNotFoundError: The list file is missing.
    """
    files = set()
    text = (repo / "eng" / "production-clock-composition.txt").read_text(encoding="utf-8")
    for raw in text.splitlines():
        line = raw.strip()
        if line == "" or line.startswith("#"):
            continue
        files.add(line.replace("\\", "/"))
    return files


def allow_for(path: str, member: str | None) -> Allowed | None:
    """Return the allowlist row for this file and member. None when it is not one of the five."""
    norm = path.replace("\\", "/")
    for row in ALLOWED:
        if member == row.member and (norm == row.file or norm.endswith("/" + row.file)):
            return row
    return None


def unconditional(text: str, member: Member, line: int) -> bool:
    """True when the lines above the member name an UnconditionalSuppressMessage for RS0030."""
    start = line_of(text, member.start) - 8
    if start < 1:
        start = 1
    window = "\n".join(text.splitlines()[start - 1:line])
    return "UnconditionalSuppressMessage" in window and "RS0030" in window


def relative_to(root: Path, full: Path) -> str:
    """Return a forward-slash path relative to root."""
    return full.resolve().relative_to(root.resolve()).as_posix()


def sarif_problems(sarif: Path, root: Path) -> list[str]:
    """Read every RS0030 result, including suppressed ones.

    Args:
        sarif: A SARIF 2.1 log from ``-p:ErrorLog=...,version=2.1``.
        root: Directory used to make paths relative. The fixture root or the repository.

    Returns:
        Problem lines. A result with no problem is an allowed suppression.
    """
    data = json.loads(sarif.read_text(encoding="utf-8"))
    if data.get("version") != "2.1.0":
        return [hit(sarif.name, 1, "ErrorLog SARIF is not version 2.1.")]
    problems = []
    for run in data.get("runs", []):
        for result in run.get("results", []):
            if result.get("ruleId") != "RS0030":
                continue
            problems.extend(one_result(result, root))
    return problems


def one_result(result: dict, root: Path) -> list[str]:
    """Classify one RS0030 result."""
    location = result["locations"][0]["physicalLocation"]
    uri = location["artifactLocation"]["uri"]
    full = uri_path(uri)
    path = display_path(full, root)
    line = int(location["region"]["startLine"])
    source_file = Path(full)
    text = source_file.read_text(encoding="utf-8") if source_file.is_file() else ""
    member = member_at(text, line) if text != "" else None
    suppressions = result.get("suppressions") or []
    if len(suppressions) == 0:
        return [hit(path, line, UNSUPPRESSED)]
    chosen = None
    for suppression in suppressions:
        props = suppression.get("properties") or {}
        if props.get("suppressionType") == "Pragma Directive":
            continue
        chosen = suppression
        break
    if chosen is None:
        return []
    if chosen.get("kind") != "inSource":
        return [hit(path, line, UNSUPPRESSED)]
    justification = chosen.get("justification")
    if justification is None or str(justification).strip() == "":
        return [hit(path, line, NO_JUST)]
    if member is not None and unconditional(text, member, line):
        return [hit(path, line, UNCONDITIONAL)]
    row = allow_for(path, None if member is None else member.name)
    if row is not None and justification == row.justification:
        if member is not None and member.block and not row.block:
            return [hit(path, line, BLOCK)]
        return []
    if row is not None:
        return [hit(path, line, bad_text(row.justification))]
    return [hit(path, line, NOT_MEMBER)]


def bad_text(allowed: str) -> str:
    """Return the mismatch line, quoting the one allowed justification.

    Args:
        allowed: The allowlist justification for this file and member. Not empty.

    Returns:
        The full message. The period after the closing quote is part of the message.
    """
    return (
        'RS0030 exception justification does not match the allowed text. Use the allowed text: "'
        + allowed
        + '".'
    )


def uri_path(uri: str) -> str:
    """Turn a SARIF artifact URI into a filesystem path."""
    if uri.startswith("file:"):
        parsed = urllib.parse.urlparse(uri)
        return urllib.parse.unquote(parsed.path)
    return urllib.parse.unquote(uri)


def display_path(full: str, root: Path) -> str:
    """Prefer a path relative to root. Otherwise keep the suffix the allowlist can match."""
    path = Path(full)
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def source_problems(path: str, text: str, composition: set[str]) -> list[str]:
    """Pragma, default-clock, and TimeProvider-derive problems for one file."""
    problems = []
    problems.extend(pragma_problems(path, text))
    problems.extend(default_problems(path, text, composition))
    problems.extend(derive_problems(path, text))
    return problems


FIXTURE_PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisLevel>none</AnalysisLevel>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <WarningsAsErrors>RS0030</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(CaseFile)" />
    <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="5.6.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <AdditionalFiles Include="BannedSymbols.txt" />
  </ItemGroup>
</Project>
"""

FIXTURE_EDITORCONFIG = """root = true

[*.cs]
dotnet_diagnostic.RS0030.severity = error
"""

FIXTURE_BANS = "P:System.DateTime.UtcNow;banned\n"


def write_fixture(directory: Path) -> None:
    """Write the shared fixture project used by every built self-test case."""
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "Fixture.csproj").write_text(FIXTURE_PROJECT, encoding="utf-8")
    (directory / ".editorconfig").write_text(FIXTURE_EDITORCONFIG, encoding="utf-8")
    (directory / "BannedSymbols.txt").write_text(FIXTURE_BANS, encoding="utf-8")
    subprocess.run(
        ["dotnet", "restore", str(directory / "Fixture.csproj"), "--nologo", "-v", "q"],
        cwd=directory,
        text=True,
        capture_output=True,
        check=False,
    )


def build_case(directory: Path, rel: str, source: str) -> list[str]:
    """Compile one file with an ErrorLog and return its RS0030 problems.

    Args:
        directory: Fixture project directory. Already restored.
        rel: Path inside the project. Also the path printed in hits.
        source: The compilation unit.

    Returns:
        Problem lines from the SARIF log.

    Side effects:
        Writes the source and runs ``dotnet build``.
    """
    destination = directory / rel
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(source, encoding="utf-8")
    sarif = directory / (rel.replace("/", "_") + ".sarif")
    if sarif.exists():
        sarif.unlink()
    completed = subprocess.run(
        [
            "dotnet", "build", str(directory / "Fixture.csproj"),
            "-c", "Release", "--nologo", "-v", "q", "--no-restore",
            "-p:CaseFile=" + rel,
            '-p:ErrorLog="' + str(sarif) + ',version=2.1"',
        ],
        cwd=directory,
        text=True,
        capture_output=True,
        check=False,
    )
    if not sarif.is_file():
        tail = (completed.stdout + "\n" + completed.stderr)[-1500:]
        return [hit(rel, 1, "fixture build wrote no SARIF. " + tail.replace("\n", " "))]
    return sarif_problems(sarif, directory)


def linked_case(parent: Path) -> list[str]:
    """Build a project whose only compile item lives outside the project directory."""
    app = parent / "App"
    outside = parent / "outside"
    app.mkdir(parents=True, exist_ok=True)
    outside.mkdir(parents=True, exist_ok=True)
    (app / "App.csproj").write_text(
        FIXTURE_PROJECT.replace('Include="$(CaseFile)"', 'Include="..\\outside\\Leak.cs"'),
        encoding="utf-8",
    )
    (app / ".editorconfig").write_text(FIXTURE_EDITORCONFIG, encoding="utf-8")
    (app / "BannedSymbols.txt").write_text(FIXTURE_BANS, encoding="utf-8")
    (outside / "Leak.cs").write_text(
        "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \"linked file\")]\n"
        "class C\n{\n    static object M() => DateTime.UtcNow;\n}\n",
        encoding="utf-8",
    )
    sarif = parent / "linked.sarif"
    completed = subprocess.run(
        [
            "dotnet", "build", str(app / "App.csproj"),
            "-c", "Release", "--nologo", "-v", "q",
            '-p:ErrorLog="' + str(sarif) + ',version=2.1"',
        ],
        cwd=app,
        text=True,
        capture_output=True,
        check=False,
    )
    if not sarif.is_file():
        tail = (completed.stdout + "\n" + completed.stderr)[-1500:]
        return [hit("outside/Leak.cs", 1, "fixture build wrote no SARIF. " + tail.replace("\n", " "))]
    return sarif_problems(sarif, parent)


def property_case(parent: Path, logical: str, body: str) -> list[str]:
    """Evaluate NoWarn or WarningsNotAsErrors on a tiny project."""
    directory = parent / logical.replace("/", "_")
    directory.mkdir(parents=True, exist_ok=True)
    project = directory / "Proj.csproj"
    project.write_text("<Project>\n  <PropertyGroup>\n" + body + "\n  </PropertyGroup>\n</Project>\n", encoding="utf-8")
    return property_problems(logical, project)


def case_results(repo: Path) -> list[tuple[str, list[str]]]:
    """Build every self-test row.

    Args:
        repo: Repository root. Supplies the composition list.

    Returns:
        ``(name, problem lines)`` in table order.

    Side effects:
        Restores and builds fixture projects under a temporary directory, then deletes it.
    """
    composition = load_composition(repo)
    rows: list[tuple[str, list[str]]] = []
    with tempfile.TemporaryDirectory(prefix="rs0030-self-") as temporary:
        root = Path(temporary)
        fixture = root / "fixture"
        write_fixture(fixture)
        for name, rel, source in built_samples():
            problems = source_problems(rel, source, composition)
            problems.extend(build_case(fixture, rel, source))
            rows.append((name, problems))
        for name, rel, source in plain_samples():
            rows.append((name, source_problems(rel, source, composition)))
        rows.append(("nowarn", property_case(root, "ALog/ALog.csproj", "<NoWarn>RS0030</NoWarn>")))
        rows.append((
            "warnings-not-as-errors",
            property_case(root, "Directory.Build.props", "<WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>"),
        ))
        rows.append(("nested-editorconfig", editorconfig_problems(
            "ALog/.editorconfig",
            "[*.cs]\ndotnet_diagnostic.RS0030.severity = none\n",
            False,
        )))
        rows.append(("globalconfig", editorconfig_problems(
            "ALog/.globalconfig",
            "is_global = true\ndotnet_diagnostic.RS0030.severity = suggestion\n",
            False,
        )))
        rows.append(("linked-file", linked_case(root / "linked")))
        rows.append(("product-editorconfig", editorconfig_problems(
            ".editorconfig",
            "root = true\n\n[ALog/**.cs]\n" + SEVERITY_LINE + "\n\n[ALog.Extensions.Logging/**.cs]\n" + SEVERITY_LINE + "\n",
            True,
        )))
        append_message_cases(rows, root, fixture, composition)
    return rows


def append_message_cases(
    rows: list[tuple[str, list[str]]],
    root: Path,
    fixture: Path,
    composition: set[str],
) -> None:
    """Append the justification, unsuppressed, dedup, and missing-SARIF rows.

    Args:
        rows: The case table. This function appends five rows.
        root: Temporary directory that contains the fixture.
        fixture: Restored fixture project.
        composition: Composition-point files.

    Side effects:
        Builds two fixture compilations and writes SARIF files under root.
    """
    changed_rel = "ALog/Core/LoggerEngine.cs"
    changed_source = (
        "using System.Diagnostics.CodeAnalysis;\n"
        "class LoggerEngine\n"
        "{\n"
        "    [SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \"not the allowed sentence\")]\n"
        "    private static object EventTimestamp() => DateTime.UtcNow;\n"
        "}\n"
    )
    changed = source_problems(changed_rel, changed_source, composition)
    changed.extend(build_case(fixture, changed_rel, changed_source))
    rows.append(("changed-justification", red_lines(changed)))

    bare_rel = "ALog/Bypass.cs"
    bare_source = "class C\n{\n    static object M() => DateTime.UtcNow;\n}\n"
    bare = source_problems(bare_rel, bare_source, composition)
    bare.extend(build_case(fixture, bare_rel, bare_source))
    rows.append(("unsuppressed", red_lines(bare)))

    first_log = fixture / (bare_rel.replace("/", "_") + ".sarif")
    second_log = fixture / "ALog.Extensions.Logging.sarif"
    if first_log.is_file():
        second_log.write_bytes(first_log.read_bytes())
    duplicated = sarif_log_problems([first_log, second_log], fixture)
    rows.append(("dedup-identical", identical_pair(duplicated)))
    rows.append(("dedup-distinct", distinct_case(root)))
    missing = root / "artifacts" / "ALog.Extensions.Logging.sarif"
    rows.append(("sarif-missing", red_lines(sarif_log_problems([missing], root))))


def identical_pair(problems: list[str]) -> list[str]:
    """Collapse two copies of one line.

    Args:
        problems: Lines from two logs that should be the same finding.

    Returns:
        The one line a red report prints. A list that is not two equal lines
        returns a marker the fixed expected line does not match.
    """
    if len(problems) != 2 or problems[0] != problems[1]:
        return ["not a duplicate pair"]
    return red_lines(problems)


def distinct_case(root: Path) -> list[str]:
    """Two different RS0030 lines on one file and line stay two lines.

    Args:
        root: Temporary directory. The source and the SARIF are written here.

    Returns:
        The two lines a red report prints.

    Side effects:
        Writes a source file and a SARIF log under root.
    """
    rel = "ALog/Core/LoggerEngine.cs"
    source = "class LoggerEngine\n{\n    static object EventTimestamp() => DateTime.UtcNow;\n}\n"
    destination = root / rel
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(source, encoding="utf-8")
    uri = destination.resolve().as_uri()
    sarif = root / "distinct.sarif"
    sarif.write_text(
        result_sarif([
            rs0030_result(uri, 3, None),
            rs0030_result(uri, 3, "not the allowed sentence"),
        ]),
        encoding="utf-8",
    )
    problems = sarif_log_problems([sarif], root)
    if len(problems) != 2 or problems[0] == problems[1]:
        return ["not two different lines"]
    return red_lines(problems)


def result_sarif(results: list[dict]) -> str:
    """Build a SARIF 2.1 document.

    Args:
        results: RS0030 results. Each result already carries its artifact URI.

    Returns:
        JSON text.
    """
    document = {"version": "2.1.0", "runs": [{"results": results}]}
    return json.dumps(document)


def rs0030_result(uri: str, line: int, justification: str | None) -> dict:
    """Build one RS0030 result.

    Args:
        uri: Artifact URI of the source file.
        line: 1-based source line.
        justification: In-source justification. None means the call is not suppressed.

    Returns:
        A SARIF result object.
    """
    result: dict = {
        "ruleId": "RS0030",
        "locations": [{
            "physicalLocation": {
                "artifactLocation": {"uri": uri},
                "region": {"startLine": line},
            },
        }],
    }
    if justification is not None:
        result["suppressions"] = [{"kind": "inSource", "justification": justification}]
    return result


def built_samples() -> list[tuple[str, str, str]]:
    """Cases whose RS0030 result has to come from the compiler."""
    event_text = ALLOWED[1].justification
    return [
        ("alias-without-justification", "ALog/Bypass.cs", """using X = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;
class C
{
    [X("ApiDesign", "RS0030")]
    void M() => DateTime.UtcNow;
}
"""),
        ("alias-namespace", "ALog/Bypass.cs", """using SCA = System.Diagnostics.CodeAnalysis;
class C
{
    [SCA.SuppressMessage("ApiDesign", "RS0030")]
    void M() => DateTime.UtcNow;
}
"""),
        ("unconditional", "ALog/Bypass.cs", """using System.Diagnostics.CodeAnalysis;
class C
{
    [UnconditionalSuppressMessage("ApiDesign", "RS0030")]
    void M() => DateTime.UtcNow;
}
"""),
        ("unconditional-justified", "ALog/Bypass.cs", """using System.Diagnostics.CodeAnalysis;
class C
{
    [UnconditionalSuppressMessage("ApiDesign", "RS0030", Justification = "still not allowed")]
    void M() => DateTime.UtcNow;
}
"""),
        ("assembly", "ALog/Bypass.cs", """[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "assembly level wall clock")]
class C
{
    static object M() => DateTime.UtcNow;
}
"""),
        ("module", "ALog/Bypass.cs", """[module: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030", Justification = "module level wall clock")]
namespace Sample;
class C
{
    static object M() => DateTime.UtcNow;
}
"""),
        ("type", "ALog/Bypass.cs", """using System.Diagnostics.CodeAnalysis;
[SuppressMessage("ApiDesign", "RS0030", Justification = "type level wall clock")]
class C
{
    static object M() => DateTime.UtcNow;
}
"""),
        ("multiline-member", "ALog/Core/LoggerEngine.cs", """using System.Diagnostics.CodeAnalysis;
class LoggerEngine
{
    [SuppressMessage("ApiDesign", "RS0030", Justification = \"""" + event_text + """\")]
    private static object EventTimestamp()
    {
        var first = DateTime.UtcNow;
        return first;
    }
}
"""),
        ("sixth", "ALog/Core/LoggerEngine.cs", """using System.Diagnostics.CodeAnalysis;
class LoggerEngine
{
    [SuppressMessage("ApiDesign", "RS0030", Justification = "This wall clock read stays until a later change removes it.")]
    private static object Extra() => DateTime.UtcNow;
}
"""),
        ("one-char", "ALog/Core/LoggerEngine.cs", """using System.Diagnostics.CodeAnalysis;
class LoggerEngine
{
    [SuppressMessage("ApiDesign", "RS0030", Justification = "x")]
    private static object Extra() => DateTime.UtcNow;
}
"""),
        ("blank", "ALog/Bypass.cs", """using System.Diagnostics.CodeAnalysis;
class C
{
    [SuppressMessage("ApiDesign", "RS0030", Justification = " ")]
    void M() => DateTime.UtcNow;
}
"""),
        ("allow-default", "ALog/Core/ProductionClock.cs", allow_member(
            "class ProductionClock { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + ALLOWED[0].justification
            + "\")] public static object Default => DateTime.UtcNow; }\n",
        )),
        ("allow-event", "ALog/Core/LoggerEngine.cs", allow_member(
            "class LoggerEngine { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + ALLOWED[1].justification
            + "\")] private static object EventTimestamp() => DateTime.UtcNow; }\n",
        )),
        ("allow-aws", "ALog/Writers/Cloud/AwsCloudWatchWriter.cs", allow_member(
            "class AwsCloudWatchWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + ALLOWED[2].justification
            + "\")] private void PutWithinTransportTimeout() { var first = DateTime.UtcNow; var second = first; } }\n",
        )),
        ("allow-file", "ALog/Writers/File/FileLogWriter.cs", allow_member(
            "class FileLogWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + ALLOWED[3].justification
            + "\")] private static object RotationStamp() => DateTime.UtcNow; }\n",
        )),
        ("allow-sql", "ALog/Writers/Sql/SqlLogWriter.cs", allow_member(
            "class SqlLogWriter { [System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0030\", Justification = \""
            + ALLOWED[4].justification
            + "\")] private static object CreatedAtStamp() => DateTime.UtcNow; }\n",
        )),
    ]


def allow_member(body: str) -> str:
    """Return one allowlisted member as a compilation unit."""
    return body


def plain_samples() -> list[tuple[str, str, str]]:
    """Cases decided from the source rules. They do not need a banned call."""
    return [
        ("pragma-bare", "ALog/Bypass.cs", "#pragma warning disable\nclass C {}\n"),
        ("pragma-lower", "ALog/Bypass.cs", "#pragma warning disable rs0030\nclass C {}\n"),
        ("pragma-upper", "ALog/Bypass.cs", "#pragma warning disable RS0030 // kept on purpose\nclass C {}\n"),
        ("pragma-restore", "ALog/Bypass.cs", "#pragma warning restore RS0030\nclass C {}\n"),
        ("comment", "ALog/Bypass.cs", "class C\n{\n    // [SuppressMessage(\"ApiDesign\", \"RS0030\")]\n    void M() {}\n}\n"),
        ("other-id", "ALog/Bypass.cs", """using System.Diagnostics.CodeAnalysis;
class C
{
    [SuppressMessage("ApiDesign", "RS0031")]
    void M() {}
}
"""),
        ("default-elsewhere", "ALog/Core/BackgroundLogQueue.cs", """namespace ALog.Core;
class ProductionClock
{
    public static System.TimeProvider Default => System.TimeProvider.System;
}
class Queue
{
    public Queue() : this(ProductionClock.Default) {}
    public Queue(System.TimeProvider clock) {}
    void Other() { _ = ProductionClock.Default; }
}
"""),
        ("derive", "ALog/Core/Manual.cs", "class Manual : System.TimeProvider {}\n"),
    ]


def cases_match(rows: list[tuple[str, list[str]]]) -> bool:
    """True when every row has the expected hit, or is clean."""
    expect_exact = {
        "changed-justification": [
            "ALog/Core/LoggerEngine.cs:5: RS0030 exception justification does not match the allowed text."
            ' Use the allowed text: "Event timestamp is wall-clock time by design; it is log data, not a deadline.".',
        ],
        "unsuppressed": [
            "ALog/Bypass.cs:3: RS0030 is not suppressed in source."
            " Remove the banned call and use the TimeProvider from the options;"
            " NoWarn, #pragma and .editorconfig are not allowed.",
        ],
        "dedup-identical": [
            "ALog/Bypass.cs:3: RS0030 is not suppressed in source."
            " Remove the banned call and use the TimeProvider from the options;"
            " NoWarn, #pragma and .editorconfig are not allowed.",
        ],
        "dedup-distinct": [
            "ALog/Core/LoggerEngine.cs:3: RS0030 is not suppressed in source."
            " Remove the banned call and use the TimeProvider from the options;"
            " NoWarn, #pragma and .editorconfig are not allowed.",
            "ALog/Core/LoggerEngine.cs:3: RS0030 exception justification does not match the allowed text."
            ' Use the allowed text: "Event timestamp is wall-clock time by design; it is log data, not a deadline.".',
        ],
        "sarif-missing": [
            "artifacts/ALog.Extensions.Logging.sarif: ErrorLog SARIF is missing."
            " Build with -p:ErrorLog=artifacts/ALog.Extensions.Logging.sarif,version=2.1.",
        ],
    }
    expect_hit = {
        "alias-without-justification": NO_JUST,
        "alias-namespace": NO_JUST,
        "unconditional": NO_JUST,
        "unconditional-justified": UNCONDITIONAL,
        "pragma-bare": PRAGMA,
        "pragma-lower": PRAGMA,
        "pragma-upper": PRAGMA,
        "assembly": NOT_MEMBER,
        "module": NOT_MEMBER,
        "type": NOT_MEMBER,
        "multiline-member": BLOCK,
        "sixth": NOT_MEMBER,
        "one-char": NOT_MEMBER,
        "blank": NO_JUST,
        "nowarn": NOWARN,
        "warnings-not-as-errors": NOWARN,
        "nested-editorconfig": SEVERITY,
        "globalconfig": SEVERITY,
        "linked-file": NOT_MEMBER,
        "default-elsewhere": DEFAULT,
        "derive": DERIVE,
    }
    expect_clean = {
        "pragma-restore",
        "comment",
        "other-id",
        "allow-default",
        "allow-event",
        "allow-aws",
        "allow-file",
        "allow-sql",
        "product-editorconfig",
    }
    if len(rows) != len(expect_hit) + len(expect_clean) + len(expect_exact):
        return False
    for name, lines in rows:
        exact = expect_exact.get(name)
        if exact is not None:
            if lines != exact:
                return False
            continue
        if name in expect_clean:
            if len(lines) != 0:
                return False
            continue
        message = expect_hit.get(name)
        if message is None or len(lines) != 1 or message not in lines[0]:
            return False
        if name == "default-elsewhere" and ":10:" not in lines[0]:
            return False
    return True


def scan_repo(repo: Path, sarif_paths: list[str]) -> list[str]:
    """Check editorconfig, msbuild properties, composition, pragmas, and the product SARIF logs.

    Args:
        repo: Repository root.
        sarif_paths: Logs from the CI build. Empty means this function builds the two product projects.

    Returns:
        Problem lines.

    Side effects:
        Runs msbuild. Builds the product projects when sarif_paths is empty.
    """
    problems = []
    composition = load_composition(repo)
    problems.extend(editorconfig_problems(".editorconfig", (repo / ".editorconfig").read_text(encoding="utf-8"), True))
    for file in config_files(repo):
        relative = file.relative_to(repo).as_posix()
        if relative == ".editorconfig":
            continue
        text = file.read_text(encoding="utf-8")
        if relative.endswith(".editorconfig") or relative.endswith(".globalconfig"):
            problems.extend(editorconfig_problems(relative, text, False))
    for project in (repo / "ALog" / "ALog.csproj", repo / "ALog.Extensions.Logging" / "ALog.Extensions.Logging.csproj"):
        problems.extend(property_problems(project.relative_to(repo).as_posix(), project))
    for path, text in product_sources(repo):
        problems.extend(source_problems(path, text, composition))
    workflow = (repo / ".github" / "workflows" / "dotnet.yml").read_text(encoding="utf-8")
    if "python3 eng/rs0030-justification-check.py" not in workflow:
        problems.append(hit(".github/workflows/dotnet.yml", 1, "the script tests do not run python3 eng/rs0030-justification-check.py."))
    if "ErrorLog=" not in workflow or "version=2.1" not in workflow:
        problems.append(hit(".github/workflows/dotnet.yml", 1, "the product build does not pass -p:ErrorLog=<file>.sarif,version=2.1."))
    logs = sarif_paths
    if len(logs) == 0:
        logs = build_products(repo)
    if len(logs) < 2:
        problems.append(hit("ALog.sln", 1, "both product projects need an ErrorLog SARIF."))
    problems.extend(sarif_log_problems([Path(log) for log in logs], repo))
    return problems


def repo_relative(log: str, root: Path) -> str:
    """Return log relative to root when it points inside root.

    Args:
        log: A SARIF path. Absolute or relative.
        root: Repository root.

    Returns:
        A forward-slash path. A relative log is normalized. An absolute log
        inside root drops the root prefix. An absolute log outside root stays absolute.
    """
    path = Path(log)
    if not path.is_absolute():
        return path.as_posix()
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def missing_sarif_line(log: str, root: Path) -> str:
    """One copyable line for an ErrorLog that was not written.

    Args:
        log: The path the check was given.
        root: Repository root, used when log is inside it.

    Returns:
        ``{RelPath}: ErrorLog SARIF is missing. Build with -p:ErrorLog={RelPath},version=2.1.``
        There is no ``:1``. RelPath is the same path in both places.
    """
    relative = repo_relative(log, root)
    return (
        relative
        + ": ErrorLog SARIF is missing. Build with -p:ErrorLog="
        + relative
        + ",version=2.1."
    )


def sarif_log_problems(logs: list[Path], root: Path) -> list[str]:
    """Read each ErrorLog. A missing file is one SARIF-missing line.

    Args:
        logs: Paths passed to the check, or written by the product build.
        root: Directory used to make paths relative.

    Returns:
        Problem lines. Two logs that report the same line both contribute it.
        The caller deduplicates.
    """
    problems = []
    for log in logs:
        if not log.is_file():
            problems.append(missing_sarif_line(str(log), root))
            continue
        problems.extend(sarif_problems(log, root))
    return problems


def config_files(repo: Path) -> list[Path]:
    """Editorconfig and globalconfig files outside build output."""
    found = []
    for path in repo.rglob("*"):
        if not path.is_file():
            continue
        if any(part in SKIP_DIRS for part in path.parts):
            continue
        name = path.name
        if name == ".editorconfig" or name.endswith(".editorconfig") or name == ".globalconfig" or name.endswith(".globalconfig"):
            found.append(path)
    return found


def product_sources(repo: Path) -> list[tuple[str, str]]:
    """C# files in the two product projects, including a linked file outside the directory."""
    files: list[tuple[str, str]] = []
    seen = set()
    for project in ("ALog", "ALog.Extensions.Logging"):
        directory = repo / project
        for path in directory.rglob("*.cs"):
            if any(part in SKIP_DIRS for part in path.parts):
                continue
            relative = path.relative_to(repo).as_posix()
            if relative in seen:
                continue
            seen.add(relative)
            files.append((relative, path.read_text(encoding="utf-8")))
        csproj = directory / (project + ".csproj")
        text = csproj.read_text(encoding="utf-8")
        for match in COMPILE_RE.finditer(text):
            include = match.group(1)
            if "*" in include:
                continue
            full = (directory / include).resolve()
            if not full.is_file():
                continue
            try:
                relative = full.relative_to(repo.resolve()).as_posix()
            except ValueError:
                relative = full.as_posix()
            if relative in seen:
                continue
            seen.add(relative)
            files.append((relative, full.read_text(encoding="utf-8")))
    return files


def build_products(repo: Path) -> list[str]:
    """Build both product projects with a SARIF 2.1 error log.

    Args:
        repo: Repository root.

    Returns:
        The two log paths. A failed build still returns a path when the log was written.

    Side effects:
        Runs ``dotnet build --no-incremental`` for ALog and ALog.Extensions.Logging.
        The compiler runs every time, so the ErrorLog is written again on a rerun.
    """
    logs = []
    root = Path(tempfile.mkdtemp(prefix="rs0030-product-"))
    projects = [
        ("ALog", repo / "ALog" / "ALog.csproj"),
        ("ALog.Extensions.Logging", repo / "ALog.Extensions.Logging" / "ALog.Extensions.Logging.csproj"),
    ]
    for name, csproj in projects:
        sarif = root / (name + ".sarif")
        subprocess.run(
            [
                "dotnet", "build", str(csproj),
                "-c", "Release", "--nologo", "--no-incremental",
                '-p:ErrorLog="' + str(sarif) + ',version=2.1"',
            ],
            cwd=repo,
            check=False,
        )
        logs.append(str(sarif))
    return logs


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
