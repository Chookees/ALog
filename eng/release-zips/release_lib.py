#!/usr/bin/env python3
"""Shared release-zip helpers: versions, asset names, changelog, README text, notes table.

Kept importable so ``eng/test_release_zips.py`` can exercise guard and notes logic
without building zips or talking to GitHub. Next: ``make_zips.py``, ``guard_tag.sh``.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
from pathlib import Path

# Stub SqlClient reference assemblies are ~93 KiB. Real RID implementations are ~1.67 MiB.
SQLCLIENT_MIN_BYTES = 500_000
PKCS_MIN_VERSION = (10, 0, 13)

# Files in ``dotnet publish -r`` output that are not part of the Full zip payload.
# Anything else under the publish directory must match the allowlist (minus LICENSE /
# NOTICE / README.txt, which the builder adds after staging).
PUBLISH_EXCLUSION_HOST_PREFIX = "ReleaseZipHost"
PUBLISH_EXCLUSION_SUFFIXES = (".pdb", ".deps.json", ".runtimeconfig.json")

# Added by the builder after publish staging; not present in publish output.
STAGE_ONLY_NAMES = frozenset({"LICENSE", "NOTICE", "README.txt"})

# RID-specific DLLs whose SHA-256 must match a NuGet ``runtimes/<family>/…`` asset.
# Kind ``lib`` → ``runtimes/<family>/lib/net*/<file>``; ``native`` → ``…/native/<file>``.
RID_NUGET_HASH_CHECKS: dict[str, list[tuple[str, str, str, str]]] = {
    "linux-x64": [
        (
            "Microsoft.Data.SqlClient.dll",
            "microsoft.data.sqlclient",
            "unix",
            "lib",
        ),
    ],
    "win-x64": [
        (
            "Microsoft.Data.SqlClient.dll",
            "microsoft.data.sqlclient",
            "win",
            "lib",
        ),
        (
            "System.Security.Cryptography.Pkcs.dll",
            "system.security.cryptography.pkcs",
            "win",
            "lib",
        ),
        (
            "System.Diagnostics.EventLog.dll",
            "system.diagnostics.eventlog",
            "win",
            "lib",
        ),
        (
            "Microsoft.Data.SqlClient.SNI.dll",
            "microsoft.data.sqlclient.sni.runtime",
            "win-x64",
            "native",
        ),
    ],
}


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


def changelog_heading_pattern(version: str) -> re.Pattern[str]:
    """Build the regex that matches a version heading, optionally with a date.

    Args:
        version: Package version such as ``0.2.1``. Escaped so ``0.2.1`` does
            not match ``0.2.10``.

    Returns:
        Compiled pattern for a full stripped line:
        ``^## {version}( - YYYY-MM-DD)?$``.
    """
    return re.compile(
        rf"^## {re.escape(version)}( - \d{{4}}-\d{{2}}-\d{{2}})?$"
    )


def extract_changelog_section(changelog_text: str, version: str) -> str:
    """Extract the ``## {version}`` section body from CHANGELOG.md.

    The heading may be plain (``## 0.2.1``) or dated (``## 0.2.1 - 2026-10-27``).
    Matching uses a stripped line against
    ``^## {re.escape(version)}( - \\d{4}-\\d{2}-\\d{2})?$`` so ``## 0.2.10``
    does not match version ``0.2.1``. The section ends at the next ``## `` line.

    Args:
        changelog_text: Full changelog file text.
        version: Section version without ``## ``, such as ``0.2.1``.

    Returns:
        The section including its heading, trimmed, with a trailing newline.

    Raises:
        ValueError: The heading is missing, the section body is empty, or the
            section has no bullet line starting with ``- ``.
    """
    pattern = changelog_heading_pattern(version)
    lines = changelog_text.splitlines()
    start = None
    for index, line in enumerate(lines):
        if pattern.fullmatch(line.strip()):
            start = index
            break
    if start is None:
        raise ValueError(f"error: CHANGELOG.md has no '## {version}' section")
    end = len(lines)
    for index in range(start + 1, len(lines)):
        if lines[index].startswith("## "):
            end = index
            break
    section = "\n".join(lines[start:end]).strip()
    if not section:
        raise ValueError(f"error: CHANGELOG.md section '## {version}' is empty")
    if not any(line.startswith("- ") for line in section.splitlines()):
        raise ValueError(
            f"error: CHANGELOG.md section '## {version}' has no bullet line "
            f"starting with '- '"
        )
    return section + "\n"


def require_changelog_section(repo: Path, version: str) -> str:
    """Read CHANGELOG.md and require a section with at least one ``- `` bullet.

    Used by the tag guard so a missing section fails before build-zips, not
    only at publish.

    Args:
        repo: Repository root that contains ``CHANGELOG.md``.
        version: Package version without a leading ``v``.

    Returns:
        The extracted section text (same contract as
        ``extract_changelog_section``).

    Raises:
        ValueError: The section is missing, empty, or has no ``- `` bullet.
        OSError: CHANGELOG.md cannot be read.
    """
    text = (repo / "CHANGELOG.md").read_text(encoding="utf-8")
    return extract_changelog_section(text, version)


def hello_world_snippet() -> str:
    """Return the compilable Hello World block for zip README.txt files.

    Returns:
        Usings plus Init/Write matching the README Quickstart namespaces, each
        line suitable for copy-paste into a top-level-statements program.
    """
    return (
        "using ALog;\n"
        "using ALog.Config;\n"
        "using ALog.Writers.Console;\n"
        "\n"
        "Log.Init(new LoggerConfig().AddWriter(new ConsoleLogWriter()));\n"
        'Log.Write("Hello");\n'
    )


def extract_hello_world_snippet(readme_text: str) -> str:
    """Pull the Hello World source from a generated README.txt body.

    Args:
        readme_text: Full README.txt contents.

    Returns:
        The C# snippet after ``Hello World:`` and before ``License:``.

    Raises:
        ValueError: The markers are missing or the block is empty.
    """
    marker = "Hello World:\n"
    start = readme_text.find(marker)
    if start < 0:
        raise ValueError("README.txt has no Hello World: marker")
    rest = readme_text[start + len(marker) :]
    end = rest.find("License:")
    if end < 0:
        raise ValueError("README.txt has no License: marker after Hello World")
    block = rest[:end].strip("\n")
    # Drop a single level of leading two-space indent used in the README layout.
    lines = []
    for line in block.splitlines():
        if line.startswith("  "):
            lines.append(line[2:])
        else:
            lines.append(line)
    snippet = "\n".join(lines).strip() + "\n"
    if "Log.Init" not in snippet or "using ALog;" not in snippet:
        raise ValueError("Hello World snippet is incomplete")
    return snippet


def format_contents_from_allowlist(entries: list[str]) -> str:
    """Format allowlist paths for the Full README Contents line.

    Lists top-level DLLs and license files only. Satellite resource DLLs under
    language folders are summarized in one phrase (not each file). Does not use
    the word ``flat``.

    Args:
        entries: Sorted relative paths from an allowlist file.

    Returns:
        Contents text: layout sentence plus comma-separated top-level files and
        a language-folder mention when satellites are present.

    Raises:
        ValueError: The allowlist has no top-level payload besides ``README.txt``.
    """
    top_level = sorted(
        name
        for name in entries
        if name != "README.txt" and "/" not in name and "\\" not in name
    )
    if not top_level:
        raise ValueError("allowlist has no top-level contents besides README.txt")
    lang_folders = sorted(
        {
            name.split("/", 1)[0] + "/"
            for name in entries
            if "/" in name.replace("\\", "/")
        }
    )
    layout = (
        "DLLs in one folder, plus language folders (de/, cs/, …) "
        "for translated SqlClient messages"
    )
    files = ", ".join(top_level)
    if lang_folders:
        folders = ", ".join(lang_folders)
        return f"{layout}. Files: {files}; language folders: {folders}"
    return f"{layout}. Files: {files}"


def build_readme_text(version: str, kind: str, *, allowlist_dir: Path | None = None) -> str:
    """Build the English README.txt for one zip using the fixed UX template.

    Args:
        version: Package version without a leading ``v``.
        kind: ``Lite``, ``Full-win-x64``, or ``Full-linux-x64``.
        allowlist_dir: Directory of ``Lite.txt`` / ``Full-*.txt``. Defaults to
            ``eng/release-zips/allowlists`` next to this module.

    Returns:
        README.txt body ending with a newline.

    Raises:
        ValueError: ``kind`` is not one of the three shipped shapes.
        OSError: An allowlist file cannot be read for a Full zip.
    """
    if allowlist_dir is None:
        allowlist_dir = Path(__file__).resolve().parent / "allowlists"

    hello = hello_world_snippet()
    hello_indented = "\n".join(
        ("  " + line) if line else "" for line in hello.rstrip("\n").splitlines()
    ) + "\n"

    if kind == "Lite":
        title = "Lite"
        contents = "ALog.dll"
        works = "Console, File, HTTP, Azure App Insights"
        missing = "SQL, AWS, ILogger provider"
        edge = (
            "Known edge cases (Lite): SqlLogWriter reports a missing Microsoft.Data.SqlClient "
            "through SelfLog (does not throw); "
            "the AWS region constructor throws FileNotFoundException;\n"
            "  Assembly.GetTypes() throws ReflectionTypeLoadException; "
            "HTTP and Azure are configured in code; "
            "JSON config supports Console and File only.\n"
        )
        nuget_line = (
            "Other platforms or SQL/AWS: use NuGet "
            "(dotnet add package ALog / ALog.Extensions.Logging).\n"
        )
    elif kind in ("Full-win-x64", "Full-linux-x64"):
        title = "Full (win-x64)" if kind == "Full-win-x64" else "Full (linux-x64)"
        allowlist_name = f"{kind}.txt"
        contents = format_contents_from_allowlist(
            load_allowlist(allowlist_dir / allowlist_name)
        )
        works = (
            "Console, File, HTTP, Azure App Insights, SQL Server, "
            "AWS CloudWatch, ILogger provider"
        )
        missing = "other platforms"
        edge = ""
        nuget_line = "For other platforms, use the NuGet packages.\n"
    else:
        raise ValueError(f"unknown zip kind: {kind}")

    body = (
        f"ALog {version} – {title}\n"
        f"Contents: {contents}\n"
        f"Works: {works}\n"
        f"Not included: {missing}\n"
        f"{edge}"
        f"{nuget_line}"
        "Hello World:\n"
        f"{hello_indented}"
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
            "Console, File, HTTP, Azure App Insights",
            "ALog.dll only",
            sizes[names[0]],
        ),
        asset_table_row(
            names[1],
            "win-x64",
            "Console, File, HTTP, Azure App Insights, SQL Server, AWS CloudWatch, ILogger provider",
            "Full graph + SNI",
            sizes[names[1]],
        ),
        asset_table_row(
            names[2],
            "linux-x64",
            "Console, File, HTTP, Azure App Insights, SQL Server, AWS CloudWatch, ILogger provider",
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


def sha256_file(path: Path) -> str:
    """Return the hex SHA-256 digest of one file.

    Args:
        path: File to hash.

    Returns:
        Lowercase hex digest.

    Raises:
        OSError: The file cannot be read.
    """
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


def is_publish_excluded(rel_path: str) -> bool:
    """True when a publish-relative path is an intentional Full-zip exclusion.

    Exclusions are only host binaries (``ReleaseZipHost*``), ``*.pdb``,
    ``*.deps.json``, and ``*.runtimeconfig.json``.

    Args:
        rel_path: Path relative to the publish directory, using ``/`` separators.

    Returns:
        True when the file must not appear in the allowlist comparison.
    """
    normalized = rel_path.replace("\\", "/")
    name = normalized.rsplit("/", 1)[-1]
    if name.startswith(PUBLISH_EXCLUSION_HOST_PREFIX):
        return True
    for suffix in PUBLISH_EXCLUSION_SUFFIXES:
        if name.endswith(suffix):
            return True
    return False


def list_publish_payload_files(publish_dir: Path) -> list[str]:
    """List publish files that must match the Full allowlist payload.

    Args:
        publish_dir: ``dotnet publish -r`` output directory.

    Returns:
        Sorted relative paths (``/`` separators), excluding host/pdb/deps/runtimeconfig.

    Raises:
        NotADirectoryError: ``publish_dir`` is not a directory.
        OSError: The tree cannot be walked.
    """
    if not publish_dir.is_dir():
        raise NotADirectoryError(f"publish directory missing: {publish_dir}")
    found: list[str] = []
    for path in publish_dir.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(publish_dir).as_posix()
        if is_publish_excluded(rel):
            continue
        found.append(rel)
    return sorted(found)


def compare_publish_to_allowlist(publish_dir: Path, allowlist: list[str]) -> None:
    """Require publish payload ≡ allowlist minus LICENSE/NOTICE/README.txt.

    Runs before staging so an extra DLL in ``dotnet publish -r`` output (for
    example Newtonsoft.Json.dll) fails the build instead of being silently dropped.

    Args:
        publish_dir: ``dotnet publish -r`` output directory.
        allowlist: Loaded allowlist paths for this Full RID.

    Raises:
        ValueError: Extra or missing files versus the allowlist payload.
        OSError: The publish tree cannot be read.
    """
    expected = sorted(
        name.replace("\\", "/")
        for name in allowlist
        if name.replace("\\", "/") not in STAGE_ONLY_NAMES
    )
    actual = list_publish_payload_files(publish_dir)
    expected_set = set(expected)
    actual_set = set(actual)
    missing = sorted(expected_set - actual_set)
    extra = sorted(actual_set - expected_set)
    if missing or extra:
        parts: list[str] = []
        if extra:
            parts.append("extra: " + ", ".join(extra))
        if missing:
            parts.append("missing: " + ", ".join(missing))
        raise ValueError(
            "publish output does not match allowlist payload (before staging); "
            + "; ".join(parts)
        )


def default_nuget_packages_root() -> Path:
    """Resolve the local NuGet package cache root.

    Returns:
        ``NUGET_PACKAGES`` when set, otherwise ``~/.nuget/packages``.
    """
    override = os.environ.get("NUGET_PACKAGES", "").strip()
    if override:
        return Path(override)
    return Path.home() / ".nuget" / "packages"


def find_nuget_rid_assets(
    package_id: str,
    rid_family: str,
    kind: str,
    filename: str,
    nuget_root: Path,
) -> list[Path]:
    """Find RID-specific assets for one DLL under the NuGet cache.

    Args:
        package_id: Lowercase package folder name (for example
            ``microsoft.data.sqlclient``).
        rid_family: ``unix``, ``win``, or ``win-x64`` (SNI native).
        kind: ``lib`` or ``native``.
        filename: File name such as ``Microsoft.Data.SqlClient.dll``.
        nuget_root: NuGet packages root.

    Returns:
        Matching file paths under ``runtimes/<rid_family>/<kind>/…``.

    Raises:
        FileNotFoundError: The package folder is missing under ``nuget_root``.
    """
    package_root = nuget_root / package_id.lower()
    if not package_root.is_dir():
        raise FileNotFoundError(f"NuGet package folder missing: {package_root}")
    needle = f"runtimes/{rid_family}/{kind}/"
    matches: list[Path] = []
    for path in package_root.rglob(filename):
        if not path.is_file():
            continue
        rel = path.relative_to(package_root).as_posix()
        if needle not in rel:
            continue
        if kind == "lib" and "/lib/" not in rel:
            continue
        if kind == "native" and "/native/" not in rel:
            continue
        matches.append(path)
    return matches


def resolve_assets_rid_package_path(
    assets_json: Path,
    rid: str,
    filename: str,
) -> tuple[str, str, str]:
    """Return the package path ``project.assets.json`` selected for one RID DLL.

    Args:
        assets_json: Host ``project.assets.json`` after ``dotnet publish -r``.
        rid: ``linux-x64`` or ``win-x64``.
        filename: DLL file name such as ``Microsoft.Data.SqlClient.dll``.

    Returns:
        ``(package_id_lower, package_version, package_relative_path)`` where the
        relative path is the exact ``runtime`` / ``runtimeTargets`` / ``native``
        entry NuGet selected (includes ``runtimes/<family>/lib/<tfm>/…``).

    Raises:
        ValueError: The assets file has no matching target or DLL entry.
        OSError: The assets file cannot be read.
        json.JSONDecodeError: The assets file is not valid JSON.
    """
    data = json.loads(assets_json.read_text(encoding="utf-8"))
    targets = data.get("targets") or {}
    target_key = None
    suffix = "/" + rid
    for key in targets:
        if key.endswith(suffix):
            target_key = key
            break
    if target_key is None:
        raise ValueError(
            f"project.assets.json has no target for RID {rid} in {assets_json}"
        )
    packages = targets[target_key]
    for package_key, package in packages.items():
        if "/" not in package_key:
            continue
        package_id, package_version = package_key.split("/", 1)
        for field in ("runtime", "runtimeTargets", "native"):
            entries = package.get(field) or {}
            for rel_path in entries:
                normalized = rel_path.replace("\\", "/")
                if not normalized.endswith("/" + filename) and normalized != filename:
                    continue
                if not normalized.startswith("runtimes/"):
                    continue
                return package_id.lower(), package_version, normalized
    raise ValueError(
        f"project.assets.json target {target_key} has no runtimes path for {filename}"
    )


def verify_rid_nuget_hashes(
    publish_dir: Path,
    rid: str,
    *,
    assets_json: Path,
    nuget_root: Path | None = None,
) -> None:
    """Require RID publish DLLs match the exact ``project.assets.json`` NuGet path.

    Checks SqlClient for every Full RID. On ``win-x64`` also checks Pkcs, EventLog,
    and SNI. The digest must equal the file at the package-relative path NuGet
    selected for this RID (including the TFM folder under ``runtimes/…/lib/``).

    Args:
        publish_dir: ``dotnet publish -r`` output directory.
        rid: ``linux-x64`` or ``win-x64``.
        assets_json: Host ``project.assets.json`` for this publish.
        nuget_root: Optional NuGet packages root; defaults to the local cache.

    Raises:
        ValueError: Unknown RID, missing file, assets path missing, or hash/TFM mismatch.
        OSError: A file cannot be read.
    """
    checks = RID_NUGET_HASH_CHECKS.get(rid)
    if checks is None:
        raise ValueError(f"unsupported RID for NuGet hash check: {rid}")
    if not assets_json.is_file():
        raise ValueError(f"project.assets.json missing: {assets_json}")
    root = nuget_root if nuget_root is not None else default_nuget_packages_root()
    for filename, _package_id, _family, _kind in checks:
        published = publish_dir / filename
        if not published.is_file():
            raise ValueError(f"published output missing RID DLL {filename} for {rid}")
        package_id, package_version, rel_path = resolve_assets_rid_package_path(
            assets_json, rid, filename
        )
        expected = root / package_id / package_version / rel_path
        if not expected.is_file():
            raise ValueError(
                f"NuGet asset missing for assets.json path: {expected.as_posix()}"
            )
        published_hash = sha256_file(published)
        expected_hash = sha256_file(expected)
        if published_hash != expected_hash:
            raise ValueError(
                f"{filename} SHA-256 {published_hash} does not match assets.json "
                f"selected {rel_path} ({expected_hash}) for RID {rid}"
            )
        print(
            f"RID hash ok {rid} {filename}: matches "
            f"{package_id}/{package_version}/{rel_path}"
        )


def check_full_publish_output(
    publish_dir: Path,
    allowlist_path: Path,
    rid: str,
    *,
    assets_json: Path,
    nuget_root: Path | None = None,
) -> None:
    """Run allowlist-before-stage and RID NuGet hash checks on one publish tree.

    Args:
        publish_dir: ``dotnet publish -r`` output directory.
        allowlist_path: ``Full-<rid>.txt`` allowlist path.
        rid: ``linux-x64`` or ``win-x64``.
        assets_json: Host ``project.assets.json`` for this publish.
        nuget_root: Optional NuGet packages root.

    Raises:
        ValueError: Allowlist or RID hash check failed.
        OSError: Inputs cannot be read.
    """
    allowlist = load_allowlist(allowlist_path)
    compare_publish_to_allowlist(publish_dir, allowlist)
    verify_rid_nuget_hashes(
        publish_dir, rid, assets_json=assets_json, nuget_root=nuget_root
    )


def azure_track_request_ok(method: str, path: str, body: str) -> None:
    """Require an Azure App Insights track request shape used by release smoke.

    Args:
        method: HTTP method (must be ``POST``).
        path: URL absolute path (must be ``/v2/track``).
        body: Raw request body (must be non-empty JSON).

    Raises:
        ValueError: Method, path, or JSON body is invalid.
    """
    if method.upper() != "POST":
        raise ValueError(f"error: Azure track method must be POST, got {method!r}")
    normalized = path.split("?", 1)[0]
    if normalized != "/v2/track":
        raise ValueError(
            f"error: Azure track path must be /v2/track, got {path!r}"
        )
    if not body or not body.strip():
        raise ValueError("error: Azure track body is empty")
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError as exc:
        raise ValueError(f"error: Azure track body is not valid JSON: {exc}") from exc
    if not isinstance(parsed, (dict, list)):
        raise ValueError("error: Azure track body JSON must be an object or array")


def workflow_run_blocks_with_expressions(workflow_text: str) -> list[str]:
    """Return ``run:`` block snippets that still embed ``${{`` expressions.

    GitHub Actions interpolates ``${{ }}`` before the shell sees the script, so
    values used inside ``run:`` must arrive via ``env:`` instead.

    Args:
        workflow_text: Full YAML text of a workflow file.

    Returns:
        One snippet per offending ``run:`` block (truncated), empty when clean.
    """
    offenders: list[str] = []
    lines = workflow_text.splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        stripped = line.lstrip(" ")
        indent = len(line) - len(stripped)
        if not stripped.startswith("run:"):
            index += 1
            continue
        block_lines = [line]
        rest = stripped[len("run:") :].strip()
        if rest in ("|", ">", "|-", ">-", "") or rest.startswith("|") or rest.startswith(">"):
            index += 1
            while index < len(lines):
                nxt = lines[index]
                if not nxt.strip():
                    block_lines.append(nxt)
                    index += 1
                    continue
                nxt_indent = len(nxt) - len(nxt.lstrip(" "))
                if nxt_indent <= indent:
                    break
                block_lines.append(nxt)
                index += 1
        else:
            index += 1
        block = "\n".join(block_lines)
        # Only the script body after ``run:`` counts; ``if:`` keys are elsewhere.
        if "${{" in block.split("run:", 1)[1]:
            offenders.append(block if len(block) < 240 else block[:240] + "…")
    return offenders
