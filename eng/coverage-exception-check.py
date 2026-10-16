#!/usr/bin/env python3
"""Require a coverage-exception line when a PR changes coverage policy.

Raising ratchet floors needs no reason. Each lowered metric needs its own
``coverage-exception: floor <package> <lines|branches> <old> -> <new>: <reason>``
line, and the drop may be at most one percentage point. Any other ratchet edit,
and any change to ``coverage.runsettings`` or ``coverage.config``, needs a
general reason of at least 10 characters with no ``<`` or ``>``. Added or
changed lines in ``*.cs``, ``*.csproj``, ``*.props``, ``*.targets``, and
``*.proj`` that contain the tokens ``ExcludeFromCodeCoverage``,
``GeneratedCode``, or ``CompilerGenerated`` need that general reason too,
including comments, aliases, and qualifiers. Before that search each added
line is normalized: C# ``\\uXXXX`` and ``\\UXXXXXXXX`` escapes are decoded,
then every Unicode format character (category Cf, which C# ignores inside
identifiers) is removed. MSBuild files (``.csproj``, ``.proj``, ``.props``,
``.targets``) also decode decimal ``&#NN;``, hex ``&#xHH;``, and the named
XML entities once, before the format characters are removed. An added MSBuild
line that contains ``<Using`` and either ``Alias=`` or ``$(`` is a hit even
when the token is not written out, because a Using alias or a property can
assemble the attribute name. In CI the body is read from the pulls API, not
from the event payload.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import unicodedata
import urllib.request
from decimal import Decimal
from pathlib import Path
from typing import Mapping

RATCHET = "eng/coverage-ratchet.json"
POLICY_FILES = ("coverage.runsettings", "coverage.config")
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


def parse_floor_lines(body: str) -> list[tuple[str, str, Decimal, Decimal]]:
    """Read floor lines whose reason is usable.

    Args:
        body: Pull-request text. HTML comments are removed first.

    Returns:
        ``(package, metric, old, new)`` for each matching line. At most 40.
        A short reason or a reason that contains ``<`` or ``>`` drops the line.

    Raises:
        ValueError: The body is larger than 1000000 characters.

    Side effects:
        None.
    """
    text = strip_html_comments(body)
    found: list[tuple[str, str, Decimal, Decimal]] = []
    seen = 0
    for line in text.splitlines():
        seen += 1
        if seen > 5000 or len(found) >= MAX_LISTED:
            break
        match = FLOOR_LINE.match(line.strip())
        if match is None or not usable_reason(match.group(5).strip()):
            continue
        found.append(
            (match.group(1), match.group(2), Decimal(match.group(3)), Decimal(match.group(4)))
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


def source_diff(repo: Path, base: str, paths: list[str]) -> str:
    """Return the unified diff of source files that can carry the tokens.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        paths: Changed paths. Only ``.cs``, ``.csproj``, ``.props``, ``.targets``,
        and ``.proj`` are kept. The MSBuild suffixes are the files whose XML
        references and ``<Using`` lines are interpreted.

    Returns:
        The diff text. Empty when none of those files changed. More than 500
        matching files returns a marker that the caller treats as a hit. The
        marker has no ``+++`` header, so it is scanned as a non-MSBuild line.

    Raises:
        ValueError: Git cannot diff the selected files.

    Side effects:
        Reads git.
    """
    selected: list[str] = []
    for path in paths:
        if not path.lower().endswith(SOURCE_SUFFIXES):
            continue
        selected.append(path)
        if len(selected) > MAX_SOURCE_FILES:
            return "+" + EXCLUSION_TOKENS[0]
    if not selected:
        return ""
    return git_output(repo, ["diff", "-U0", base, "--", *selected])


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
    show = subprocess.run(
        ["git", "-C", str(repo), "show", f"{base}:{RATCHET}"],
        check=False,
        capture_output=True,
        text=True,
    )
    if show.returncode != 0:
        return None
    return show.stdout


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
    lines: list[tuple[str, str, Decimal, Decimal]],
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


def unauthorized_drops(
    lowered: list[tuple[str, str, Decimal, Decimal]],
    body: str,
) -> list[str]:
    """Return drops that are larger than one point or lack a matching floor line.

    Args:
        lowered: Metrics that went down. Each entry is package, metric, old, new.
        body: Pull-request text.

    Returns:
        One problem per drop that this body does not authorize. Empty when
        every drop is at most one percentage point and named exactly.

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
        if old - new > MAX_FLOOR_DROP:
            problems.append(
                f"{package} {metric} drop {old} -> {new} exceeds 1 percentage point."
            )
            continue
        if not drop_is_listed(drop, lines):
            problems.append(
                f"{package} {metric} {old} -> {new} has no matching coverage-exception floor line."
            )
    return problems


def decide(repo: Path, base: str, body: str) -> list[str]:
    """Return policy changes that are still missing their exception line.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        body: Pull-request body.

    Returns:
        Trigger names. ``floor-lower`` means a drop is missing its floor line
        or is larger than one percentage point. A general reason does not
        clear ``floor-lower``. Empty when the diff is clean or authorized.

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
    missing: list[str] = []
    if found and not valid_reason(body):
        missing.extend(found)
    if unauthorized_drops(lowered, body):
        missing.append("floor-lower")
    return missing


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
            "at most 1 percentage point.\n"
        )
    others = [item for item in missing if item != "floor-lower"]
    if others:
        lines.append(
            "PR changes coverage policy (" + ", ".join(others) + ") without a "
            "`coverage-exception:` reason of at least 10 characters and no "
            "angle brackets.\n"
        )
    return "".join(lines)


def main(argv: list[str]) -> int:
    """Diff the repo against a base ref and require a reason when needed.

    Args:
        argv: ``--base <ref>`` and optional ``--repo <path>``. The body comes
        from the pulls API when ``GITHUB_TOKEN`` is set, otherwise ``PR_BODY``.

    Returns:
        ``0`` when no reason is required or one is present, ``1`` when a reason
        is missing, ``2`` when the arguments are wrong.

    Raises:
        None. Git and JSON errors exit ``1``.

    Side effects:
        Reads git and the working tree. May GET the pull-request body. Writes
        to stdout and stderr.
    """
    base = ""
    repo = Path.cwd()
    index = 0
    while index < len(argv):
        token = argv[index]
        if token == "--base" and index + 1 < len(argv):
            base = argv[index + 1]
            index += 2
            continue
        if token == "--repo" and index + 1 < len(argv):
            repo = Path(argv[index + 1])
            index += 2
            continue
        sys.stderr.write("usage: coverage-exception-check.py --base <ref> [--repo <path>]\n")
        return 2
    if not base:
        sys.stderr.write("coverage-exception-check.py requires --base.\n")
        return 2
    try:
        missing = decide(repo, base, load_body())
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
