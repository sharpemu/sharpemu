<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Game Patches

Place PS-Game-Patch XML files in `user/patches/<game-name>.xml` next to SharpEmu.
Right-click a game in the library and open **Patches** to manage entries in a separate
window. Version-specific patches are listed only when they match the installed
game version; `AppVer="mask"` entries use pattern matching across versions. Search or
filter by XML file, and select a patch to see its author, notes, version and target
executable. Selections are saved automatically.

Use **Download patches** in this window to fetch compatible XML files from
[PS4 Game Patches](https://github.com/sharpemu/ps4patches) for PS4 games in GnmMode,
or [PS5 Game Patches](https://github.com/sharpemu/ps5patches) for PS5 games. The
repository is selected automatically from the game's compatibility mode; both
use the same window, installation directory, and patch settings. The download index
matches both title ID and game version; unrelated games and incompatible
versions are not downloaded. A dialog confirms completion or reports that no
compatible patch was found. Downloads are not enabled automatically.

Patches are off by default and take effect on the next
launch, including CLI launches. Selections are saved separately per title ID.

The loader matches `TitleID`, `AppVer` and `AppElf`, relocates addresses using
`ImageBase`, and modifies loaded memory, not game files or `.zar` archives.
Imported PS4 patches explicitly use `ImageBase="0x00400000"` to preserve their
original virtual-address convention when the executable is relocated.
Supported types are `byte`, `bytes16`, `bytes32`, `bytes64`, `bytes`, `float32`,
`float64`, `utf8`, `utf16`, `mask` and `mask_jump32`. Numbers are written in
little-endian order; text is null-terminated automatically. Pattern searches are
restricted to the target executable's loadable segments, and branch patches
require executable segments and valid signed 32-bit displacements.

Invalid lines, unresolved patterns and out-of-range writes reject the whole
patch. `AppVer="mask"` requires every line to use `mask` or `mask_jump32`.
Updating a patch's contents requires enabling it again. Only use patches from
sources you trust. See [PS4 Game Patches](https://github.com/sharpemu/ps4patches)
and [PS5 Game Patches](https://github.com/sharpemu/ps5patches)
for the format reference, examples and available patches.
