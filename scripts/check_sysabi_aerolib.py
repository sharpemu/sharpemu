# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

#!/usr/bin/env python3

"""Offline check: SysAbiExport ExportName must hash to its Nid (name2nid).

NIDs absent from aerolib.bin are skipped (unknown/unresolved symbols).
Known historic mislabels may be allowlisted with a one-line reason.

Run from the repository root:
  python scripts/check_sysabi_aerolib.py
  python scripts/check_sysabi_aerolib.py --strict
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
import sys
from base64 import b64encode as base64enc
from binascii import unhexlify as uhx
from pathlib import Path

SRC_ROOT = Path("src")
AEROLIB_BIN = Path("src/SharpEmu.HLE/Aerolib/aerolib.bin")
SYSABI_EXPORT_RE = re.compile(r"\[SysAbiExport\((.*?)\)\]", re.DOTALL)
NID_RE = re.compile(r'Nid\s*=\s*"([^"]+)"')
EXPORT_NAME_RE = re.compile(r'ExportName\s*=\s*"([^"]+)"')

# NID -> reason. Keep minimal; fix ExportName when safe instead of growing this list.
ALLOWLISTED_NIDS: dict[str, str] = {
    "KMcEa+rHsIo": "Historic kernel MapMemory stub bound to sceAvPlayerAddSource NID; API rewrite deferred.",
    "WV1GwM32NgY": "Historic WebApi2 init alias for PushEventCreateHandle NID; ABI rewrite deferred.",
}


def name2nid(name: str) -> str:
    symbol = hashlib.sha1(name.encode() + uhx("518D64A635DED8C1E6B039B1C3E55230")).digest()
    id_val = struct.unpack("<Q", symbol[:8])[0]
    nid = base64enc(uhx("%016x" % id_val), b"+-").rstrip(b"=")
    return nid.decode("utf-8")


def find_repo_root() -> Path:
    cwd = Path.cwd()
    if (cwd / SRC_ROOT).is_dir() and (cwd / "scripts").is_dir():
        return cwd
    script_root = Path(__file__).resolve().parent.parent
    if (script_root / SRC_ROOT).is_dir():
        return script_root
    raise SystemExit("Run from the repository root (src/ and scripts/ expected).")


def load_aerolib_nids(aerolib_path: Path) -> set[str]:
    data = aerolib_path.read_bytes()
    if len(data) < 4:
        raise SystemExit(f"Aerolib binary too small: {aerolib_path}")

    count = struct.unpack_from("<I", data, 0)[0]
    offset = 4
    nids: set[str] = set()
    for _ in range(count):
        if offset >= len(data):
            raise SystemExit(f"Truncated aerolib.bin while reading NIDs: {aerolib_path}")
        nid_len = data[offset]
        offset += 1
        nid = data[offset : offset + nid_len].decode("utf-8")
        offset += nid_len
        if offset + 2 > len(data):
            raise SystemExit(f"Truncated aerolib.bin name length: {aerolib_path}")
        name_len = struct.unpack_from("<H", data, offset)[0]
        offset += 2 + name_len
        nids.add(nid)
    return nids


def iter_sysabi_exports(cs_path: Path, text: str):
    for match in SYSABI_EXPORT_RE.finditer(text):
        block = match.group(1)
        nid_match = NID_RE.search(block)
        export_match = EXPORT_NAME_RE.search(block)
        if nid_match is None or export_match is None:
            continue

        nid = nid_match.group(1)
        export_name = export_match.group(1)
        nid_attr = f'Nid = "{nid}"'
        abs_pos = text.find(nid_attr, match.start(), match.end())
        if abs_pos < 0:
            abs_pos = match.start()
        line = text.count("\n", 0, abs_pos) + 1
        yield cs_path, line, nid, export_name


def scan(src_root: Path, catalog_nids: set[str]):
    checked = 0
    mismatches = []
    skipped_no_catalog = 0
    allowlisted = 0

    for cs_path in sorted(src_root.rglob("*.cs")):
        text = cs_path.read_text(encoding="utf-8")
        for path, line, nid, export_name in iter_sysabi_exports(cs_path, text):
            checked += 1
            computed = name2nid(export_name)
            if computed == nid:
                continue

            if nid not in catalog_nids:
                skipped_no_catalog += 1
                continue

            if nid in ALLOWLISTED_NIDS:
                allowlisted += 1
                continue

            mismatches.append((path, line, nid, export_name, computed))

    return checked, mismatches, skipped_no_catalog, allowlisted


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Check that SysAbiExport ExportName values hash to their Nid via name2nid."
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit 1 when any non-skipped/non-allowlisted ExportName does not hash to its Nid.",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="Print only the summary line.",
    )
    args = parser.parse_args()

    repo_root = find_repo_root()
    aerolib_path = repo_root / AEROLIB_BIN
    if not aerolib_path.is_file():
        raise SystemExit(f"Missing Aerolib catalog: {aerolib_path.as_posix()}")

    catalog_nids = load_aerolib_nids(aerolib_path)
    checked, mismatches, skipped_no_catalog, allowlisted = scan(
        repo_root / SRC_ROOT, catalog_nids
    )
    ok = checked - len(mismatches) - skipped_no_catalog - allowlisted

    if not args.quiet:
        for path, line, nid, export_name, computed in mismatches:
            rel = path.relative_to(repo_root).as_posix()
            print(
                f"{rel}:{line}: NID={nid} ExportName={export_name!r} "
                f"computed={computed}"
            )

    print(
        f"checked={checked} ok={ok} fail={len(mismatches)} "
        f"skipped_no_catalog={skipped_no_catalog} allowlisted={allowlisted} "
        f"allowlist_size={len(ALLOWLISTED_NIDS)}"
    )

    if args.strict and mismatches:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
