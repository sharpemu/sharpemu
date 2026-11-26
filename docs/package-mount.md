<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Running a package without installing it

A fake PS5 package (`.pkg`) can be run straight from the file. SharpEmu mounts the package's inner
image as `/app0`, so the game tree is read out of the package block by block instead of being
unpacked first.

This matters for large games: their **unpacked** size is often several times the download. A game
that ships as a 200 GB package can need well over 500 GB on disk when installed, which many systems
cannot hold next to the package itself. Mounting needs only a few hundred megabytes.

## Using it

* **GUI** – add the folder that holds the package as a library folder. The package appears as a game
  with its own cover and title; choosing Play mounts it.
* **Command line** –

  ```powershell
  .\SharpEmu.exe "D:\Games\Some Game\game.pkg"
  ```

The cache folder under `user/pkg-cache/<package name>` holds the stamp, the `sce_sys` metadata and
the few files host code opens as real files (`eboot.bin`, `sce_module`, `Media/Modules`,
`Media/Plugins`). Everything else stays inside the package.

`--install-pkg` still extracts a package into a normal game folder; that is the only way to run a
PS4 package, a retail (encrypted) PS5 package, or a package whose image has no NAPS layout.

## Limits

* Only fake/debug PS5 packages (`\x7FFIH`) whose outer PFS is not encrypted can be mounted.
* Reading a mounted file decodes its Kraken blocks on demand, at the same speed as extracting the
  same file (roughly 20–25 MB/s per open file on current builds). A game whose world streams from a
  very large archive will load more slowly than from an installed folder.
