#!/usr/bin/env python3
from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

MAX_FILE_SIZE = int(os.environ.get("OTAPI_MAX_TRACKED_FILE_BYTES", 5 * 1024 * 1024))


def tracked_files() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--stage", "-z"],
        check=True,
        stdout=subprocess.PIPE,
    )

    files: list[Path] = []
    for entry in result.stdout.split(b"\0"):
        if not entry:
            continue

        metadata, raw_path = entry.split(b"\t", 1)
        mode = metadata.split(b" ", 1)[0]
        if mode == b"160000":  # gitlink / submodule
            continue

        files.append(Path(os.fsdecode(raw_path)))

    return files


def main() -> int:
    oversized: list[tuple[int, Path]] = []
    for path in tracked_files():
        try:
            size = path.stat().st_size
        except FileNotFoundError:
            continue

        if size > MAX_FILE_SIZE:
            oversized.append((size, path))

    if not oversized:
        print(f"Repository bloat check passed: no tracked file exceeds {MAX_FILE_SIZE / (1024 * 1024):.1f} MiB.")
        return 0

    print("Tracked files exceed the repository size limit:", file=sys.stderr)
    for size, path in sorted(oversized, reverse=True):
        print(f"  {size / (1024 * 1024):8.2f} MiB  {path}", file=sys.stderr)

    print(
        "Store build artifacts in GitHub Actions/Releases and keep large external dependencies as submodules or packages.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
