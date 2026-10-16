#!/usr/bin/env python3
"""Tests for the coverage-exception pull-request check.

Each policy trigger is run with and without a reason line. A pure floor raise
without a reason line stays green.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import importlib.util

spec = importlib.util.spec_from_file_location(
    "coverage_exception_check",
    Path(__file__).resolve().parent / "coverage-exception-check.py",
)
module = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
sys.modules[spec.name] = module
spec.loader.exec_module(module)

REASON = "coverage-exception: fixture reason for the coverage policy change.\n"
RATCHET = """{
  "linesTarget": 90,
  "branchesTarget": 80,
  "packages": {
    "ALog": { "lines": 50, "branches": 40 },
    "ALog.Extensions.Logging": { "lines": 50, "branches": 40 }
  }
}
"""
RAISED = RATCHET.replace('"lines": 50', '"lines": 51', 1)
LOWERED = RATCHET.replace('"lines": 50', '"lines": 49', 1)


def git(repo: Path, *args: str) -> str:
    """Run git in a fixture repo and return stdout.

    Args:
        repo: Fixture repository.
        args: Git arguments.

    Returns:
        Standard output.

    Raises:
        SystemExit: Git fails.

    Side effects:
        Writes the fixture repository when the git command does.
    """
    env = os.environ.copy()
    env["GIT_AUTHOR_NAME"] = "coverage-fixture"
    env["GIT_AUTHOR_EMAIL"] = "coverage-fixture@example.com"
    env["GIT_COMMITTER_NAME"] = "coverage-fixture"
    env["GIT_COMMITTER_EMAIL"] = "coverage-fixture@example.com"
    completed = subprocess.run(
        [
            "git",
            "-C",
            str(repo),
            "-c",
            "user.name=coverage-fixture",
            "-c",
            "user.email=coverage-fixture@example.com",
            *args,
        ],
        check=False,
        capture_output=True,
        text=True,
        env=env,
    )
    if completed.returncode != 0:
        raise SystemExit(completed.stderr.strip() or "git failed")
    return completed.stdout.strip()


def commit_tree(files: dict[str, str | None]) -> tuple[Path, str]:
    """Build a two-commit repo and return it plus the base commit.

    Args:
        files: Head-commit paths. ``None`` deletes a path that existed in the base.

    Returns:
        Repository path and the base commit SHA. The caller owns the temp dir
        only for the life of the process; the OS reclaims it later.

    Raises:
        SystemExit: Git cannot create the commits.

    Side effects:
        Writes a temporary git repository.
    """
    root = Path(tempfile.mkdtemp(prefix="coverage-exception-"))
    git(root, "init", "-b", "master")
    base_files = {
        "eng/coverage-ratchet.json": RATCHET,
        "ALog/Clean.cs": "namespace ALog;\npublic class Clean {}\n",
        "ALog.Extensions.Logging/Clean.cs": "namespace ALog.Extensions.Logging;\npublic class Clean {}\n",
    }
    for path, text in base_files.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    git(root, "add", ".")
    git(root, "commit", "-m", "base")
    base = git(root, "rev-parse", "HEAD")
    for path, text in files.items():
        target = root / path
        if text is None:
            if target.exists():
                target.unlink()
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-m", "change")
    return root, base


def expect(label: str, files: dict[str, str | None], body: str, blocked: bool) -> None:
    """Fail the process when a fixture does not match the expected gate.

    Args:
        label: Name printed on failure.
        files: Head commit contents.
        body: Pull-request body.
        blocked: True when the check must return trigger names.

    Returns:
        None.

    Raises:
        SystemExit: The fixture result is wrong.

    Side effects:
        Writes a temporary git repository.
    """
    repo, base = commit_tree(files)
    missing = module.decide(repo, base, body)
    if blocked and not missing:
        raise SystemExit(f"{label} was allowed without a reason")
    if not blocked and missing:
        raise SystemExit(f"{label} was blocked: {missing}")
    print(f"ok {label}")


def main() -> None:
    """Run the exception-check fixtures.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A fixture fails.

    Side effects:
        Writes temporary repositories and prints one line per fixture.
    """
    expect("pure raise without reason", {"eng/coverage-ratchet.json": RAISED}, "", False)
    expect("pure raise with reason", {"eng/coverage-ratchet.json": RAISED}, REASON, False)
    expect("lowered floor without reason", {"eng/coverage-ratchet.json": LOWERED}, "", True)
    expect("lowered floor with reason", {"eng/coverage-ratchet.json": LOWERED}, REASON, False)
    expect(
        "runsettings without reason",
        {"coverage.runsettings": "<RunSettings></RunSettings>\n"},
        "",
        True,
    )
    expect(
        "runsettings with reason",
        {"coverage.runsettings": "<RunSettings></RunSettings>\n"},
        REASON,
        False,
    )
    expect(
        "coverage config without reason",
        {"coverage.config": "{}\n"},
        "",
        True,
    )
    attribute = "namespace ALog;\n[ExcludeFromCodeCoverage]\npublic class Hidden {}\n"
    expect("attribute without reason", {"ALog/Hidden.cs": attribute}, "", True)
    expect("attribute with reason", {"ALog/Hidden.cs": attribute}, REASON, False)
    generated = "namespace ALog.Extensions.Logging;\n[GeneratedCode(\"t\",\"1\")]\npublic class Gen {}\n"
    expect(
        "generated attribute without reason",
        {"ALog.Extensions.Logging/Gen.cs": generated},
        "",
        True,
    )
    comment = "namespace ALog;\n// [ExcludeFromCodeCoverage] mentioned only\npublic class Note {}\n"
    expect("commented attribute", {"ALog/Note.cs": comment}, "", False)
    empty = "coverage-exception:\n"
    expect("empty reason", {"coverage.runsettings": "<RunSettings></RunSettings>\n"}, empty, True)
    print("coverage exception tests passed")


if __name__ == "__main__":
    main()
