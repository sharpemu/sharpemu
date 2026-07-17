<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

## Release Script

SharpEmu releases are prepared and published using `scripts/release.py`.

The release process consists of two steps:

1. Prepare the version bump through a pull request.
2. Create and push the release tag after the pull request has been merged.

### Preparing a Release

Run:

```bash
python scripts/release.py prepare 0.0.2-beta.2
```

This command will:

- Verify that the working tree is clean.
- Update the local `main` branch.
- Create a new branch named `release/0.0.2-beta.2`.
- Update `SharpEmuVersion` in `Directory.Build.props`.
- Create a version bump commit.
- Push the release branch to the remote repository.

Afterwards, open a pull request from:

```text
release/0.0.2-beta.2
```

into:

```text
main
```

### Publishing a Release

Once the pull request has been merged, update your local repository:

```bash
git switch main
git pull --ff-only
```

Then create and push the release tag:

```bash
python scripts/release.py tag 0.0.2-beta.2
```

This command will:

- Verify that the working tree is clean.
- Confirm that `Directory.Build.props` contains the requested version.
- Create an annotated Git tag (`v0.0.2-beta.2`).
- Push the tag to the remote repository.

Pushing the tag automatically triggers the GitHub Release workflow.

### Version Format

Specify the version **without** the `v` prefix.

Examples:

```text
0.0.2
0.0.2-alpha.1
0.0.2-beta.1
0.0.2-beta.2
0.0.2-rc.1
```

The script automatically prefixes the Git tag with `v`.

### Notes

- Run `prepare` only from the `main` branch.
- Run `tag` only after the version bump pull request has been merged.
- Do not create release tags manually before merging the version bump.
- Both commands require a clean working tree.
- The version in `Directory.Build.props` must exactly match the version passed to the `tag` command.
