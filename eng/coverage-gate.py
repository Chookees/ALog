#!/usr/bin/env python3
"""Coverage ratchet and exclusion check for the ALog CI gate.

Implements arc_spec 4.1 point 4 and A2a-20. Cobertura package names that end
in ``.dll`` are normalized, line and branch percents are compared with
``eng/coverage-ratchet.json``, and every ``[ExcludeFromCodeCoverage]`` in the
library sources needs ``// Coverage-Ausnahme: <reason>`` on the line above it.
``COVERAGE_ENFORCE=true`` also applies the targets (lines > 90, branches >= 80).
S4 leaves that switch false. Allocation is deliberate: the script runs once per
job and exits. Next file: ``eng/coverage-ratchet.json``.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import Mapping

# Packages the gate always requires. Names are the shipped assemblies.
REQUIRED_PACKAGES = ("ALog", "ALog.Extensions.Logging")
LINE_TARGET = Decimal("90")
BRANCH_TARGET = Decimal("80")
EXCLUSION_ATTRIBUTE = "ExcludeFromCodeCoverage"
EXEMPTION_COMMENT = re.compile(r"^\s*//\s*Coverage-Ausnahme:\s*\S.*$")


@dataclass(frozen=True)
class PackageRates:
    """Line and branch coverage for one package, in percent with one decimal."""

    lines: Decimal
    branches: Decimal


@dataclass(frozen=True)
class PackageFloor:
    """Minimum line and branch percent a later pull request must keep."""

    lines: Decimal
    branches: Decimal


@dataclass(frozen=True)
class Ratchet:
    """Targets plus the per-package floors loaded from a ratchet file."""

    lines_target: Decimal
    branches_target: Decimal
    packages: dict[str, PackageFloor]


@dataclass(frozen=True)
class ScanLimits:
    """Caps for the source walk. Every value is an inclusive upper bound except depth."""

    max_files: int = 5000
    max_lines: int = 20000
    max_depth: int = 12
    max_directories: int = 2000
    max_children: int = 500


@dataclass(frozen=True)
class GateResult:
    """Text for the job summary and the problems that make the gate red."""

    summary: str
    problems: list[str]


def normalize_package_name(name: str) -> str:
    """Drop one trailing ``.dll`` so both Cobertura producers share a package name.

    Coverlet writes the assembly name. Microsoft Code Coverage (from v3/MTP)
    writes the file name, including ``.dll``. The ratchet uses the bare name.

    Args:
        name: Package name from XML or from the ratchet. Empty is rejected.

    Returns:
        The stripped name. A different suffix, including ``.DLL``, is kept.

    Raises:
        ValueError: The name is empty or only the suffix.

    Side effects:
        None.
    """
    if not isinstance(name, str) or name.strip() == "":
        raise ValueError("Package name is empty.")
    cleaned = name.strip()
    if cleaned.endswith(".dll"):
        cleaned = cleaned[: -len(".dll")]
    if cleaned == "" or "|" in cleaned or "\n" in cleaned:
        raise ValueError(f"Package name {name!r} is not usable in the summary.")
    return cleaned


def round_percent(rate: Decimal) -> Decimal:
    """Convert a 0..1 Cobertura rate to a percent with one decimal place.

    Args:
        rate: ``line-rate`` or ``branch-rate``. Values outside 0..1 are rejected.

    Returns:
        ``rate * 100`` rounded half up to 0.1. The same rounding is used for floors.

    Raises:
        ValueError: ``rate`` is not a finite number from 0 to 1.

    Side effects:
        None.
    """
    if not isinstance(rate, Decimal) or rate.is_nan() or rate < 0 or rate > 1:
        raise ValueError(f"Coverage rate {rate} is outside 0..1.")
    return (rate * Decimal(100)).quantize(Decimal("0.1"), rounding=ROUND_HALF_UP)


def as_percent(value: object, label: str) -> Decimal:
    """Read a 0..100 percent from JSON, rejecting booleans and non-numbers.

    Args:
        value: A JSON integer or decimal. Floats are not produced by the loader.
        label: Field name used in the error so the bad file is identifiable.

    Returns:
        The value quantized half up to one decimal.

    Raises:
        ValueError: The value is missing, not a number, or outside 0..100.

    Side effects:
        None.
    """
    if isinstance(value, bool) or not isinstance(value, (int, Decimal)):
        raise ValueError(f"{label} must be a number.")
    number = value if isinstance(value, Decimal) else Decimal(value)
    if number.is_nan() or number < 0 or number > 100:
        raise ValueError(f"{label} must be from 0 to 100.")
    return number.quantize(Decimal("0.1"), rounding=ROUND_HALF_UP)


def decimal_attr(element: ElementTree.Element, attr: str, path: Path) -> Decimal:
    """Read a Cobertura rate attribute as a Decimal.

    Args:
        element: A ``package`` element.
        attr: ``line-rate`` or ``branch-rate``.
        path: File path included in the error.

    Returns:
        The attribute as a Decimal, not yet scaled to a percent.

    Raises:
        ValueError: The attribute is missing or not a finite decimal.

    Side effects:
        None.
    """
    raw = element.get(attr)
    if raw is None or raw.strip() == "":
        raise ValueError(f"{path} is missing {attr}.")
    try:
        value = Decimal(raw)
    except InvalidOperation as error:
        raise ValueError(f"{path} has {attr}={raw}.") from error
    if value.is_nan():
        raise ValueError(f"{path} has a non-numeric {attr}.")
    return value


def local_name(tag: str) -> str:
    """Return an XML tag without its namespace.

    Args:
        tag: Element tag, possibly ``{namespace}name``.

    Returns:
        The local name. A tag without a namespace is returned unchanged.

    Raises:
        ValueError: The tag is empty.

    Side effects:
        None.
    """
    if tag == "":
        raise ValueError("XML tag is empty.")
    if "}" in tag:
        return tag.split("}", 1)[1]
    return tag


def load_ratchet(path: Path) -> Ratchet:
    """Load floors and targets from a ratchet JSON file.

    Unknown keys are ignored so a human-readable ``note`` can live in the file.
    At most 20 packages are accepted; the shipped set is two.

    Args:
        path: Path to ``coverage-ratchet.json`` or a fixture.

    Returns:
        Targets and normalized package floors.

    Raises:
        ValueError: The file is missing, not an object, or a number is out of range.

    Side effects:
        Reads the file.
    """
    if not path.is_file():
        raise ValueError(f"Ratchet file {path} does not exist.")
    try:
        data = json.loads(path.read_text(encoding="utf-8"), parse_float=Decimal)
    except json.JSONDecodeError as error:
        raise ValueError(f"Ratchet file {path} is not JSON: {error}") from error
    if not isinstance(data, dict):
        raise ValueError(f"Ratchet file {path} must be an object.")
    packages = data.get("packages")
    if not isinstance(packages, dict):
        raise ValueError(f"{path} packages must be an object.")
    if len(packages) > 20:
        raise ValueError(f"{path} has more than 20 packages.")
    parsed: dict[str, PackageFloor] = {}
    for name, body in packages.items():
        normalized = normalize_package_name(str(name))
        if normalized in parsed:
            raise ValueError(f"{path} repeats {normalized}.")
        if not isinstance(body, dict):
            raise ValueError(f"{path} package {name} must be an object.")
        parsed[normalized] = PackageFloor(
            lines=as_percent(body.get("lines"), f"{path} {name} lines"),
            branches=as_percent(body.get("branches"), f"{path} {name} branches"),
        )
    return Ratchet(
        lines_target=as_percent(data.get("linesTarget"), f"{path} linesTarget"),
        branches_target=as_percent(data.get("branchesTarget"), f"{path} branchesTarget"),
        packages=parsed,
    )


def read_cobertura_file(path: Path) -> dict[str, PackageRates]:
    """Read per-package line-rate and branch-rate from one Cobertura file.

    Args:
        path: A Cobertura XML file. Child line elements are not required.

    Returns:
        Normalized package name to rounded percents. Duplicate names must agree.

    Raises:
        ValueError: The file is missing, not XML, or a rate is unusable.
            More than 20 packages is rejected.

    Side effects:
        Reads and parses the file.
    """
    if not path.is_file():
        raise ValueError(f"Cobertura file {path} does not exist.")
    try:
        root = ElementTree.parse(path).getroot()
    except ElementTree.ParseError as error:
        raise ValueError(f"Cobertura file {path} is not XML: {error}") from error
    packages = None
    for child in root:
        if local_name(child.tag) == "packages":
            packages = child
            break
    if packages is None:
        raise ValueError(f"{path} has no packages element.")
    found: dict[str, PackageRates] = {}
    count = 0
    for element in packages:
        if local_name(element.tag) != "package":
            continue
        count += 1
        if count > 20:
            raise ValueError(f"{path} has more than 20 packages.")
        name = normalize_package_name(element.get("name") or "")
        rates = PackageRates(
            lines=round_percent(decimal_attr(element, "line-rate", path)),
            branches=round_percent(decimal_attr(element, "branch-rate", path)),
        )
        if name in found and found[name] != rates:
            raise ValueError(f"{path} has conflicting rates for {name}.")
        found[name] = rates
    if not found:
        raise ValueError(f"{path} has no package rates.")
    return found


def load_cobertura(paths: list[Path]) -> dict[str, PackageRates]:
    """Merge package rates from Cobertura files.

    Args:
        paths: Files to read, in order. One to 50 files.

    Returns:
        Normalized rates. When two files report the same package, the percents must match.

    Raises:
        ValueError: The list is empty, too long, or two files disagree.

    Side effects:
        Reads each file.
    """
    if not paths:
        raise ValueError("No Cobertura files were provided.")
    if len(paths) > 50:
        raise ValueError("Refusing more than 50 Cobertura files.")
    merged: dict[str, PackageRates] = {}
    for path in paths:
        for name, rates in read_cobertura_file(path).items():
            if name in merged and merged[name] != rates:
                raise ValueError(f"Conflicting rates for {name} in {path}.")
            merged[name] = rates
    return merged


def keep_child_directories(names: list[str], skipped: tuple[str, ...], max_children: int) -> list[str]:
    """Copy directory names that should be walked, skipping generated folders.

    Args:
        names: Child directory names from ``os.walk``.
        skipped: Names to drop, such as ``bin`` and ``obj``.
        max_children: Inclusive cap on names that are kept. Must be 1..500.

    Returns:
        The kept names, in the original order.

    Raises:
        ValueError: The cap is outside 1..500 or too many children remain.

    Side effects:
        None. Callers assign the result back onto the walk list to stop descent.
    """
    if max_children < 1 or max_children > 500:
        raise ValueError("Directory child cap must be from 1 to 500.")
    kept: list[str] = []
    for name in names:
        if name in skipped:
            continue
        if len(kept) >= max_children:
            raise ValueError(f"Directory has more than {max_children} subdirectories.")
        kept.append(name)
    return kept


def collect_cobertura(files: list[str], directories: list[str], max_files: int = 50) -> list[Path]:
    """Collect explicit Cobertura paths and ``coverage.cobertura.xml`` under directories.

    Args:
        files: Paths passed as ``--cobertura``. They are not renamed.
        directories: Roots passed as ``--cobertura-dir``.
        max_files: Inclusive cap, from 1 to 50.

    Returns:
        The collected paths. Explicit files come first.

    Raises:
        ValueError: Nothing was found, a directory is missing, or a cap is hit.
            The walk does not follow symlinks, so a link cycle cannot spin.

    Side effects:
        Reads directory entries only.
    """
    if max_files < 1 or max_files > 50:
        raise ValueError("Cobertura file cap must be from 1 to 50.")
    found: list[Path] = []
    for raw in files:
        found.append(Path(raw))
        if len(found) > max_files:
            raise ValueError(f"More than {max_files} Cobertura files.")
    seen_dirs = 0
    for raw_dir in directories:
        root = Path(raw_dir)
        if not root.is_dir():
            raise ValueError(f"Cobertura directory {root} does not exist.")
        for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
            seen_dirs += 1
            if seen_dirs > 2000:
                raise ValueError("Cobertura search exceeded 2000 directories.")
            dirnames[:] = keep_child_directories(dirnames, ("bin", "obj"), 500)
            for name in filenames:
                if name != "coverage.cobertura.xml":
                    continue
                found.append(Path(dirpath) / name)
                if len(found) > max_files:
                    raise ValueError(f"More than {max_files} Cobertura files.")
    if not found:
        raise ValueError("No Cobertura files were found.")
    return found


def validate_scan_limits(limits: ScanLimits) -> None:
    """Reject scan caps outside the ranges the walk is built for.

    Args:
        limits: Caps for one source scan.

    Returns:
        None.

    Raises:
        ValueError: A cap is below 1 or above its hard maximum. Depth may be 0.

    Side effects:
        None.
    """
    if limits.max_files < 1 or limits.max_files > 5000:
        raise ValueError("Source file cap must be from 1 to 5000.")
    if limits.max_lines < 1 or limits.max_lines > 20000:
        raise ValueError("Source line cap must be from 1 to 20000.")
    if limits.max_depth < 0 or limits.max_depth > 12:
        raise ValueError("Source depth cap must be from 0 to 12.")
    if limits.max_directories < 1 or limits.max_directories > 2000:
        raise ValueError("Source directory cap must be from 1 to 2000.")
    if limits.max_children < 1 or limits.max_children > 500:
        raise ValueError("Source child cap must be from 1 to 500.")


def is_comment_line(line: str) -> bool:
    """Tell a comment from code so a mentioned attribute is not treated as one.

    Args:
        line: One source line without its newline.

    Returns:
        True for ``//``, ``/*``, and ``*`` lines.

    Raises:
        None.

    Side effects:
        None.
    """
    stripped = line.strip()
    return stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("/*")


def is_exclusion_attribute(line: str) -> bool:
    """Detect an ``[ExcludeFromCodeCoverage]`` attribute on its own line.

    Args:
        line: One source line. A mention inside a comment or a string that does
            not start with ``[`` is ignored.

    Returns:
        True when the trimmed line is an attribute using that name.

    Raises:
        None.

    Side effects:
        None.
    """
    if is_comment_line(line):
        return False
    stripped = line.strip()
    return stripped.startswith("[") and EXCLUSION_ATTRIBUTE in stripped


def scan_source_file(path: Path, max_lines: int) -> list[str]:
    """Find exclusion attributes whose previous line is not the required comment.

    Args:
        path: A ``.cs`` file.
        max_lines: Inclusive line cap. A longer file fails closed instead of
            skipping an attribute past the cap.

    Returns:
        Problem strings. An empty list means this file is clean.

    Raises:
        OSError: The file cannot be read.

    Side effects:
        Reads the file.
    """
    lines = path.read_text(encoding="utf-8").splitlines()
    if len(lines) > max_lines:
        return [f"{path} has {len(lines)} lines, above the scan cap of {max_lines}."]
    problems: list[str] = []
    for index in range(len(lines)):
        if not is_exclusion_attribute(lines[index]):
            continue
        previous = lines[index - 1] if index > 0 else ""
        if index == 0 or EXEMPTION_COMMENT.match(previous) is None:
            problems.append(
                f"{path}:{index + 1}: [ExcludeFromCodeCoverage] needs "
                "// Coverage-Ausnahme: <reason> on the line directly above."
            )
    return problems


def scan_exemptions(roots: list[Path], limits: ScanLimits | None = None) -> list[str]:
    """Walk library sources for A2a-20 exclusion comments.

    Args:
        roots: Directories such as ``ALog`` and ``ALog.Extensions.Logging``.
        limits: Caps. ``None`` uses the production caps.

    Returns:
        Problem strings for missing comments, over-deep trees, and cap violations
        that are reported rather than raised. Cap arguments outside their range raise.

    Raises:
        ValueError: A root is not a directory, a cap is illegal, or a walk cap trips.
            Symlinks are not followed.

    Side effects:
        Reads ``.cs`` files under the roots. ``bin`` and ``obj`` are not entered.
    """
    active = limits if limits is not None else ScanLimits()
    validate_scan_limits(active)
    problems: list[str] = []
    seen_files = 0
    seen_dirs = 0
    for root in roots:
        if not root.is_dir():
            raise ValueError(f"Source root {root} is not a directory.")
        for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
            seen_dirs += 1
            if seen_dirs > active.max_directories:
                raise ValueError(f"Source scan exceeded {active.max_directories} directories.")
            relative = Path(dirpath).relative_to(root)
            depth = 0 if relative == Path(".") else len(relative.parts)
            if depth > active.max_depth:
                problems.append(f"{dirpath} is deeper than {active.max_depth}.")
                dirnames.clear()
                continue
            dirnames[:] = keep_child_directories(dirnames, ("bin", "obj"), active.max_children)
            for name in sorted(filenames):
                if not name.endswith(".cs"):
                    continue
                seen_files += 1
                if seen_files > active.max_files:
                    raise ValueError(f"Source scan exceeded {active.max_files} C# files.")
                problems.extend(scan_source_file(Path(dirpath) / name, active.max_lines))
    return problems


def target_problems(ratchet: Ratchet) -> list[str]:
    """Require the documented targets of 90 lines and 80 branches.

    Args:
        ratchet: A loaded ratchet file.

    Returns:
        One string per target that is not the required number.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    if ratchet.lines_target != LINE_TARGET:
        problems.append(f"Line target must be 90, found {ratchet.lines_target}.")
    if ratchet.branches_target != BRANCH_TARGET:
        problems.append(f"Branch target must be 80, found {ratchet.branches_target}.")
    return problems


def floor_problems(coverage: dict[str, PackageRates], ratchet: Ratchet) -> list[str]:
    """Compare measured percents with the ratchet floors.

    Args:
        coverage: Normalized measurements.
        ratchet: Floors. Both required packages must be present on each side.

    Returns:
        One string per missing package or percent below its floor. Equal to the floor passes.

    Raises:
        None.

    Side effects:
        None.
    """
    problems: list[str] = []
    for name in REQUIRED_PACKAGES:
        if name not in ratchet.packages:
            problems.append(f"Ratchet is missing required package {name}.")
    names = list(REQUIRED_PACKAGES)
    for name in sorted(set(coverage) | set(ratchet.packages)):
        if name not in names:
            names.append(name)
    for name in names:
        in_coverage = name in coverage
        in_ratchet = name in ratchet.packages
        if not in_coverage and not in_ratchet:
            continue
        if not in_coverage:
            problems.append(f"{name} is in the ratchet but missing from coverage.")
            continue
        if not in_ratchet:
            problems.append(f"{name} is in coverage but missing from the ratchet.")
            continue
        measured = coverage[name]
        floor = ratchet.packages[name]
        if measured.lines < floor.lines:
            problems.append(f"{name} lines {measured.lines} are below floor {floor.lines}.")
        if measured.branches < floor.branches:
            problems.append(
                f"{name} branches {measured.branches} are below floor {floor.branches}."
            )
    return problems


def enforce_problems(
    coverage: dict[str, PackageRates], ratchet: Ratchet, enforce: bool
) -> list[str]:
    """Apply the 90/80 target gate when enforcement is on.

    Args:
        coverage: Normalized measurements.
        ratchet: Provides the target numbers, which must be 90 and 80.
        enforce: True only when ``COVERAGE_ENFORCE`` is exactly ``true``.

    Returns:
        An empty list when enforcement is off. Otherwise one string per package
        whose lines are not strictly above the line target or whose branches
        are below the branch target.

    Raises:
        None.

    Side effects:
        None.
    """
    if not enforce:
        return []
    problems: list[str] = []
    for name in REQUIRED_PACKAGES:
        measured = coverage.get(name)
        if measured is None:
            continue
        if measured.lines <= ratchet.lines_target:
            problems.append(f"{name} lines {measured.lines} are not above {ratchet.lines_target}.")
        if measured.branches < ratchet.branches_target:
            problems.append(
                f"{name} branches {measured.branches} are below {ratchet.branches_target}."
            )
    return problems


def lowering_problems(baseline: Ratchet | None, candidate: Ratchet) -> list[str]:
    """Reject a ratchet that drops a package or lowers a floor from the baseline.

    Args:
        baseline: The file on ``origin/master``. ``None`` means the file is new.
        candidate: The file in this pull request.

    Returns:
        Problem strings. Raising a floor, or introducing the file, is allowed.

    Raises:
        None.

    Side effects:
        None.
    """
    if baseline is None:
        return []
    problems: list[str] = []
    for name, old in sorted(baseline.packages.items()):
        if name not in candidate.packages:
            problems.append(f"Ratchet removed the floor for {name}.")
            continue
        new = candidate.packages[name]
        if new.lines < old.lines:
            problems.append(f"Ratchet lowered {name} lines from {old.lines} to {new.lines}.")
        if new.branches < old.branches:
            problems.append(
                f"Ratchet lowered {name} branches from {old.branches} to {new.branches}."
            )
    return problems


def enforce_from_env(env: Mapping[str, str]) -> bool:
    """Read ``COVERAGE_ENFORCE``. Only the exact strings ``true`` and ``false`` are valid.

    Args:
        env: Process environment. A missing variable means ``false`` (S4 default).

    Returns:
        True when the variable is ``true``.

    Raises:
        ValueError: The variable is set to anything else, including ``TRUE``.

    Side effects:
        None.
    """
    value = env.get("COVERAGE_ENFORCE", "false")
    if value == "true":
        return True
    if value == "false":
        return False
    raise ValueError("COVERAGE_ENFORCE must be true or false.")


def format_percent(value: Decimal) -> str:
    """Format a percent with one decimal place.

    Args:
        value: A percent. It is quantized half up to 0.1 before formatting so
            ``90`` and ``90.0`` render the same way.

    Returns:
        Text such as ``60.2`` or ``90.0``.

    Raises:
        None.

    Side effects:
        None.
    """
    quantized = value.quantize(Decimal("0.1"), rounding=ROUND_HALF_UP)
    return format(quantized, "f")


def coverage_rows(
    coverage: dict[str, PackageRates],
    ratchet: Ratchet,
) -> tuple[list[str], list[str]]:
    """Build one markdown row and one greppable line per package.

    Args:
        coverage: Measurements. A missing package is shown as ``missing``.
        ratchet: Floors. A package with no floor is shown as ``missing``.

    Returns:
        Table rows, then ``COVERAGE package=...`` lines for packages that have both.

    Raises:
        None.

    Side effects:
        None.
    """
    names = list(REQUIRED_PACKAGES)
    for name in sorted(set(coverage) | set(ratchet.packages)):
        if name not in names:
            names.append(name)
    rows: list[str] = []
    machine: list[str] = []
    for name in names:
        measured = coverage.get(name)
        floor = ratchet.packages.get(name)
        lines_text = format_percent(measured.lines) if measured else "missing"
        branches_text = format_percent(measured.branches) if measured else "missing"
        floor_lines = format_percent(floor.lines) if floor else "missing"
        floor_branches = format_percent(floor.branches) if floor else "missing"
        rows.append(
            f"| {name} | {lines_text} | {branches_text} | {floor_lines} | {floor_branches} |"
        )
        if measured is not None and floor is not None:
            machine.append(
                f"COVERAGE package={name} lines={lines_text} branches={branches_text} "
                f"floor_lines={floor_lines} floor_branches={floor_branches}"
            )
    return rows, machine


def target_gap_warnings(
    coverage: dict[str, PackageRates],
    ratchet: Ratchet,
    enforce: bool,
) -> list[str]:
    """Describe how far each package sits from the 90/80 targets.

    Args:
        coverage: Measurements. A missing package is skipped.
        ratchet: Targets. The line target is strict greater-than.
        enforce: When true the caller already fails, so no warning is added.

    Returns:
        Warning lines. Empty when the target gate is on or every package clears it.

    Raises:
        None.

    Side effects:
        None.
    """
    if enforce:
        return []
    warnings: list[str] = []
    for name in REQUIRED_PACKAGES:
        measured = coverage.get(name)
        if measured is None:
            continue
        if measured.lines <= ratchet.lines_target:
            gap = ratchet.lines_target - measured.lines
            warnings.append(
                f"Warning: {name} lines {format_percent(measured.lines)} are short of "
                f"> {format_percent(ratchet.lines_target)} by {format_percent(gap)}. "
                "COVERAGE_ENFORCE is false, so this is not a failure."
            )
        if measured.branches < ratchet.branches_target:
            gap = ratchet.branches_target - measured.branches
            warnings.append(
                f"Warning: {name} branches {format_percent(measured.branches)} are short of "
                f">= {format_percent(ratchet.branches_target)} by {format_percent(gap)}. "
                "COVERAGE_ENFORCE is false, so this is not a failure."
            )
    return warnings


def format_summary(
    coverage: dict[str, PackageRates],
    ratchet: Ratchet,
    enforce: bool,
    problems: list[str],
) -> str:
    """Build the job-summary text, including one machine-readable line per package.

    Args:
        coverage: Measurements. Missing packages are shown as ``missing``.
        ratchet: Floors and targets.
        enforce: Whether the target gate was applied.
        problems: Failures already computed. They are listed under the table.

    Returns:
        Markdown ending in a newline. ``COVERAGE package=...`` lines are greppable.

    Raises:
        None.

    Side effects:
        None.
    """
    rows, machine = coverage_rows(coverage, ratchet)
    lines = [
        "## Coverage",
        "",
        "| Package | Lines % | Branches % | Line floor | Branch floor |",
        "|---|---:|---:|---:|---:|",
    ]
    lines.extend(rows)
    lines.append("")
    lines.extend(machine)
    lines.append("")
    switch = "true" if enforce else "false"
    lines.append(
        f"Line target > {format_percent(ratchet.lines_target)}. "
        f"Branch target >= {format_percent(ratchet.branches_target)}. "
        f"COVERAGE_ENFORCE={switch}."
    )
    warnings = target_gap_warnings(coverage, ratchet, enforce)
    if warnings:
        lines.append("")
        lines.extend(warnings)
    if problems:
        lines.append("")
        lines.append("Coverage gate failed:")
        for problem in problems:
            lines.append(f"- {problem}")
    return "\n".join(lines) + "\n"


def resolve_source_roots(values: list[str] | None) -> list[Path]:
    """Choose the source directories to scan for exclusion attributes.

    Args:
        values: ``--source-root`` values. ``None`` or empty uses the two libraries.

    Returns:
        One path per root. At most 10 roots.

    Raises:
        ValueError: More than 10 roots were passed.

    Side effects:
        None.
    """
    if not values:
        return [Path("ALog"), Path("ALog.Extensions.Logging")]
    if len(values) > 10:
        raise ValueError("At most 10 source roots are accepted.")
    return [Path(value) for value in values]


def evaluate_gate(
    coverage: dict[str, PackageRates],
    ratchet: Ratchet,
    baseline: Ratchet | None,
    enforce: bool,
    source_roots: list[Path],
) -> GateResult:
    """Run every S4 coverage check and format the summary.

    Args:
        coverage: Measured percents.
        ratchet: This pull request's ratchet.
        baseline: Master's ratchet, or ``None`` when the file is new.
        enforce: Target gate switch.
        source_roots: Library directories for the exclusion comment check.

    Returns:
        Summary text and the combined problem list. The summary is produced even
        when the list is not empty, so a red job still shows the numbers.

    Raises:
        ValueError: The source walk hits a cap or a root is missing.

    Side effects:
        Reads the source files.
    """
    problems: list[str] = []
    problems.extend(target_problems(ratchet))
    if baseline is not None:
        problems.extend(target_problems(baseline))
    problems.extend(floor_problems(coverage, ratchet))
    problems.extend(enforce_problems(coverage, ratchet, enforce))
    problems.extend(lowering_problems(baseline, ratchet))
    problems.extend(scan_exemptions(source_roots))
    summary = format_summary(coverage, ratchet, enforce, problems)
    return GateResult(summary=summary, problems=problems)


def write_summary(path: str | None, text: str) -> None:
    """Append text to the GitHub step summary when a path was given.

    Args:
        path: ``GITHUB_STEP_SUMMARY``. ``None`` or empty skips the write.
        text: Summary already ending in a newline when produced by this script.

    Returns:
        None.

    Raises:
        ValueError: The parent directory does not exist.
        OSError: The file cannot be written.

    Side effects:
        Appends to the summary file. Existing text from earlier steps is kept.
    """
    if path is None or path == "":
        return
    summary = Path(path)
    parent = summary.parent
    if parent != Path("") and not parent.exists():
        raise ValueError(f"Summary directory {parent} does not exist.")
    with summary.open("a", encoding="utf-8") as handle:
        written = handle.write(text)
        if written != len(text):
            raise OSError(f"Wrote {written} of {len(text)} summary characters.")
        if not text.endswith("\n"):
            newline = handle.write("\n")
            if newline != 1:
                raise OSError("Could not finish the summary with a newline.")


def parse_args(argv: list[str]) -> argparse.Namespace:
    """Parse the coverage-gate command line.

    Args:
        argv: Arguments without the program name.

    Returns:
        Parsed flags. ``--cobertura`` and ``--cobertura-dir`` may be repeated.

    Raises:
        SystemExit: argparse rejects the command line.

    Side effects:
        None.
    """
    parser = argparse.ArgumentParser(prog="coverage-gate.py")
    parser.add_argument("--cobertura", action="append", default=[])
    parser.add_argument("--cobertura-dir", action="append", default=[])
    parser.add_argument("--ratchet", required=True)
    parser.add_argument("--baseline", default="")
    parser.add_argument("--source-root", action="append", default=None)
    parser.add_argument("--summary", default="")
    return parser.parse_args(argv)


def run_gate(args: argparse.Namespace, env: Mapping[str, str]) -> GateResult:
    """Load inputs and evaluate the gate.

    Args:
        args: Parsed command line.
        env: Environment used for ``COVERAGE_ENFORCE``.

    Returns:
        The gate result. Callers write the summary and choose the exit code.

    Raises:
        ValueError: An input is missing or inconsistent.

    Side effects:
        Reads Cobertura, ratchet, optional baseline, and source files.
    """
    paths = collect_cobertura(list(args.cobertura), list(args.cobertura_dir))
    coverage = load_cobertura(paths)
    ratchet = load_ratchet(Path(args.ratchet))
    baseline = None
    if args.baseline:
        baseline = load_ratchet(Path(args.baseline))
    return evaluate_gate(
        coverage,
        ratchet,
        baseline,
        enforce_from_env(env),
        resolve_source_roots(args.source_root),
    )


def main(argv: list[str], env: Mapping[str, str] | None = None) -> int:
    """Run the gate and return 0, 1, or 2.

    Args:
        argv: Arguments without the program name.
        env: Environment. ``None`` uses ``os.environ``.

    Returns:
        0 when every check passed, 1 when a floor, target, ratchet, or comment
        check failed, and 2 when an input could not be read.

    Raises:
        SystemExit: The command line is invalid.

    Side effects:
        Writes the summary to stdout and, when requested, to the summary file.
        Problems also go to stderr.
    """
    args = parse_args(argv)
    environment = os.environ if env is None else env
    try:
        result = run_gate(args, environment)
    except (ValueError, OSError) as error:
        message = f"Coverage gate could not read its inputs: {error}\n"
        print(message, file=sys.stderr, end="")
        write_summary(args.summary or None, message)
        return 2
    print(result.summary, end="")
    write_summary(args.summary or None, result.summary)
    if result.problems:
        for problem in result.problems:
            print(problem, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
