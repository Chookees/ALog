#!/usr/bin/env python3
"""Require a coverage-exception line when a PR changes coverage policy.

Raising ratchet floors needs no reason. Each lowered metric needs its own
``coverage-exception: floor <package> <lines|branches> <old> -> <new>: <reason>``
line, and the drop may be at most one percentage point. A larger drop is
accepted only for the one-time C1-07 baseline: the base ``coverage.runsettings``
still lists ``CompilerGenerated`` in ``ExcludeByAttribute``, the head file does
not, every lowered metric has a floor line whose reason contains
``Lücken-Baseline``, and the new floor is not below the measured percent
rounded down to a whole number. After that removal the base file no longer
lists the name, so the larger drop cannot fire again. Adding any
``ExcludeByAttribute`` name (case-insensitive, with or without an ``Attribute``
suffix) is red even with a valid reason. Any other ratchet edit, and any
change to ``coverage.runsettings`` or ``coverage.config``, needs a general
reason of at least 10 characters with no ``<`` or ``>``. Added or changed
lines in ``*.cs``, ``*.csproj``, ``*.props``, ``*.targets``, and ``*.proj``
that contain the tokens ``ExcludeFromCodeCoverage``, ``GeneratedCode``, or
``CompilerGenerated`` need that general reason too, including comments,
aliases, and qualifiers. Before that search each added line is normalized:
C# ``\\uXXXX`` and ``\\UXXXXXXXX`` escapes are decoded, then every Unicode
format character (category Cf, which C# ignores inside identifiers) is
removed. MSBuild files (``.csproj``, ``.proj``, ``.props``, ``.targets``)
also decode decimal ``&#NN;``, hex ``&#xHH;``, and the named XML entities
once, before the format characters are removed. An added MSBuild line that
contains ``<Using`` and either ``Alias=`` or ``$(`` is a hit even when the
token is not written out, because a Using alias or a property can assemble
the attribute name. Paths under ``eng/`` are not scanned for those tokens,
because that tree is not measured. A change to a gate file
(``eng/coverage-*`` except the ratchet, ``eng/coverage-metadata/**``, or a
coverage step in ``.github/workflows/dotnet.yml``) needs the same general
reason. The ratchet file stays on rule (a), so a pure raise needs no reason.
In CI the body is read from the pulls API, not from the event payload.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import unicodedata
import urllib.request
import xml.etree.ElementTree as ElementTree
from decimal import Decimal, ROUND_DOWN
from pathlib import Path
from typing import Mapping

RATCHET = "eng/coverage-ratchet.json"
RUNSETTINGS = "coverage.runsettings"
POLICY_FILES = ("coverage.runsettings", "coverage.config")
WORKFLOW = ".github/workflows/dotnet.yml"
COVERAGE_STEP_MARKERS = (
    "coverage-gate.py",
    "coverage-exception-check.py",
    "coverage.runsettings",
    "coverage-ratchet",
    "coverage-metadata",
    "XPlat Code Coverage",
    "COVERAGE_ENFORCE",
    "COVERAGE_BASE",
)
MAX_WORKFLOW_LINES = 5000
MAX_WORKFLOW_STEPS = 200
HUNK_RE = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
# Normalized ExcludeByAttribute name. The one-time large-drop exception keys off this.
COMPILER_GENERATED = "compilergenerated"
ATTRIBUTE_SUFFIX = "attribute"
LUCKEN_MARKER = "Lücken-Baseline"
MAX_EXCLUDE_NAMES = 100
MAX_XML_ELEMENTS = 5000
EXCLUSION_TOKENS = ("ExcludeFromCodeCoverage", "GeneratedCode", "CompilerGenerated")
SOURCE_SUFFIXES = (".cs", ".csproj", ".props", ".targets", ".proj")
TOKEN_RE = re.compile(
    "(?<![A-Za-z0-9_])(?:" + "|".join(EXCLUSION_TOKENS) + ")(?:Attribute)?(?![A-Za-z0-9_])"
)
CS_ESCAPE = re.compile(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})")
# One pass only. A reference produced by decoding another reference is not expanded again.
XML_REF = re.compile(r"&#([0-9]{1,7});|&#x([0-9A-Fa-f]{1,6});|&(amp|lt|gt|quot|apos);")
XML_NAMED = {"amp": "&", "lt": "<", "gt": ">", "quot": '"', "apos": "'"}
MSBUILD_SUFFIXES = (".csproj", ".proj", ".props", ".targets")
HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)
FLOOR_LINE = re.compile(
    r"^coverage-exception:\s*floor\s+(\S+)\s+(lines|branches)\s+"
    r"([0-9]+(?:\.[0-9]+)?)\s+->\s+([0-9]+(?:\.[0-9]+)?)\s*:\s*(.*)$"
)
MIN_REASON_LENGTH = 10
MAX_FLOOR_DROP = Decimal("1")
MAX_LISTED = 40
MAX_DIFF_CHARS = 2_000_000
MAX_DIFF_LINES = 20_000
MAX_SOURCE_FILES = 500
MAX_BODY_CHARS = 1_000_000
MAX_ESCAPES = 1000
MAX_LINE_CHARS = 10_000


def strip_html_comments(body: str) -> str:
    """Remove HTML comments so a reason hidden in one does not count.

    Args:
        body: Pull-request text. Comments may span lines.

    Returns:
        The text with each ``<!-- ... -->`` region removed.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    if len(body) > MAX_BODY_CHARS:
        raise ValueError("pull request body is larger than 1000000 characters.")
    return HTML_COMMENT.sub("", body)


def usable_reason(text: str) -> bool:
    """Return whether a reason is long enough and has no angle brackets.

    Args:
        text: Reason text already stripped of surrounding space.

    Returns:
        True when the text has at least 10 characters and contains neither
        ``<`` nor ``>``. A placeholder such as ``<reason>`` fails both ways.

    Raises:
        None.

    Side effects:
        None.
    """
    if "<" in text or ">" in text:
        return False
    return len(text) >= MIN_REASON_LENGTH


def valid_reason(body: str) -> bool:
    """Return whether the body has a usable general coverage-exception reason.

    Args:
        body: Pull-request text, including any HTML comments.

    Returns:
        True when a line starts with ``coverage-exception:`` and the text after
        it has at least 10 characters and no angle brackets. A floor line is
        not a general reason.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    text = strip_html_comments(body)
    seen = 0
    for line in text.splitlines():
        seen += 1
        if seen > 5000:
            return False
        stripped = line.strip()
        if FLOOR_LINE.match(stripped) is not None:
            continue
        if not stripped.startswith("coverage-exception:"):
            continue
        if usable_reason(stripped.split(":", 1)[1].strip()):
            return True
    return False


def parse_floor_lines(body: str) -> list[tuple[str, str, Decimal, Decimal, str]]:
    """Read floor lines whose reason is usable.

    Args:
        body: Pull-request text. HTML comments are removed first.

    Returns:
        ``(package, metric, old, new, reason)`` for each matching line. At most
        40. A short reason or a reason that contains ``<`` or ``>`` drops the line.
        The reason is kept so a large drop can require the Lücken-Baseline marker.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    text = strip_html_comments(body)
    found: list[tuple[str, str, Decimal, Decimal, str]] = []
    seen = 0
    for line in text.splitlines():
        seen += 1
        if seen > 5000 or len(found) >= MAX_LISTED:
            break
        match = FLOOR_LINE.match(line.strip())
        if match is None or not usable_reason(match.group(5).strip()):
            continue
        found.append(
            (
                match.group(1),
                match.group(2),
                Decimal(match.group(3)),
                Decimal(match.group(4)),
                match.group(5).strip(),
            )
        )
    return found


def load_ratchet_text(text: str) -> tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]]:
    """Read targets and per-package floors from ratchet JSON text.

    Args:
        text: JSON object with ``linesTarget``, ``branchesTarget``, and ``packages``.

    Returns:
        Line target, branch target, then package name to ``(lines, branches)``.

    Raises:
        ValueError: The JSON is not a ratchet object.

    Side effects:
        None.
    """
    document = json.loads(text, parse_float=Decimal, parse_int=Decimal)
    if not isinstance(document, dict):
        raise ValueError("ratchet is not an object.")
    packages = document.get("packages")
    if not isinstance(packages, dict):
        raise ValueError("ratchet has no packages object.")
    floors: dict[str, tuple[Decimal, Decimal]] = {}
    for name, body in packages.items():
        if not isinstance(body, dict):
            raise ValueError(f"{name} floor is not an object.")
        floors[str(name)] = (Decimal(body["lines"]), Decimal(body["branches"]))
    return Decimal(document["linesTarget"]), Decimal(document["branchesTarget"]), floors


def is_pure_raise(
    baseline: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
    candidate: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
) -> bool:
    """Return whether the candidate only raises floors.

    Args:
        baseline: Targets and floors on the base branch.
        candidate: Targets and floors in the working tree.

    Returns:
        True when targets and package names are unchanged and at least one floor
        went up and none went down.

    Raises:
        None.

    Side effects:
        None.
    """
    if baseline[0] != candidate[0] or baseline[1] != candidate[1]:
        return False
    if set(baseline[2]) != set(candidate[2]):
        return False
    raised = False
    for name, old in baseline[2].items():
        new = candidate[2][name]
        if new[0] < old[0] or new[1] < old[1]:
            return False
        if new[0] > old[0] or new[1] > old[1]:
            raised = True
    return raised


def git_output(repo: Path, args: list[str]) -> str:
    """Run git and return stdout.

    Args:
        repo: Repository root.
        args: Arguments after ``git -C``.

    Returns:
        Standard output text.

    Raises:
        ValueError: Git exits non-zero.

    Side effects:
        Reads the git repository. Does not write it.
    """
    completed = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "git failed"
        raise ValueError(detail)
    return completed.stdout


def changed_paths(repo: Path, base: str) -> list[str]:
    """List every path that differs from the base ref.

    Args:
        repo: Repository root.
        base: Git ref such as ``origin/master``.

    Returns:
        Repo-relative paths. At most 5000.

    Raises:
        ValueError: Git cannot diff the ref, or the diff is too wide.

    Side effects:
        Reads git.
    """
    text = git_output(repo, ["diff", "--name-only", base])
    paths = [line.strip() for line in text.splitlines() if line.strip()]
    if len(paths) > 5000:
        raise ValueError("diff lists more than 5000 paths.")
    return paths


def decode_csharp_escapes(text: str) -> str:
    """Replace C# ``\\uXXXX`` and ``\\UXXXXXXXX`` escapes with characters.

    Args:
        text: One added diff line, without the leading plus.

    Returns:
        The line with those escapes decoded. A code point above U+10FFFF is
        left as the escape text. More than 1000 escapes fails closed by
        appending an exclusion token. The caller still has to strip format
        characters, and MSBuild lines still have to decode XML references.

    Raises:
        None.

    Side effects:
        None.
    """
    parts: list[str] = []
    cursor = 0
    seen = 0
    for match in CS_ESCAPE.finditer(text):
        seen += 1
        if seen > MAX_ESCAPES:
            parts.append(EXCLUSION_TOKENS[0])
            return "".join(parts)
        parts.append(text[cursor:match.start()])
        code = int(match.group(1) or match.group(2) or "0", 16)
        if code > 0x10FFFF:
            parts.append(match.group(0))
        else:
            parts.append(chr(code))
        cursor = match.end()
    parts.append(text[cursor:])
    return "".join(parts)


def xml_reference_text(match: re.Match[str]) -> str:
    """Turn one XML reference match into the character it names.

    Args:
        match: A match of ``XML_REF``. Group 1 is decimal, group 2 is hex,
        and group 3 is a named entity (``amp``, ``lt``, ``gt``, ``quot``,
        ``apos``).

    Returns:
        The decoded character. A code point above U+10FFFF is returned as the
        original reference text so ``chr`` is never called on it.

    Raises:
        None.

    Side effects:
        None.
    """
    named = match.group(3)
    if named:
        return XML_NAMED[named]
    digits = match.group(1) or match.group(2) or "0"
    code = int(digits, 10 if match.group(1) else 16)
    if code > 0x10FFFF:
        return match.group(0)
    return chr(code)


def decode_xml_references(text: str) -> str:
    """Replace XML character references on one MSBuild line.

    Args:
        text: One added line after C# ``\\u`` / ``\\U`` decoding.

    Returns:
        Decimal ``&#NN;``, hex ``&#xHH;``, and the five named entities replaced
        once. ``&#38;#69;`` becomes the text ``&#69;`` and is not expanded
        again. More than 1000 references fails closed.

    Raises:
        None.

    Side effects:
        None.
    """
    parts: list[str] = []
    cursor = 0
    seen = 0
    for match in XML_REF.finditer(text):
        seen += 1
        if seen > MAX_ESCAPES:
            parts.append(EXCLUSION_TOKENS[0])
            return "".join(parts)
        parts.append(text[cursor:match.start()])
        parts.append(xml_reference_text(match))
        cursor = match.end()
    parts.append(text[cursor:])
    return "".join(parts)


def strip_format_characters(text: str) -> str:
    """Drop Unicode format characters before the token search.

    Args:
        text: One added line after escapes and, for MSBuild, XML references.

    Returns:
        The line with every character whose Unicode category is Cf removed.
        C# ignores those characters inside identifiers, including the soft
        hyphen U+00AD, the zero-width space U+200B, and the word joiner
        U+2060. A line longer than 10000 characters fails closed.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(text) > MAX_LINE_CHARS:
        return EXCLUSION_TOKENS[0]
    kept: list[str] = []
    seen = 0
    for char in text:
        seen += 1
        if seen > MAX_LINE_CHARS:
            return EXCLUSION_TOKENS[0]
        if unicodedata.category(char) == "Cf":
            continue
        kept.append(char)
    return "".join(kept)


def is_msbuild_path(path: str) -> bool:
    """Return whether a diff path is an MSBuild file.

    Args:
        path: Repo-relative path from a ``+++ b/`` header. Empty when the diff
        has no header, which is how an over-cap marker is scanned.

    Returns:
        True for ``.csproj``, ``.proj``, ``.props``, and ``.targets``. The
        match is case-insensitive. A ``.cs`` file is false.

    Raises:
        None.

    Side effects:
        None.
    """
    if not path:
        return False
    return path.lower().endswith(MSBUILD_SUFFIXES)


def prepare_scanned_line(text: str, msbuild: bool) -> str:
    """Normalize one added line the way the compiler or MSBuild would read it.

    Args:
        text: One added diff line, without the leading plus.
        msbuild: True when the current file is ``.csproj``, ``.proj``,
        ``.props``, or ``.targets``.

    Returns:
        C# unicode escapes decoded, then XML references when ``msbuild`` is
        true, then format characters removed. XML decoding is one pass.

    Raises:
        None.

    Side effects:
        None.
    """
    decoded = decode_csharp_escapes(text)
    if msbuild:
        decoded = decode_xml_references(decoded)
    return strip_format_characters(decoded)


def using_line_triggers(text: str) -> bool:
    """Return whether an MSBuild Using line can hide an exclusion attribute.

    Args:
        text: One prepared MSBuild line. The strings are matched exactly.

    Returns:
        True when the line contains ``<Using`` and either ``Alias=`` or
        ``$(``. A plain ``<Using Include="System.Linq"/>`` is false.
        ``<UsingTask`` contains ``<Using`` and is a hit only when an alias or
        a property is also present.

    Raises:
        None.

    Side effects:
        None.
    """
    if "<Using" not in text:
        return False
    return "Alias=" in text or "$(" in text


def added_line_triggers(text: str, msbuild: bool) -> bool:
    """Return whether one added line is an exclusion hit.

    Args:
        text: One added diff line, without the leading plus.
        msbuild: True for an MSBuild file. XML references and the Using rule
        apply only then.

    Returns:
        True when the prepared line contains an exclusion token, or when an
        MSBuild line contains ``<Using`` plus ``Alias=`` or ``$(``.

    Raises:
        None.

    Side effects:
        None.
    """
    prepared = prepare_scanned_line(text, msbuild)
    if prepared and TOKEN_RE.search(prepared):
        return True
    if not msbuild:
        return False
    return using_line_triggers(prepared)


def diff_path(header: str) -> str:
    """Read the repo path from a unified-diff ``+++`` header.

    Args:
        header: A line that starts with ``+++ ``, including the space Git
        writes before ``b/``.

    Returns:
        The path after the ``b/`` prefix. ``/dev/null`` stays unchanged so it
        does not look like a source file.

    Raises:
        None.

    Side effects:
        None.
    """
    raw = header[4:].strip()
    if raw.startswith("b/"):
        return raw[2:]
    return raw


def exclusion_token_added(diff_text: str) -> bool:
    """Return whether an added or changed line contains an exclusion token.

    Args:
        diff_text: Unified diff of C# and MSBuild files. The current file comes
        from each ``+++ b/`` header. Comments, aliases, qualifiers, C# unicode
        escapes, Unicode format characters, and MSBuild character references
        count. A huge diff fails closed.

    Returns:
        True when a ``+`` line contains one of ``EXCLUSION_TOKENS`` after that
        normalization, or an MSBuild line contains ``<Using`` and either
        ``Alias=`` or ``$(``. A line that itself starts with ``+++`` is a
        header or an added ``++`` line and is not searched.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(diff_text) > MAX_DIFF_CHARS:
        return True
    seen = 0
    path = ""
    for line in diff_text.splitlines():
        seen += 1
        if seen > MAX_DIFF_LINES:
            return True
        if line.startswith("+++"):
            if line.startswith("+++ "):
                path = diff_path(line)
            continue
        if not line.startswith("+"):
            continue
        if added_line_triggers(line[1:], is_msbuild_path(path)):
            return True
    return False


def measured_source_path(path: str) -> bool:
    """Return whether a path is scanned for exclusion tokens.

    Args:
        path: Repo-relative path from the diff.

    Returns:
        True for a ``.cs`` or MSBuild file outside ``eng/``. Files under
        ``eng/`` are not measured; gate files there use the gate-file rule.

    Raises:
        None.

    Side effects:
        None.
    """
    normalized = path.replace("\\", "/")
    if normalized == "eng" or normalized.startswith("eng/"):
        return False
    return normalized.lower().endswith(SOURCE_SUFFIXES)


def source_diff(repo: Path, base: str, paths: list[str]) -> str:
    """Return the unified diff of source files that can carry the tokens.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        paths: Changed paths. Only ``.cs``, ``.csproj``, ``.props``, ``.targets``,
        and ``.proj`` are kept. The MSBuild suffixes are the files whose XML
        references and ``<Using`` lines are interpreted.

    Returns:
        The diff text. Empty when none of those files changed. Paths under
        ``eng/`` are skipped. More than 500 matching files returns a marker
        that the caller treats as a hit. The marker has no ``+++`` header, so
        it is scanned as a non-MSBuild line.

    Raises:
        ValueError: Git cannot diff the selected files.

    Side effects:
        Reads git.
    """
    selected: list[str] = []
    for path in paths:
        if not measured_source_path(path):
            continue
        selected.append(path)
        if len(selected) > MAX_SOURCE_FILES:
            return "+" + EXCLUSION_TOKENS[0]
    if not selected:
        return ""
    return git_output(repo, ["diff", "-U0", base, "--", *selected])


def show_git_text(repo: Path, base: str, path: str) -> str | None:
    """Return one file at a git ref, or None when that path is absent.

    Args:
        repo: Repository root.
        base: Git ref such as ``origin/master``.
        path: Repo-relative path. A missing path is not an error.

    Returns:
        File text. None when git cannot show that path.

    Raises:
        None.

    Side effects:
        Reads git. Does not write it.
    """
    show = subprocess.run(
        ["git", "-C", str(repo), "show", f"{base}:{path}"],
        check=False,
        capture_output=True,
        text=True,
    )
    if show.returncode != 0:
        return None
    return show.stdout


def show_base_ratchet(repo: Path, base: str) -> str | None:
    """Return the ratchet file at the base ref, or None when it is absent.

    Args:
        repo: Repository root.
        base: Git ref that may or may not contain the ratchet file.

    Returns:
        File text. None when git cannot show that path.

    Raises:
        None.

    Side effects:
        Reads git.
    """
    return show_git_text(repo, base, RATCHET)


def normalize_exclusion_entry(token: str) -> str:
    """Return one ExcludeByAttribute name in comparable form.

    Args:
        token: One comma-separated entry. Whitespace is ignored. An ``Attribute``
        suffix is optional. Empty input stays empty.

    Returns:
        The name folded to lower case. A trailing ``Attribute`` suffix is removed
        when characters remain, so ``CompilerGenerated`` and
        ``CompilerGeneratedAttribute`` compare equal.

    Raises:
        None.

    Side effects:
        None.
    """
    cleaned = token.strip().casefold()
    if cleaned.endswith(ATTRIBUTE_SUFFIX) and len(cleaned) > len(ATTRIBUTE_SUFFIX):
        return cleaned[: -len(ATTRIBUTE_SUFFIX)]
    return cleaned


def local_xml_name(tag: str) -> str:
    """Return an XML tag without its namespace.

    Args:
        tag: Element tag, possibly ``{namespace}name``.

    Returns:
        The local name. A tag without a namespace is returned unchanged.

    Raises:
        None.

    Side effects:
        None.
    """
    if "}" in tag:
        return tag.split("}", 1)[1]
    return tag


def parse_exclude_by_attribute(text: str | None) -> set[str]:
    """Read the normalized ExcludeByAttribute set from runsettings text.

    Args:
        text: ``coverage.runsettings`` contents. ``None`` means the file is
        absent and the set is empty.

    Returns:
        Names compared case-insensitively, without an ``Attribute`` suffix.
        Duplicate names collapse. At most 100 names are accepted.

    Raises:
        ValueError: The text is not XML, the document is larger than 2000000
        characters, or it has more than 5000 elements or 100 names.

    Side effects:
        None.
    """
    if text is None:
        return set()
    if len(text) > MAX_DIFF_CHARS:
        raise ValueError("coverage.runsettings is larger than 2000000 characters.")
    root = ElementTree.fromstring(text)
    names: set[str] = set()
    seen = 0
    for element in root.iter():
        seen += 1
        if seen > MAX_XML_ELEMENTS:
            raise ValueError("coverage.runsettings has more than 5000 elements.")
        if local_xml_name(element.tag).casefold() != "excludebyattribute":
            continue
        parts = (element.text or "").split(",")
        if len(parts) > MAX_EXCLUDE_NAMES:
            raise ValueError("ExcludeByAttribute lists more than 100 names.")
        index = 0
        while index < len(parts) and index < MAX_EXCLUDE_NAMES:
            name = normalize_exclusion_entry(parts[index])
            if name:
                names.add(name)
                if len(names) > MAX_EXCLUDE_NAMES:
                    raise ValueError("ExcludeByAttribute lists more than 100 names.")
            index += 1
    return names


def read_worktree_exclude(repo: Path) -> set[str]:
    """Return ExcludeByAttribute names from the working tree runsettings.

    Args:
        repo: Repository root. The head file is ``coverage.runsettings``.

    Returns:
        Normalized names. An absent file is an empty set.

    Raises:
        ValueError: The file is present but not usable XML.
        OSError: The file cannot be read.

    Side effects:
        Reads the working tree.
    """
    path = repo / RUNSETTINGS
    if not path.is_file():
        return set()
    return parse_exclude_by_attribute(path.read_text(encoding="utf-8"))


def compiler_generated_removed(repo: Path, base: str) -> bool:
    """Return whether this diff drops CompilerGenerated from ExcludeByAttribute.

    Args:
        repo: Repository root. The working tree is the head file.
        base: Git ref of the base branch.

    Returns:
        True only when the base file lists ``CompilerGenerated`` and the head
        file does not. A missing file, a parse error, or any other edit is
        False, so a large floor drop stays on the one-point rule.

    Raises:
        None. XML and IO errors return False.

    Side effects:
        Reads git and the working tree.
    """
    try:
        before = parse_exclude_by_attribute(show_git_text(repo, base, RUNSETTINGS))
        after = read_worktree_exclude(repo)
    except (OSError, ValueError, ElementTree.ParseError):
        return False
    return COMPILER_GENERATED in before and COMPILER_GENERATED not in after


def exclude_names_added(repo: Path, base: str) -> bool:
    """Return whether head's ExcludeByAttribute set gained a name.

    Args:
        repo: Repository root. The working tree is the head file.
        base: Git ref of the base branch.

    Returns:
        True when head has a normalized name that base does not. Reordering,
        case, and an ``Attribute`` suffix do not count. Removing a name is False.

    Raises:
        ValueError: A present file is not usable XML.
        OSError: The head file cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    before = parse_exclude_by_attribute(show_git_text(repo, base, RUNSETTINGS))
    after = read_worktree_exclude(repo)
    return bool(after - before)


def lowered_metrics(
    baseline: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
    candidate: tuple[Decimal, Decimal, dict[str, tuple[Decimal, Decimal]]],
) -> list[tuple[str, str, Decimal, Decimal]]:
    """List package metrics whose floor went down.

    Args:
        baseline: Targets and floors on the base ref.
        candidate: Targets and floors in the working tree.

    Returns:
        ``(package, metric, old, new)`` for each drop. At most 40 metrics.

    Raises:
        None.

    Side effects:
        None.
    """
    names = sorted(set(baseline[2]) & set(candidate[2]))
    if len(names) > MAX_LISTED:
        names = names[:MAX_LISTED]
    found: list[tuple[str, str, Decimal, Decimal]] = []
    for name in names:
        old = baseline[2][name]
        new = candidate[2][name]
        if new[0] < old[0]:
            found.append((name, "lines", old[0], new[0]))
        if new[1] < old[1]:
            found.append((name, "branches", old[1], new[1]))
    return found


def ratchet_parts(
    repo: Path, base: str
) -> tuple[bool, list[tuple[str, str, Decimal, Decimal]]]:
    """Split a ratchet diff into a general-reason flag and the lowered metrics.

    Args:
        repo: Repository root.
        base: Git ref to compare.

    Returns:
        Whether something other than a pure raise or a plain floor drop needs
        a general reason, and the lowered metrics. A new file needs a general
        reason and has no old values to name.

    Raises:
        ValueError: A ratchet file is not valid JSON.

    Side effects:
        Reads git and the working tree file.
    """
    candidate = load_ratchet_text((repo / RATCHET).read_text(encoding="utf-8"))
    shown = show_base_ratchet(repo, base)
    if shown is None:
        return True, []
    baseline = load_ratchet_text(shown)
    if is_pure_raise(baseline, candidate):
        return False, []
    lowered = lowered_metrics(baseline, candidate)
    same_targets = baseline[0] == candidate[0] and baseline[1] == candidate[1]
    same_packages = set(baseline[2]) == set(candidate[2])
    if same_targets and same_packages and lowered:
        return False, lowered
    return True, lowered


def drop_is_listed(
    drop: tuple[str, str, Decimal, Decimal],
    lines: list[tuple[str, str, Decimal, Decimal, str]],
) -> bool:
    """Return whether one drop has a floor line with the same package and numbers.

    Args:
        drop: ``(package, metric, old, new)`` from the ratchet diff.
        lines: Parsed floor lines. At most 40 are inspected.

    Returns:
        True when package, metric, old, and new all match. Numeric equality
        treats ``50`` and ``50.0`` as the same floor.

    Raises:
        None.

    Side effects:
        None.
    """
    seen = 0
    for line in lines:
        seen += 1
        if seen > MAX_LISTED:
            return False
        if line[0] == drop[0] and line[1] == drop[1] and line[2] == drop[2] and line[3] == drop[3]:
            return True
    return False


def whole_percent(percent: Decimal) -> Decimal:
    """Round a measured percent down to a whole number.

    Args:
        percent: A percent already on the coverage gate's scale, one decimal
        place, from 0 to 100. This is not the raw 0..1 Cobertura rate.

    Returns:
        The greatest whole percent that is not above ``percent``. ``59.2``
        becomes ``59``. ``60.0`` stays ``60``.

    Raises:
        None.

    Side effects:
        None.
    """
    return percent.to_integral_value(rounding=ROUND_DOWN)


def luecken_line_matches(
    drop: tuple[str, str, Decimal, Decimal],
    lines: list[tuple[str, str, Decimal, Decimal, str]],
) -> bool:
    """Return whether a drop has a floor line marked Lücken-Baseline.

    Args:
        drop: ``(package, metric, old, new)`` from the ratchet diff.
        lines: Parsed floor lines, including the reason. At most 40 are read.

    Returns:
        True when package, metric, old, and new match a line whose reason
        contains ``Lücken-Baseline``. A normal floor reason does not match.

    Raises:
        None.

    Side effects:
        None.
    """
    seen = 0
    for line in lines:
        seen += 1
        if seen > MAX_LISTED:
            return False
        if line[0] != drop[0] or line[1] != drop[1]:
            continue
        if line[2] != drop[2] or line[3] != drop[3]:
            continue
        if LUCKEN_MARKER in line[4]:
            return True
    return False


def measurement_allows(
    drop: tuple[str, str, Decimal, Decimal],
    measured: Mapping[str, tuple[Decimal, Decimal]] | None,
) -> bool:
    """Return whether a new floor is at least the measured percent rounded down.

    Args:
        drop: ``(package, metric, old, new)``. ``new`` is the proposed floor.
        measured: Package name to ``(lines, branches)`` in percent. ``None``
        means this run has no Cobertura report.

    Returns:
        True when the package was measured and ``new`` is greater than or equal
        to that percent rounded down. Missing data is False, so a large drop
        cannot skip the measurement.

    Raises:
        None.

    Side effects:
        None.
    """
    if measured is None:
        return False
    package, metric, _old, new = drop
    pair = measured.get(package)
    if pair is None:
        return False
    percent = pair[0] if metric == "lines" else pair[1]
    return new >= whole_percent(percent)


def below_measurement_problem(
    drop: tuple[str, str, Decimal, Decimal],
    measured: Mapping[str, tuple[Decimal, Decimal]] | None,
) -> str:
    """Describe a floor that sits below the measured coverage.

    Args:
        drop: ``(package, metric, old, new)``. ``new`` is the proposed floor.
        measured: Package percents, or ``None`` when no report was collected.

    Returns:
        One problem sentence. The rounded-down percent is ``missing`` when that
        package was not measured, so the log shows why the drop was rejected.

    Raises:
        None.

    Side effects:
        None.
    """
    package, metric, _old, new = drop
    shown = "missing"
    if measured is not None and package in measured:
        pair = measured[package]
        percent = pair[0] if metric == "lines" else pair[1]
        shown = format(whole_percent(percent), "f")
    return (
        f"{package} {metric} floor {new} is below the measured coverage "
        f"rounded down to {shown}."
    )


def unauthorized_drops(
    lowered: list[tuple[str, str, Decimal, Decimal]],
    body: str,
    *,
    compiler_generated_removed: bool = False,
    measured: Mapping[str, tuple[Decimal, Decimal]] | None = None,
) -> list[str]:
    """Return drops that this body does not authorize.

    Args:
        lowered: Metrics that went down. Each entry is package, metric, old, new.
        body: Pull-request text.
        compiler_generated_removed: True only when this diff is the one-time
        removal of ``CompilerGenerated`` from ``ExcludeByAttribute``. The caller
        decides that from the runsettings files. The reason text alone is not
        enough, and the default keeps the one-point rule.
        measured: Head coverage in percent, or ``None`` when it was not collected.
        A large drop must be at least the measured value rounded down.

    Returns:
        One problem per drop that this body does not authorize. Empty when
        every drop is at most one percentage point and named exactly, or when
        a larger drop meets every Lücken-Baseline condition.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    lines = parse_floor_lines(body)
    problems: list[str] = []
    seen = 0
    for drop in lowered:
        seen += 1
        if seen > MAX_LISTED:
            problems.append("More than 40 lowered floors.")
            break
        package, metric, old, new = drop
        if old - new <= MAX_FLOOR_DROP:
            if not drop_is_listed(drop, lines):
                problems.append(
                    f"{package} {metric} {old} -> {new} has no matching coverage-exception floor line."
                )
            continue
        if compiler_generated_removed and luecken_line_matches(drop, lines):
            if measurement_allows(drop, measured):
                continue
            problems.append(below_measurement_problem(drop, measured))
            continue
        problems.append(
            f"{package} {metric} drop {old} -> {new} exceeds 1 percentage point."
        )
    return problems


def is_gate_script_path(path: str) -> bool:
    """Return whether a path is a coverage gate file other than the ratchet.

    Args:
        path: Repo-relative path.

    Returns:
        True for ``eng/coverage-*`` and for everything under
        ``eng/coverage-metadata``. ``eng/coverage-ratchet.json`` is false so a
        pure raise stays on rule (a) and needs no reason.

    Raises:
        None.

    Side effects:
        None.
    """
    normalized = path.replace("\\", "/")
    if normalized == RATCHET:
        return False
    if normalized.startswith("eng/coverage-metadata/"):
        return True
    parent, _, name = normalized.rpartition("/")
    return parent == "eng" and name.startswith("coverage-")


def gate_file_changed(paths: list[str]) -> bool:
    """Return whether the diff touches a coverage gate file.

    Args:
        paths: Changed repo-relative paths.

    Returns:
        True when one path is a gate script or metadata file. The ratchet file
        is not a gate file here.

    Raises:
        None.

    Side effects:
        None.
    """
    for path in paths:
        if is_gate_script_path(path):
            return True
    return False


def marker_present(text: str) -> bool:
    """Return whether text names a coverage gate or its workflow step.

    Args:
        text: A step body or one diff line.

    Returns:
        True when a coverage marker appears as a substring.

    Raises:
        None.

    Side effects:
        None.
    """
    return any(marker in text for marker in COVERAGE_STEP_MARKERS)


def collect_coverage_steps(
    lines: list[str],
    index: int,
    indent: str,
    ranges: list[tuple[int, int]],
) -> int:
    """Append coverage-step ranges from one ``steps:`` block.

    Args:
        lines: Workflow lines.
        index: Zero-based index of the first line after ``steps:``.
        indent: Whitespace before ``steps:``. A step item is that plus ``- ``.
        ranges: Output list of 1-based inclusive line ranges.

    Returns:
        The zero-based index of the first line after this steps block.

    Raises:
        ValueError: The block has more than 200 steps.

    Side effects:
        Appends to ``ranges``.
    """
    prefix = indent + "- "
    steps = 0
    while index < len(lines):
        line = lines[index]
        if line.strip() == "":
            index += 1
            continue
        if not line.startswith(prefix):
            return index
        steps += 1
        if steps > MAX_WORKFLOW_STEPS:
            raise ValueError("workflow has more than 200 steps.")
        start = index
        index += 1
        while index < len(lines) and not lines[index].startswith(prefix):
            if lines[index] and not lines[index].startswith(indent):
                break
            index += 1
        if marker_present("\n".join(lines[start:index])):
            ranges.append((start + 1, index))
    return index


def coverage_step_ranges(text: str) -> list[tuple[int, int]]:
    """Return 1-based inclusive ranges of workflow steps that run coverage.

    Args:
        text: Workflow YAML. The indent of ``steps:`` defines the step prefix.

    Returns:
        Ranges whose step text contains a coverage marker. Empty when the file
        has no such step.

    Raises:
        ValueError: The file is longer than 5000 lines.

    Side effects:
        None.
    """
    lines = text.splitlines()
    if len(lines) > MAX_WORKFLOW_LINES:
        raise ValueError("workflow is too large to scan.")
    ranges: list[tuple[int, int]] = []
    index = 0
    while index < len(lines):
        matched = re.match(r"^(\s*)steps:\s*$", lines[index])
        if matched is None:
            index += 1
            continue
        index = collect_coverage_steps(lines, index + 1, matched.group(1), ranges)
    return ranges


def line_in_ranges(number: int, ranges: list[tuple[int, int]]) -> bool:
    """Return whether a 1-based line number sits in a coverage step.

    Args:
        number: 1-based line number from a diff hunk.
        ranges: Inclusive ranges from ``coverage_step_ranges``.

    Returns:
        True when ``number`` is inside one range.

    Raises:
        None.

    Side effects:
        None.
    """
    for start, end in ranges:
        if start <= number <= end:
            return True
    return False


def hunk_starts(line: str) -> tuple[int, int]:
    """Read the old and new start lines from a unified-diff hunk header.

    Args:
        line: A line that starts with ``@@``.

    Returns:
        Old start line and new start line. A new file starts the old side at 0.

    Raises:
        ValueError: The header is not a unified hunk.

    Side effects:
        None.
    """
    match = HUNK_RE.match(line)
    if match is None:
        raise ValueError("workflow diff hunk is not readable.")
    return int(match.group(1)), int(match.group(3))


def diff_touches_coverage(
    diff_text: str,
    base_ranges: list[tuple[int, int]],
    head_ranges: list[tuple[int, int]],
) -> bool:
    """Return whether a workflow diff edits a coverage step.

    Args:
        diff_text: Unified diff with zero context lines.
        base_ranges: Coverage steps in the base file.
        head_ranges: Coverage steps in the head file.

    Returns:
        True when an added line is in a head coverage step, a removed line is
        in a base coverage step, or the changed line itself contains a marker.
        A huge diff is a hit.

    Raises:
        ValueError: A hunk header cannot be read.

    Side effects:
        None.
    """
    if len(diff_text) > MAX_DIFF_CHARS:
        return True
    old_line = 0
    new_line = 0
    seen = 0
    for line in diff_text.splitlines():
        seen += 1
        if seen > MAX_DIFF_LINES:
            return True
        if line.startswith("@@"):
            old_line, new_line = hunk_starts(line)
            continue
        if line.startswith("+") and not line.startswith("+++"):
            if line_in_ranges(new_line, head_ranges) or marker_present(line[1:]):
                return True
            new_line += 1
            continue
        if line.startswith("-") and not line.startswith("---"):
            if line_in_ranges(old_line, base_ranges) or marker_present(line[1:]):
                return True
            old_line += 1
            continue
        if line.startswith("\\"):
            continue
        old_line += 1
        new_line += 1
    return False


def read_head_text(repo: Path, path: str) -> str:
    """Return a working-tree file, or empty text when it is absent.

    Args:
        repo: Repository root.
        path: Repo-relative path.

    Returns:
        UTF-8 text. Empty when the path is not a file.

    Raises:
        OSError: The file exists but cannot be read.

    Side effects:
        Reads the working tree.
    """
    target = repo / path
    if not target.is_file():
        return ""
    return target.read_text(encoding="utf-8")


def workflow_coverage_changed(repo: Path, base: str) -> bool:
    """Return whether a coverage step in the library workflow changed.

    Args:
        repo: Repository root.
        base: Git ref to diff against.

    Returns:
        True when an added or removed line sits in a step that contains a
        coverage marker, or the line itself contains one. A workflow that is
        too large to parse is a hit. False when the workflow did not change.

    Raises:
        ValueError: Git cannot show the file or the diff.

    Side effects:
        Reads git and the working tree.
    """
    diff = git_output(repo, ["diff", "-U0", base, "--", WORKFLOW])
    if not diff:
        return False
    base_text = show_git_text(repo, base, WORKFLOW) or ""
    try:
        base_ranges = coverage_step_ranges(base_text)
        head_ranges = coverage_step_ranges(read_head_text(repo, WORKFLOW))
    except ValueError:
        return True
    return diff_touches_coverage(diff, base_ranges, head_ranges)


def decide(
    repo: Path,
    base: str,
    body: str,
    measured: Mapping[str, tuple[Decimal, Decimal]] | None = None,
) -> list[str]:
    """Return policy changes that are still missing their exception line.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        body: Pull-request body.
        measured: Head line and branch percents by package. ``None`` means no
        Cobertura report was collected. A large Lücken-Baseline drop then fails
        closed, because the floor cannot be checked against the measurement.

    Returns:
        Trigger names. ``floor-lower`` means a drop is missing its floor line,
        is larger than one percentage point without the one-time baseline
        exception, or sits below the measured coverage. ``exclude-attribute-added``
        means         ``ExcludeByAttribute`` gained a name; a reason does not clear it.
        ``coverage-gate-file`` means a gate script, coverage metadata, or a
        coverage workflow step changed. A general reason does not clear
        ``floor-lower``. Empty when the diff is clean or authorized.

    Raises:
        ValueError: Git or JSON cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    paths = changed_paths(repo, base)
    found: list[str] = []
    lowered: list[tuple[str, str, Decimal, Decimal]] = []
    if RATCHET in paths:
        needs_reason, lowered = ratchet_parts(repo, base)
        if needs_reason:
            found.append("coverage-ratchet")
    if any(path in paths for path in POLICY_FILES):
        found.append("coverage-settings")
    if exclusion_token_added(source_diff(repo, base, paths)):
        found.append("exclusion-attribute")
    if gate_file_changed(paths) or workflow_coverage_changed(repo, base):
        found.append("coverage-gate-file")
    missing: list[str] = []
    if found and not valid_reason(body):
        missing.extend(found)
    removed = compiler_generated_removed(repo, base) if RUNSETTINGS in paths else False
    if unauthorized_drops(
        lowered,
        body,
        compiler_generated_removed=removed,
        measured=measured,
    ):
        missing.append("floor-lower")
    if RUNSETTINGS in paths and runsettings_added_name(repo, base):
        missing.append("exclude-attribute-added")
    return missing


def runsettings_added_name(repo: Path, base: str) -> bool:
    """Return whether the runsettings diff added an exclusion name.

    Args:
        repo: Repository root.
        base: Git ref to diff against.

    Returns:
        True when head gained a normalized ``ExcludeByAttribute`` name, or when
        the file cannot be parsed. A parse error is treated as an addition so
        broken XML cannot hide a new name.

    Raises:
        None.

    Side effects:
        Reads git and the working tree.
    """
    try:
        return exclude_names_added(repo, base)
    except (OSError, ValueError, ElementTree.ParseError):
        return True


def fetch_pull_request_body(env: Mapping[str, str] | None = None) -> str:
    """Read the current pull-request body from the GitHub pulls API.

    Args:
        env: Environment with ``GITHUB_TOKEN``, ``GITHUB_REPOSITORY``, and
        ``PR_NUMBER``. ``None`` uses ``os.environ``.

    Returns:
        The body text. A null body becomes an empty string.

    Raises:
        ValueError: The environment or the payload is not usable.
        OSError: The API request fails.

    Side effects:
        Sends one HTTPS GET to ``/repos/{owner}/{repo}/pulls/{number}``.
    """
    source = os.environ if env is None else env
    token = source.get("GITHUB_TOKEN", "")
    repo = source.get("GITHUB_REPOSITORY", "")
    number = source.get("PR_NUMBER", "")
    if not token:
        raise ValueError("GITHUB_TOKEN is missing.")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ValueError("GITHUB_REPOSITORY is missing.")
    if not number.isdigit() or len(number) > 10:
        raise ValueError("PR_NUMBER is missing.")
    url = f"https://api.github.com/repos/{repo}/pulls/{number}"
    request = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "User-Agent": "alog-coverage-exception-check",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read(MAX_BODY_CHARS + 1)
    if len(raw) > MAX_BODY_CHARS:
        raise ValueError("pull request payload is too large.")
    payload = json.loads(raw.decode("utf-8"))
    body = payload.get("body") or ""
    if not isinstance(body, str):
        raise ValueError("pull request body is not text.")
    return body


def load_body(env: Mapping[str, str] | None = None) -> str:
    """Choose the pull-request body from the API or from ``PR_BODY``.

    Args:
        env: Environment. ``None`` uses ``os.environ``. When the GitHub token
        and pull-request number are set, the API wins over ``PR_BODY``.

    Returns:
        The body text to validate.

    Raises:
        ValueError: The API environment or payload is not usable.
        OSError: The API request fails.

    Side effects:
        May send one HTTPS GET.
    """
    source = os.environ if env is None else env
    if source.get("GITHUB_TOKEN") and source.get("PR_NUMBER") and source.get("GITHUB_REPOSITORY"):
        return fetch_pull_request_body(source)
    return source.get("PR_BODY", "")


# BB-09. The tool resolves attribute types from metadata. These two names are
# the only ones that make a new head entry need a coverage-exception line.
METADATA_TOOL = Path("eng/coverage-metadata/bin/Release/net10.0/CoverageMetadata.dll")
HEAD_ASSEMBLIES = (
    Path("ALog/bin/Release/net10.0/ALog.dll"),
    Path("ALog.Extensions.Logging/bin/Release/net10.0/ALog.Extensions.Logging.dll"),
)
BASE_ASSEMBLY_NAMES = ("ALog.dll", "ALog.Extensions.Logging.dll")
MAX_ASSEMBLIES = 8
MAX_METADATA_LINES = 100_000
MAX_METADATA_LINE_CHARS = 4_000


def metadata_requires_reason(new_lines: list[str], body: str) -> bool:
    """Return whether new metadata entries still need a coverage-exception line.

    Args:
        new_lines: Attribute lines present in head and absent in base. Empty
            means nothing was added, including a removed attribute.
        body: Pull-request text. A floor line does not authorize an attribute.

    Returns:
        True when ``new_lines`` is non-empty and the body has no general
        reason of at least 10 characters without angle brackets.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    if not new_lines:
        return False
    return not valid_reason(body)


def entries_new_in_head(base_lines: list[str], head_lines: list[str]) -> list[str]:
    """Return attribute lines that head has and base does not.

    Args:
        base_lines: Lines from the base assemblies. At most 100000.
        head_lines: Lines from the head assemblies. At most 100000.

    Returns:
        Head lines missing from base, in head order. A line that only exists
        on base is not returned, so removing an attribute stays empty.

    Raises:
        ValueError: Either list is longer than 100000 lines.

    Side effects:
        None.
    """
    if len(base_lines) > MAX_METADATA_LINES or len(head_lines) > MAX_METADATA_LINES:
        raise ValueError("metadata list is longer than 100000 lines.")
    base = set(base_lines)
    found: list[str] = []
    for line in head_lines:
        if line in base:
            continue
        found.append(line)
    return found


def read_metadata_lines(repo: Path, assemblies: list[Path]) -> list[str]:
    """Run the BB-09 tool and return the lines it prints.

    Args:
        repo: Repository root. The Release build of the tool must already exist.
        assemblies: One to eight assembly paths. Each must be a readable file.

    Returns:
        The tool's stdout lines. Empty when the assemblies have no tracked
        attributes. Each line has four tab-separated fields.

    Raises:
        ValueError: The tool is missing, an assembly cannot be read, the tool
            exits non-zero, or a printed line is not four fields. A missing
            assembly is a failure, not an empty result.

    Side effects:
        Reads the assemblies and starts ``dotnet exec``.
    """
    if not assemblies or len(assemblies) > MAX_ASSEMBLIES:
        raise ValueError("assembly list must contain 1 to 8 paths.")
    tool = repo / METADATA_TOOL
    if not tool.is_file():
        raise ValueError(f"coverage metadata tool is missing: {tool}")
    args = ["dotnet", "exec", str(tool)]
    for path in assemblies:
        if not path.is_file():
            raise ValueError(f"assembly missing or unreadable: {path}")
        try:
            with path.open("rb") as handle:
                if handle.read(1) == b"":
                    raise ValueError(f"assembly missing or unreadable: {path}")
        except OSError as exc:
            raise ValueError(f"assembly missing or unreadable: {path}") from exc
        args.append(str(path))
    completed = subprocess.run(args, check=False, capture_output=True, text=True)
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip() or "metadata tool failed"
        raise ValueError(detail)
    lines: list[str] = []
    for line in completed.stdout.splitlines():
        if not line:
            continue
        if len(line) > MAX_METADATA_LINE_CHARS or line.count("\t") != 3:
            raise ValueError("metadata tool printed a line that is not four fields.")
        lines.append(line)
        if len(lines) > MAX_METADATA_LINES:
            raise ValueError("metadata tool printed more than 100000 lines.")
    return lines


def head_assembly_paths(repo: Path) -> list[Path]:
    """Return the Release assemblies of the two shipped libraries.

    Args:
        repo: Repository root.

    Returns:
        ``ALog.dll`` then ``ALog.Extensions.Logging.dll`` under ``bin/Release``.
        The files are not opened here.

    Raises:
        None.

    Side effects:
        None.
    """
    return [repo / relative for relative in HEAD_ASSEMBLIES]


def base_assembly_paths(base_bin: Path) -> list[Path]:
    """Return the two library assemblies copied from a base build.

    Args:
        base_bin: Directory that holds ``ALog.dll`` and
            ``ALog.Extensions.Logging.dll``.

    Returns:
        Those two paths, in that order.

    Raises:
        None.

    Side effects:
        None.
    """
    return [base_bin / name for name in BASE_ASSEMBLY_NAMES]


def new_metadata_entries(repo: Path, base_bin: Path) -> list[str]:
    """Diff the tracked attributes of the two libraries, base against head.

    Args:
        repo: Repository root with a built tool and built head assemblies.
        base_bin: Directory with the base assemblies. A missing file fails.

    Returns:
        Attribute lines that are new in head.

    Raises:
        ValueError: An assembly or the tool cannot be read.

    Side effects:
        Reads four assemblies and runs the metadata tool twice.
    """
    base_lines = read_metadata_lines(repo, base_assembly_paths(base_bin))
    head_lines = read_metadata_lines(repo, head_assembly_paths(repo))
    return entries_new_in_head(base_lines, head_lines)


def format_failure(missing: list[str]) -> str:
    """Describe which exception lines are still missing.

    Args:
        missing: Trigger names from ``decide``. ``floor-lower`` is special.

    Returns:
        A multi-line message for stderr. Empty when nothing is missing.

    Raises:
        None.

    Side effects:
        None.
    """
    lines: list[str] = []
    if "floor-lower" in missing:
        lines.append(
            "Each lowered floor needs `coverage-exception: floor <package> "
            "<lines|branches> <old> -> <new>: <reason>`, and the drop must be "
            "at most 1 percentage point. A larger drop is accepted only when "
            "this diff removes CompilerGenerated from ExcludeByAttribute, each "
            "lowered metric's reason contains Lücken-Baseline, and the new floor "
            "is not below the measured coverage rounded down to a whole percent.\n"
        )
    if "exclude-attribute-added" in missing:
        lines.append(
            "coverage.runsettings added an ExcludeByAttribute entry. A new "
            "exclusion name is rejected even when the pull request has a "
            "coverage-exception line.\n"
        )
    others = [
        item for item in missing if item not in ("floor-lower", "exclude-attribute-added")
    ]
    if others:
        lines.append(
            "PR changes coverage policy (" + ", ".join(others) + ") without a "
            "`coverage-exception:` reason of at least 10 characters and no "
            "angle brackets.\n"
        )
    return "".join(lines)


def load_coverage_gate():
    """Load the coverage gate so measured percents use its rounding.

    Args:
        None.

    Returns:
        The loaded gate module. Package rates are one-decimal percents.

    Raises:
        ValueError: The gate script cannot be loaded.

    Side effects:
        Executes ``eng/coverage-gate.py`` once per call. The gate does not run.
    """
    import importlib.util

    path = Path(__file__).resolve().parent / "coverage-gate.py"
    spec = importlib.util.spec_from_file_location("coverage_gate_for_exception", path)
    if spec is None or spec.loader is None:
        raise ValueError(f"coverage gate script is missing: {path}")
    loaded = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = loaded
    if spec.loader is None:
        raise ValueError(f"coverage gate script is missing: {path}")
    spec.loader.exec_module(loaded)
    return loaded


def load_measured_coverage(repo: Path) -> dict[str, tuple[Decimal, Decimal]] | None:
    """Read package percents from Cobertura files under TestResults.

    Args:
        repo: Repository root. CI writes ``TestResults/**/coverage.cobertura.xml``
        before this check runs. A checkout without that directory has no
        measurement yet.

    Returns:
        Package name to ``(lines, branches)`` in percent, using the coverage
        gate's one-decimal rounding. ``None`` when TestResults is missing or
        empty. A large floor drop is then rejected.

    Raises:
        ValueError: A Cobertura file is present but cannot be read, or the gate
        script cannot be loaded.

    Side effects:
        Reads TestResults and loads the coverage gate module.
    """
    directory = repo / "TestResults"
    if not directory.is_dir():
        return None
    gate = load_coverage_gate()
    try:
        paths = gate.collect_cobertura([], [str(directory)])
    except ValueError as exc:
        if str(exc) == "No Cobertura files were found.":
            return None
        raise
    rates = gate.load_cobertura(paths)
    measured: dict[str, tuple[Decimal, Decimal]] = {}
    count = 0
    for name, rate in rates.items():
        count += 1
        if count > MAX_LISTED:
            raise ValueError("Cobertura lists more than 40 packages.")
        measured[str(name)] = (rate.lines, rate.branches)
    return measured


def parse_check_args(argv: list[str]) -> tuple[str, Path, str] | None:
    """Read ``--base``, ``--repo``, and ``--base-bin`` from the command line.

    Args:
        argv: Arguments after the program name.

    Returns:
        ``(base ref, repo, base bin)``. The base bin is empty when omitted.
        None when an argument is not recognized. The caller prints usage.

    Raises:
        None.

    Side effects:
        None.
    """
    base = ""
    repo = Path.cwd()
    base_bin = ""
    index = 0
    while index < len(argv) and index < 20:
        token = argv[index]
        if token == "--base" and index + 1 < len(argv):
            base = argv[index + 1]
            index += 2
            continue
        if token == "--repo" and index + 1 < len(argv):
            repo = Path(argv[index + 1])
            index += 2
            continue
        if token == "--base-bin" and index + 1 < len(argv):
            base_bin = argv[index + 1]
            index += 2
            continue
        return None
    if index != len(argv):
        return None
    return base, repo, base_bin


def main(argv: list[str]) -> int:
    """Diff the repo against a base ref and require a reason when needed.

    Args:
        argv: ``--base <ref>``, optional ``--repo <path>``, and ``--base-bin``
        pointing at the base Release assemblies. The body comes from the pulls
        API when ``GITHUB_TOKEN`` is set, otherwise ``PR_BODY``.

    Returns:
        ``0`` when no reason is required or one is present, ``1`` when a reason
        is missing or an assembly cannot be read, ``2`` when the arguments are wrong.

    Raises:
        None. Git, JSON, and metadata errors exit ``1``.

    Side effects:
        Reads git, the working tree, and four assemblies. May GET the
        pull-request body. Writes to stdout and stderr.
    """
    parsed = parse_check_args(argv)
    if parsed is None:
        sys.stderr.write(
            "usage: coverage-exception-check.py --base <ref> --base-bin <dir> [--repo <path>]\n"
        )
        return 2
    base, repo, base_bin = parsed
    if not base or not base_bin:
        sys.stderr.write("coverage-exception-check.py requires --base and --base-bin.\n")
        return 2
    try:
        body = load_body()
        missing = decide(repo, base, body, load_measured_coverage(repo))
        new_lines = new_metadata_entries(repo, Path(base_bin))
        if metadata_requires_reason(new_lines, body):
            missing.append("metadata-exclusion")
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, KeyError) as exc:
        sys.stderr.write(f"coverage exception check failed: {exc}\n")
        return 1
    if missing:
        sys.stderr.write(format_failure(missing))
        return 1
    sys.stdout.write("coverage exception check passed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
