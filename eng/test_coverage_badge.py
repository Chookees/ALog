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
import shlex
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
PUSH_RED_PREFIX = "coverage badges were not published: git push failed: "
PUSH_RED_SUFFIX = (
    ". Check that the badge job has contents: write "
    "and that no ruleset blocks the badges branch."
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
            [
                "git",
                "push",
                "--porcelain",
                "origin",
                "refs/heads/badges:refs/heads/badges",
            ],
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

    def test_five_parallel_runs_with_the_same_sha_stay_green(self) -> None:
        """Five publishers of one master SHA all exit 0, three times.

        Each round uses a fresh bare origin. A red exit, including a flaky
        one, fails the suite. The winning SVG is not asserted because the
        writers race.

        Side effects:
            Creates three temporary bare origins and fifteen clones.
        """
        for round_index in range(1, 4):
            with self.subTest(round=round_index):
                self._assert_same_sha_race()

    def test_push_conflict_warns_after_three_retries(self) -> None:
        """A badges push that stays a real conflict warns after three retries.

        Each push is moved to a commit the client does not have, so git
        reports fetch first. The publisher waits 1, 2, and 4 seconds and
        then warns. The previous SVG stays.

        Side effects:
            Creates a temporary bare origin and a git wrapper on ``PATH``.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._publish_ok(work, sha, "kept")
            pauses = self._record_pauses()
            self._divert_badges_pushes(root, origin, "always-side")
            try:
                code, out, err = self._publish(work, sha, "lost")
            finally:
                self._restore_path()
            self.assertEqual(code, 0, err)
            self.assertIn("::warning::", out)
            self.assertIn("after 3 retries", out)
            self.assertNotIn("::notice::", out)
            self.assertNotIn("lost", out)
            self.assertEqual(pauses, [1, 2, 4])
            self.assertEqual((origin / "push-attempts").read_text().strip(), "4")
            self.assertEqual(self._badge_text(origin), "<svg>kept</svg>")

    def test_retry_stops_when_a_newer_commit_appears(self) -> None:
        """A non-fast-forward retry fetches master again and skips when it moved.

        Side effects:
            Creates a temporary bare origin and a git wrapper on ``PATH``.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, first, second = self._two_master_commits(root)
            self._git(origin, ["update-ref", "refs/heads/master", first])
            self._publish_ok(work, first, "seed")
            pauses = self._record_pauses()
            self._divert_badges_pushes(root, origin, "once-master")
            calls = {"count": 0}
            real_head = self.publish._master_head

            def move_on_retry(repo: Path, *, fetch: bool) -> str:
                """Move origin master on the second head check.

                Args:
                    repo: Repository the publisher is updating.
                    fetch: Forwarded to the real head lookup.

                Returns:
                    The master SHA after the optional move.

                Raises:
                    AssertionError: Git cannot read or move the ref.

                Side effects:
                    May move ``refs/heads/master`` on the bare origin.
                """
                calls["count"] += 1
                if calls["count"] == 2:
                    self._git(origin, ["update-ref", "refs/heads/master", second])
                return real_head(repo, fetch=fetch)

            self.publish._master_head = move_on_retry
            try:
                code, out, err = self._publish(work, first, "stale")
            finally:
                self.publish._master_head = real_head
                self._restore_path()
            self.assertEqual(code, 0, err)
            self.assertIn("::notice::", out)
            self.assertNotIn("::warning::", out)
            self.assertEqual(calls["count"], 2)
            self.assertEqual(pauses, [1])
            self.assertEqual((origin / "push-attempts").read_text().strip(), "1")
            shown = self._git(
                origin,
                ["show", f"refs/heads/badges:{SVG_NAMES[0]}"],
                check=False,
            )
            self.assertNotIn("stale", shown.stdout)

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
            self.assertNotIn("git push failed", err)
            self.assertNotIn("::notice::", out)
            self.assertNotIn("::warning::", out)
            empty = root / "empty"
            empty.mkdir()
            code, out, err = self._publish_dir(root, empty, "a" * 40)
            self.assertEqual(code, 2, err)
            self.assertIn("coverage badges were not published", err)
            self.assertNotIn("git push failed", err)
            self.assertNotIn("::warning::", out)

    def test_unreachable_pushurl_exits_red(self) -> None:
        """An unreachable push URL is a real error, not a conflict warning.

        Side effects:
            Creates a temporary bare origin and points its push URL at a
            closed local port. Fetch still uses the local bare repository.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._git(
                work,
                ["remote", "set-url", "--push", "origin", "http://127.0.0.1:1/missing.git"],
            )
            code, out, err = self._publish(work, sha, "nowhere")
            self._assert_real_push_error(code, out, err, "Failed to connect")
            self.assertFalse((origin / "push-attempts").exists())

    def test_permission_denied_hook_exits_red(self) -> None:
        """A hook that says permission denied is a real error and is not retried.

        Side effects:
            Creates a temporary bare origin with a rejecting pre-receive hook.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._reject_with_message(
                origin,
                "Permission to Chookees/ALog.git denied to github-actions[bot]",
            )
            code, out, err = self._publish(work, sha, "denied")
            self._assert_real_push_error(code, out, err, "Permission")
            self.assertIn("denied", err)
            self.assertEqual((origin / "push-attempts").read_text().strip(), "1")

    def test_hook_non_fast_forward_wording_exits_red(self) -> None:
        """Hook text that merely says non-fast-forward is not a conflict.

        Side effects:
            Creates a temporary bare origin with a rejecting pre-receive hook.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._reject_with_message(
                origin,
                "rejected: non-fast-forward is not allowed by this hook",
            )
            code, out, err = self._publish(work, sha, "hook-words")
            self._assert_real_push_error(code, out, err, "non-fast-forward")
            self.assertEqual((origin / "push-attempts").read_text().strip(), "1")

    def test_push_failure_keeps_a_percent_sign_in_the_red_line(self) -> None:
        """A real push failure shows 100% unescaped and does not retry.

        The hook text also starts a fake workflow command on the next line.
        The publisher still prints exactly one red line.

        Side effects:
            Creates a temporary bare origin with a rejecting pre-receive hook.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._reject_with_message(origin, "rejected 100%\n::error::injected")
            code, out, err = self._publish(work, sha, "percent")
            self._assert_real_push_error(code, out, err, "100%")
            self.assertNotIn("%25", err)
            self.assertNotIn("::warning::", out)
            self.assertFalse(err.strip().startswith("::"))
            self.assertEqual((origin / "push-attempts").read_text().strip(), "1")

    def test_percent_is_escaped_once_inside_workflow_commands(self) -> None:
        """A 100% git reason is escaped once in notice and warning text.

        The red path keeps the percent sign. These commands are the only
        place it becomes ``100%25``. A second escape would show ``100%2525``.

        Side effects:
            None.
        """
        reason = self.publish._push_error_text("note 100%\n::error::hidden", "")
        self.assertIn("100%", reason)
        self.assertNotIn("%25", reason)
        self.assertNotIn("\n", reason)
        self.assertFalse(reason.startswith("::"))
        warning_out = io.StringIO()
        with redirect_stdout(warning_out):
            code = self.publish._report_outcome("conflict", reason, "b" * 40)
        self.assertEqual(code, 0)
        self._assert_percent_escaped_once(warning_out.getvalue().strip(), "::warning::")
        notice = self.publish._workflow_command("notice", reason)
        self._assert_percent_escaped_once(notice, "::notice::")

    def test_real_non_fast_forward_is_retried(self) -> None:
        """A real non-fast-forward waits one second and then publishes.

        Side effects:
            Creates a temporary bare origin and a git wrapper on ``PATH``.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, work, sha = self._one_master_commit(root)
            self._publish_ok(work, sha, "old")
            pauses = self._record_pauses()
            self._divert_badges_pushes(root, origin, "once-master")
            try:
                code, out, err = self._publish(work, sha, "new")
            finally:
                self._restore_path()
            self.assertEqual(code, 0, err)
            self.assertEqual(out.strip(), "committed")
            self.assertNotIn("::warning::", out)
            self.assertEqual(pauses, [1])
            self.assertEqual((origin / "push-attempts").read_text().strip(), "2")
            self.assertEqual(self._badge_text(origin), "<svg>new</svg>")

    def test_german_locale_still_detects_non_fast_forward(self) -> None:
        """A German parent locale still treats a real non-fast-forward as a conflict.

        The parent sets ``LANG`` and leaves ``LC_ALL`` unset. Git itself is
        run with ``LC_ALL=C``.

        Side effects:
            Creates a temporary bare origin, changes the process locale for
            the call, and prepends a git wrapper to ``PATH``.
        """
        saved = {key: os.environ.get(key) for key in ("LANG", "LC_ALL", "LANGUAGE")}
        os.environ["LANG"] = "de_DE.UTF-8"
        os.environ.pop("LC_ALL", None)
        os.environ.pop("LANGUAGE", None)
        try:
            self.assertNotIn("LC_ALL", os.environ)
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw)
                origin, work, sha = self._one_master_commit(root)
                self._publish_ok(work, sha, "alt")
                pauses = self._record_pauses()
                self._divert_badges_pushes(root, origin, "once-master")
                try:
                    code, out, err = self._publish(work, sha, "neu")
                finally:
                    self._restore_path()
                self.assertEqual(code, 0, err)
                self.assertEqual(out.strip(), "committed")
                self.assertEqual(pauses, [1])
                self.assertEqual((origin / "push-locale").read_text(encoding="utf-8"), "LC_ALL=C\nLANGUAGE=C\n")
                self.assertEqual(self._badge_text(origin), "<svg>neu</svg>")
        finally:
            for key, value in saved.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

    def test_porcelain_remote_rejected_is_not_a_conflict(self) -> None:
        """A remote rejection stays an error when the hook text says non-fast-forward.

        Side effects:
            None.
        """
        stdout = (
            "To /tmp/origin.git\n"
            "!\trefs/heads/badges:refs/heads/badges\t"
            "[remote rejected] (non-fast-forward)\n"
            "Done\n"
        )
        stderr = "remote: non-fast-forward cannot lock ref refs/heads/badges\n"
        self.assertEqual(self.publish._classify_push(stdout, stderr, 1), "error")

    def test_porcelain_conflict_reasons_are_conflicts(self) -> None:
        """Porcelain conflict reasons and a cannot-lock failure are conflicts.

        Side effects:
            None.
        """
        for summary in (
            "[rejected] (non-fast-forward)",
            "[rejected] (fetch first)",
            "[rejected] (stale info)",
        ):
            stdout = f"!\trefs/heads/badges:refs/heads/badges\t{summary}\n"
            self.assertEqual(self.publish._classify_push(stdout, "", 1), "conflict", summary)
        locked = "error: cannot lock ref 'refs/heads/badges': is at abc but expected def\n"
        self.assertEqual(self.publish._classify_push("", locked, 1), "conflict")
        remote_lock = "!\trefs/heads/badges:refs/heads/badges\t[remote rejected] (cannot lock ref)\n"
        self.assertEqual(self.publish._classify_push(remote_lock, "cannot lock ref", 1), "conflict")

    def test_failed_to_update_ref_with_the_lock_line_is_a_conflict(self) -> None:
        """GitHub's failed-to-update-ref pair is a badges lock conflict.

        Side effects:
            None.
        """
        stdout = "!\trefs/heads/badges:refs/heads/badges\t[remote rejected] (failed to update ref)\n"
        stderr = "remote: error: cannot lock ref 'refs/heads/badges': is at X but expected Y\n"
        self.assertEqual(self.publish._classify_push(stdout, stderr, 1), "conflict")

    def test_remote_rejected_cannot_lock_ref_prefix_is_a_conflict(self) -> None:
        """A remote rejection whose reason starts with cannot lock ref is a conflict.

        Side effects:
            None.
        """
        for reason in (
            "cannot lock ref",
            "cannot lock ref 'refs/heads/badges'",
        ):
            stdout = f"!\trefs/heads/badges:refs/heads/badges\t[remote rejected] ({reason})\n"
            self.assertEqual(self.publish._classify_push(stdout, "", 1), "conflict", reason)

    def test_failed_to_update_ref_without_the_lock_line_is_red(self) -> None:
        """failed to update ref is an error unless stderr has the badges lock line.

        Side effects:
            None.
        """
        stdout = "!\trefs/heads/badges:refs/heads/badges\t[remote rejected] (failed to update ref)\n"
        self.assertEqual(self.publish._classify_push(stdout, "", 1), "error")
        other_ref = "remote: error: cannot lock ref 'refs/heads/other': is at X but expected Y\n"
        self.assertEqual(self.publish._classify_push(stdout, other_ref, 1), "error")
        loose = "remote: cannot lock ref 'refs/heads/badges'\n"
        self.assertEqual(self.publish._classify_push(stdout, loose, 1), "error")

    def test_git_2_55_lock_reasons_with_the_lock_line_are_conflicts(self) -> None:
        """Git 2.55 names the same badges lock with a shorter porcelain reason.

        The runner's receive-pack still prints the cannot-lock line on stderr.
        The porcelain reason is ``incorrect old value provided`` when the ref
        moved, or ``reference already exists`` when the branch was just
        created. Both are conflicts. The same words without that stderr line
        are not.

        Side effects:
            None.
        """
        stderr = "remote: error: cannot lock ref 'refs/heads/badges': is at X but expected Y\n"
        created = "remote: error: cannot lock ref 'refs/heads/badges': reference already exists\n"
        cases = (
            ("incorrect old value provided", stderr),
            ("reference already exists", created),
        )
        for reason, err in cases:
            stdout = f"!\trefs/heads/badges:refs/heads/badges\t[remote rejected] ({reason})\n"
            self.assertEqual(self.publish._classify_push(stdout, err, 1), "conflict", reason)
            self.assertEqual(self.publish._classify_push(stdout, "", 1), "error", reason)

    def test_hook_reason_with_cannot_lock_ref_in_the_middle_is_red(self) -> None:
        """A hook reason that only contains cannot lock ref in the middle is an error.

        Side effects:
            None.
        """
        stdout = (
            "!\trefs/heads/badges:refs/heads/badges\t"
            "[remote rejected] (pre-receive hook declined: cannot lock ref is not a conflict)\n"
        )
        stderr = "remote: pre-receive hook declined: cannot lock ref is not a conflict\n"
        self.assertEqual(self.publish._classify_push(stdout, stderr, 1), "error")

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

    def _publish_ok(self, work: Path, sha: str, marker: str) -> None:
        """Publish one SVG pair and require a new badges commit.

        Args:
            work: Repository to publish from.
            sha: Master commit id.
            marker: Text stored in the ALog SVG.

        Returns:
            None.

        Raises:
            AssertionError: The publisher does not commit.

        Side effects:
            May fetch, commit, and push badges.
        """
        code, out, err = self._publish(work, sha, marker)
        self.assertEqual(code, 0, err)
        self.assertEqual(out.strip(), "committed")

    def _record_pauses(self) -> list[int]:
        """Replace the publisher sleep with a list of requested delays.

        Returns:
            The list the publisher appends to. It is empty until a retry.

        Raises:
            None.

        Side effects:
            Replaces ``_pause`` on the publisher module for this test.
        """
        recorded: list[int] = []

        def pause(seconds: int) -> None:
            """Record one retry delay.

            Args:
                seconds: The delay the publisher asked for.

            Returns:
                None.

            Raises:
                None.

            Side effects:
                Appends ``seconds`` to ``recorded``.
            """
            recorded.append(seconds)

        self.publish._pause = pause
        return recorded

    def _assert_real_push_error(self, code: int, out: str, err: str, needle: str) -> None:
        """Require one red line and no conflict warning.

        Args:
            code: Publisher exit code.
            out: Publisher stdout.
            err: Publisher stderr.
            needle: Text the error line must contain.

        Returns:
            None.

        Raises:
            AssertionError: The failure was not a single red line.

        Side effects:
            None.
        """
        self.assertEqual(code, 2)
        self.assertNotIn("::warning::", out)
        self.assertNotIn("::notice::", out)
        self.assertNotIn("Traceback", err)
        line = err.strip()
        self.assertEqual(len(line.splitlines()), 1, err)
        self.assertTrue(line.startswith(PUSH_RED_PREFIX), line)
        self.assertTrue(line.endswith(PUSH_RED_SUFFIX), line)
        reason = line[len(PUSH_RED_PREFIX) : -len(PUSH_RED_SUFFIX)]
        self.assertIn(needle, reason)
        self.assertFalse(line.startswith("::"))

    def _reject_with_message(self, origin: Path, message: str) -> None:
        """Install a pre-receive hook that rejects every push with one message.

        Args:
            origin: Bare repository. ``git init --bare`` creates ``hooks``.
            message: Text written to the hook's stderr. It is quoted for the
                shell.

        Returns:
            None.

        Raises:
            OSError: The hook file cannot be written.

        Side effects:
            Writes ``hooks/pre-receive``. Each push increments
            ``push-attempts`` in the bare repository.
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
            f"printf '%s\\n' {shlex.quote(message)} >&2\n"
            "exit 1\n",
            encoding="utf-8",
        )
        hook.chmod(0o755)

    def _divert_badges_pushes(self, root: Path, origin: Path, mode: str) -> None:
        """Prepend a git wrapper that turns a badges push into a conflict.

        Args:
            root: Temporary directory that will hold the wrapper.
            origin: Bare origin whose badges ref the wrapper moves.
            mode: ``once-master`` points badges at master before the first
                push, which is a non-fast-forward. ``always-side`` points
                badges at a new commit of the same tree before every push,
                which is fetch first.

        Returns:
            None.

        Raises:
            AssertionError: ``mode`` is unknown or git cannot read master.

        Side effects:
            Prepends ``root/bin`` to ``PATH``. The caller restores it with
            ``_restore_path``. Writes ``push-attempts`` and ``push-locale``
            into the bare origin when a push runs.
        """
        if mode not in {"once-master", "always-side"}:
            raise AssertionError("unknown divert mode")
        master = self._git(origin, ["rev-parse", "refs/heads/master"]).stdout.strip()
        bindir = root / "bin"
        bindir.mkdir()
        script = bindir / "git"
        script.write_text(self._diverter_script(origin, master, mode), encoding="utf-8")
        script.chmod(0o755)
        self._saved_path = os.environ.get("PATH", "")
        os.environ["PATH"] = str(bindir) + os.pathsep + self._saved_path

    def _diverter_script(self, origin: Path, master: str, mode: str) -> str:
        """Return the shell script that fronts git for one divert mode.

        Args:
            origin: Bare origin path baked into the script.
            master: Master commit id baked into the script.
            mode: ``once-master`` or ``always-side``.

        Returns:
            A complete shell script.

        Raises:
            None.

        Side effects:
            None.
        """
        origin_q = shlex.quote(str(origin))
        master_q = shlex.quote(master)
        mode_q = shlex.quote(mode)
        lines = [
            "#!/bin/sh",
            "real=/usr/bin/git",
            f"origin={origin_q}",
            f"master={master_q}",
            f"mode={mode_q}",
            'is_push=0; for arg in "$@"; do [ "$arg" = push ] && is_push=1; done',
            '[ "$is_push" = 1 ] || exec "$real" "$@"',
            'printf "%s\\n" "LC_ALL=${LC_ALL-unset}" > "$origin/push-locale"',
            'printf "%s\\n" "LANGUAGE=${LANGUAGE-unset}" >> "$origin/push-locale"',
            'n=0; [ -f "$origin/push-attempts" ] && n=$(cat "$origin/push-attempts")',
            'n=$((n + 1)); printf "%s\\n" "$n" > "$origin/push-attempts"',
            'divert=0; [ "$mode" = always-side ] && divert=1; [ "$n" = 1 ] && divert=1',
            '[ "$divert" = 1 ] || exec "$real" "$@"',
            'if [ "$mode" = always-side ]; then',
            '  tip=$("$real" --git-dir="$origin" rev-parse refs/heads/badges) || exit 1',
            '  tree=$("$real" --git-dir="$origin" rev-parse "$tip^{tree}") || exit 1',
            "  side=$(GIT_AUTHOR_NAME=Badge GIT_AUTHOR_EMAIL=badge@example.com "
            "GIT_COMMITTER_NAME=Badge GIT_COMMITTER_EMAIL=badge@example.com "
            '"$real" --git-dir="$origin" commit-tree "$tree" -m "side-$n") || exit 1',
            '  "$real" --git-dir="$origin" update-ref refs/heads/badges "$side" || exit 1',
            "else",
            '  "$real" --git-dir="$origin" update-ref refs/heads/badges "$master" || exit 1',
            "fi",
            'exec "$real" "$@"',
        ]
        return "\n".join(lines) + "\n"

    def _assert_same_sha_race(self) -> None:
        """Run five publishers of one SHA and require every exit code to be 0.

        Returns:
            None.

        Raises:
            AssertionError: A publisher exits red or does not finish.

        Side effects:
            Creates a temporary bare origin and five clones.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            origin, shas = self._origin_with_commits(root, 1)
            sha = shas[0]
            clones = []
            for index in range(5):
                clone = root / f"clone-{index}"
                self._git(root, ["clone", str(origin), str(clone)])
                clones.append(clone)
            with ThreadPoolExecutor(max_workers=5) as pool:
                futures = [
                    pool.submit(self._publish_subprocess, clone, sha, f"same-{index}")
                    for index, clone in enumerate(clones)
                ]
                results = [future.result(timeout=90) for future in futures]
            for code, _out, err in results:
                self.assertEqual(code, 0, err)
            self.assertTrue(self._badge_text(origin).startswith("<svg>same-"))

    def _assert_percent_escaped_once(self, line: str, prefix: str) -> None:
        """Require one workflow command that contains 100%25 a single time.

        Args:
            line: The command, without a trailing newline.
            prefix: ``::notice::`` or ``::warning::``.

        Returns:
            None.

        Raises:
            AssertionError: The line is not one command, or the percent sign
                was escaped twice or not at all.

        Side effects:
            None.
        """
        self.assertEqual(len(line.splitlines()), 1, line)
        self.assertTrue(line.startswith(prefix), line)
        self.assertEqual(line.count("100%25"), 1, line)
        self.assertNotIn("%2525", line)
        self.assertNotIn("100%2525", line)

    def _restore_path(self) -> None:
        """Put ``PATH`` back after a git wrapper test.

        Returns:
            None.

        Raises:
            None.

        Side effects:
            Restores the process ``PATH`` saved by ``_divert_badges_pushes``.
        """
        saved = getattr(self, "_saved_path", None)
        if isinstance(saved, str):
            os.environ["PATH"] = saved

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
