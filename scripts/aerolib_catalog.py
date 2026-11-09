#!/usr/bin/env python3

# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import argparse
import base64
import hashlib
import re
import sys
from pathlib import Path


NID_SUFFIX = bytes.fromhex("518d64a635ded8c1e6b039b1c3e55230")
NID_PATTERN = re.compile(r"^[A-Za-z0-9+-]{11}$")
DEFAULT_NAMES_FILE = Path(__file__).resolve().with_name("ps5_names.txt")
DEFAULT_EXPORT_FILE = Path(__file__).resolve().parents[1] / "artifacts" / "aerolib.txt"


def compute_nid(export_name: str) -> str:
    digest = hashlib.sha1(export_name.encode("utf-8") + NID_SUFFIX).digest()
    encoded = base64.b64encode(digest[:8][::-1]).decode("ascii")
    return encoded.rstrip("=").replace("/", "-")


def read_names(path: Path) -> list[str]:
    try:
        return [
            line.strip()
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    except OSError as error:
        raise SystemExit(f"Unable to read catalog '{path}': {error}") from error


def write_pair(nid: str, export_name: str) -> None:
    print(f"{nid}\t{export_name}")


def lookup(args: argparse.Namespace) -> int:
    values = [value.strip() for value in args.value.split(",") if value.strip()]
    names = read_names(args.names)
    name_set = set(names)

    result = 0

    for value in values:
        if NID_PATTERN.fullmatch(value):
            found = False

            for export_name in names:
                if compute_nid(export_name) == value:
                    write_pair(value, export_name)
                    found = True
                    break

            if not found:
                print(f"NID not found in catalog: {value}", file=sys.stderr)
                result = 1

            continue

        write_pair(compute_nid(value), value)

        if value not in name_set:
            print(
                f"Warning: export name is not present in the catalog: {value}",
                file=sys.stderr,
            )
    return result


def search(args: argparse.Namespace) -> int:
    names = read_names(args.names)
    if args.regex:
        try:
            pattern = re.compile(args.query, 0 if args.case_sensitive else re.IGNORECASE)
        except re.error as error:
            print(f"Invalid regular expression: {error}", file=sys.stderr)
            return 2

        matches = (name for name in names if pattern.search(name))
    elif args.case_sensitive:
        matches = (name for name in names if args.query in name)
    else:
        query = args.query.casefold()
        matches = (name for name in names if query in name.casefold())

    count = 0
    for export_name in matches:
        write_pair(compute_nid(export_name), export_name)
        count += 1
        if args.limit and count >= args.limit:
            break

    if count == 0:
        print(f"No catalog names matched: {args.query}", file=sys.stderr)
        return 1
    return 0


def export_catalog(args: argparse.Namespace) -> int:
    pairs = [(compute_nid(name), name) for name in read_names(args.names)]
    if args.sort == "nid":
        pairs.sort(key=lambda pair: (pair[0], pair[1]))
    elif args.sort == "name":
        pairs.sort(key=lambda pair: pair[1])

    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with args.output.open("w", encoding="utf-8", newline="\n") as output:
            output.write("# NID\tExportName\n")
            for nid, export_name in pairs:
                output.write(f"{nid}\t{export_name}\n")
    except OSError as error:
        print(f"Unable to write catalog '{args.output}': {error}", file=sys.stderr)
        return 1

    print(f"Wrote {len(pairs)} entries to {args.output}")
    return 0


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Inspect the SharpEmu PS5 export-name/NID catalog.",
        epilog=(
            "Examples:\n"
            "  python scripts/aerolib_catalog.py lookup Zxa0VhQVTsk\n"
            "  python scripts/aerolib_catalog.py lookup sceKernelWaitSema\n"
            "  python scripts/aerolib_catalog.py search VideoOut --limit 20\n"
            "  python scripts/aerolib_catalog.py export"
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--names",
        type=Path,
        default=DEFAULT_NAMES_FILE,
        help=f"source name list (default: {DEFAULT_NAMES_FILE})",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    lookup_parser = subparsers.add_parser(
        "lookup", help="resolve a NID or calculate the NID for an export name"
    )
    lookup_parser.add_argument(
        "value", help="11-character NID or exact export name; comma-separated values are supported"
    )
    lookup_parser.set_defaults(handler=lookup)

    search_parser = subparsers.add_parser(
        "search", help="find export names and print matching NID/name pairs"
    )
    search_parser.add_argument("query", help="name substring or regular expression")
    search_parser.add_argument(
        "--limit", type=int, default=50, help="maximum matches; 0 means unlimited"
    )
    search_parser.add_argument(
        "--case-sensitive", action="store_true", help="match case exactly"
    )
    search_parser.add_argument(
        "--regex", action="store_true", help="treat the query as a regular expression"
    )
    search_parser.set_defaults(handler=search)

    export_parser = subparsers.add_parser(
        "export", help="write every NID/name pair to a tab-separated text file"
    )
    export_parser.add_argument(
        "output",
        type=Path,
        nargs="?",
        default=DEFAULT_EXPORT_FILE,
        help=f"output file (default: {DEFAULT_EXPORT_FILE})",
    )
    export_parser.add_argument(
        "--sort",
        choices=("source", "nid", "name"),
        default="nid",
        help="output ordering (default: nid)",
    )
    export_parser.set_defaults(handler=export_catalog)

    return parser


def main() -> int:
    parser = create_parser()
    args = parser.parse_args()
    return args.handler(args)


if __name__ == "__main__":
    raise SystemExit(main())
