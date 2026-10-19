#!/usr/bin/env python3
"""Run the Roslyn RS0030 exception check.

The rules live in eng/Rs0030Check. This file is the script-test entry.
Success prints rs0030-justification: ok.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path


def main() -> int:
    """Run the checker and return its exit code.

    Returns:
        The checker exit code. 0 when the tree is allowed.

    Raises:
        OSError: dotnet cannot be started.

    Side effects:
        Builds the checker when it is out of date. Writes its report.
        Extra arguments are forwarded after ``--``.
    """
    root = Path(__file__).resolve().parent.parent
    project = root / "eng" / "Rs0030Check" / "Rs0030Check.csproj"
    command = [
        "dotnet",
        "run",
        "--project",
        str(project),
        "-c",
        "Release",
        "--nologo",
        "-v",
        "q",
        "--",
    ]
    command.extend(sys.argv[1:])
    completed = subprocess.run(command, cwd=root, check=False)
    return completed.returncode


if __name__ == "__main__":
    raise SystemExit(main())
