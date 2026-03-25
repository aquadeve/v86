#!/usr/bin/env python3
"""
disk2folder.py - Convert a raw disk image to a folder-based layout for v86.

The output directory will contain:
  manifest.json          - metadata (disk size, chunk size)
  <start>-<end>          - raw binary chunks named by byte range

The resulting directory can be served by any static HTTP server and used
as a disk image in v86 with:

    hda: { url: "ROOT_DISK/", type: "folder" }

Usage:
    disk2folder.py [--chunk-size SIZE] <disk-image> <output-directory>

Options:
    --chunk-size SIZE   Chunk size in bytes (default: 1048576 = 1 MiB).
                        Suffix 'k'/'m' is accepted (e.g. 4m, 512k).

Example:
    # Convert disk.img into ROOT_DISK/ with 4 MiB chunks
    python3 tools/disk2folder.py --chunk-size 4m disk.img ROOT_DISK

    # Serve it (Python's built-in server works fine)
    python3 -m http.server 8080

    # Then in your HTML:
    hda: { url: "http://localhost:8080/ROOT_DISK/", type: "folder" }
"""

import argparse
import json
import os
import sys


def parse_size(s: str) -> int:
    s = s.strip().lower()
    if s.endswith("mb") or s.endswith("m"):
        return int(s.rstrip("mb").rstrip("m")) * 1024 * 1024
    if s.endswith("kb") or s.endswith("k"):
        return int(s.rstrip("kb").rstrip("k")) * 1024
    return int(s)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Convert a raw disk image to a v86 folder-disk layout.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("disk_image", help="Path to the source raw disk image")
    parser.add_argument("output_dir", help="Output directory (created if needed)")
    parser.add_argument(
        "--chunk-size",
        default="1m",
        metavar="SIZE",
        help="Chunk size (default: 1m = 1 MiB). Accepts k/m suffix.",
    )
    args = parser.parse_args()

    chunk_size = parse_size(args.chunk_size)
    if chunk_size <= 0 or chunk_size % 512 != 0:
        print("Error: chunk-size must be a positive multiple of 512 bytes.", file=sys.stderr)
        sys.exit(1)

    disk_path = args.disk_image
    out_dir = args.output_dir

    if not os.path.isfile(disk_path):
        print(f"Error: disk image not found: {disk_path}", file=sys.stderr)
        sys.exit(1)

    disk_size = os.path.getsize(disk_path)
    if disk_size == 0:
        print("Error: disk image is empty.", file=sys.stderr)
        sys.exit(1)

    os.makedirs(out_dir, exist_ok=True)

    num_chunks = (disk_size + chunk_size - 1) // chunk_size
    print(
        f"Disk size: {disk_size} bytes  |  chunk size: {chunk_size} bytes  |  chunks: {num_chunks}"
    )

    with open(disk_path, "rb") as f:
        offset = 0
        chunk_index = 0
        while offset < disk_size:
            chunk = f.read(chunk_size)
            end = offset + len(chunk)

            # Pad the last chunk to a full chunk_size.  The chunk filename
            # always uses {offset}-{offset+chunk_size} (same convention as
            # split-image.py and AsyncXHRPartfileBuffer) so FolderBuffer can
            # derive the URL without consulting a file list.  The manifest
            # "size" field records the true disk size so reads never go beyond
            # the actual data.
            if len(chunk) < chunk_size:
                chunk = chunk + bytes(chunk_size - len(chunk))

            chunk_filename = f"{offset}-{offset + chunk_size}"
            chunk_path = os.path.join(out_dir, chunk_filename)
            with open(chunk_path, "wb") as cf:
                cf.write(chunk)

            chunk_index += 1
            print(f"  [{chunk_index}/{num_chunks}] {chunk_filename} ({len(chunk)} bytes)")
            offset = end

    manifest = {
        "version": 1,
        "size": disk_size,
        "block_size": chunk_size,
    }
    manifest_path = os.path.join(out_dir, "manifest.json")
    with open(manifest_path, "w") as mf:
        json.dump(manifest, mf, indent=2)
        mf.write("\n")

    print(f"\nDone. Output: {out_dir}/")
    print(f"  manifest.json: size={disk_size}, block_size={chunk_size}")
    print()
    print("To use in v86:")
    print(f'    hda: {{ url: "{out_dir}/", type: "folder" }}')


if __name__ == "__main__":
    main()
