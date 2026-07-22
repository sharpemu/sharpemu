<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Bink 2 bridge

Demon's Souls plays Bink 2 (.bk2) files through a Bink implementation linked
directly into eboot.bin. It does not use libSceVideodec, therefore an HLE video
decoder cannot observe or replace those frames.

SharpEmu observes successful guest .bk2 opens and, when a Bink bridge is
available, presents its decoded BGRA frames at the normal guest-flip boundary.
This preserves the game's own timing and lets the host Vulkan presenter display
the movie without trying to execute the PS5-specific Bink GPU decode path.

The default path decodes through the bundled FFmpeg-backed native bridge
(`native/bink2-bridge/sharpemu_bink2_bridge.c`); see "Supplying the adapter"
below for where that binary comes from. Set `SHARPEMU_BINK_MODE=guest` to
leave decoding to the Bink implementation statically linked into the game
instead. Set `skip` only when explicitly testing a title whose cinematics are
optional.

Set SHARPEMU_BINK_MODE=dummy to retain the open and show a built-in,
non-decoded placeholder frame. This requires no SDK, but is a visual diagnostic
only; it does not decode the movie or alter its game logic.
SHARPEMU_BINK_MODE=native is equivalent to the default and mainly useful for
being explicit about it.

The experimental `SHARPEMU_BINK_MODE=ffmpeg` override forces a host FFmpeg
source. SharpEmu searches
`SHARPEMU_FFMPEG_PATH`, the executable directory, its `ffmpeg` subdirectory,
and then `PATH`. The FFmpeg build must contain the Bink 2 decoder; stock FFmpeg
builds that only recognize the Bink container are not sufficient.

## Supplying the adapter

The adapter (`native/bink2-bridge/sharpemu_bink2_bridge.c`) links against a
custom FFmpeg build (`github.com/sharpemu/ffmpeg-core`, LGPL-2.1) that adds a
Bink 2 decoder to FFmpeg 7.1.2; no proprietary RAD SDK is needed to build or
run SharpEmu.

`dotnet publish` builds it from source with CMake + Ninja + clang-cl (Windows)
or the platform's default C compiler (Linux/macOS), targeting win-x64,
linux-x64, osx-x64, or osx-arm64, then embeds the result in the published
single-file executable. Publishing SharpEmu therefore requires that
toolchain locally (the same one the CI runners already ship with); there is
no prebuilt/download fallback. A downloaded release needs no such setup: the
compiled adapter is already inside `SharpEmu.exe`.

To use a different build of the adapter, point to it explicitly:

    SHARPEMU_BINK2_BRIDGE=/absolute/path/sharpemu_bink2_bridge.dll \
      ./SharpEmu /path/to/eboot.bin

The expected exports are sharpemu_bink2_open_utf8,
sharpemu_bink2_open_scaled_utf8, sharpemu_bink2_decode_next_bgra, and
sharpemu_bink2_close. The adapter opens one movie, optionally scaling it down
to a maximum size, exposes BGRA pixels, and advances after each decoded
frame. The managed side validates dimensions and retains ownership of the
destination buffer.

If the bridge is absent in native mode, SharpEmu logs one informational line
and retains the existing guest rendering path.
