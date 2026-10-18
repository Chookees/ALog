#!/usr/bin/env python3
"""Structural B1-01 check for the library workflow.

arc_spec v5.20 reads the badge job from parsed YAML. Counting the condition
text stays green when that condition moves onto a step, and it stays green
when an extra write scope is added beside ``contents: write``. This module
rejects those shapes. It also rejects a concurrency group: a cancelled
queued run would fail the workflow and the CI badge. The badge job instead
skips when its commit is no longer the head of master. Next file:
``eng/test_badge_workflow.py``.
"""

from __future__ import annotations

import sys
from pathlib import Path

import yaml

# Job-level expression required by arc_spec B1-01. A copy on a step does not count.
BADGE_IF = "github.event_name == 'push' && github.ref == 'refs/heads/master'"
_MAX_JOBS = 20
_MAX_STEPS = 40
_MAX_PERMISSIONS = 20
_MAX_PROBLEMS = 20
_MAX_WORKFLOW_CHARS = 200_000
_MAX_ERROR_CHARS = 200
_WRITE_VALUES = frozenset({"write", "write-all"})


def load_workflow(path: Path) -> dict:
    """Parse a workflow file into a mapping.

    Args:
        path: Path to ``.github/workflows/dotnet.yml`` or a mutation of it.
            The file must be UTF-8 and at most 200000 characters.

    Returns:
        The parsed document. ``on`` may appear as the boolean ``True`` because
        YAML 1.1 treats that word as a boolean. Callers inspect ``jobs`` and
        ``permissions``, not that key.

    Raises:
        ValueError: The path is not a file, the file is too large, the YAML
            cannot be parsed, or the document is not a mapping.
        OSError: The file cannot be read.

    Side effects:
        Reads the file.
    """
    if not path.is_file():
        raise ValueError(f"{path} is not a file")
    text = path.read_text(encoding="utf-8")
    if len(text) > _MAX_WORKFLOW_CHARS:
        raise ValueError("workflow is larger than 200000 characters")
    try:
        parsed = yaml.safe_load(text)
    except yaml.YAMLError as error:
        message = " ".join(str(error).split())
        if len(message) > _MAX_ERROR_CHARS:
            message = message[:_MAX_ERROR_CHARS]
        raise ValueError(f"workflow YAML cannot be parsed: {message}") from error
    if not isinstance(parsed, dict):
        raise ValueError("workflow is not a mapping")
    return parsed


def check_badge_workflow(document: object) -> list[str]:
    """Return the B1-01 problems in one parsed workflow.

    Args:
        document: Object returned by ``yaml.safe_load``. A non-mapping is a
            problem rather than an exception, so a mutation test can pass the
            value through.

    Returns:
        Problem sentences. Empty when the default permissions are exactly
        ``contents: read``, the badge job's ``if`` is the master push
        expression and is not repeated on a step, badge needs only build,
        badge permissions are exactly ``contents: write``, no other job
        grants a write scope, and the document has no concurrency group.
        At most 20 problems are returned.

    Raises:
        None.

    Side effects:
        None.
    """
    if not isinstance(document, dict):
        return ["workflow is not a mapping"]
    problems: list[str] = []
    if "concurrency" in document:
        problems.append("workflow declares a concurrency group")
    problems.extend(_default_permission_problems(document.get("permissions")))
    jobs = document.get("jobs")
    if not isinstance(jobs, dict) or len(jobs) == 0:
        problems.append("workflow has no jobs")
        return _cap(problems)
    if len(jobs) > _MAX_JOBS:
        problems.append("workflow has more than 20 jobs")
        return _cap(problems)
    badge = jobs.get("badge")
    if not isinstance(badge, dict):
        problems.append("badge job is missing")
        return _cap(problems)
    problems.extend(_badge_condition_problems(badge))
    problems.extend(_badge_needs_problems(badge.get("needs")))
    problems.extend(_badge_permission_problems(badge.get("permissions")))
    problems.extend(_other_job_write_problems(jobs))
    return _cap(problems)


def _default_permission_problems(permissions: object) -> list[str]:
    """Return a problem when the workflow default is not exactly contents read.

    Args:
        permissions: The top-level ``permissions`` value. ``None`` is rejected.
            An extra scope, including another read scope, is rejected.

    Returns:
        One problem, or an empty list when the value is exactly
        ``{"contents": "read"}``.

    Raises:
        None.

    Side effects:
        None.
    """
    if permissions != {"contents": "read"}:
        return ["workflow default permissions are not exactly contents: read"]
    return []


def _badge_permission_problems(permissions: object) -> list[str]:
    """Return a problem when badge permissions are not exactly contents write.

    Args:
        permissions: The badge job's ``permissions`` value. ``write-all`` and
            any extra scope are rejected.

    Returns:
        One problem, or an empty list when the value is exactly
        ``{"contents": "write"}``.

    Raises:
        None.

    Side effects:
        None.
    """
    if permissions != {"contents": "write"}:
        return ["badge permissions are not exactly contents: write"]
    return []


def _badge_needs_problems(needs: object) -> list[str]:
    """Return a problem when badge does not need only the build job.

    Args:
        needs: The badge job's ``needs`` value. The string ``build`` and the
            one-item list ``["build"]`` are accepted.

    Returns:
        One problem, or an empty list when build is the only dependency.

    Raises:
        None.

    Side effects:
        None.
    """
    if needs == "build" or needs == ["build"]:
        return []
    return ["badge does not need only build"]


def _badge_condition_problems(badge: dict) -> list[str]:
    """Return problems when the master condition is missing or sits on a step.

    Args:
        badge: The badge job mapping. It must contain ``steps``.

    Returns:
        Problems for a wrong job-level ``if``, a missing step list, a step
        list longer than 40, a step that is not a mapping, or any step that
        has its own ``if``.

    Raises:
        None.

    Side effects:
        None.
    """
    found: list[str] = []
    if badge.get("if") != BADGE_IF:
        found.append("badge job condition is not push to master")
    steps = badge.get("steps")
    if not isinstance(steps, list) or len(steps) == 0 or len(steps) > _MAX_STEPS:
        found.append("badge steps are missing or too long")
        return found
    for index, step in enumerate(steps):
        if index >= _MAX_STEPS:
            break
        if not isinstance(step, dict):
            found.append("badge step is not a mapping")
            break
        if "if" in step:
            found.append("badge condition is on a step")
            break
    return found


def _other_job_write_problems(jobs: dict) -> list[str]:
    """Return one problem for each non-badge job that grants a write scope.

    Args:
        jobs: The workflow ``jobs`` mapping. At most 20 entries are inspected.
            The badge job is skipped. A job with no ``permissions`` block
            inherits the read-only default and is allowed.

    Returns:
        Problem sentences. A job that is not a mapping is reported as well.

    Raises:
        None.

    Side effects:
        None.
    """
    found: list[str] = []
    seen = 0
    for name, body in jobs.items():
        seen += 1
        if seen > _MAX_JOBS:
            break
        if name == "badge":
            continue
        if not isinstance(body, dict) or _grants_write(body.get("permissions")):
            found.append(f"{name} has write permissions")
    return found


def _grants_write(permissions: object) -> bool:
    """Return whether a permissions value grants a write scope.

    Args:
        permissions: A job's ``permissions`` value. ``None`` means the job
            inherits the workflow default and does not itself grant write.
            A string ``write-all`` grants write. Any mapping value of
            ``write`` or ``write-all`` grants write. An unknown shape is
            treated as write so a new syntax cannot hide a scope.

    Returns:
        True when the value grants write or cannot be shown to be read-only.

    Raises:
        None.

    Side effects:
        None.
    """
    if permissions is None:
        return False
    if isinstance(permissions, str):
        return permissions.strip().lower() in _WRITE_VALUES
    if not isinstance(permissions, dict):
        return True
    seen = 0
    for key, value in permissions.items():
        seen += 1
        if seen > _MAX_PERMISSIONS:
            return True
        if not isinstance(key, str) or not isinstance(value, str):
            return True
        if value.strip().lower() in _WRITE_VALUES:
            return True
    return False


def _cap(problems: list[str]) -> list[str]:
    """Keep at most 20 problems.

    Args:
        problems: Problems collected so far.

    Returns:
        The same list when it is short enough, otherwise the first 20.

    Raises:
        None.

    Side effects:
        None.
    """
    if len(problems) > _MAX_PROBLEMS:
        return problems[:_MAX_PROBLEMS]
    return problems


def main(argv: list[str]) -> int:
    """Check one workflow file and print any B1-01 problems.

    Args:
        argv: Arguments without the program name. Exactly one path.

    Returns:
        0 when the workflow matches B1-01. 1 when it has problems. 2 when
        the file cannot be read or parsed, or the usage is wrong.

    Raises:
        None.

    Side effects:
        Reads the file. Writes problems to stderr.
    """
    if len(argv) != 1:
        print("usage: badge_workflow_check.py <workflow>", file=sys.stderr)
        return 2
    try:
        document = load_workflow(Path(argv[0]))
    except (OSError, ValueError) as error:
        print(f"badge workflow check failed: {error}", file=sys.stderr)
        return 2
    problems = check_badge_workflow(document)
    if problems:
        for problem in problems:
            print(problem, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
