#!/usr/bin/env python3
"""Render one line-coverage SVG per shipped ALog package.

Implements arc_spec B1-02 and uiux_spec §4b. The percent and the line floor
come from the coverage gate's measurement file, which is the same SQL run
that checks the floors. Green starts at the package floor, yellow reaches
down to 10 points below that floor, and every lower percent is red. ALog's
floor is 95 and ALog.Extensions.Logging's floor is 90 in the current ratchet.
Allocation is deliberate: one short SVG per package per CI job. Next file:
``eng/publish-coverage-badges.py``.
"""

from __future__ import annotations

import argparse
import json
import sys
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
from pathlib import Path
from typing import Mapping

# Shields flat-badge colours. Green, yellow, and red are the only fills used.
_FILLS = {
    "green": "#4c1",
    "yellow": "#dfb317",
    "red": "#e05d44",
}
# Yellow includes the percent that is exactly this far below the floor.
_YELLOW_SPAN = Decimal("10")
# File names are part of the public README URLs. Do not rename them.
PACKAGE_SVGS = (
    ("ALog", "coverage-ALog.svg"),
    ("ALog.Extensions.Logging", "coverage-ALog.Extensions.Logging.svg"),
)
_MAX_DOCUMENT_CHARS = 4000
_MAX_PACKAGES = 20


def _quantize_percent(value: Decimal) -> Decimal:
    """Round a percent half up to one decimal, rejecting values outside 0..100.

    Args:
        value: A finite Decimal. Booleans and other types are rejected.

    Returns:
        The value quantized to 0.1. ``95`` and ``95.0`` become the same number.

    Raises:
        ValueError: ``value`` is not a finite Decimal from 0 to 100.

    Side effects:
        None.
    """
    if isinstance(value, bool) or not isinstance(value, Decimal):
        raise ValueError("percent must be a Decimal")
    if value.is_nan() or value.is_infinite() or value < 0 or value > 100:
        raise ValueError("percent must be from 0 to 100")
    return value.quantize(Decimal("0.1"), rounding=ROUND_HALF_UP)


def colour_for_lines(lines: Decimal, floor: Decimal) -> str:
    """Choose green, yellow, or red for a line percent against its floor.

    Args:
        lines: Measured line coverage, from 0 to 100. Extra precision is
            rounded half up to one decimal before the comparison.
        floor: Package line floor, from 0 to 100, rounded the same way.
            The current floors are 95 for ALog and 90 for Extensions.

    Returns:
        ``green`` when the rounded percent is at or above the floor,
        ``yellow`` when it is at least 10 points below the floor and still
        under the floor, and ``red`` otherwise.

    Raises:
        ValueError: Either percent is missing or outside 0..100.

    Side effects:
        None.
    """
    measured = _quantize_percent(lines)
    minimum = _quantize_percent(floor)
    if measured >= minimum:
        return "green"
    if measured >= minimum - _YELLOW_SPAN:
        return "yellow"
    return "red"


def _coverage_text(lines: Decimal) -> str:
    """Format a line percent the way the badge draws it.

    Args:
        lines: Measured line coverage, from 0 to 100.

    Returns:
        Text such as ``95.0%``. The number has one decimal place.

    Raises:
        ValueError: ``lines`` is outside 0..100.

    Side effects:
        None.
    """
    text = format(_quantize_percent(lines), "f") + "%"
    if any(char in text for char in "<>&\"'"):
        raise ValueError("coverage text is not safe for SVG")
    return text


def _text_width(text: str) -> int:
    """Estimate the pixel width of one badge label.

    Args:
        text: A short label or percent. Empty and over-long text is rejected.

    Returns:
        Width in pixels, including padding. At most 200.

    Raises:
        ValueError: The text is empty, longer than 16 characters, or wider
            than 200 pixels.

    Side effects:
        None.
    """
    if text == "" or len(text) > 16:
        raise ValueError("badge text must be 1 to 16 characters")
    width = 12
    for _character in text:
        width += 7
        if width > 200:
            raise ValueError("badge text is wider than 200 pixels")
    return width


def _svg_document(label: str, value: str, fill: str, label_width: int, value_width: int) -> str:
    """Build one shields-style SVG document.

    Args:
        label: Left-hand caption. Must already be free of XML markup.
        value: Right-hand percent, including the percent sign.
        fill: Colour for the value rectangle. One of the three known fills.
        label_width: Width of the left rectangle, in pixels.
        value_width: Width of the right rectangle, in pixels.

    Returns:
        An SVG document ending with a newline.

    Raises:
        ValueError: The badge would be wider than 400 pixels.

    Side effects:
        None.
    """
    total = label_width + value_width
    if total > 400 or label_width < 1 or value_width < 1:
        raise ValueError("coverage badge dimensions are not usable")
    label_x = label_width // 2
    value_x = label_width + (value_width // 2)
    title = f"Coverage: {value}"
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{total}" height="20" '
        f'role="img" aria-label="{title}">\n'
        f"<title>{title}</title>\n"
        '<linearGradient id="smooth" x2="0" y1="100%">\n'
        '<stop offset="0" stop-color="#bbb" stop-opacity=".1"/>\n'
        '<stop offset="1" stop-opacity=".1"/>\n'
        "</linearGradient>\n"
        f'<clipPath id="round"><rect width="{total}" height="20" rx="3" fill="#fff"/></clipPath>\n'
        '<g clip-path="url(#round)">\n'
        f'<rect width="{label_width}" height="20" fill="#555"/>\n'
        f'<rect x="{label_width}" width="{value_width}" height="20" fill="{fill}"/>\n'
        f'<rect width="{total}" height="20" fill="url(#smooth)"/>\n'
        "</g>\n"
        '<g fill="#fff" text-anchor="middle" '
        'font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">\n'
        f'<text x="{label_x}" y="14">{label}</text>\n'
        f'<text x="{value_x}" y="14">{value}</text>\n'
        "</g>\n"
        "</svg>\n"
    )


def render_coverage_svg(lines: Decimal, floor: Decimal) -> str:
    """Render the line-coverage badge for one package.

    Args:
        lines: Measured line coverage, from 0 to 100.
        floor: Package line floor, from 0 to 100. Colour is chosen from this
            floor, not from a fixed number inside the SVG.

    Returns:
        An SVG document whose visible percent matches ``lines`` and whose
        value colour matches ``colour_for_lines``. The document ends with a
        newline so a later byte compare stays stable.

    Raises:
        ValueError: A percent is outside 0..100 or the colour is unknown.

    Side effects:
        None.
    """
    colour = colour_for_lines(lines, floor)
    fill = _FILLS.get(colour)
    if fill is None:
        raise ValueError(f"unknown coverage colour {colour}")
    value = _coverage_text(lines)
    return _svg_document("coverage", value, fill, _text_width("coverage"), _text_width(value))


def _percent_field(value: object, package: str, field: str) -> Decimal:
    """Read one percent field from a measurement object.

    Args:
        value: A JSON string, integer, or Decimal. Booleans are rejected.
        package: Package name, used only in the error.
        field: ``lines`` or ``lineFloor``, used only in the error.

    Returns:
        The percent quantized to one decimal.

    Raises:
        ValueError: The field is missing or not a percent from 0 to 100.

    Side effects:
        None.
    """
    if isinstance(value, bool) or value is None:
        raise ValueError(f"{package} {field} is missing")
    if isinstance(value, str):
        try:
            number = Decimal(value)
        except InvalidOperation as error:
            raise ValueError(f"{package} {field} is not a percent") from error
    elif isinstance(value, Decimal):
        number = value
    elif isinstance(value, int):
        number = Decimal(value)
    else:
        raise ValueError(f"{package} {field} is not a percent")
    return _quantize_percent(number)


def load_measurement(document: str) -> dict[str, tuple[Decimal, Decimal]]:
    """Read line percents and line floors for both shipped packages.

    Args:
        document: JSON text written by the coverage gate. It must contain
            ``packages.<name>.lines`` and ``packages.<name>.lineFloor`` for
            ALog and ALog.Extensions.Logging. At most 4000 characters.

    Returns:
        Package name to ``(lines, line floor)``. Both packages are present.

    Raises:
        ValueError: The document is empty, too large, not the expected object,
            or a required package or percent is missing.

    Side effects:
        None.
    """
    if not isinstance(document, str) or document.strip() == "":
        raise ValueError("measurement document is empty")
    if len(document) > _MAX_DOCUMENT_CHARS:
        raise ValueError("measurement document is larger than 4000 characters")
    try:
        data = json.loads(document, parse_float=Decimal)
    except json.JSONDecodeError as error:
        raise ValueError(f"measurement document is not JSON: {error}") from error
    if not isinstance(data, dict):
        raise ValueError("measurement document must be an object")
    packages = data.get("packages")
    if not isinstance(packages, dict):
        raise ValueError("measurement packages must be an object")
    if len(packages) > _MAX_PACKAGES:
        raise ValueError("measurement has more than 20 packages")
    found: dict[str, tuple[Decimal, Decimal]] = {}
    for package, _filename in PACKAGE_SVGS:
        body = packages.get(package)
        if not isinstance(body, dict):
            raise ValueError(f"measurement is missing {package}")
        found[package] = (
            _percent_field(body.get("lines"), package, "lines"),
            _percent_field(body.get("lineFloor"), package, "lineFloor"),
        )
    if len(found) != len(PACKAGE_SVGS):
        raise ValueError("measurement did not contain both packages")
    return found


def render_package_files(measurement: Mapping[str, tuple[Decimal, Decimal]]) -> dict[str, str]:
    """Render one SVG text per shipped package.

    Args:
        measurement: Package name to ``(lines, line floor)``, as returned by
            ``load_measurement``. Both shipped packages are required.

    Returns:
        SVG file name to document text. The names are the public badge files.

    Raises:
        ValueError: A package is missing or a percent cannot be drawn.

    Side effects:
        None.
    """
    if set(measurement) != {package for package, _filename in PACKAGE_SVGS}:
        raise ValueError("measurement must contain both packages")
    files: dict[str, str] = {}
    for package, filename in PACKAGE_SVGS:
        lines, floor = measurement[package]
        files[filename] = render_coverage_svg(lines, floor)
    if len(files) != len(PACKAGE_SVGS):
        raise ValueError("expected two SVG files")
    return files


def write_package_files(document: str, output_dir: Path) -> list[str]:
    """Write both package SVGs into a directory.

    Args:
        document: Coverage-gate measurement JSON.
        output_dir: Destination directory. It is created when missing,
            including any missing parent directories.

    Returns:
        The two file names, in package order.

    Raises:
        ValueError: The document is unusable or the directory is a file.
        OSError: A file cannot be written.

    Side effects:
        Creates the directory and replaces the two SVG files.
    """
    if output_dir.exists() and not output_dir.is_dir():
        raise ValueError(f"{output_dir} is not a directory")
    output_dir.mkdir(parents=True, exist_ok=True)
    files = render_package_files(load_measurement(document))
    written: list[str] = []
    for name, svg in files.items():
        if name != Path(name).name:
            raise ValueError(f"{name} is not a file name")
        target = output_dir / name
        if target.parent.resolve() != output_dir.resolve():
            raise ValueError("SVG path escapes the output directory")
        target.write_text(svg, encoding="utf-8")
        written.append(name)
    if len(written) != len(PACKAGE_SVGS):
        raise ValueError("did not write both SVG files")
    return written


def main(argv: list[str]) -> int:
    """Write the two coverage SVGs from a measurement file.

    Args:
        argv: Arguments without the program name. ``--measurement`` is the
            gate JSON and ``--output-dir`` is the destination.

    Returns:
        0 when both files were written, and 2 when the input cannot be used.

    Raises:
        SystemExit: argparse rejects the command line.

    Side effects:
        Reads the measurement file and writes two SVG files.
    """
    parser = argparse.ArgumentParser(prog="coverage-badge.py")
    parser.add_argument("--measurement", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args(argv)
    try:
        path = Path(args.measurement)
        if not path.is_file():
            raise ValueError(f"measurement file {path} does not exist")
        write_package_files(path.read_text(encoding="utf-8"), Path(args.output_dir))
    except (ValueError, OSError) as error:
        print(f"coverage badge could not be rendered: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
