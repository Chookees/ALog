#!/usr/bin/env python3
"""Fail when the packed ALog.dll still names the runtime-internal stat calls."""

from __future__ import annotations

import sys
import zipfile
from pathlib import Path

NEEDLES = (b"SystemNative_", b"libSystem.Native")
DLL = "lib/net10.0/ALog.dll"


def main() -> int:
    """Scan the core nupkg and return 0 when neither needle is present.

    Args:
        None. ``--dir`` is the pack directory and is read from argv.

    Returns:
        0 when the assembly is clean. 1 when the argument or the bytes are wrong.

    Raises:
        SystemExit: Not used. Failures return 1.

    Side effects:
        Reads one nupkg. Writes one line to stdout or stderr.
    """
    if len(sys.argv) != 3 or sys.argv[1] != "--dir":
        print("FAIL usage: eng/test_nupkg_native.py --dir <pack-directory>", file=sys.stderr)
        return 1
    directory = Path(sys.argv[2])
    matches = sorted(path for path in directory.glob("ALog.[0-9]*.nupkg") if path.is_file())
    if len(matches) != 1:
        print(f"FAIL expected one ALog nupkg in {directory}, found {len(matches)}", file=sys.stderr)
        return 1
    nupkg = matches[0]
    with zipfile.ZipFile(nupkg) as package:
        try:
            data = package.read(DLL)
        except KeyError:
            print(f"FAIL {nupkg.name}: missing {DLL}", file=sys.stderr)
            return 1
    for needle in NEEDLES:
        if needle in data:
            print(f"FAIL {nupkg.name}: {DLL} contains {needle.decode('ascii')}", file=sys.stderr)
            return 1
    print(f"nupkg native strings absent: {nupkg.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
