#!/usr/bin/env python3
"""Require ``coverage-exception: <reason>`` when a PR changes coverage policy.

arc_spec 4.1 point 4 and S4-12. The pull-request body is read from the
environment variable ``PR_BODY`` (not the command line). Raising every ratchet
floor, without removing a package or changing the 90/80 targets, is not an
exception. Any other change to the ratchet, plus edits to ``coverage.runsettings``
or ``coverage.config``, plus a new ``ExcludeFromCodeCoverage`` or
``GeneratedCode`` attribute under the two libraries, needs the reason line.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from decimal import Decimal
from pathlib import Path

RATCHET = "eng/coverage-ratchet.json"
POLICY_FILES = (RATCHET, "coverage.runsettings", "coverage.config")
LIBRARY_ROOTS = ("ALog", "ALog.Extensions.Logging")
ATTRIBUTES = ("[ExcludeFromCodeCoverage", "[GeneratedCode")
MAX_DIFF_CHARS = 2_000_000
MAX_DIFF_LINES = 20_000


def reason_present(body: str) -> bool:
    """Return whether the body has a non-empty coverage-exception reason.

    Args:
        body: Pull-request text. An empty string has no reason.

    Returns:
        True when a line starts with ``coverage-exception:`` and has text after it.

    Raises:
        None.

    Side effects:
        None.
    """
    for line in body.splitlines():
        if not line.startswith("coverage-exception:"):
            continue
        reason = line.split(":", 1)[1].strip()
        if reason:
            return True
    return False


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


def changed_policy_paths(repo: Path, base: str) -> list[str]:
    """List policy and library paths that differ from the base ref.

    Args:
        repo: Repository root.
        base: Git ref such as ``origin/master``.

    Returns:
        Repo-relative paths, at most a few hundred.

    Raises:
        ValueError: Git cannot diff the ref.

    Side effects:
        Reads git.
    """
    text = git_output(repo, ["diff", "--name-only", base, "--", *POLICY_FILES, *LIBRARY_ROOTS])
    paths = [line.strip() for line in text.splitlines() if line.strip()]
    if len(paths) > 5000:
        raise ValueError("diff lists more than 5000 paths.")
    return paths


def attribute_was_added(diff_text: str) -> bool:
    """Return whether the diff adds an exclusion attribute.

    Args:
        diff_text: Unified diff. A huge diff is treated as an addition so the
        check fails closed.

    Returns:
        True when an added line contains an exclusion attribute and is not a comment.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(diff_text) > MAX_DIFF_CHARS:
        return True
    seen = 0
    for line in diff_text.splitlines():
        seen += 1
        if seen > MAX_DIFF_LINES:
            return True
        if not line.startswith("+") or line.startswith("+++"):
            continue
        code = line[1:].strip()
        if code.startswith("//") or code.startswith("*") or code.startswith("/*"):
            continue
        if any(attribute in code for attribute in ATTRIBUTES):
            return True
    return False


def ratchet_needs_reason(repo: Path, base: str) -> bool:
    """Return whether the ratchet diff is something other than a pure raise.

    Args:
        repo: Repository root.
        base: Git ref that may or may not contain the ratchet file.

    Returns:
        True when the file changed and the change is not only higher floors.

    Raises:
        ValueError: The candidate file is not a ratchet, or git fails.

    Side effects:
        Reads git and the working tree file.
    """
    candidate = load_ratchet_text((repo / RATCHET).read_text(encoding="utf-8"))
    show = subprocess.run(
        ["git", "-C", str(repo), "show", f"{base}:{RATCHET}"],
        check=False,
        capture_output=True,
        text=True,
    )
    if show.returncode != 0:
        return True
    baseline = load_ratchet_text(show.stdout)
    return not is_pure_raise(baseline, candidate)


def triggers(repo: Path, base: str) -> list[str]:
    """Collect the coverage-policy changes that need a reason.

    Args:
        repo: Repository root.
        base: Git ref to diff against.

    Returns:
        Short trigger names. Empty when the diff needs no exception line.

    Raises:
        ValueError: Git or the ratchet JSON cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    paths = changed_policy_paths(repo, base)
    found: list[str] = []
    if RATCHET in paths and ratchet_needs_reason(repo, base):
        found.append("coverage-ratchet")
    if any(path in paths for path in ("coverage.runsettings", "coverage.config")):
        found.append("coverage-settings")
    library_diff = git_output(repo, ["diff", "-U0", base, "--", *LIBRARY_ROOTS])
    if attribute_was_added(library_diff):
        found.append("exclusion-attribute")
    return found


def decide(repo: Path, base: str, body: str) -> list[str]:
    """Return trigger names that are missing a reason line.

    Args:
        repo: Repository root.
        base: Git ref to diff against.
        body: Pull-request body.

    Returns:
        Triggers that still need ``coverage-exception:``. Empty when the diff
        is clean, is a pure floor raise, or the body carries a reason.

    Raises:
        ValueError: Git or JSON cannot be read.

    Side effects:
        Reads git and the working tree.
    """
    found = triggers(repo, base)
    if not found or reason_present(body):
        return []
    return found


def main(argv: list[str]) -> int:
    """Diff the repo against a base ref and require a reason when needed.

    Args:
        argv: ``--base <ref>`` and optional ``--repo <path>``. The body comes
        from the ``PR_BODY`` environment variable.

    Returns:
        ``0`` when no reason is required or one is present, ``1`` when a reason
        is missing, ``2`` when the arguments are wrong.

    Raises:
        None. Git and JSON errors exit ``1``.

    Side effects:
        Reads git, the working tree, and ``PR_BODY``. Writes to stdout and stderr.
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
        missing = decide(repo, base, os.environ.get("PR_BODY", ""))
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, KeyError) as exc:
        sys.stderr.write(f"coverage exception check failed: {exc}\n")
        return 1
    if missing:
        sys.stderr.write(
            "PR changes coverage policy (" + ", ".join(missing) + ") "
            "without a line `coverage-exception: <reason>` in the pull request body.\n"
        )
        return 1
    sys.stdout.write("coverage exception check passed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
