#!/usr/bin/env python3
"""Unit tests for the B1 coverage-badge generator.

The colour tests lock the floors from uiux_spec §4b: green at the package
floor, yellow down to 10 points below that floor, and red under that.
ALog's floor is 95. ALog.Extensions.Logging's floor is 90. The same
measurement the coverage gate writes is rendered, not a second test run.
Next file: ``eng/coverage-badge.py``.
"""

from __future__ import annotations

import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import redirect_stderr, redirect_stdout
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DATA = ROOT / "testdata"
GREEN = "#4c1"
YELLOW = "#dfb317"
RED = "#e05d44"
SVG_NAMES = (
    "coverage-ALog.svg",
    "coverage-ALog.Extensions.Logging.svg",
)


def load_module(filename: str, module_name: str):
    """Load one eng script by file name.

    Args:
        filename: File in the eng directory, such as ``coverage-badge.py``.
        module_name: Name to register in ``sys.modules``.

    Returns:
        The loaded module.

    Raises:
        RuntimeError: The file is missing or cannot be executed.

    Side effects:
        Executes the module and stores it in ``sys.modules``.
    """
    path = ROOT / filename
    if not path.is_file():
        raise RuntimeError(f"{filename} is missing")
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"{filename} cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        del sys.modules[module_name]
        raise
    return module


class CoverageBadgeTests(unittest.TestCase):
    """Colour thresholds, SVG text, and the badge-branch push guard."""

    def setUp(self) -> None:
        """Load the generator and the publisher.

        Side effects:
            Executes both scripts once per test.
        """
        self.badge = load_module("coverage-badge.py", "coverage_badge")
        self.publish = load_module("publish-coverage-badges.py", "publish_coverage_badges")

    def test_exactly_at_the_alog_floor_is_green(self) -> None:
        """95.0 against ALog's floor of 95 is green and the SVG says 95.0%.

        The floor is inclusive. A tenth below is a different test.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("95.0"), Decimal("95")), "green")
        svg = self.badge.render_coverage_svg(Decimal("95.0"), Decimal("95"))
        self.assertIn("95.0%", svg)
        self.assertIn(f'fill="{GREEN}"', svg)
        self.assertNotIn(YELLOW, svg)
        self.assertNotIn(RED, svg)

    def test_just_below_the_alog_floor_is_yellow(self) -> None:
        """94.9 against a floor of 95 is yellow, not green.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("94.9"), Decimal("95")), "yellow")
        svg = self.badge.render_coverage_svg(Decimal("94.9"), Decimal("95"))
        self.assertIn("94.9%", svg)
        self.assertIn(f'fill="{YELLOW}"', svg)
        self.assertNotIn(GREEN, svg)
        self.assertNotIn(RED, svg)

    def test_exactly_ten_below_the_alog_floor_is_yellow(self) -> None:
        """85.0 is the last yellow value under ALog's floor of 95.

        Yellow reaches down to 10 points below the floor, and that edge is included.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("85.0"), Decimal("95")), "yellow")
        svg = self.badge.render_coverage_svg(Decimal("85.0"), Decimal("95"))
        self.assertIn("85.0%", svg)
        self.assertIn(f'fill="{YELLOW}"', svg)
        self.assertNotIn(RED, svg)

    def test_just_below_ten_under_the_alog_floor_is_red(self) -> None:
        """84.9 is below the yellow band under ALog's floor of 95.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("84.9"), Decimal("95")), "red")
        svg = self.badge.render_coverage_svg(Decimal("84.9"), Decimal("95"))
        self.assertIn("84.9%", svg)
        self.assertIn(f'fill="{RED}"', svg)
        self.assertNotIn(GREEN, svg)
        self.assertNotIn(YELLOW, svg)

    def test_exactly_at_the_extensions_floor_is_green(self) -> None:
        """90.0 against the Extensions floor of 90 is green and the SVG says 90.0%.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("90.0"), Decimal("90")), "green")
        svg = self.badge.render_coverage_svg(Decimal("90.0"), Decimal("90"))
        self.assertIn("90.0%", svg)
        self.assertIn(f'fill="{GREEN}"', svg)
        self.assertNotIn(YELLOW, svg)
        self.assertNotIn(RED, svg)

    def test_just_below_the_extensions_floor_is_yellow(self) -> None:
        """89.9 against the Extensions floor of 90 is yellow.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("89.9"), Decimal("90")), "yellow")
        svg = self.badge.render_coverage_svg(Decimal("89.9"), Decimal("90"))
        self.assertIn("89.9%", svg)
        self.assertIn(f'fill="{YELLOW}"', svg)
        self.assertNotIn(GREEN, svg)

    def test_exactly_ten_below_the_extensions_floor_is_yellow(self) -> None:
        """80.0 is the last yellow value under the Extensions floor of 90.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("80.0"), Decimal("90")), "yellow")
        svg = self.badge.render_coverage_svg(Decimal("80.0"), Decimal("90"))
        self.assertIn("80.0%", svg)
        self.assertIn(f'fill="{YELLOW}"', svg)
        self.assertNotIn(RED, svg)

    def test_just_below_ten_under_the_extensions_floor_is_red(self) -> None:
        """79.9 is below the yellow band under the Extensions floor of 90.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("79.9"), Decimal("90")), "red")
        svg = self.badge.render_coverage_svg(Decimal("79.9"), Decimal("90"))
        self.assertIn("79.9%", svg)
        self.assertIn(f'fill="{RED}"', svg)
        self.assertNotIn(YELLOW, svg)

    def test_zero_and_full_coverage_stay_inside_the_scale(self) -> None:
        """0.0 is red and 100.0 is green against a floor of 95.

        Values outside 0..100 are rejected. The SVG text uses one decimal.

        Side effects:
            None.
        """
        self.assertEqual(self.badge.colour_for_lines(Decimal("0"), Decimal("95")), "red")
        self.assertIn("0.0%", self.badge.render_coverage_svg(Decimal("0"), Decimal("95")))
        self.assertEqual(self.badge.colour_for_lines(Decimal("100"), Decimal("95")), "green")
        self.assertIn("100.0%", self.badge.render_coverage_svg(Decimal("100"), Decimal("95")))
        with self.assertRaises(ValueError):
            self.badge.colour_for_lines(Decimal("-0.1"), Decimal("95"))
        with self.assertRaises(ValueError):
            self.badge.colour_for_lines(Decimal("100.1"), Decimal("90"))

    def test_colour_uses_the_floor_in_the_measurement(self) -> None:
        """The JSON floor, not a hard-coded 95, selects the colour.

        60.2 on a floor of 60.2 is green. 50.2 on a floor of 60.2 is exactly
        10 below and stays yellow. Each package is written to its own file.

        Side effects:
            None.
        """
        document = json.dumps(
            {
                "packages": {
                    "ALog": {"lines": "60.2", "lineFloor": "60.2"},
                    "ALog.Extensions.Logging": {"lines": "50.2", "lineFloor": "60.2"},
                }
            }
        )
        files = self.badge.render_package_files(self.badge.load_measurement(document))
        self.assertEqual(set(files), set(SVG_NAMES))
        alog = files["coverage-ALog.svg"]
        extensions = files["coverage-ALog.Extensions.Logging.svg"]
        self.assertIn("60.2%", alog)
        self.assertIn(f'fill="{GREEN}"', alog)
        self.assertIn("50.2%", extensions)
        self.assertIn(f'fill="{YELLOW}"', extensions)

    def test_svg_text_comes_from_the_gate_measurement(self) -> None:
        """The SVG repeats the line percent the coverage gate would compare.

        The Cobertura fixture is the gate's own input. No test run is started.

        Side effects:
            Reads the Cobertura fixture and the ratchet file.
        """
        gate = load_module("coverage-gate.py", "coverage_gate_for_badge")
        coverage = gate.load_cobertura([DATA / "cobertura-assembly-name.xml"])
        ratchet = gate.load_ratchet(ROOT / "coverage-ratchet.json")
        document = gate.line_measurement_document(coverage, ratchet)
        files = self.badge.render_package_files(self.badge.load_measurement(document))
        self.assertIn("60.2%", files["coverage-ALog.svg"])
        self.assertIn("73.7%", files["coverage-ALog.Extensions.Logging.svg"])
        self.assertIn('"lines": "60.2"', document)
        self.assertIn('"lineFloor": "95.0"', document)
        self.assertIn('"lineFloor": "90.0"', document)

    def test_measurement_missing_a_package_is_rejected(self) -> None:
        """A document with only ALog cannot paint the Extensions badge.

        Side effects:
            None.
        """
        document = '{"packages": {"ALog": {"lines": "95.0", "lineFloor": "95.0"}}}'
        with self.assertRaises(ValueError):
            self.badge.load_measurement(document)

    def test_push_command_updates_badges_without_force(self) -> None:
        """The only push refspec is badges, with no force flag and no master.

        Side effects:
            None.
        """
        command = self.publish.push_command()
        self.assertEqual(
            command,
            ["git", "push", "origin", "refs/heads/badges:refs/heads/badges"],
        )
        self.assertNotIn("--force", command)
        self.assertNotIn("-f", command)
        self.assertNotIn("master", command)

    def test_unchanged_svgs_do_not_commit_and_master_is_not_moved(self) -> None:
        """The same SVG bytes produce no second commit, and master stays put.

        A changed byte adds one commit on the orphan badges branch. That
        branch does not share history with master, and nothing is pushed.

        Side effects:
            Creates a temporary git repository.
        """
        first = {
            SVG_NAMES[0]: b"<svg>one</svg>\n",
            SVG_NAMES[1]: b"<svg>two</svg>\n",
        }
        changed = {
            SVG_NAMES[0]: b"<svg>one</svg>\n",
            SVG_NAMES[1]: b"<svg>changed</svg>\n",
        }
        with tempfile.TemporaryDirectory() as raw:
            repo = Path(raw)
            self._git(repo, ["init", "-b", "master"])
            (repo / "README.md").write_text("master\n", encoding="utf-8")
            self._git(repo, ["add", "README.md"])
            self._commit(repo, "init")
            master = self._git(repo, ["rev-parse", "HEAD"]).stdout.strip()
            outcome = self.publish.sync_badges(repo, first, fetch=False, push=False)
            self.assertEqual(outcome, "committed")
            self.assertEqual(self._git(repo, ["rev-parse", "--abbrev-ref", "HEAD"]).stdout.strip(), "badges")
            names = self._git(repo, ["ls-tree", "-r", "--name-only", "HEAD"]).stdout.split()
            self.assertEqual(set(names), set(SVG_NAMES))
            self.assertEqual(self._git(repo, ["rev-parse", "master"]).stdout.strip(), master)
            self.assertEqual(self._git(repo, ["rev-list", "--count", "master"]).stdout.strip(), "1")
            again = self.publish.sync_badges(repo, first, fetch=False, push=False)
            self.assertEqual(again, "unchanged")
            self.assertEqual(self._git(repo, ["rev-list", "--count", "badges"]).stdout.strip(), "1")
            updated = self.publish.sync_badges(repo, changed, fetch=False, push=False)
            self.assertEqual(updated, "committed")
            self.assertEqual(self._git(repo, ["rev-list", "--count", "badges"]).stdout.strip(), "2")
            self.assertEqual(self._git(repo, ["rev-parse", "master"]).stdout.strip(), master)
            shared = self._git(repo, ["merge-base", "master", "badges"], check=False)
            self.assertNotEqual(shared.returncode, 0)
            self.assertEqual(self._git(repo, ["remote"]).stdout.strip(), "")

    def test_local_origin_push_is_idempotent_and_leaves_master(self) -> None:
        """A real push updates only badges, and the same bytes do not push again.

        Origin is a local bare repository. The second call must not add a
        commit. Master on origin stays at the initial commit.

        Side effects:
            Creates two temporary repositories and pushes badges between them.
        """
        svgs = {
            SVG_NAMES[0]: b"<svg>one</svg>\n",
            SVG_NAMES[1]: b"<svg>two</svg>\n",
        }
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            self._git(root, ["init", "--bare", "-b", "master", "origin.git"])
            self._git(root, ["init", "-b", "master", "work"])
            work = root / "work"
            origin = root / "origin.git"
            (work / "README.md").write_text("master\n", encoding="utf-8")
            self._git(work, ["add", "README.md"])
            self._commit(work, "init")
            self._git(work, ["remote", "add", "origin", str(origin)])
            self._git(work, ["push", "origin", "HEAD:refs/heads/master"])
            master = self._git(origin, ["rev-parse", "refs/heads/master"]).stdout.strip()
            first = self.publish.sync_badges(work, svgs, fetch=True, push=True)
            self.assertEqual(first, "committed")
            self.assertEqual(self._git(origin, ["rev-parse", "refs/heads/master"]).stdout.strip(), master)
            names = self._git(origin, ["ls-tree", "-r", "--name-only", "refs/heads/badges"]).stdout.split()
            self.assertEqual(set(names), set(SVG_NAMES))
            self.assertEqual(self._git(origin, ["rev-list", "--count", "refs/heads/badges"]).stdout.strip(), "1")
            second = self.publish.sync_badges(work, svgs, fetch=True, push=True)
            self.assertEqual(second, "unchanged")
            self.assertEqual(self._git(origin, ["rev-list", "--count", "refs/heads/badges"]).stdout.strip(), "1")
            self.assertEqual(self._git(origin, ["rev-parse", "refs/heads/master"]).stdout.strip(), master)
            shared = self._git(work, ["merge-base", "master", "badges"], check=False)
            self.assertNotEqual(shared.returncode, 0)

    def test_missing_output_parents_are_created(self) -> None:
        """The SVG directory is created even when its parents do not exist.

        Side effects:
            Writes two SVG files under a temporary directory.
        """
        document = json.dumps(
            {
                "packages": {
                    "ALog": {"lines": "95.0", "lineFloor": "95.0"},
                    "ALog.Extensions.Logging": {"lines": "90.0", "lineFloor": "90.0"},
                }
            }
        )
        with tempfile.TemporaryDirectory() as raw:
            target = Path(raw) / "missing" / "badges"
            names = self.badge.write_package_files(document, target)
            self.assertEqual(names, list(SVG_NAMES))
            self.assertTrue((target / SVG_NAMES[0]).is_file())
            self.assertTrue((target / SVG_NAMES[1]).is_file())

    def test_stale_master_keeps_the_previous_badge(self) -> None:
        """An older commit does not replace badges written for a newer one.

        The newest commit is pushed and then not published, which is the
        failed-build case: the previous SVG stays. A later publish of that
        newest commit replaces it, and the older commit still cannot.

        Side effects:
            Creates a temporary bare origin and pushes badges to it.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, first, second = self._two_master_commits(root)
            code, out, err = self._publish(work, first, "value-a")
            self.assertEqual(code, 0, err)
            self.assertEqual(out.strip(), "committed")
            self._git(work, ["push", "origin", f"{second}:refs/heads/master"])
            code, out, err = self._publish(work, first, "value-a-late")
            self.assertEqual(code, 0, err)
            self.assertIn("::notice::", out)
            self.assertNotIn("::warning::", out)
            self.assertEqual(self._badge_text(origin), "<svg>value-a</svg>")
            code, out, err = self._publish(work, second, "value-b")
            self.assertEqual(code, 0, err)
            self.assertEqual(out.strip(), "committed")
            self.assertEqual(self._badge_text(origin), "<svg>value-b</svg>")
            code, out, err = self._publish(work, first, "value-a-again")
            self.assertEqual(code, 0, err)
            self.assertIn("::notice::", out)
            self.assertEqual(self._badge_text(origin), "<svg>value-b</svg>")

    def test_five_parallel_runs_keep_the_newest_commit(self) -> None:
        """Five overlapping runs stay green, and only the newest commit writes.

        Side effects:
            Creates a temporary bare origin, five clones, and one badges push.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, shas = self._origin_with_commits(root, 5)
            clones = []
            for index in range(5):
                clone = root / f"clone-{index}"
                self._git(root, ["clone", str(origin), str(clone)])
                clones.append(clone)
            with ThreadPoolExecutor(max_workers=5) as pool:
                futures = [
                    pool.submit(self._publish_subprocess, clone, sha, f"run-{index}")
                    for index, (clone, sha) in enumerate(zip(clones, shas))
                ]
                results = [future.result(timeout=90) for future in futures]
            for code, _out, err in results:
                self.assertEqual(code, 0, err)
            self.assertEqual(self._badge_text(origin), "<svg>run-4</svg>")
            notices = [out for code, out, _err in results if "::notice::" in out]
            self.assertEqual(len(notices), 4)
            self.assertIn("committed", results[4][1])

    def test_push_conflict_warns_after_three_retries(self) -> None:
        """A badges push that keeps failing exits 0 and states the reason.

        The hook rejects every badges push. The publisher tries once and
        retries three times, then warns. The badges branch is not created.

        Side effects:
            Creates a temporary bare origin whose pre-receive hook rejects
            pushes.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._reject_pushes(origin)
            code, out, err = self._publish(work, sha, "never")
            self.assertEqual(code, 0, err)
            self.assertIn("::warning::", out)
            self.assertIn("after 3 retries", out)
            self.assertIn("badge push rejected by test hook", out)
            self.assertNotIn("::notice::", out)
            self.assertEqual((origin / "push-attempts").read_text().strip(), "4")
            missing = self._git(origin, ["rev-parse", "--verify", "refs/heads/badges"], check=False)
            self.assertNotEqual(missing.returncode, 0)

    def test_retry_stops_when_a_newer_commit_appears(self) -> None:
        """A conflict retry fetches master again and does not write when it moved.

        Side effects:
            Creates a temporary bare origin and rejects the first badges push.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, first, second = self._two_master_commits(root)
            self._git(origin, ["update-ref", "refs/heads/master", first])
            self._reject_pushes(origin)
            calls = {"count": 0}
            real_head = self.publish._master_head

            def move_on_retry(repo: Path, *, fetch: bool) -> str:
                calls["count"] += 1
                if calls["count"] == 2:
                    self._git(origin, ["update-ref", "refs/heads/master", second])
                return real_head(repo, fetch=fetch)

            self.publish._master_head = move_on_retry
            try:
                code, out, err = self._publish(work, first, "stale")
            finally:
                self.publish._master_head = real_head
            self.assertEqual(code, 0, err)
            self.assertIn("::notice::", out)
            self.assertNotIn("::warning::", out)
            self.assertEqual(calls["count"], 2)
            self.assertEqual((origin / "push-attempts").read_text().strip(), "1")
            missing = self._git(origin, ["rev-parse", "--verify", "refs/heads/badges"], check=False)
            self.assertNotEqual(missing.returncode, 0)

    def test_invalid_input_and_a_missing_svg_stay_red(self) -> None:
        """A bad commit id or a missing SVG file fails the process.

        Side effects:
            Creates a temporary directory and does not push.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            code, out, err = self._publish(root, "not-a-sha", "nope")
            self.assertEqual(code, 2)
            self.assertIn("coverage badges were not published", err)
            self.assertNotIn("::notice::", out)
            self.assertNotIn("::warning::", out)
            empty = root / "empty"
            empty.mkdir()
            code, out, err = self._publish_dir(root, empty, "a" * 40)
            self.assertEqual(code, 2, err)
            self.assertIn("coverage badges were not published", err)
            self.assertNotIn("::warning::", out)

    def _publish_subprocess(self, repo: Path, sha: str, marker: str) -> tuple[int, str, str]:
        """Publish from a separate process so parallel runs do not share stdout.

        Args:
            repo: Repository the publisher should update.
            sha: Commit id passed as ``--sha``.
            marker: Text stored in the ALog SVG.

        Returns:
            Exit code, stdout, and stderr from that process.

        Raises:
            None. A failed process is reported through the exit code.

        Side effects:
            Writes the SVG directory and may fetch, commit, and push.
        """
        directory = repo / f"svgs-{marker}"
        directory.mkdir(exist_ok=True)
        (directory / SVG_NAMES[0]).write_bytes(f"<svg>{marker}</svg>\n".encode())
        (directory / SVG_NAMES[1]).write_bytes(b"<svg>ext</svg>\n")
        script = ROOT / "publish-coverage-badges.py"
        result = subprocess.run(
            [
                sys.executable,
                str(script),
                "--svg-dir",
                str(directory),
                "--repo",
                str(repo),
                "--sha",
                sha,
            ],
            check=False,
            capture_output=True,
            text=True,
            timeout=60,
        )
        return result.returncode, result.stdout, result.stderr

    def _publish(self, repo: Path, sha: str, marker: str) -> tuple[int, str, str]:
        """Publish one pair of SVG files through the CLI entry point.

        Args:
            repo: Repository the publisher should update.
            sha: Commit id passed as ``--sha``.
            marker: Text stored in the ALog SVG so the test can see who wrote.

        Returns:
            Exit code, stdout, and stderr.

        Raises:
            None.

        Side effects:
            Writes the SVG directory and may fetch, commit, and push.
        """
        directory = repo / f"svgs-{marker}"
        directory.mkdir(exist_ok=True)
        (directory / SVG_NAMES[0]).write_bytes(f"<svg>{marker}</svg>\n".encode())
        (directory / SVG_NAMES[1]).write_bytes(b"<svg>ext</svg>\n")
        return self._publish_dir(repo, directory, sha)

    def _publish_dir(self, repo: Path, directory: Path, sha: str) -> tuple[int, str, str]:
        """Run the publisher CLI against one SVG directory.

        Args:
            repo: Repository passed as ``--repo``.
            directory: Directory passed as ``--svg-dir``.
            sha: Commit id passed as ``--sha``.

        Returns:
            Exit code, stdout, and stderr.

        Raises:
            None.

        Side effects:
            May fetch, commit, and push.
        """
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = self.publish.main(
                ["--svg-dir", str(directory), "--repo", str(repo), "--sha", sha]
            )
        return code, stdout.getvalue(), stderr.getvalue()

    def _badge_text(self, origin: Path) -> str:
        """Read the ALog coverage SVG stored on origin's badges branch.

        Args:
            origin: Bare repository.

        Returns:
            The SVG text without the trailing newline.

        Raises:
            AssertionError: Git cannot read the file.

        Side effects:
            Reads the bare repository.
        """
        result = self._git(origin, ["show", f"refs/heads/badges:{SVG_NAMES[0]}"])
        return result.stdout.strip()

    def _one_master_commit(self, root: Path) -> tuple[Path, Path, str]:
        """Create a bare origin and a worktree with one master commit.

        Args:
            root: Temporary directory that will hold both repositories.

        Returns:
            The bare origin, the worktree, and the master SHA.

        Raises:
            AssertionError: Git cannot create or push the commit.

        Side effects:
            Writes two repositories and pushes master.
        """
        origin, work, shas = self._commits_on_origin(root, 1)
        return origin, work, shas[0]

    def _two_master_commits(self, root: Path) -> tuple[Path, Path, str, str]:
        """Create a bare origin whose master has two commits.

        Args:
            root: Temporary directory that will hold both repositories.

        Returns:
            The bare origin, the worktree, the first SHA, and the second SHA.
            Origin master is the first SHA. The second commit is in the
            worktree and on origin, but origin's master ref stays on the first
            until the caller moves it.

        Raises:
            AssertionError: Git cannot create the commits.

        Side effects:
            Writes two repositories. Pushes only the first commit to master,
            and pushes the second commit as a ref that does not move master
            until the caller asks.
        """
        origin, work, shas = self._commits_on_origin(root, 1)
        (work / "README.md").write_text("commit 2\n", encoding="utf-8")
        self._git(work, ["add", "README.md"])
        self._commit(work, "commit 2")
        second = self._git(work, ["rev-parse", "HEAD"]).stdout.strip()
        self._git(work, ["push", "origin", f"{second}:refs/heads/commit-2"])
        return origin, work, shas[0], second

    def _origin_with_commits(self, root: Path, count: int) -> tuple[Path, list[str]]:
        """Create a bare origin whose master tip is the newest of several commits.

        Args:
            root: Temporary directory.
            count: Number of commits, from 1 to 5.

        Returns:
            The bare origin and the commit ids from oldest to newest.

        Raises:
            AssertionError: Git cannot create the commits.

        Side effects:
            Writes two repositories and pushes master.
        """
        origin, _work, shas = self._commits_on_origin(root, count)
        return origin, shas

    def _commits_on_origin(self, root: Path, count: int) -> tuple[Path, Path, list[str]]:
        """Create a bare origin and push the requested number of master commits.

        Args:
            root: Temporary directory.
            count: Number of commits, from 1 to 5.

        Returns:
            The bare origin, the worktree, and the SHAs from oldest to newest.
            Origin master is the newest SHA.

        Raises:
            AssertionError: ``count`` is outside 1 to 5, or git fails.

        Side effects:
            Writes two repositories and pushes master.
        """
        if count < 1 or count > 5:
            raise AssertionError("commit count must be from 1 to 5")
        self._git(root, ["init", "--bare", "-b", "master", "origin.git"])
        self._git(root, ["init", "-b", "master", "work"])
        origin = root / "origin.git"
        work = root / "work"
        self._git(work, ["remote", "add", "origin", str(origin)])
        shas: list[str] = []
        for index in range(1, count + 1):
            (work / "README.md").write_text(f"commit {index}\n", encoding="utf-8")
            self._git(work, ["add", "README.md"])
            self._commit(work, f"commit {index}")
            shas.append(self._git(work, ["rev-parse", "HEAD"]).stdout.strip())
        self._git(work, ["push", "origin", "HEAD:refs/heads/master"])
        return origin, work, shas

    def _reject_pushes(self, origin: Path) -> None:
        """Install a hook that rejects every push and counts the attempts.

        Args:
            origin: Bare repository. Its ``hooks`` directory must exist, which
                ``git init --bare`` creates.

        Returns:
            None.

        Raises:
            OSError: The hook file cannot be written.

        Side effects:
            Writes ``hooks/pre-receive``. Later pushes append to
            ``push-attempts`` inside the bare repository.
        """
        hook = origin / "hooks" / "pre-receive"
        hook.write_text(
            "#!/bin/sh\n"
            'count_file="$GIT_DIR/push-attempts"\n'
            "n=0\n"
            'if [ -f "$count_file" ]; then\n'
            '  n=$(cat "$count_file")\n'
            "fi\n"
            "n=$((n + 1))\n"
            "printf '%s\\n' \"$n\" > \"$count_file\"\n"
            "echo \"badge push rejected by test hook\" >&2\n"
            "exit 1\n",
            encoding="utf-8",
        )
        hook.chmod(0o755)

    def _git(self, repo: Path, args: list[str], check: bool = True) -> subprocess.CompletedProcess[str]:
        """Run git in a temporary repository.

        Args:
            repo: Repository root.
            args: Git arguments, without the git binary. At most 12.
            check: When true, a non-zero status fails the test.

        Returns:
            The completed process. stdout and stderr are text.

        Raises:
            AssertionError: ``check`` is true and git failed.

        Side effects:
            Runs git.
        """
        if len(args) > 12:
            raise AssertionError("git command is too long")
        result = subprocess.run(
            ["git", "-C", str(repo), *args],
            check=False,
            capture_output=True,
            text=True,
            timeout=30,
        )
        if check and result.returncode != 0:
            raise AssertionError(result.stderr)
        return result

    def _commit(self, repo: Path, message: str) -> None:
        """Create a commit with a fixed test identity.

        Args:
            repo: Repository root.
            message: Commit subject.

        Returns:
            None.

        Raises:
            AssertionError: Git cannot commit.

        Side effects:
            Creates a commit in ``repo``.
        """
        env = {
            "GIT_AUTHOR_NAME": "Badge Test",
            "GIT_AUTHOR_EMAIL": "badge@example.com",
            "GIT_COMMITTER_NAME": "Badge Test",
            "GIT_COMMITTER_EMAIL": "badge@example.com",
        }
        result = subprocess.run(
            ["git", "-C", str(repo), "commit", "-m", message],
            check=False,
            capture_output=True,
            text=True,
            timeout=30,
            env={**os.environ, **env},
        )
        if result.returncode != 0:
            raise AssertionError(result.stderr)


if __name__ == "__main__":
    raise SystemExit(unittest.main())
