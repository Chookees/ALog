#!/usr/bin/env python3
"""Prove the smoke consumer restored ALog from the local feed.

S4-10: after restore with ``-p:ALogSmokeFromFeed=true``, ``project.assets.json``
must list ``ALog`` as a library of type ``package`` at the just-packed version,
with no ``project`` entry for that package, and the feed path must appear in
the file. Success prints ``SMOKE-PACKAGE ALog <version> from-feed``.
"""

from __future__ import annotations

import base64
import hashlib
import json
import sys
from pathlib import Path

MAX_LIBRARIES = 500
MAX_ASSETS_BYTES = 5_000_000


def prove_alog_package(raw: str, version: str, feed: str) -> str:
    """Return the proof line, or raise ``ValueError`` when the assets file is wrong.

    Args:
        raw: UTF-8 text of ``project.assets.json``.
        version: Package version just packed, such as ``0.2.1``.
        feed: Absolute local feed directory that must appear in the file.

    Returns:
        ``SMOKE-PACKAGE ALog <version> from-feed``.

    Raises:
        ValueError: The file is not the packed package, the hash differs, or the feed path is missing.

    Side effects:
        Reads ``ALog.<version>.nupkg`` from the feed.
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
    found: str | None = None
    recorded_sha = None
    for name, body in libraries.items():
        package_id, separator, package_version = str(name).partition("/")
        if package_id != "ALog" or separator != "/":
            continue
        library_type = body.get("type") if isinstance(body, dict) else None
        if library_type == "project":
            raise ValueError("ALog is a project reference in project.assets.json.")
        if found is not None:
            raise ValueError("ALog is listed more than once in project.assets.json.")
        if library_type != "package":
            raise ValueError(f"ALog library type is {library_type}, not package.")
        found = package_version
        recorded_sha = body.get("sha512") if isinstance(body, dict) else None
    if found != version:
        raise ValueError(f"ALog package version {found} does not match {version}.")
    if recorded_sha != nupkg_sha512(feed, version):
        raise ValueError("ALog sha512 does not match the just-packed nupkg.")
    return f"SMOKE-PACKAGE ALog {version} from-feed"


def nupkg_sha512(feed: str, version: str) -> str:
    """Return the base64 SHA-512 of the packed ALog nupkg.

    Args:
        feed: Directory that holds ``ALog.<version>.nupkg``.
        version: Package version.

    Returns:
        Base64 SHA-512, the same form NuGet writes to ``project.assets.json``.

    Raises:
        ValueError: The nupkg is missing or larger than 20 MB.

    Side effects:
        Reads the nupkg.
    """
    path = Path(feed) / f"ALog.{version}.nupkg"
    if not path.is_file():
        raise ValueError("packed ALog nupkg is missing from the feed.")
    size = path.stat().st_size
    if size > 20_000_000:
        raise ValueError("packed ALog nupkg is larger than 20 MB.")
    digest = hashlib.sha512(path.read_bytes()).digest()
    return base64.b64encode(digest).decode("ascii")


def main(argv: list[str]) -> int:
    """Check one assets file and print the proof line.

    Args:
        argv: ``[assets path, version, feed directory]``.

    Returns:
        ``0`` when the proof line was printed, ``1`` otherwise.

    Raises:
        None. Input and JSON errors are printed to stderr.

    Side effects:
        Reads the assets file. Writes one stdout line on success.
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
