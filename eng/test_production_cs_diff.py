#!/usr/bin/env python3
"""Tests for the BB-06 C# path check.

A product ``*.cs`` path is red. A file under ``eng/coverage-metadata/`` is
green. Deleting a product file is red as well.
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPT = REPO / "eng" / "production-cs-diff-check.py"


def load_module():
    """Load the diff script as a module.

    Args:
        None.

    Returns:
        The loaded module.

    Raises:
        SystemExit: The script file is missing.

    Side effects:
        Inserts the module into ``sys.modules``.
    """
    if not SCRIPT.is_file():
        raise SystemExit(f"production cs diff script is missing: {SCRIPT}")
    spec = importlib.util.spec_from_file_location("production_cs_diff_check", SCRIPT)
    if spec is None or spec.loader is None:
        raise SystemExit("production cs diff script could not be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def expect(label: str, found: list[str], red: bool) -> None:
    """Require a path list to be red or green.

    Args:
        label: Check name.
        found: Disallowed paths.
        red: True when at least one path is required.

    Returns:
        None.

    Raises:
        SystemExit: The color does not match ``red``.

    Side effects:
        Prints one success line.
    """
    if red and not found:
        raise SystemExit(f"{label} was green")
    if not red and found:
        raise SystemExit(f"{label} was red: {found}")
    print(f"ok {label}")


def main() -> None:
    """Check path classification and one git diff.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A case does not match BB-06.

    Side effects:
        Writes a temporary git repository. Prints one line per check.
    """
    module = load_module()
    product = module.disallowed_cs([("M", "ALog/Logger.cs")])
    expect("product edit", product, True)
    tool = module.disallowed_cs([("A", "eng/coverage-metadata/Program.cs")])
    expect("metadata tool", tool, False)
    deleted = module.disallowed_cs([("D", "ALog/Gone.cs")])
    expect("product delete", deleted, True)
    other = module.disallowed_cs([("M", "eng/consumer-smoke/console/Program.cs")])
    expect("other eng cs", other, True)
    prose = module.disallowed_cs([("M", "README.md")])
    expect("non cs", prose, False)
    run_git_case(module)
    print("production cs diff tests passed")


def run_git_case(module) -> None:
    """Prove the command sees a product file added after the parent commit.

    Args:
        module: Loaded diff module.

    Returns:
        None.

    Raises:
        SystemExit: The product file is allowed, or the tool file is rejected.

    Side effects:
        Writes a temporary git repository.
    """
    work = Path(tempfile.mkdtemp(prefix="cs-diff-"))
    run(["git", "init", "-q"], work)
    run(["git", "config", "user.email", "diff@example.com"], work)
    run(["git", "config", "user.name", "diff"], work)
    (work / "README.md").write_text("base\n", encoding="utf-8")
    run(["git", "add", "README.md"], work)
    run(["git", "commit", "-q", "-m", "base"], work)
    parent = run(["git", "rev-parse", "HEAD"], work).stdout.strip()
    tool_dir = work / "eng" / "coverage-metadata"
    tool_dir.mkdir(parents=True)
    (tool_dir / "Program.cs").write_text("class Tool {}\n", encoding="utf-8")
    run(["git", "add", "eng/coverage-metadata/Program.cs"], work)
    if module.main(["--base", parent, "--repo", str(work)]) != 0:
        raise SystemExit("metadata tool file was rejected")
    (work / "ALog").mkdir()
    (work / "ALog" / "New.cs").write_text("class New {}\n", encoding="utf-8")
    run(["git", "add", "ALog/New.cs"], work)
    status = module.main(["--base", parent, "--repo", str(work)])
    if status != 1:
        raise SystemExit(f"product file returned {status}")
    print("ok git diff rejects a product file")


def run(args: list[str], cwd: Path) -> subprocess.CompletedProcess[str]:
    """Run a command and require status 0.

    Args:
        args: Program and arguments. At most 12.
        cwd: Working directory.

    Returns:
        The completed process.

    Raises:
        SystemExit: The command fails or more than 12 arguments were passed.

    Side effects:
        Starts a process.
    """
    if len(args) > 12:
        raise SystemExit("refusing to run more than 12 arguments")
    completed = subprocess.run(args, cwd=cwd, check=False, capture_output=True, text=True)
    if completed.returncode != 0:
        raise SystemExit(completed.stderr or completed.stdout)
    return completed


if __name__ == "__main__":
    main()
