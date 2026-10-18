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
import json
import os
import subprocess
import sys
import tempfile
import unittest
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
