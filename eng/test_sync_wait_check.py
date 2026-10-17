#!/usr/bin/env python3
"""Tests for the sync-wait allowlist gate.

A hit that is not listed fails. A listed line with no timeout fails.
A listed line that matches nothing fails. ``Task.Run`` in the file writer fails.
"""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPT = REPO / "eng" / "sync-wait-check.py"


def load_module():
    """Load the checker as a module.

    Args:
        None.

    Returns:
        The loaded module.

    Raises:
        SystemExit: The checker file is missing.

    Side effects:
        Inserts the module into ``sys.modules``.
    """
    if not SCRIPT.is_file():
        raise SystemExit(f"sync-wait checker is missing: {SCRIPT}")
    spec = importlib.util.spec_from_file_location("sync_wait_check", SCRIPT)
    if spec is None or spec.loader is None:
        raise SystemExit(f"sync-wait checker cannot be loaded: {SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


CHECK = load_module()


def write_tree(root: Path, allowlist: str, source: str, file_writer: str = "public void Write() { }\n") -> None:
    """Create the smallest tree the checker reads.

    Args:
        root: Temporary repository root.
        allowlist: Allowlist text.
        source: Contents of one library file that may contain a sync wait.
        file_writer: Contents of ``FileLogWriter.cs``.

    Returns:
        None.

    Raises:
        OSError: A file cannot be created.

    Side effects:
        Writes files under ``root``.
    """
    library = root / "ALog" / "Core"
    library.mkdir(parents=True)
    (library / "Sample.cs").write_text(source, encoding="utf-8")
    writer = root / "ALog" / "Writers" / "File"
    writer.mkdir(parents=True)
    (writer / "FileLogWriter.cs").write_text(file_writer, encoding="utf-8")
    eng = root / "eng"
    eng.mkdir()
    (eng / "sync-wait-allowlist.txt").write_text(allowlist, encoding="utf-8")


class SyncWaitCheckTests(unittest.TestCase):
    """Gate behavior on tiny trees."""

    def test_repo_passes(self) -> None:
        """The real repository matches its allowlist."""
        self.assertEqual(CHECK.evaluate(REPO), [])

    def test_unlisted_hit_fails(self) -> None:
        """A GetResult call outside the allowlist is a failure."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(root, "", "x.GetResult();\n")
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_declaration_allowlist_does_not_cover_a_call(self) -> None:
        """Listing the awaiter method does not permit a GetResult call."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "ALog/Core/Sample.cs:void GetResult\\( # Awaiter method returns immediately. Timeout: not applicable.\n",
                "public void GetResult() { }\nx.GetResult();\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("x.GetResult" in item for item in problems))
        self.assertFalse(any("void GetResult" in item for item in problems))

    def test_reason_without_timeout_fails(self) -> None:
        """An allowlist reason that does not name a timeout is a failure."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "ALog/Core/Sample.cs:GetResult\\( # no limit is named here at all\n",
                "x.GetResult();\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("Timeout:" in item for item in problems))

    def test_stale_entry_fails(self) -> None:
        """An allowlist line that matches no source line is a failure."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "ALog/Core/Missing.cs:pending\\.Wait\\( # Kept with no call site. Timeout: 5 seconds.\n",
                "var ok = 1;\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("matches nothing" in item for item in problems))

    def test_task_run_in_file_writer_fails(self) -> None:
        """Task.Run in the file writer is a failure even when no sync wait exists."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(root, "", "var ok = 1;\n", file_writer="await Task.Run(() => { });\n")
            problems = CHECK.evaluate(root)
        self.assertTrue(any("Task.Run" in item for item in problems))


if __name__ == "__main__":
    unittest.main()
