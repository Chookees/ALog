#!/usr/bin/env python3
"""Prove the no-argument RS0030 check stays green on a second run.

The check builds the product projects itself. An up-to-date build used to skip
the compiler and leave the ErrorLog unwritten, so the second run was a false
red. This script runs that command twice on the unchanged tree, then adds one
banned call and expects exit 1. The added file is removed before return.

The double build is not a ``--cases`` row. That table runs inside every checker
invocation, including these runs.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


PROOF = "ALog/ClkRerunProof.cs"
PROOF_SOURCE = """namespace ALog.Core;

/// <summary>Temporary banned call. eng/test_rs0030_rerun.py deletes this file.</summary>
internal static class ClkRerunProof
{
    /// <summary>Reads the wall clock so RS0030 must report the call.</summary>
    /// <returns>The current year.</returns>
    internal static int Touch()
    {
        return DateTime.UtcNow.Year;
    }
}
"""
OK_LINE = "rs0030-justification: ok"
RED_LINE = (
    "RS0030 is not suppressed in source."
    " Remove the banned call and use the TimeProvider from the options;"
    " NoWarn, #pragma and .editorconfig are not allowed."
)


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


def run_check(repo: Path) -> subprocess.CompletedProcess[str]:
    """Run the checker with no arguments from the repository root.

    Args:
        repo: Repository root. The process working directory.

    Returns:
        The finished process. Output is captured.

    Side effects:
        Starts ``python3 eng/rs0030-justification-check.py``. That command may
        build both product projects.
    """
    return subprocess.run(
        [sys.executable, str(repo / "eng" / "rs0030-justification-check.py")],
        cwd=repo,
        text=True,
        capture_output=True,
        check=False,
    )


def show(completed: subprocess.CompletedProcess[str]) -> None:
    """Print one checker run when the proof fails.

    Args:
        completed: The finished checker process.

    Side effects:
        Writes the captured output to stdout and stderr.
    """
    print(completed.stdout, end="")
    print(completed.stderr, end="", file=sys.stderr)


def restore_products(repo: Path) -> bool:
    """Rebuild the product projects after the proof file is gone.

    The injected build fails and can remove the Release assembly. The rebuild
    puts that assembly back for the next reader.

    Args:
        repo: Repository root.

    Returns:
        True when both Release builds exit 0 and both assemblies exist.

    Side effects:
        Runs ``dotnet build --no-incremental`` for both product projects.
    """
    projects = (
        ("ALog/ALog.csproj", "ALog/bin/Release/net10.0/ALog.dll"),
        (
            "ALog.Extensions.Logging/ALog.Extensions.Logging.csproj",
            "ALog.Extensions.Logging/bin/Release/net10.0/ALog.Extensions.Logging.dll",
        ),
    )
    for project, assembly in projects:
        completed = subprocess.run(
            [
                "dotnet", "build", str(repo / project),
                "-c", "Release", "--nologo", "--no-incremental",
            ],
            cwd=repo,
            text=True,
            capture_output=True,
            check=False,
        )
        if completed.returncode != 0 or not (repo / assembly).is_file():
            print(completed.stdout, end="")
            print(completed.stderr, end="", file=sys.stderr)
            return False
    return True


def main() -> int:
    """Run two green checks, then one red check, then delete the proof file.

    Returns:
        0 when both clean runs exit 0, the injected call exits 1, and the
        product assemblies are rebuilt.
        1 when a run has the wrong exit code or the wrong summary, or the
        rebuild fails.
        2 when the proof file is already present.

    Side effects:
        Builds the product projects. Writes and deletes ``ALog/ClkRerunProof.cs``.
        Rebuilds both product projects after that file is gone.
    """
    repo = find_repo()
    proof = repo / PROOF
    if proof.exists():
        print("rs0030-rerun: " + PROOF + " already exists", file=sys.stderr)
        return 2
    failed = False
    try:
        for index in (1, 2):
            completed = run_check(repo)
            if completed.returncode != 0 or OK_LINE not in completed.stdout:
                show(completed)
                print("rs0030-rerun: clean run " + str(index) + " exited " + str(completed.returncode), file=sys.stderr)
                failed = True
                break
        if not failed:
            proof.write_text(PROOF_SOURCE, encoding="utf-8")
            red = run_check(repo)
            if red.returncode != 1 or RED_LINE not in red.stdout:
                show(red)
                print("rs0030-rerun: injected call exited " + str(red.returncode), file=sys.stderr)
                failed = True
    finally:
        if proof.exists():
            proof.unlink()
        if not restore_products(repo):
            print("rs0030-rerun: product rebuild failed", file=sys.stderr)
            failed = True
    if failed:
        return 1
    print("rs0030-rerun: ok")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
