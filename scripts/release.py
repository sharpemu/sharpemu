#!/usr/bin/env python3

# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path


VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")
VERSION_ELEMENT_PATTERN = re.compile(
    r"(<SharpEmuVersion>)([^<]+)(</SharpEmuVersion>)"
)


class ReleaseError(RuntimeError):
    pass


def run_git(
    *args: str,
    cwd: Path,
    capture_output: bool = False,
) -> str:
    command = ["git", *args]

    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            check=True,
            text=True,
            capture_output=capture_output,
        )
    except FileNotFoundError:
        raise ReleaseError("Git was not found in PATH.") from None
    except subprocess.CalledProcessError as error:
        stderr = error.stderr.strip() if error.stderr else ""
        detail = f"\n{stderr}" if stderr else ""

        raise ReleaseError(
            f"Git command failed: {' '.join(command)}{detail}"
        ) from error

    return result.stdout.strip() if capture_output else ""


def find_repository_root(script_path: Path) -> Path:
    root = run_git(
        "rev-parse",
        "--show-toplevel",
        cwd=script_path.resolve().parent,
        capture_output=True,
    )

    return Path(root)


def get_status(repository_root: Path) -> str:
    return run_git(
        "status",
        "--porcelain",
        cwd=repository_root,
        capture_output=True,
    )


def ensure_clean_worktree(repository_root: Path) -> None:
    status = get_status(repository_root)

    if status:
        raise ReleaseError(
            "The working tree is not clean.\n\n"
            f"{status}\n\n"
            "Commit, stash, or remove these changes first."
        )


def get_current_branch(repository_root: Path) -> str:
    branch = run_git(
        "branch",
        "--show-current",
        cwd=repository_root,
        capture_output=True,
    )

    if not branch:
        raise ReleaseError(
            "HEAD is detached. Switch to a branch before continuing."
        )

    return branch


def ensure_branch_does_not_exist(
    repository_root: Path,
    branch: str,
    remote: str,
) -> None:
    local_branch = run_git(
        "branch",
        "--list",
        branch,
        cwd=repository_root,
        capture_output=True,
    )

    if local_branch:
        raise ReleaseError(f"Branch {branch} already exists locally.")

    remote_branch = run_git(
        "ls-remote",
        "--heads",
        remote,
        branch,
        cwd=repository_root,
        capture_output=True,
    )

    if remote_branch:
        raise ReleaseError(
            f"Branch {branch} already exists on {remote}."
        )


def ensure_tag_does_not_exist(
    repository_root: Path,
    tag: str,
    remote: str,
) -> None:
    local_tag = run_git(
        "tag",
        "--list",
        tag,
        cwd=repository_root,
        capture_output=True,
    )

    if local_tag:
        raise ReleaseError(f"Tag {tag} already exists locally.")

    remote_tag = run_git(
        "ls-remote",
        "--tags",
        remote,
        f"refs/tags/{tag}",
        cwd=repository_root,
        capture_output=True,
    )

    if remote_tag:
        raise ReleaseError(f"Tag {tag} already exists on {remote}.")


def read_version(props_path: Path) -> str:
    if not props_path.exists():
        raise ReleaseError(f"Version file not found: {props_path}")

    content = props_path.read_text(encoding="utf-8")
    match = VERSION_ELEMENT_PATTERN.search(content)

    if match is None:
        raise ReleaseError(
            f"SharpEmuVersion was not found in {props_path.name}."
        )

    return match.group(2).strip()


def update_version(props_path: Path, version: str) -> str:
    content = props_path.read_text(encoding="utf-8")
    current_version = read_version(props_path)

    if current_version == version:
        raise ReleaseError(
            f"SharpEmuVersion is already set to {version}."
        )

    updated_content, replacement_count = VERSION_ELEMENT_PATTERN.subn(
        rf"\g<1>{version}\g<3>",
        content,
        count=1,
    )

    if replacement_count != 1:
        raise ReleaseError(
            "Expected exactly one SharpEmuVersion element."
        )

    props_path.write_text(
        updated_content,
        encoding="utf-8",
        newline="\n",
    )

    return current_version


def prepare_release(
    repository_root: Path,
    props_path: Path,
    version: str,
    remote: str,
) -> None:
    ensure_clean_worktree(repository_root)

    current_branch = get_current_branch(repository_root)

    if current_branch != "main":
        raise ReleaseError(
            f"Prepare must be run from main, not {current_branch}."
        )

    run_git(
        "pull",
        "--ff-only",
        remote,
        "main",
        cwd=repository_root,
    )

    branch = f"release/{version}"
    ensure_branch_does_not_exist(
        repository_root,
        branch,
        remote,
    )

    previous_version = read_version(props_path)

    run_git(
        "switch",
        "-c",
        branch,
        cwd=repository_root,
    )

    try:
        update_version(props_path, version)

        relative_props_path = props_path.relative_to(repository_root)

        run_git(
            "add",
            relative_props_path.as_posix(),
            cwd=repository_root,
        )
        run_git(
            "commit",
            "-m",
            f"chore: bump version to {version}",
            cwd=repository_root,
        )
        run_git(
            "push",
            "-u",
            remote,
            branch,
            cwd=repository_root,
        )
    except Exception:
        print(
            "\nPrepare failed. The release branch may still exist locally.",
            file=sys.stderr,
        )
        raise

    print()
    print(f"Prepared release {previous_version} -> {version}")
    print(f"Branch pushed: {branch}")
    print()
    print("Open a pull request from:")
    print(f"  {branch}")
    print("into:")
    print("  main")
    print()
    print("After merging the PR, run:")
    print(f"  python scripts/release.py tag {version}")


def create_release_tag(
    repository_root: Path,
    props_path: Path,
    version: str,
    remote: str,
) -> None:
    ensure_clean_worktree(repository_root)

    current_branch = get_current_branch(repository_root)

    if current_branch != "main":
        raise ReleaseError(
            f"Tagging must be run from main, not {current_branch}."
        )

    run_git(
        "pull",
        "--ff-only",
        remote,
        "main",
        cwd=repository_root,
    )

    current_version = read_version(props_path)

    if current_version != version:
        raise ReleaseError(
            "Version mismatch:\n"
            f"  Directory.Build.props: {current_version}\n"
            f"  Requested tag:         {version}"
        )

    tag = f"v{version}"

    ensure_tag_does_not_exist(
        repository_root,
        tag,
        remote,
    )

    run_git(
        "tag",
        "-a",
        tag,
        "-m",
        f"SharpEmu {version}",
        cwd=repository_root,
    )
    run_git(
        "push",
        remote,
        tag,
        cwd=repository_root,
    )

    print()
    print(f"Successfully pushed tag {tag}.")
    print("The release workflow should start automatically.")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare or tag a SharpEmu release."
    )

    subparsers = parser.add_subparsers(
        dest="command",
        required=True,
    )

    for command in ("prepare", "tag"):
        subparser = subparsers.add_parser(command)
        subparser.add_argument(
            "version",
            help="Version without the v prefix, e.g. 0.0.2-beta.2.",
        )
        subparser.add_argument(
            "--remote",
            default="origin",
            help="Git remote. Default: origin.",
        )

    arguments = parser.parse_args()

    if not VERSION_PATTERN.fullmatch(arguments.version):
        parser.error(
            "Version must look like 0.0.2, "
            "0.0.2-beta.2, or 0.0.2-rc.1."
        )

    return arguments


def main() -> int:
    arguments = parse_arguments()

    try:
        repository_root = find_repository_root(Path(__file__))
        props_path = repository_root / "Directory.Build.props"

        if arguments.command == "prepare":
            prepare_release(
                repository_root,
                props_path,
                arguments.version,
                arguments.remote,
            )
        else:
            create_release_tag(
                repository_root,
                props_path,
                arguments.version,
                arguments.remote,
            )
    except ReleaseError as error:
        print(f"Error: {error}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
