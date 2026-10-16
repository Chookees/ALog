#!/usr/bin/env python3
"""Compare the Build-Basis analyzer ratchet with the base branch.

The block starts at the marker line
``# analyzer-ratchet (BB): bestehende Befunde, nur schrumpfen`` and continues
until the next editorconfig section. Each rule line is
``dotnet_diagnostic.<ID>.severity = suggestion # <count>``. A missing block on
the base is the first introduction and is allowed. After that, a new rule id
or a higher count fails the check. A lower count or a removed line is allowed,
because the ratchet only shrinks.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

MARKER = "# analyzer-ratchet (BB): bestehende Befunde, nur schrumpfen"
MAX_FILE_CHARS = 200_000
MAX_RULES = 200
MAX_COUNT = 1_000_000


def parse_ratchet(text: str) -> tuple[bool, dict[str, int], str]:
    """Read the ratchet block from one editorconfig file.

    Args:
        text: Full ``.editorconfig`` text. At most 200000 characters.

    Returns:
        ``(present, rules, error)``. ``present`` is false when the marker is
        absent and the text is otherwise ignored. ``rules`` maps a diagnostic
        id to its count. ``error`` is empty when the block is well formed.

    Raises:
        ValueError: The file is larger than 200000 characters.

    Side effects:
        None.
    """
    if len(text) > MAX_FILE_CHARS:
        raise ValueError("editorconfig is larger than 200000 characters.")
    lines = text.splitlines()
    marker_at = [index for index, line in enumerate(lines) if line == MARKER]
    if not marker_at:
        return False, {}, ""
    if len(marker_at) != 1:
        return True, {}, "analyzer ratchet marker appears more than once."
    rules: dict[str, int] = {}
    index = marker_at[0] + 1
    while index < len(lines) and index < marker_at[0] + MAX_RULES + 1:
        line = lines[index]
        if line.startswith("["):
            break
        if line.strip() == "":
            index += 1
            continue
        parsed = parse_rule_line(line)
        if parsed is None:
            return True, {}, f"analyzer ratchet line is not a counted suggestion: {line}"
        rule_id, count = parsed
        if rule_id in rules:
            return True, {}, f"analyzer ratchet repeats {rule_id}."
        rules[rule_id] = count
        if len(rules) > MAX_RULES:
            return True, {}, "analyzer ratchet lists more than 200 rules."
        index += 1
    return True, rules, ""


def parse_rule_line(line: str) -> tuple[str, int] | None:
    """Parse one ``dotnet_diagnostic`` suggestion line.

    Args:
        line: One physical line with no leading whitespace.

    Returns:
        ``(diagnostic id, count)`` when the line is
        ``dotnet_diagnostic.<ID>.severity = suggestion # <count>``.
        None when the shape does not match. The count has no leading zero.

    Raises:
        None.

    Side effects:
        None.
    """
    prefix = "dotnet_diagnostic."
    suffix = ".severity = suggestion # "
    if not line.startswith(prefix) or suffix not in line:
        return None
    rule_id, _, count_text = line.removeprefix(prefix).partition(suffix)
    if not rule_id.isalnum() or not count_text.isdigit():
        return None
    if len(count_text) > 1 and count_text.startswith("0"):
        return None
    count = int(count_text)
    if count < 1 or count > MAX_COUNT:
        return None
    return rule_id, count


def compare_ratchets(
    base_present: bool,
    base_rules: dict[str, int],
    head_present: bool,
    head_rules: dict[str, int],
    head_error: str,
) -> list[str]:
    """Return the ratchet failures of head against base.

    Args:
        base_present: True when the base file contains the marker.
        base_rules: Counts from the base block. Ignored when the marker is absent.
        head_present: True when the head file contains the marker.
        head_rules: Counts from the head block.
        head_error: Parse error for the head block. Empty when the block is valid.

    Returns:
        Failure messages. Empty when head may replace base. A missing base
        marker allows any well-formed head block. A higher count or a new id
        is a failure once the base marker exists.

    Raises:
        None.

    Side effects:
        None.
    """
    if head_error:
        return [head_error]
    if not head_present:
        return ["head .editorconfig has no analyzer-ratchet block."]
    if not base_present:
        return []
    failures: list[str] = []
    for rule_id, count in head_rules.items():
        if len(failures) >= MAX_RULES:
            break
        previous = base_rules.get(rule_id)
        if previous is None:
            failures.append(f"new ratchet rule {rule_id}.")
            continue
        if count > previous:
            failures.append(f"{rule_id} count {count} is higher than {previous}.")
    return failures


def show_base_editorconfig(repo: Path, base: str) -> str | None:
    """Read ``.editorconfig`` from a git revision.

    Args:
        repo: Repository root.
        base: Commit-ish such as ``origin/master``. It must already be fetched.

    Returns:
        The file text, or None when that revision has no ``.editorconfig``.

    Raises:
        ValueError: Git fails for a reason other than a missing file, or the
            output is larger than 200000 characters.

    Side effects:
        Reads the git object database.
    """
    completed = subprocess.run(
        ["git", "show", f"{base}:.editorconfig"],
        cwd=repo,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        if "exists on disk, but not in" in completed.stderr or "does not exist" in completed.stderr:
            return None
        if "fatal:" in completed.stderr and "Path" in completed.stderr:
            return None
        detail = completed.stderr.strip() or "git show failed"
        raise ValueError(detail)
    if len(completed.stdout) > MAX_FILE_CHARS:
        raise ValueError("base editorconfig is larger than 200000 characters.")
    return completed.stdout


def evaluate(repo: Path, base: str) -> list[str]:
    """Compare the working tree ratchet with one base revision.

    Args:
        repo: Repository root that contains ``.editorconfig``.
        base: Commit-ish already present in ``repo``.

    Returns:
        Failure messages. Empty when the ratchet holds.

    Raises:
        ValueError: The head file or the base revision cannot be read.

    Side effects:
        Reads ``.editorconfig`` and runs ``git show``.
    """
    head_path = repo / ".editorconfig"
    if not head_path.is_file():
        raise ValueError("head .editorconfig is missing.")
    head_text = head_path.read_text(encoding="utf-8")
    head_present, head_rules, head_error = parse_ratchet(head_text)
    base_text = show_base_editorconfig(repo, base)
    if base_text is None:
        base_present, base_rules, base_error = False, {}, ""
    else:
        base_present, base_rules, base_error = parse_ratchet(base_text)
    if base_error:
        raise ValueError(base_error)
    return compare_ratchets(base_present, base_rules, head_present, head_rules, head_error)


def main(argv: list[str]) -> int:
    """Check the working tree ratchet against ``--base``.

    Args:
        argv: ``--base <ref>`` and an optional ``--repo <path>``.

    Returns:
        ``0`` when the ratchet holds, ``1`` when it grew or a file is unreadable,
        ``2`` when the arguments are wrong.

    Raises:
        None. Failures are printed to stderr.

    Side effects:
        Reads git and ``.editorconfig``. Writes the result to stdout or stderr.
    """
    if len(argv) not in (2, 4) or argv[0] != "--base":
        sys.stderr.write("usage: analyzer-ratchet-check.py --base <ref> [--repo <path>]\n")
        return 2
    repo = Path.cwd()
    if len(argv) == 4:
        if argv[2] != "--repo":
            sys.stderr.write("usage: analyzer-ratchet-check.py --base <ref> [--repo <path>]\n")
            return 2
        repo = Path(argv[3])
    try:
        failures = evaluate(repo, argv[1])
    except (OSError, UnicodeError, ValueError) as exc:
        sys.stderr.write(f"analyzer ratchet check failed: {exc}\n")
        return 1
    if failures:
        sys.stderr.write("analyzer ratchet check failed:\n")
        for failure in failures:
            sys.stderr.write(f"- {failure}\n")
        return 1
    sys.stdout.write("analyzer ratchet check passed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
