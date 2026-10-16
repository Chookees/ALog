#!/usr/bin/env python3
"""Tests for the Build-Basis analyzer ratchet.

A missing base block may introduce the head block. Once the base has the
marker, a new rule or a higher count is red, and a lower count or a removed
rule is green.
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPT = REPO / "eng" / "analyzer-ratchet-check.py"
MARKER = "# analyzer-ratchet (BB): bestehende Befunde, nur schrumpfen"


def load_module():
    """Load the ratchet script as a module.

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
        raise SystemExit(f"analyzer ratchet script is missing: {SCRIPT}")
    spec = importlib.util.spec_from_file_location("analyzer_ratchet_check", SCRIPT)
    if spec is None or spec.loader is None:
        raise SystemExit("analyzer ratchet script could not be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def block(lines: list[str]) -> str:
    """Wrap rule lines in a minimal editorconfig.

    Args:
        lines: Rule lines. At most 20.

    Returns:
        File text containing the marker and those lines.

    Raises:
        SystemExit: More than 20 lines were passed.

    Side effects:
        None.
    """
    if len(lines) > 20:
        raise SystemExit("test block has more than 20 lines")
    body = "\n".join(lines)
    return f"root = true\n\n[*.cs]\n{MARKER}\n{body}\n\n[ALog.Tests/**.cs]\ndotnet_diagnostic.CA2007.severity = none\n"


def expect(label: str, failures: list[str], red: bool) -> None:
    """Require a comparison to be red or green.

    Args:
        label: Check name printed on success.
        failures: Messages returned by ``compare_ratchets``.
        red: True when at least one failure is required.

    Returns:
        None.

    Raises:
        SystemExit: The color does not match ``red``.

    Side effects:
        Prints one success line.
    """
    if red and not failures:
        raise SystemExit(f"{label} was green")
    if not red and failures:
        raise SystemExit(f"{label} was red: {failures}")
    print(f"ok {label}")


def main() -> None:
    """Run the ratchet comparisons and one git-backed command.

    Args:
        None.

    Returns:
        None.

    Raises:
        SystemExit: A comparison does not match BB-03.

    Side effects:
        Creates a temporary git repository. Prints one line per check.
    """
    module = load_module()
    base_text = block(["dotnet_diagnostic.CA2007.severity = suggestion # 4"])
    base_present, base_rules, base_error = module.parse_ratchet(base_text)
    if base_error or not base_present or base_rules.get("CA2007") != 4:
        raise SystemExit(f"base block did not parse: {base_error} {base_rules}")
    cases = (
        ("higher count", block(["dotnet_diagnostic.CA2007.severity = suggestion # 5"]), True),
        ("new rule", block([
            "dotnet_diagnostic.CA2007.severity = suggestion # 4",
            "dotnet_diagnostic.IDE0130.severity = suggestion # 1",
        ]), True),
        ("lower count", block(["dotnet_diagnostic.CA2007.severity = suggestion # 3"]), False),
        ("removed rule", block([]), False),
    )
    index = 0
    while index < len(cases) and index < 10:
        label, head_text, red = cases[index]
        head_present, head_rules, head_error = module.parse_ratchet(head_text)
        failures = module.compare_ratchets(base_present, base_rules, head_present, head_rules, head_error)
        expect(label, failures, red)
        index += 1
    missing_head = module.compare_ratchets(True, base_rules, False, {}, "")
    expect("missing head block", missing_head, True)
    introduced, introduced_rules, introduced_error = module.parse_ratchet(base_text)
    intro_failures = module.compare_ratchets(False, {}, introduced, introduced_rules, introduced_error)
    expect("first introduction", intro_failures, False)
    junk_present, _, junk_error = module.parse_ratchet(f"[*.cs]\n{MARKER}\nnot a rule\n")
    if not junk_present or not junk_error:
        raise SystemExit("junk ratchet line was accepted")
    print("ok junk line rejected")
    run_git_case(module)
    print("analyzer ratchet tests passed")


def run_git_case(module) -> None:
    """Prove the command fails when a committed parent count is raised.

    Args:
        module: Loaded ratchet module. ``main`` is invoked, not a private helper.

    Returns:
        None.

    Raises:
        SystemExit: The command does not fail on the raised count, or the
            parent comparison is not green.

    Side effects:
        Writes a temporary git repository.
    """
    work = Path(tempfile.mkdtemp(prefix="ratchet-git-"))
    run(["git", "init", "-q"], work)
    run(["git", "config", "user.email", "ratchet@example.com"], work)
    run(["git", "config", "user.name", "ratchet"], work)
    editor = work / ".editorconfig"
    editor.write_text(block(["dotnet_diagnostic.CA2007.severity = suggestion # 2"]), encoding="utf-8")
    run(["git", "add", ".editorconfig"], work)
    run(["git", "commit", "-q", "-m", "base"], work)
    parent = run(["git", "rev-parse", "HEAD"], work).stdout.strip()
    if module.main(["--base", parent, "--repo", str(work)]) != 0:
        raise SystemExit("unchanged ratchet was red against its parent")
    editor.write_text(block(["dotnet_diagnostic.CA2007.severity = suggestion # 9"]), encoding="utf-8")
    status = module.main(["--base", parent, "--repo", str(work)])
    if status != 1:
        raise SystemExit(f"raised ratchet count returned {status}")
    print("ok git parent rejects a higher count")


def run(args: list[str], cwd: Path) -> subprocess.CompletedProcess[str]:
    """Run a git command and require status 0.

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
