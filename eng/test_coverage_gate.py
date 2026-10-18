#!/usr/bin/env python3
"""Unit tests for eng/coverage-gate.py.

The fixtures under eng/testdata cover both Cobertura package-name forms and a
ratchet that lowers a floor. Temporary files cover the exclusion comment and
the enforce switch. Nothing here starts a build or talks to the network.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DATA = ROOT / "testdata"


def load_gate():
    """Load coverage-gate.py as a module.

    The file name contains a hyphen, so a normal import cannot see it.

    Returns:
        The loaded module.

    Raises:
        RuntimeError: The file cannot be loaded.

    Side effects:
        Executes the module once. Later calls return the cached module.
    """
    path = ROOT / "coverage-gate.py"
    spec = importlib.util.spec_from_file_location("coverage_gate", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {path}.")
    module = importlib.util.module_from_spec(spec)
    # dataclasses looks the class up in sys.modules while the module executes.
    sys.modules[spec.name] = module
    try:
        spec.loader.exec_module(module)
    except Exception:
        del sys.modules[spec.name]
        raise
    return module


GATE = load_gate()


class CoverageGateTests(unittest.TestCase):
    """Checks for normalization, floors, enforcement, and exclusion comments."""

    def test_plain_and_dll_names_normalize_to_the_same_rates(self) -> None:
        """Both Cobertura variants report ALog at 60.2 lines and 55.8 branches.

        The assembly-name fixture and the ``.dll`` fixture carry the same rates.
        Extensions is checked the same way. A mismatch fails the test.

        Side effects:
            Reads the two fixture files.
        """
        plain = GATE.load_cobertura([DATA / "cobertura-assembly-name.xml"])
        suffixed = GATE.load_cobertura([DATA / "cobertura-dll-suffix.xml"])
        self.assertEqual(plain.keys(), suffixed.keys())
        self.assertEqual(plain["ALog"], PackageRates(Decimal("60.2"), Decimal("55.8")))
        self.assertEqual(
            plain["ALog.Extensions.Logging"],
            PackageRates(Decimal("73.7"), Decimal("62.1")),
        )
        self.assertEqual(plain, suffixed)

    def test_dll_suffix_is_stripped_once(self) -> None:
        """``ALog.dll`` becomes ``ALog`` and ``ALog.dll.dll`` keeps one suffix.

        ``.DLL`` is not the suffix the spec names, so it stays. An empty name
        and a name that is only ``.dll`` are rejected.

        Side effects:
            None.
        """
        self.assertEqual(GATE.normalize_package_name("ALog.dll"), "ALog")
        self.assertEqual(GATE.normalize_package_name("ALog.dll.dll"), "ALog.dll")
        self.assertEqual(GATE.normalize_package_name("ALog.DLL"), "ALog.DLL")
        with self.assertRaises(ValueError):
            GATE.normalize_package_name("  ")
        with self.assertRaises(ValueError):
            GATE.normalize_package_name(".dll")

    def test_measurement_equal_to_the_floor_passes(self) -> None:
        """A percent equal to its floor is not a failure.

        The floor is a minimum. The test builds that minimum from the fixture
        rates and expects no floor problem.

        Side effects:
            Reads one Cobertura fixture.
        """
        coverage = GATE.load_cobertura([DATA / "cobertura-assembly-name.xml"])
        ratchet = ratchet_for(coverage)
        self.assertEqual(GATE.floor_problems(coverage, ratchet), [])

    def test_measurement_below_the_floor_fails(self) -> None:
        """One tenth of a percent under the line floor is a failure.

        The branch floor stays equal, so only the line message should appear.

        Side effects:
            Reads one Cobertura fixture.
        """
        coverage = GATE.load_cobertura([DATA / "cobertura-assembly-name.xml"])
        ratchet = ratchet_for(coverage)
        floor = ratchet.packages["ALog"]
        ratchet.packages["ALog"] = GATE.PackageFloor(floor.lines + Decimal("0.1"), floor.branches)
        problems = GATE.floor_problems(coverage, ratchet)
        self.assertTrue(any("ALog lines" in problem and "below floor" in problem for problem in problems))
        self.assertFalse(any("ALog branches" in problem for problem in problems))

    def test_lowered_ratchet_fixture_fails_and_a_raise_passes(self) -> None:
        """The two fixture files show that lowering ALog lines is rejected.

        ``ratchet-lowered.json`` drops ALog lines from 50.0 to 49.9. A copy
        that raises that floor to 50.1 is allowed. Removing a package is not.

        Side effects:
            Reads the two ratchet fixtures.
        """
        baseline = GATE.load_ratchet(DATA / "ratchet-baseline.json")
        lowered = GATE.load_ratchet(DATA / "ratchet-lowered.json")
        problems = GATE.lowering_problems(baseline, lowered)
        self.assertTrue(any("lowered ALog lines from 50.0 to 49.9" in problem for problem in problems))
        raised_packages = dict(baseline.packages)
        old = raised_packages["ALog"]
        raised_packages["ALog"] = GATE.PackageFloor(Decimal("50.1"), old.branches)
        raised = GATE.Ratchet(baseline.lines_target, baseline.branches_target, raised_packages)
        self.assertEqual(GATE.lowering_problems(baseline, raised), [])
        removed = dict(baseline.packages)
        del removed["ALog"]
        gone = GATE.Ratchet(baseline.lines_target, baseline.branches_target, removed)
        self.assertTrue(any("removed the floor for ALog" in problem for problem in GATE.lowering_problems(baseline, gone)))

    def test_missing_baseline_allows_the_first_ratchet(self) -> None:
        """No master file means there is nothing to lower.

        Side effects:
            Reads the baseline fixture as the candidate only.
        """
        candidate = GATE.load_ratchet(DATA / "ratchet-baseline.json")
        self.assertEqual(GATE.lowering_problems(None, candidate), [])

    def test_enforce_false_ignores_the_90_80_targets(self) -> None:
        """S4 measurements are far below 90/80 and must still pass the target check.

        The floor check is separate and is not what this test calls.

        Side effects:
            Reads one Cobertura fixture.
        """
        coverage = GATE.load_cobertura([DATA / "cobertura-assembly-name.xml"])
        ratchet = GATE.load_ratchet(DATA / "ratchet-baseline.json")
        self.assertEqual(GATE.enforce_problems(coverage, ratchet, False), [])

    def test_enforce_true_rejects_lines_at_90_and_accepts_90_1(self) -> None:
        """The line gate is strict greater-than. 90.0 fails and 90.1 passes.

        Branches at exactly 80 pass. Branches at 79.9 fail. The switch must be
        the exact string ``true``; ``TRUE`` and a missing value are checked too.

        Side effects:
            None beyond the assertions.
        """
        ratchet = GATE.load_ratchet(DATA / "ratchet-baseline.json")
        at_target = both(Decimal("90.0"), Decimal("80.0"))
        above = both(Decimal("90.1"), Decimal("80.0"))
        low_branch = both(Decimal("90.1"), Decimal("79.9"))
        at_problems = GATE.enforce_problems(at_target, ratchet, True)
        self.assertTrue(any("not above 90" in problem for problem in at_problems))
        self.assertEqual(GATE.enforce_problems(above, ratchet, True), [])
        branch_problems = GATE.enforce_problems(low_branch, ratchet, True)
        self.assertTrue(any("branches 79.9" in problem for problem in branch_problems))
        self.assertFalse(GATE.enforce_from_env({}))
        self.assertFalse(GATE.enforce_from_env({"COVERAGE_ENFORCE": "false"}))
        self.assertTrue(GATE.enforce_from_env({"COVERAGE_ENFORCE": "true"}))
        with self.assertRaises(ValueError):
            GATE.enforce_from_env({"COVERAGE_ENFORCE": "TRUE"})

    def test_targets_other_than_90_and_80_fail(self) -> None:
        """A ratchet that changes a target is rejected even when floors match.

        Side effects:
            Reads the baseline fixture and copies it with a different target.
        """
        baseline = GATE.load_ratchet(DATA / "ratchet-baseline.json")
        self.assertEqual(GATE.target_problems(baseline), [])
        moved = GATE.Ratchet(Decimal("89.9"), baseline.branches_target, baseline.packages)
        self.assertTrue(any("Line target must be 90" in problem for problem in GATE.target_problems(moved)))

    def test_exclusion_comment_rules(self) -> None:
        """The comment must sit on the line directly above the attribute.

        A blank line, an empty reason, and a leading attribute fail. A real
        comment passes. An attribute mentioned only in a comment is ignored.

        Side effects:
            Writes a temporary directory and deletes it.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_cs(root / "Good.cs", "// Coverage-Ausnahme: process exit cannot run in a test\n[ExcludeFromCodeCoverage]\n")
            write_cs(root / "Bare.cs", "[ExcludeFromCodeCoverage]\n")
            write_cs(root / "Gap.cs", "// Coverage-Ausnahme: reason\n\n[ExcludeFromCodeCoverage]\n")
            write_cs(root / "Empty.cs", "// Coverage-Ausnahme:\n[ExcludeFromCodeCoverage]\n")
            write_cs(root / "Mention.cs", "// [ExcludeFromCodeCoverage] is not an attribute here\n")
            problems = GATE.scan_exemptions([root], GATE.ScanLimits(max_files=10))
            self.assertFalse(any("Good.cs" in problem for problem in problems))
            self.assertTrue(any("Bare.cs" in problem for problem in problems))
            self.assertTrue(any("Gap.cs" in problem for problem in problems))
            self.assertTrue(any("Empty.cs" in problem for problem in problems))
            self.assertFalse(any("Mention.cs" in problem for problem in problems))

    def test_source_file_cap_and_depth_cap(self) -> None:
        """The file cap and the depth cap fail closed at their boundaries.

        One file is allowed when the cap is 1. A second file raises. Depth 0
        reports a nested directory instead of scanning it. A cap of 5001 is
        rejected before the walk.

        Side effects:
            Writes a temporary tree and deletes it.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            write_cs(root / "One.cs", "class One {}\n")
            write_cs(root / "Two.cs", "class Two {}\n")
            self.assertEqual(GATE.scan_exemptions([root], GATE.ScanLimits(max_files=2)), [])
            with self.assertRaises(ValueError):
                GATE.scan_exemptions([root], GATE.ScanLimits(max_files=1))
            nested = root / "nested"
            nested.mkdir()
            write_cs(nested / "Deep.cs", "[ExcludeFromCodeCoverage]\n")
            deep = GATE.scan_exemptions([root], GATE.ScanLimits(max_files=10, max_depth=0))
            self.assertTrue(any("deeper than 0" in problem for problem in deep))
            self.assertFalse(any("Deep.cs" in problem for problem in deep))
        with self.assertRaises(ValueError):
            GATE.validate_scan_limits(GATE.ScanLimits(max_files=5001))
        with self.assertRaises(ValueError):
            GATE.keep_child_directories(["a", "b"], (), 1)
        self.assertEqual(GATE.keep_child_directories(["bin", "src"], ("bin", "obj"), 1), ["src"])

    def test_child_directory_cap_rejects_501(self) -> None:
        """501 kept children exceed the hard cap of 500.

        The production walk uses 500. The helper rejects a requested cap above
        that before it walks.

        Side effects:
            None.
        """
        with self.assertRaises(ValueError):
            GATE.keep_child_directories(["only"], (), 501)
        names = [f"d{index}" for index in range(500)]
        self.assertEqual(len(GATE.keep_child_directories(names, (), 500)), 500)

    def test_summary_lists_both_packages_for_either_variant(self) -> None:
        """The summary names both packages and the greppable coverage lines.

        The ``.dll`` fixture must produce the same package names as the plain one.

        Side effects:
            Reads fixtures and writes a temporary summary file.
        """
        coverage = GATE.load_cobertura([DATA / "cobertura-dll-suffix.xml"])
        ratchet = ratchet_for(coverage)
        text = GATE.format_summary(coverage, ratchet, False, [])
        self.assertIn("| ALog | 60.2 | 55.8 |", text)
        self.assertIn("| ALog.Extensions.Logging | 73.7 | 62.1 |", text)
        self.assertIn("COVERAGE package=ALog lines=60.2 branches=55.8", text)
        self.assertIn("COVERAGE_ENFORCE=false", text)
        self.assertIn("Line target > 90.0", text)
        with tempfile.TemporaryDirectory() as raw:
            summary = Path(raw) / "summary.md"
            summary.write_text("earlier\n", encoding="utf-8")
            GATE.write_summary(str(summary), text)
            combined = summary.read_text(encoding="utf-8")
            self.assertTrue(combined.startswith("earlier\n"))
            self.assertIn("COVERAGE package=ALog.Extensions.Logging lines=73.7", combined)

    def test_measurement_flag_writes_the_compared_line_percents(self) -> None:
        """``--measurement`` stores the line percents this run compared.

        The badge job reads the file. It does not start another test run.
        The fixture rates are 60.2 and 73.7.

        Side effects:
            Writes a temporary ratchet, a clean source file, and the JSON file.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "src"
            source.mkdir()
            write_cs(source / "Clean.cs", "class Clean {}\n")
            ratchet = root / "ratchet.json"
            ratchet.write_text(ratchet_text(0, 0), encoding="utf-8")
            measurement = root / "lines.json"
            code = GATE.main(
                [
                    "--cobertura",
                    str(DATA / "cobertura-assembly-name.xml"),
                    "--ratchet",
                    str(ratchet),
                    "--source-root",
                    str(source),
                    "--measurement",
                    str(measurement),
                ],
                {"COVERAGE_ENFORCE": "false", "GITHUB_EVENT_NAME": "push"},
            )
            self.assertEqual(code, 0)
            document = measurement.read_text(encoding="utf-8")
            self.assertIn('"lines": "60.2"', document)
            self.assertIn('"lines": "73.7"', document)
            self.assertTrue(document.endswith("\n"))
            self.assertFalse((root / "lines.json.tmp").exists())

    def test_main_enforce_false_passes_and_true_fails(self) -> None:
        """The command exits 0 for S4 and 1 when the target gate is on.

        Floors are 0 so the ratchet itself is not the reason for the red result.
        A bad exclusion in a temp root is also red while the summary is written.

        Side effects:
            Writes temporary ratchet, summary, and source files.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            ratchet = root / "ratchet.json"
            ratchet.write_text(
                json.dumps(
                    {
                        "linesTarget": 90,
                        "branchesTarget": 80,
                        "packages": {
                            "ALog": {"lines": 0.0, "branches": 0.0},
                            "ALog.Extensions.Logging": {"lines": 0.0, "branches": 0.0},
                        },
                    }
                ),
                encoding="utf-8",
            )
            source = root / "src"
            source.mkdir()
            write_cs(source / "Clean.cs", "class Clean {}\n")
            summary = root / "summary.md"
            code = GATE.main(
                [
                    "--cobertura",
                    str(DATA / "cobertura-assembly-name.xml"),
                    "--ratchet",
                    str(ratchet),
                    "--source-root",
                    str(source),
                    "--summary",
                    str(summary),
                ],
                {"COVERAGE_ENFORCE": "false"},
            )
            self.assertEqual(code, 0)
            self.assertIn("COVERAGE package=ALog lines=60.2", summary.read_text(encoding="utf-8"))
            red = GATE.main(
                [
                    "--cobertura",
                    str(DATA / "cobertura-dll-suffix.xml"),
                    "--ratchet",
                    str(ratchet),
                    "--source-root",
                    str(source),
                ],
                {"COVERAGE_ENFORCE": "true"},
            )
            self.assertEqual(red, 1)
            write_cs(source / "Bare.cs", "[ExcludeFromCodeCoverage]\n")
            bare = GATE.main(
                [
                    "--cobertura",
                    str(DATA / "cobertura-assembly-name.xml"),
                    "--ratchet",
                    str(ratchet),
                    "--source-root",
                    str(source),
                ],
                {"COVERAGE_ENFORCE": "false"},
            )
            self.assertEqual(bare, 1)

    def test_main_lowered_baseline_exits_1(self) -> None:
        """Passing the lowered fixture as the candidate and the baseline as master fails.

        Side effects:
            Reads the two ratchet fixtures and one Cobertura fixture.
        """
        with tempfile.TemporaryDirectory() as raw:
            source = Path(raw) / "src"
            source.mkdir()
            write_cs(source / "Clean.cs", "class Clean {}\n")
            # Floors in the lowered file are 49.9/40, above these fixture rates? No.
            # Use a ratchet whose floors are below the fixture so the only failure
            # is the lowering check. The lowered fixture's floors are below the
            # measurements (60.2 >= 49.9), so floor_problems is empty.
            code = GATE.main(
                [
                    "--cobertura",
                    str(DATA / "cobertura-assembly-name.xml"),
                    "--ratchet",
                    str(DATA / "ratchet-lowered.json"),
                    "--baseline",
                    str(DATA / "ratchet-baseline.json"),
                    "--source-root",
                    str(source),
                ],
                {"COVERAGE_ENFORCE": "false"},
            )
            self.assertEqual(code, 1)

    def test_lowered_floor_passes_only_with_a_matching_floor_line(self) -> None:
        """A one-point drop passes only when the floor line names that drop.

        Two points, a missing line, a placeholder, a short reason, and the wrong
        old or new value stay red on a pull request. The same gap on a push is
        a warning and exits 0.

        Side effects:
            Writes temporary ratchet files and reads one Cobertura fixture.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "src"
            source.mkdir()
            write_cs(source / "Clean.cs", "class Clean {}\n")
            baseline = root / "baseline.json"
            one = root / "one.json"
            two = root / "two.json"
            baseline.write_text(ratchet_text(50, 40), encoding="utf-8")
            one.write_text(ratchet_text(49, 40), encoding="utf-8")
            two.write_text(ratchet_text(48, 40), encoding="utf-8")
            summary = root / "summary.md"
            one_args = gate_args(one, baseline, source, summary)
            two_args = gate_args(two, baseline, source, summary)
            match = "coverage-exception: floor ALog lines 50 -> 49: measured drop after the filter\n"
            self.assertEqual(run_gate(one_args, match), 0)
            self.assertEqual(run_gate(two_args, match.replace("49", "48")), 1)
            self.assertEqual(run_gate(one_args, ""), 1)
            self.assertEqual(
                run_gate(one_args, "coverage-exception: SQL branches fell after the filter change.\n"),
                1,
            )
            self.assertEqual(run_gate(one_args, "coverage-exception: <reason>\n"), 1)
            self.assertEqual(run_gate(one_args, "coverage-exception: short\n"), 1)
            wrong = "coverage-exception: floor ALog lines 50 -> 47: measured drop after the filter\n"
            self.assertEqual(run_gate(one_args, wrong), 1)
            code, text = run_gate_text(
                one_args,
                {"COVERAGE_ENFORCE": "false", "GITHUB_EVENT_NAME": "push", "PR_BODY": ""},
            )
            self.assertEqual(code, 0)
            self.assertIn("::warning::", text)
            self.assertIn("no matching coverage-exception floor line", text)
            self.assertIn("Ratchet comparison (reported on push, not a failure):", summary.read_text(encoding="utf-8"))
            high = root / "high.json"
            high.write_text(ratchet_text(90, 80), encoding="utf-8")
            high_args = [
                "--cobertura",
                str(DATA / "cobertura-assembly-name.xml"),
                "--ratchet",
                str(high),
                "--source-root",
                str(source),
                "--summary",
                str(summary),
            ]
            self.assertEqual(run_gate(high_args, ""), 1)
            soft, soft_text = run_gate_text(
                high_args,
                {"COVERAGE_ENFORCE": "false", "GITHUB_EVENT_NAME": "push"},
            )
            self.assertEqual(soft, 0)
            self.assertIn("::warning::ALog lines 60.2 are below floor 90.0.", soft_text)

    def test_luecken_text_alone_does_not_allow_a_two_point_drop(self) -> None:
        """A Lücken-Baseline line does not authorize a two-point drop by itself.

        This call does not set ``COVERAGE_BASE_REF`` and does not remove
        ``CompilerGenerated`` from runsettings. The one-point rule stays in
        force, so the gate exits 1.

        Side effects:
            Writes temporary ratchet files and reads one Cobertura fixture.
        """
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source = root / "src"
            source.mkdir()
            write_cs(source / "Clean.cs", "class Clean {}\n")
            baseline = root / "baseline.json"
            two = root / "two.json"
            baseline.write_text(ratchet_text(50, 40), encoding="utf-8")
            two.write_text(ratchet_text(48, 40), encoding="utf-8")
            summary = root / "summary.md"
            body = (
                "coverage-exception: floor ALog lines 50 -> 48: "
                "Lücken-Baseline (C1-07, CompilerGenerated no longer excluded)\n"
            )
            self.assertEqual(run_gate(gate_args(two, baseline, source, summary), body), 1)

    def test_real_ratchet_file_has_both_packages_and_targets(self) -> None:
        """The committed ratchet parses and names the two shipped packages.

        Floors may change after the CI measurement. Targets stay 90 and 80.

        Side effects:
            Reads eng/coverage-ratchet.json.
        """
        ratchet = GATE.load_ratchet(ROOT / "coverage-ratchet.json")
        self.assertEqual(GATE.target_problems(ratchet), [])
        self.assertIn("ALog", ratchet.packages)
        self.assertIn("ALog.Extensions.Logging", ratchet.packages)
        for name, floor in ratchet.packages.items():
            if floor.lines != floor.lines.to_integral_value():
                raise AssertionError(f"{name} line floor is not a whole percent")
            if floor.branches != floor.branches.to_integral_value():
                raise AssertionError(f"{name} branch floor is not a whole percent")

    def test_more_than_20_packages_is_rejected(self) -> None:
        """The package cap is 20. The 21st package fails before comparison.

        Side effects:
            Writes one temporary JSON file.
        """
        packages = {
            f"P{index}": {"lines": 1.0, "branches": 1.0}
            for index in range(21)
        }
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "too-many.json"
            path.write_text(
                json.dumps({"linesTarget": 90, "branchesTarget": 80, "packages": packages}),
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                GATE.load_ratchet(path)

    def test_cobertura_file_cap(self) -> None:
        """Two explicit files pass a cap of 2 and fail a cap of 1.

        Side effects:
            None beyond reading no directories.
        """
        files = [str(DATA / "cobertura-assembly-name.xml"), str(DATA / "cobertura-dll-suffix.xml")]
        self.assertEqual(len(GATE.collect_cobertura(files, [], max_files=2)), 2)
        with self.assertRaises(ValueError):
            GATE.collect_cobertura(files, [], max_files=1)
        with self.assertRaises(ValueError):
            GATE.collect_cobertura([], [], max_files=1)


def PackageRates(lines: Decimal, branches: Decimal):
    """Build a PackageRates value using the loaded module's type.

    Args:
        lines: Line percent.
        branches: Branch percent.

    Returns:
        A frozen rate pair.

    Raises:
        None.

    Side effects:
        None.
    """
    return GATE.PackageRates(lines, branches)


def both(lines: Decimal, branches: Decimal) -> dict:
    """Build coverage for both required packages at the same percents.

    Args:
        lines: Line percent for each package.
        branches: Branch percent for each package.

    Returns:
        A coverage dictionary with the two shipped names.

    Raises:
        None.

    Side effects:
        None.
    """
    rates = PackageRates(lines, branches)
    return {"ALog": rates, "ALog.Extensions.Logging": rates}


def ratchet_for(coverage: dict) -> object:
    """Build a ratchet whose floors equal the measured rates and whose targets are 90/80.

    Args:
        coverage: Measurements keyed by package name.

    Returns:
        A Ratchet that the floor check accepts.

    Raises:
        None.

    Side effects:
        None.
    """
    packages = {
        name: GATE.PackageFloor(rates.lines, rates.branches)
        for name, rates in coverage.items()
    }
    return GATE.Ratchet(Decimal("90"), Decimal("80"), packages)


def ratchet_text(alog_lines: int, alog_branches: int) -> str:
    """Build a two-package ratchet whose ALog floors are the given percents.

    Args:
        alog_lines: ALog line floor.
        alog_branches: ALog branch floor.

    Returns:
        JSON text. Extensions stays above the Cobertura fixture.

    Raises:
        None.

    Side effects:
        None.
    """
    return json.dumps(
        {
            "linesTarget": 90,
            "branchesTarget": 80,
            "packages": {
                "ALog": {"lines": alog_lines, "branches": alog_branches},
                "ALog.Extensions.Logging": {"lines": 60, "branches": 50},
            },
        }
    )


def gate_args(ratchet: Path, baseline: Path, source: Path, summary: Path) -> list[str]:
    """Build a coverage-gate command for one ratchet pair.

    Args:
        ratchet: Candidate ratchet.
        baseline: Base ratchet.
        source: Directory with no exclusion attribute.
        summary: Job-summary path.

    Returns:
        Arguments for ``GATE.main``.

    Raises:
        None.

    Side effects:
        None.
    """
    return [
        "--cobertura",
        str(DATA / "cobertura-assembly-name.xml"),
        "--ratchet",
        str(ratchet),
        "--baseline",
        str(baseline),
        "--source-root",
        str(source),
        "--summary",
        str(summary),
    ]


def run_gate(args: list[str], body: str) -> int:
    """Run the gate as a pull request and return the exit code.

    Args:
        args: Command arguments.
        body: Pull-request body.

    Returns:
        The process exit code.

    Raises:
        None.

    Side effects:
        Reads the files named in ``args`` and writes the summary.
    """
    code, _text = run_gate_text(
        args,
        {"COVERAGE_ENFORCE": "false", "GITHUB_EVENT_NAME": "pull_request", "PR_BODY": body},
    )
    return code


def run_gate_text(args: list[str], env: dict[str, str]) -> tuple[int, str]:
    """Run the gate and capture stdout.

    Args:
        args: Command arguments.
        env: Environment passed to ``GATE.main``. Replaces the process environment.

    Returns:
        Exit code and stdout text.

    Raises:
        None.

    Side effects:
        Reads inputs and writes the summary file named in ``args``.
    """
    import io
    from contextlib import redirect_stdout

    captured = io.StringIO()
    with redirect_stdout(captured):
        code = GATE.main(args, env)
    return code, captured.getvalue()


def write_cs(path: Path, text: str) -> None:
    """Write a small C# file encoded as UTF-8.

    Args:
        path: Destination. The parent must exist.
        text: File contents.

    Returns:
        None.

    Raises:
        OSError: The file cannot be written.

    Side effects:
        Creates or replaces the file.
    """
    path.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(unittest.main())
