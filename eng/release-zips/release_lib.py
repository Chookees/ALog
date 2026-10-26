#!/usr/bin/env python3
"""Shared release-zip helpers: versions, asset names, changelog, README text, notes table.

Kept importable so ``eng/test_release_zips.py`` can exercise guard and notes logic
without building zips or talking to GitHub. Next: ``make_zips.py``, ``guard_tag.sh``.
"""

from __future__ import annotations

import re
from pathlib import Path

# Stub SqlClient reference assemblies are ~93 KiB. Real RID implementations are ~1.67 MiB.
SQLCLIENT_MIN_BYTES = 500_000
PKCS_MIN_VERSION = (10, 0, 13)


def read_csproj_version(csproj: Path) -> str:
    """Return the ``<Version>`` element from one library csproj.

    Args:
        csproj: Path to ``ALog.csproj`` or ``ALog.Extensions.Logging.csproj``.

    Returns:
        The version string such as ``0.2.1``.

    Raises:
        ValueError: The file has no single ``<Version>`` element, or it is empty.
        OSError: The file cannot be read.
    """
    text = csproj.read_text(encoding="utf-8")
    matches = re.findall(r"<Version>([^<]+)</Version>", text)
    if len(matches) != 1:
        raise ValueError(f"{csproj}: expected exactly one <Version>, found {len(matches)}")
    version = matches[0].strip()
    if not version:
        raise ValueError(f"{csproj}: <Version> is empty")
    return version


def require_matching_package_versions(repo: Path) -> str:
    """Require both shipped libraries share one ``<Version>`` and return it.

    Args:
        repo: Repository root that contains ``ALog/`` and ``ALog.Extensions.Logging/``.

    Returns:
        The shared version string.

    Raises:
        ValueError: The two csproj versions differ or either is missing.
        OSError: A csproj cannot be read.
    """
    core = read_csproj_version(repo / "ALog" / "ALog.csproj")
    ext = read_csproj_version(
        repo / "ALog.Extensions.Logging" / "ALog.Extensions.Logging.csproj"
    )
    if core != ext:
        raise ValueError(
            f"package versions differ: ALog={core} ALog.Extensions.Logging={ext}"
        )
    return core


def parse_tag_version(tag_ref: str) -> str:
    """Strip a leading ``v`` / ``refs/tags/v`` from a tag name.

    Args:
        tag_ref: ``v0.2.1``, ``refs/tags/v0.2.1``, or ``0.2.1``.

    Returns:
        The version without a leading ``v``.

    Raises:
        ValueError: The ref is empty or not a version-like tag.
    """
    value = tag_ref.strip()
    if value.startswith("refs/tags/"):
        value = value[len("refs/tags/") :]
    if value.startswith("v") or value.startswith("V"):
        value = value[1:]
    if not value or not re.fullmatch(r"\d+\.\d+\.\d+[0-9A-Za-z\.\-\+]*", value):
        raise ValueError(f"tag is not a vMAJOR.MINOR.PATCH version: {tag_ref!r}")
    return value


def expected_asset_names(version: str) -> list[str]:
    """Return the exact three zip names for one release version.

    Args:
        version: Package version without a leading ``v``.

    Returns:
        Lite, Full-win-x64, Full-linux-x64 names in that fixed order.
    """
    return [
        f"ALog-v{version}-Lite.zip",
        f"ALog-v{version}-Full-win-x64.zip",
        f"ALog-v{version}-Full-linux-x64.zip",
    ]


def zip_folder_name(version: str, kind: str) -> str:
    """Return the root folder name stored inside one zip.

    Args:
        version: Package version without a leading ``v``.
        kind: ``Lite``, ``Full-win-x64``, or ``Full-linux-x64``.

    Returns:
        ``ALog-v{version}-{kind}``.
    """
    return f"ALog-v{version}-{kind}"


def informational_matches(expected: str, informational: str) -> bool:
    """True when InformationalVersion is exactly expected or expected plus ``+`` metadata.

    Args:
        expected: Version from the csproj, such as ``0.2.1``.
        informational: Assembly informational version, possibly with ``+git``.

    Returns:
        True when the assembly version matches the package version.
    """
    if not informational:
        return False
    head = informational.split("+", 1)[0].strip()
    return head == expected


def file_version_matches(expected: str, file_version: str) -> bool:
    """True when FileVersion is expected or expected with a trailing ``.0`` build.

    Args:
        expected: Version from the csproj, such as ``0.2.1``.
        file_version: Win32 FileVersion string such as ``0.2.1.0``.

    Returns:
        True when the file version matches the package version.
    """
    if not file_version:
        return False
    cleaned = file_version.strip()
    if cleaned == expected:
        return True
    return cleaned == f"{expected}.0" or cleaned.startswith(f"{expected}.")


def parse_dotted_version(value: str) -> tuple[int, ...]:
    """Parse a dotted numeric version prefix into ints.

    Args:
        value: A FileVersion or package version such as ``10.0.1326.50504``.

    Returns:
        At least one integer component.

    Raises:
        ValueError: No leading numeric component exists.
    """
    parts: list[int] = []
    for piece in value.strip().split("."):
        if not piece:
            break
        digits = re.match(r"(\d+)", piece)
        if digits is None:
            break
        parts.append(int(digits.group(1), 10))
    if not parts:
        raise ValueError(f"not a dotted version: {value!r}")
    return tuple(parts)


def version_at_least(actual: str, minimum: tuple[int, ...]) -> bool:
    """Compare a dotted version string to a minimum tuple.

    Args:
        actual: FileVersion or similar.
        minimum: Required lower bound, such as ``(10, 0, 13)``.

    Returns:
        True when actual >= minimum using component-wise comparison.
    """
    left = list(parse_dotted_version(actual))
    right = list(minimum)
    width = max(len(left), len(right))
    left.extend([0] * (width - len(left)))
    right.extend([0] * (width - len(right)))
    return tuple(left) >= tuple(right)


def extract_changelog_section(changelog_text: str, version: str) -> str:
    """Extract the ``## {version}`` section body from CHANGELOG.md.

    Args:
        changelog_text: Full changelog file text.
        version: Section heading without ``## ``, such as ``0.2.1``.

    Returns:
        The section including the ``## {version}`` heading, trimmed, without
        following version sections.

    Raises:
        ValueError: The heading is missing.
    """
    heading = f"## {version}"
    lines = changelog_text.splitlines()
    start = None
    for index, line in enumerate(lines):
        if line.strip() == heading:
            start = index
            break
    if start is None:
        raise ValueError(f"CHANGELOG.md has no '{heading}' section")
    end = len(lines)
    for index in range(start + 1, len(lines)):
        if lines[index].startswith("## ") and lines[index].strip() != heading:
            end = index
            break
    section = "\n".join(lines[start:end]).strip()
    if not section:
        raise ValueError(f"CHANGELOG.md section '{heading}' is empty")
    return section + "\n"


def build_readme_text(version: str, kind: str) -> str:
    """Build the English README.txt for one zip using the fixed UX template.

    Args:
        version: Package version without a leading ``v``.
        kind: ``Lite``, ``Full-win-x64``, or ``Full-linux-x64``.

    Returns:
        README.txt body ending with a newline.

    Raises:
        ValueError: ``kind`` is not one of the three shipped shapes.
    """
    if kind == "Lite":
        title = "Lite"
        contents = "ALog.dll"
        works = (
            "Console, File, HTTP, background queue, SelfLog, enrichers, "
            "formatters, Log.Init(ALogOptions)"
        )
        missing = "SQL, AWS, ILogger provider"
        edge = (
            "Known edge cases (Lite): SqlLogWriter and the AWS region constructor "
            "throw FileNotFoundException;\n"
            "  Assembly.GetTypes() throws ReflectionTypeLoadException; "
            "JSON config supports Console and File only.\n"
        )
        other_platforms = ""
    elif kind == "Full-win-x64":
        title = "Full (win-x64)"
        contents = (
            "ALog.dll, ALog.Extensions.Logging.dll, and RID-specific dependencies "
            "(flat; see allowlist Full-win-x64.txt)"
        )
        works = (
            "Console, File, HTTP, SQL (SqlClient + SNI), AWS CloudWatch, "
            "ILogger provider (AddALog), background queue, SelfLog"
        )
        missing = "other platforms"
        edge = ""
        other_platforms = "For other platforms, use the NuGet packages.\n"
    elif kind == "Full-linux-x64":
        title = "Full (linux-x64)"
        contents = (
            "ALog.dll, ALog.Extensions.Logging.dll, and RID-specific dependencies "
            "(flat; see allowlist Full-linux-x64.txt)"
        )
        works = (
            "Console, File, HTTP, SQL (unix SqlClient), AWS CloudWatch, "
            "ILogger provider (AddALog), background queue, SelfLog"
        )
        missing = "other platforms"
        edge = ""
        other_platforms = "For other platforms, use the NuGet packages.\n"
    else:
        raise ValueError(f"unknown zip kind: {kind}")

    body = (
        f"ALog {version} – {title}\n"
        f"Contents: {contents}\n"
        f"Works: {works}\n"
        f"Not included: {missing}\n"
        f"{edge}"
        "Other platforms or SQL/AWS: use NuGet "
        "(dotnet add package ALog / ALog.Extensions.Logging).\n"
        f"{other_platforms}"
        "Hello World:\n"
        "  Log.Init(new LoggerConfig().AddWriter(new ConsoleLogWriter()));\n"
        '  Log.Write("Hello");\n'
        "  await Log.FlushAsync();\n"
        "License: Apache-2.0, see LICENSE and NOTICE.\n"
    )
    return body


def asset_table_row(
    asset: str, platform: str, writers: str, dependencies: str, size_bytes: int
) -> str:
    """Format one markdown table row for release notes.

    Args:
        asset: Zip file name.
        platform: ``portable``, ``win-x64``, or ``linux-x64``.
        writers: Short writer summary.
        dependencies: Short dependency summary.
        size_bytes: Exact byte size from this run.

    Returns:
        One markdown table row including leading ``|`` and trailing newline.
    """
    return (
        f"| `{asset}` | {platform} | {writers} | {dependencies} | "
        f"{size_bytes} B |\n"
    )


def build_asset_table(version: str, sizes: dict[str, int]) -> str:
    """Build the Asset | Platform | Writers | Dependencies | Size table.

    Args:
        version: Package version without a leading ``v``.
        sizes: Map of exact zip file name to byte size from this run.

    Returns:
        A markdown table with a header and one row per asset.

    Raises:
        ValueError: A required asset size is missing.
    """
    names = expected_asset_names(version)
    for name in names:
        if name not in sizes:
            raise ValueError(f"missing size for asset {name}")
    rows = [
        "| Asset | Platform | Writers | Dependencies | Size |\n",
        "| --- | --- | --- | --- | --- |\n",
        asset_table_row(
            names[0],
            "portable",
            "Console, File, HTTP, Queue, SelfLog",
            "ALog.dll only",
            sizes[names[0]],
        ),
        asset_table_row(
            names[1],
            "win-x64",
            "Console, File, HTTP, SQL, AWS, ILogger",
            "Full graph + SNI",
            sizes[names[1]],
        ),
        asset_table_row(
            names[2],
            "linux-x64",
            "Console, File, HTTP, SQL, AWS, ILogger",
            "Full graph (unix SqlClient)",
            sizes[names[2]],
        ),
    ]
    return "".join(rows)


def build_release_notes(
    changelog_section: str, version: str, sizes: dict[str, int], sha256_text: str
) -> str:
    """Compose GitHub release notes from changelog, asset table, and checksums.

    Args:
        changelog_section: Text from ``extract_changelog_section``.
        version: Package version without a leading ``v``.
        sizes: Map of zip name to bytes.
        sha256_text: Contents of SHA256SUMS for the three zips.

    Returns:
        Release notes body for ``gh release create``.
    """
    table = build_asset_table(version, sizes)
    return (
        f"{changelog_section.rstrip()}\n\n"
        f"## Release assets\n\n"
        f"{table}\n"
        f"### SHA256SUMS\n\n"
        f"```\n{sha256_text.strip()}\n```\n"
    )


def load_allowlist(path: Path) -> list[str]:
    """Load an allowlist file as a sorted list of relative paths.

    Args:
        path: Allowlist text file, one relative path per line.

    Returns:
        Sorted unique relative paths.

    Raises:
        ValueError: The file is empty or has blank/duplicate lines.
        OSError: The file cannot be read.
    """
    lines = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        lines.append(line.replace("\\", "/"))
    if not lines:
        raise ValueError(f"allowlist is empty: {path}")
    if len(lines) != len(set(lines)):
        raise ValueError(f"allowlist has duplicates: {path}")
    return sorted(lines)


def epoch_to_zip_timestamp(epoch: int) -> tuple[int, int, int, int, int, int]:
    """Convert SOURCE_DATE_EPOCH seconds to a zip ``date_time`` tuple in UTC.

    Args:
        epoch: Unix seconds. Values before 1980-01-01 are raised to that floor
            because the zip format cannot store earlier dates.

    Returns:
        ``(year, month, day, hour, minute, second)`` suitable for ``ZipInfo``.
    """
    import datetime as dt

    floor = 315532800  # 1980-01-01 UTC
    seconds = max(int(epoch), floor)
    moment = dt.datetime.fromtimestamp(seconds, tz=dt.timezone.utc)
    return (
        moment.year,
        moment.month,
        moment.day,
        moment.hour,
        moment.minute,
        moment.second,
    )
