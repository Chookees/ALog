#!/usr/bin/env python3
"""Prove the smoke consumer restored both packages from the local feed.

S4-10: after restore with ``-p:ALogSmokeFromFeed=true``, ``project.assets.json``
must list ``ALog`` and ``ALog.Extensions.Logging`` as libraries of type
``package`` at the just-packed version, with no ``project`` entry for either
package, and the feed path must appear in the file. Each package's ``sha512``
must be the base64 SHA-512 of the nupkg that was just packed. Success prints
one ``SMOKE-PACKAGE <id> <version> from-feed`` line per package.
"""

from __future__ import annotations

import base64
import hashlib
import json
import sys
from pathlib import Path

MAX_LIBRARIES = 500
MAX_ASSETS_BYTES = 5_000_000
MAX_NUPKG_BYTES = 20_000_000
PACKAGE_IDS = ("ALog", "ALog.Extensions.Logging")


def prove_alog_package(raw: str, version: str, feed: str) -> str:
    """Return the proof lines, or raise ``ValueError`` when the assets file is wrong.

    Args:
        raw: UTF-8 text of ``project.assets.json``.
        version: Package version just packed, such as ``0.2.1``.
        feed: Absolute local feed directory that must appear in the file.

    Returns:
        ``SMOKE-PACKAGE ALog <version> from-feed`` and
        ``SMOKE-PACKAGE ALog.Extensions.Logging <version> from-feed``.

    Raises:
        ValueError: A package is missing, not a package, or its hash differs.

    Side effects:
        Reads both nupkgs from the feed.
    """
    libraries = load_libraries(raw, feed)
    found = collect_packages(libraries)
    lines: list[str] = []
    index = 0
    while index < len(PACKAGE_IDS) and index < 8:
        package_id = PACKAGE_IDS[index]
        lines.append(prove_one(found.get(package_id), package_id, version, feed))
        index += 1
    return "\n".join(lines)


def load_libraries(raw: str, feed: str) -> dict:
    """Return the libraries object after the size and feed checks.

    Args:
        raw: Assets file text. At most 5000000 characters.
        feed: Feed directory that must appear in the text.

    Returns:
        The ``libraries`` object.

    Raises:
        ValueError: The file is too large, not JSON, or does not name the feed.

    Side effects:
        None.
    """
    if len(raw) > MAX_ASSETS_BYTES:
        raise ValueError("project.assets.json is larger than 5000000 bytes.")
    if len(feed) < 4 or feed not in raw:
        raise ValueError("local feed path is missing from project.assets.json.")
    try:
        document = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError("project.assets.json is not JSON.") from exc
    libraries = document.get("libraries") if isinstance(document, dict) else None
    if not isinstance(libraries, dict):
        raise ValueError("project.assets.json has no libraries object.")
    if len(libraries) > MAX_LIBRARIES:
        raise ValueError("project.assets.json lists more than 500 libraries.")
    return libraries


def collect_packages(libraries: dict) -> dict[str, tuple[str, object]]:
    """Index the two smoke packages by id.

    Args:
        libraries: NuGet ``libraries`` object. At most 500 entries.

    Returns:
        Map of package id to ``(version, sha512)``. A project entry for either
        id is rejected. A second entry for the same id is rejected.

    Raises:
        ValueError: An entry is a project reference or a duplicate.

    Side effects:
        None.
    """
    found: dict[str, tuple[str, object]] = {}
    seen = 0
    for name, body in libraries.items():
        seen += 1
        if seen > MAX_LIBRARIES:
            raise ValueError("project.assets.json lists more than 500 libraries.")
        package_id, separator, package_version = str(name).partition("/")
        if package_id not in PACKAGE_IDS or separator != "/":
            continue
        library_type = body.get("type") if isinstance(body, dict) else None
        if library_type == "project":
            raise ValueError(f"{package_id} is a project reference in project.assets.json.")
        if package_id in found:
            raise ValueError(f"{package_id} is listed more than once in project.assets.json.")
        if library_type != "package":
            raise ValueError(f"{package_id} library type is {library_type}, not package.")
        recorded_sha = body.get("sha512") if isinstance(body, dict) else None
        found[package_id] = (package_version, recorded_sha)
    return found


def prove_one(recorded: tuple[str, object] | None, package_id: str, version: str, feed: str) -> str:
    """Return one proof line after checking the version and the nupkg hash.

    Args:
        recorded: ``(version, sha512)`` from the assets file, or None if absent.
        package_id: ``ALog`` or ``ALog.Extensions.Logging``.
        version: Version that was just packed.
        feed: Directory that holds ``<id>.<version>.nupkg``.

    Returns:
        ``SMOKE-PACKAGE <id> <version> from-feed``.

    Raises:
        ValueError: The package is missing or the hash does not match.

    Side effects:
        Reads the nupkg.
    """
    if recorded is None:
        raise ValueError(f"{package_id} package is missing from project.assets.json.")
    found_version, recorded_sha = recorded
    if found_version != version:
        raise ValueError(f"{package_id} package version {found_version} does not match {version}.")
    if recorded_sha != nupkg_sha512(feed, package_id, version):
        raise ValueError(f"{package_id} sha512 does not match the just-packed nupkg.")
    return f"SMOKE-PACKAGE {package_id} {version} from-feed"


def nupkg_sha512(feed: str, package_id: str, version: str) -> str:
    """Return the base64 SHA-512 of one packed nupkg.

    Args:
        feed: Directory that holds the nupkg.
        package_id: Package id. Used only as the file name prefix.
        version: Package version.

    Returns:
        Base64 SHA-512, the same form NuGet writes to ``project.assets.json``.

    Raises:
        ValueError: The nupkg is missing or larger than 20 MB.

    Side effects:
        Reads the nupkg.
    """
    path = Path(feed) / f"{package_id}.{version}.nupkg"
    if not path.is_file():
        raise ValueError(f"packed {package_id} nupkg is missing from the feed.")
    size = path.stat().st_size
    if size > MAX_NUPKG_BYTES:
        raise ValueError(f"packed {package_id} nupkg is larger than 20 MB.")
    digest = hashlib.sha512(path.read_bytes()).digest()
    return base64.b64encode(digest).decode("ascii")


def main(argv: list[str]) -> int:
    """Check one assets file and print the proof lines.

    Args:
        argv: ``[assets path, version, feed directory]``.

    Returns:
        ``0`` when both proof lines were printed, ``1`` otherwise.

    Raises:
        None. Input and JSON errors are printed to stderr.

    Side effects:
        Reads the assets file and both nupkgs. Writes stdout on success.
    """
    if len(argv) != 3:
        sys.stderr.write("usage: smoke-assets-check.py <assets.json> <version> <feed>\n")
        return 1
    path, version, feed = argv
    try:
        raw = Path(path).read_text(encoding="utf-8")
        sys.stdout.write(prove_alog_package(raw, version, feed) + "\n")
    except (OSError, UnicodeError, ValueError) as exc:
        sys.stderr.write(f"{exc}\n")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
