#!/usr/bin/env python3
"""Fail when a pull request changes C# outside the BB-09 tool.

BB-06 allows ``*.cs`` only under ``eng/coverage-metadata/``. That project is
the metadata reader. Every other added, modified, deleted, or renamed ``*.cs``
path fails the check. The comparison is ``git diff --name-status`` against the
base revision, which CI sets to ``origin/master``.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ALLOWED_PREFIX = "eng/coverage-metadata/"
MAX_PATHS = 500
MAX_PATH_CHARS = 500


def disallowed_cs(records: list[tuple[str, str]]) -> list[str]:
    """Return C# paths that are outside the metadata tool.

    Args:
        records: ``(status, path)`` pairs from ``git diff --name-status``.
            A rename contributes the old path and the new path. At most 500.

    Returns:
        Disallowed paths, in input order. A path under
        ``eng/coverage-metadata/`` that does not contain ``..`` is allowed.

    Raises:
        ValueError: More than 500 paths were passed.

    Side effects:
        None.
    """
    if len(records) > MAX_PATHS:
        raise ValueError("diff lists more than 500 paths.")
    found: list[str] = []
    for status, path in records:
        if len(found) >= MAX_PATHS:
            break
        if not status or len(path) > MAX_PATH_CHARS:
            raise ValueError("diff path is empty or longer than 500 characters.")
        if not is_cs(path):
            continue
        if path.startswith(ALLOWED_PREFIX) and ".." not in path.split("/"):
            continue
        found.append(path)
    return found


def is_cs(path: str) -> bool:
    """Return whether a diff path is a C# file.

    Args:
        path: Relative path using forward slashes.

    Returns:
        True when the path ends in ``.cs`` and is not a directory placeholder.

    Raises:
        None.

    Side effects:
        None.
    """
    return path.endswith(".cs") and not path.endswith("/")


def read_name_status(repo: Path, base: str) -> list[tuple[str, str]]:
    """List changed paths between a base revision and the working tree.

    Args:
        repo: Repository root.
        base: Commit-ish such as ``origin/master``.

    Returns:
        Status and path pairs. Renames and copies produce two pairs so both
        names are checked.

    Raises:
        ValueError: Git fails or the diff is larger than 500 paths.

    Side effects:
        Runs ``git diff --name-status``.
    """
    completed = subprocess.run(
        ["git", "diff", "--name-status", "--find-renames", base],
        cwd=repo,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or "git diff failed"
        raise ValueError(detail)
    records: list[tuple[str, str]] = []
    for line in completed.stdout.splitlines():
        if len(records) > MAX_PATHS:
            raise ValueError("diff lists more than 500 paths.")
        if not line:
            continue
        parts = line.split("\t")
        status = parts[0]
        if status.startswith("R") or status.startswith("C"):
            if len(parts) != 3:
                raise ValueError(f"rename line is not three fields: {line}")
            records.append((status, parts[1].replace("\\", "/")))
            records.append((status, parts[2].replace("\\", "/")))
            continue
        if len(parts) != 2:
            raise ValueError(f"diff line is not two fields: {line}")
        records.append((status, parts[1].replace("\\", "/")))
    return records


def evaluate(repo: Path, base: str) -> list[str]:
    """Return disallowed C# paths against one base revision.

    Args:
        repo: Repository root.
        base: Commit-ish already fetched.

    Returns:
        Disallowed paths. Empty when BB-06 holds.

    Raises:
        ValueError: The diff cannot be read.

    Side effects:
        Runs git.
    """
    return disallowed_cs(read_name_status(repo, base))


def main(argv: list[str]) -> int:
    """Check ``*.cs`` paths in the diff against ``--base``.

    Args:
        argv: ``--base <ref>`` and an optional ``--repo <path>``.

    Returns:
        ``0`` when every changed C# file is under ``eng/coverage-metadata/``,
        ``1`` when a path is not or git fails, ``2`` when the arguments are wrong.

    Raises:
        None. Failures are printed to stderr.

    Side effects:
        Runs git. Writes the result to stdout or stderr.
    """
    if len(argv) not in (2, 4) or argv[0] != "--base":
        sys.stderr.write("usage: production-cs-diff-check.py --base <ref> [--repo <path>]\n")
        return 2
    repo = Path.cwd()
    if len(argv) == 4:
        if argv[2] != "--repo":
            sys.stderr.write("usage: production-cs-diff-check.py --base <ref> [--repo <path>]\n")
            return 2
        repo = Path(argv[3])
    try:
        found = evaluate(repo, argv[1])
    except (OSError, UnicodeError, ValueError) as exc:
        sys.stderr.write(f"production cs diff check failed: {exc}\n")
        return 1
    if found:
        sys.stderr.write("production cs diff check failed:\n")
        index = 0
        while index < len(found) and index < MAX_PATHS:
            sys.stderr.write(f"- {found[index]}\n")
            index += 1
        return 1
    sys.stdout.write("production cs diff check passed\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
