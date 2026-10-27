#!/usr/bin/env python3
"""Pack staged release directories into deterministic zips and write SHA256SUMS.

Expects each stage folder to already contain exactly the allowlisted files.
Uses Python ``zipfile`` deflate level 9, sorted members, fixed timestamps, and
mode ``0644``. Next: ``eng/build-release-zips.sh``.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import sys
import zipfile
from pathlib import Path

from release_lib import epoch_to_zip_timestamp, load_allowlist, zip_folder_name


def list_stage_files(stage: Path) -> list[str]:
    """Return sorted relative paths of every regular file under stage.

    Args:
        stage: Directory that will become the zip root folder's contents.

    Returns:
        POSIX relative paths, sorted.

    Raises:
        ValueError: ``stage`` is missing or not a directory.
    """
    if not stage.is_dir():
        raise ValueError(f"stage directory missing: {stage}")
    found: list[str] = []
    for path in stage.rglob("*"):
        if path.is_file():
            found.append(path.relative_to(stage).as_posix())
    return sorted(found)


def require_allowlist_match(stage: Path, allowlist: list[str]) -> None:
    """Fail when the stage tree is not exactly the allowlist.

    Args:
        stage: Staged directory.
        allowlist: Exact relative paths that must be present.

    Raises:
        ValueError: A file is missing or an unexpected file is present.
    """
    actual = list_stage_files(stage)
    expected = sorted(allowlist)
    missing = [name for name in expected if name not in actual]
    extra = [name for name in actual if name not in expected]
    if missing or extra:
        parts = []
        if missing:
            parts.append("missing: " + ", ".join(missing))
        if extra:
            parts.append("unexpected: " + ", ".join(extra))
        raise ValueError(f"{stage.name} allowlist mismatch; " + "; ".join(parts))


def write_deterministic_zip(
    stage: Path,
    zip_path: Path,
    folder_name: str,
    date_time: tuple[int, int, int, int, int, int],
) -> None:
    """Write one reproducible zip from a stage directory.

    Args:
        stage: Directory whose files become ``folder_name/...`` members.
        zip_path: Output zip path. Overwritten when it exists.
        folder_name: Root folder stored inside the zip.
        date_time: Fixed timestamp applied to every member.

    Raises:
        OSError: A file cannot be read or the zip cannot be written.
    """
    files = list_stage_files(stage)
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(
        zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
    ) as archive:
        for rel in files:
            info = zipfile.ZipInfo(f"{folder_name}/{rel}", date_time=date_time)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            data = (stage / rel).read_bytes()
            archive.writestr(info, data, compresslevel=9)


def sha256_file(path: Path) -> str:
    """Return the hex SHA-256 of one file.

    Args:
        path: File to hash.

    Returns:
        Lowercase hex digest.

    Raises:
        OSError: The file cannot be read.
    """
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def write_sha256sums(out_dir: Path, zip_names: list[str]) -> Path:
    """Write ``SHA256SUMS`` for the named zips in sorted order.

    Args:
        out_dir: Directory that contains the zips and receives SHA256SUMS.
        zip_names: Zip file names (not paths) to include.

    Returns:
        Path to the written SHA256SUMS file.

    Raises:
        OSError: A zip or the output file cannot be read/written.
    """
    lines = []
    for name in sorted(zip_names):
        digest = sha256_file(out_dir / name)
        lines.append(f"{digest}  {name}\n")
    target = out_dir / "SHA256SUMS"
    target.write_text("".join(lines), encoding="utf-8", newline="\n")
    return target


def pack_one(
    repo: Path,
    stage_root: Path,
    out_dir: Path,
    version: str,
    kind: str,
    epoch: int,
) -> str:
    """Validate one stage against its allowlist and write its zip.

    Args:
        repo: Repository root (for allowlists).
        stage_root: Parent of staged ``ALog-v...`` directories.
        out_dir: Directory that receives the zip.
        version: Package version without ``v``.
        kind: ``Lite``, ``Full-win-x64``, or ``Full-linux-x64``.
        epoch: SOURCE_DATE_EPOCH seconds for member timestamps.

    Returns:
        The zip file name that was written.

    Raises:
        ValueError: Allowlist mismatch or unknown kind.
        OSError: IO failure.
    """
    folder = zip_folder_name(version, kind)
    stage = stage_root / folder
    allowlist_name = {
        "Lite": "Lite.txt",
        "Full-win-x64": "Full-win-x64.txt",
        "Full-linux-x64": "Full-linux-x64.txt",
    }[kind]
    allowlist = load_allowlist(repo / "eng" / "release-zips" / "allowlists" / allowlist_name)
    require_allowlist_match(stage, allowlist)
    zip_name = f"{folder}.zip"
    write_deterministic_zip(
        stage, out_dir / zip_name, folder, epoch_to_zip_timestamp(epoch)
    )
    return zip_name


def parse_args(argv: list[str]) -> argparse.Namespace:
    """Parse CLI arguments for packing already-staged directories.

    Args:
        argv: Argument vector without the program name.

    Returns:
        Parsed namespace with repo, stage, out, version, kinds, and epoch.
    """
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True, help="Repository root")
    parser.add_argument(
        "--stage", type=Path, required=True, help="Parent of staged zip folders"
    )
    parser.add_argument("--out", type=Path, required=True, help="Output directory")
    parser.add_argument("--version", required=True, help="Package version, no v")
    parser.add_argument(
        "--kind",
        action="append",
        dest="kinds",
        required=True,
        choices=("Lite", "Full-win-x64", "Full-linux-x64"),
        help="Zip kind to pack (repeatable)",
    )
    parser.add_argument(
        "--epoch",
        type=int,
        required=True,
        help="SOURCE_DATE_EPOCH seconds for zip member timestamps",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    """Pack the requested kinds and refresh SHA256SUMS for zips in --out.

    Args:
        argv: Argument vector without the program name.

    Returns:
        0 on success, 1 on validation or IO errors.
    """
    args = parse_args(argv)
    try:
        names: list[str] = []
        for kind in args.kinds:
            names.append(
                pack_one(
                    args.repo.resolve(),
                    args.stage.resolve(),
                    args.out.resolve(),
                    args.version,
                    kind,
                    args.epoch,
                )
            )
        # Include any sibling zips already in out so a split CI matrix can merge later.
        existing = [path.name for path in args.out.glob("ALog-v*.zip")]
        write_sha256sums(args.out.resolve(), sorted(set(existing) | set(names)))
    except (OSError, ValueError, KeyError) as error:
        print(f"make_zips failed: {error}", file=sys.stderr)
        return 1
    for name in names:
        path = args.out / name
        print(f"{path.stat().st_size}\t{name}")
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    raise SystemExit(main(sys.argv[1:]))
