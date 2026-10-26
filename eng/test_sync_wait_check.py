#!/usr/bin/env python3
"""Tests for the sync-wait allowlist gate.

A hit that is not listed fails. A listed line with no timeout fails.
A listed line that matches nothing, or matches more than one hit, fails.
``Task.Run`` in the file writer fails. ``WaitHandle.WaitAny``, ``WaitAll``,
and ``WaitOne`` without a finite timeout fail the same way, including
``Timeout.Infinite``, a call split across lines, and ``SignalAndWait`` without
a timeout.
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
        self.assertTrue(any("exactly one hit (found 0)" in item for item in problems))

    def test_allowlist_entry_with_two_hits_fails(self) -> None:
        """An allowlist pattern that matches two call sites is a failure."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "ALog/Core/Sample.cs:GetResult\\(\\); // WaitWriterOnConsumerThread # Only one site. Timeout: the writer task itself.\n",
                "x.GetResult(); // WaitWriterOnConsumerThread\n"
                "y.GetResult(); // WaitWriterOnConsumerThread\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("exactly one hit (found 2)" in item for item in problems))
        self.assertTrue(any("ALog/Core/Sample.cs" in item for item in problems))

    def test_queue_entries_name_the_consumer_thread(self) -> None:
        """The consumer waits name that thread. Block and dispose waits name their own timeout."""
        text = (REPO / "eng" / "sync-wait-allowlist.txt").read_text(encoding="utf-8")
        self.assertNotIn("void GetResult", text)
        entries, problems = CHECK.parse_allowlist(text)
        self.assertEqual(problems, [])
        queue = [item for item in entries if item[0].endswith("BackgroundLogQueue.cs")]
        self.assertEqual(len(queue), 5)
        consumer = [reason for _path, _pattern, reason in queue if "consumer thread" in reason]
        self.assertEqual(len(consumer), 3)
        for _path, _pattern, reason in queue:
            self.assertIn("Timeout:", reason)
            self.assertGreaterEqual(len(reason), 20)

    def test_waitany_without_timeout_is_flagged(self) -> None:
        """WaitHandle.WaitAny with only the handles is a sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(new WaitHandle[] { left, right });\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_with_timeout_is_not_a_hit(self) -> None:
        """A WaitAny call that passes a timeout is outside this rule."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(new WaitHandle[] { left, right }, 1000);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitany_with_timeout_infinite_is_flagged(self) -> None:
        """WaitAny with Timeout.Infinite is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(new WaitHandle[] { left, right }, Timeout.Infinite);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_split_across_lines_is_flagged(self) -> None:
        """WaitHandle on one line and WaitAny on the next is still a sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle\n    .WaitAny(new WaitHandle[] { left, right });\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_signal_and_wait_without_timeout_is_flagged(self) -> None:
        """WaitHandle.SignalAndWait with only the two handles is a sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.SignalAndWait(toSignal, toWaitOn);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitone_named_milliseconds_timeout_infinite_is_flagged(self) -> None:
        """_evt.WaitOne(millisecondsTimeout: -1) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(millisecondsTimeout: -1);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitone_named_timeout_with_spaces_around_minus_is_flagged(self) -> None:
        """_evt.WaitOne(timeout: - 1) is still an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(timeout: - 1);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_named_timeout_infinite_is_flagged(self) -> None:
        """WaitAny with a named Timeout.Infinite timeout is unbounded."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(waitHandles: hs, millisecondsTimeout: Timeout.Infinite);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_fully_qualified_timeout_infinite_is_flagged(self) -> None:
        """System.Threading.Timeout.Infinite is still an unbounded timeout."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, System.Threading.Timeout.Infinite);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_timespan_from_milliseconds_minus_one_is_flagged(self) -> None:
        """TimeSpan.FromMilliseconds(-1) is an unbounded timeout."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, TimeSpan.FromMilliseconds(-1));\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_infinite_timespan_is_flagged(self) -> None:
        """Timeout.InfiniteTimeSpan is an unbounded timeout."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, Timeout.InfiniteTimeSpan);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitone_minus_one_with_exit_context_is_flagged(self) -> None:
        """_evt.WaitOne(-1, false) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(-1, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitone_parameterless_instance_is_flagged(self) -> None:
        """_evt.WaitOne() is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne();\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitany_infinite_with_exit_context_is_flagged(self) -> None:
        """WaitAny(hs, Timeout.Infinite, false) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, Timeout.Infinite, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitone_finite_with_exit_context_is_not_a_hit(self) -> None:
        """_evt.WaitOne(100, false) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(100, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitone_named_finite_milliseconds_is_not_a_hit(self) -> None:
        """_evt.WaitOne(millisecondsTimeout: 100) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(millisecondsTimeout: 100);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitone_instance_finite_milliseconds_is_not_a_hit(self) -> None:
        """_evt.WaitOne(1000) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "_evt.WaitOne(1000);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitany_timespan_from_milliseconds_finite_is_not_a_hit(self) -> None:
        """WaitAny(hs, TimeSpan.FromMilliseconds(100)) is bounded and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, TimeSpan.FromMilliseconds(100));\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitany_finite_with_exit_context_is_not_a_hit(self) -> None:
        """WaitAny(hs, 100, false) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAny(hs, 100, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_waitall_minus_one_with_exit_context_is_flagged(self) -> None:
        """WaitAll(hs, -1, true) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAll(hs, -1, true);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_waitall_finite_with_exit_context_is_not_a_hit(self) -> None:
        """WaitAll(hs, 100, true) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.WaitAll(hs, 100, true);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_signal_and_wait_minus_one_with_exit_context_is_flagged(self) -> None:
        """SignalAndWait(a, b, -1, false) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.SignalAndWait(a, b, -1, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_signal_and_wait_timeout_infinite_is_flagged(self) -> None:
        """SignalAndWait(a, b, Timeout.Infinite) is an unbounded sync wait."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.SignalAndWait(a, b, Timeout.Infinite);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertTrue(any("not allowlisted" in item for item in problems))

    def test_signal_and_wait_finite_with_exit_context_is_not_a_hit(self) -> None:
        """SignalAndWait(a, b, 100, false) is a bounded wait and must not be flagged."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(
                root,
                "",
                "WaitHandle.SignalAndWait(a, b, 100, false);\n",
            )
            problems = CHECK.evaluate(root)
        self.assertEqual(problems, [])

    def test_task_run_in_file_writer_fails(self) -> None:
        """Task.Run in the file writer is a failure even when no sync wait exists."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_tree(root, "", "var ok = 1;\n", file_writer="await Task.Run(() => { });\n")
            problems = CHECK.evaluate(root)
        self.assertTrue(any("Task.Run" in item for item in problems))


if __name__ == "__main__":
    unittest.main()
