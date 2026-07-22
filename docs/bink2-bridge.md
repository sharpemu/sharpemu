<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Bink 2 bridge

Demon's Souls plays Bink 2 (.bk2) files through a Bink implementation linked
directly into eboot.bin. It does not use libSceVideodec, therefore an HLE video
decoder cannot observe or replace those frames.

SharpEmu observes successful guest .bk2 opens and, when a Bink decoder is
available, presents its decoded BGRA frames at the normal guest-flip boundary.
This preserves the game's own timing and lets the host Vulkan presenter display
the movie without trying to execute the PS5-specific Bink GPU decode path.

The default path decodes by calling FFmpeg's own C API directly from managed
code (`src/SharpEmu.Libs/Bink/FfmpegNativeBinkFrameSource.cs`, via the
[FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) P/Invoke
bindings) against a custom FFmpeg build
(`github.com/sharpemu/ffmpeg-core`, LGPL-2.1) that adds a Bink 2 decoder to
FFmpeg 7.1.2; see "Supplying the FFmpeg libraries" below for where those
libraries come from. No proprietary RAD SDK is needed to build or run
SharpEmu, and there is no C/C++ code of SharpEmu's own involved in decoding
-- SharpEmu.CLI.csproj only downloads a prebuilt release archive.

Set `SHARPEMU_BINK_MODE=guest` to leave decoding to the Bink implementation
statically linked into the game instead. Set `skip` only when explicitly
testing a title whose cinematics are optional.

Set SHARPEMU_BINK_MODE=dummy to retain the open and show a built-in,
non-decoded placeholder frame. This requires no SDK, but is a visual diagnostic
only; it does not decode the movie or alter its game logic.
SHARPEMU_BINK_MODE=native is equivalent to the default and mainly useful for
being explicit about it.

The experimental `SHARPEMU_BINK_MODE=ffmpeg` override is unrelated to the
default path above: instead of calling into FFmpeg in-process, it spawns a
standalone `ffmpeg` executable and reads raw frames from its stdout
(`src/SharpEmu.Libs/Bink/FfmpegBinkFrameSource.cs`). SharpEmu searches
`SHARPEMU_FFMPEG_PATH`, the executable directory, its `ffmpeg` subdirectory,
and then `PATH` (plus a couple of common Homebrew paths on macOS). That
`ffmpeg` build must contain a Bink 2 decoder itself; a stock FFmpeg build that
only recognizes the Bink container is not sufficient. Most users want the
default `native` mode instead, which always has Bink 2 support since it's
built against `ffmpeg-core` specifically.

## Supplying the FFmpeg libraries

`dotnet publish` fetches a prebuilt release of `github.com/sharpemu/ffmpeg-core`
(the tag is pinned in `SharpEmu.CLI.csproj`'s `FfmpegRuntimeTag`, matched to
the `FFmpeg.AutoGen` package version in `Directory.Packages.props` -- both
need to agree on the same FFmpeg ABI) and copies its dynamically linked
libraries into a `plugins` folder next to the published executable. No C
toolchain is required to build SharpEmu; publishing just downloads a zip.
`plugins` is a loose, unpacked folder rather than something embedded in the
single-file bundle, so the OS loader can resolve the libraries' own
inter-dependencies (`avcodec` depends on `avutil`, etc.) itself.

A plain `dotnet publish` with no `-r` still works: it defaults to the host
machine's own RID (see `Directory.Build.props`), so it fetches the matching
`ffmpeg-core` archive and populates `plugins` without any extra flags.
Passing an explicit `-r <rid>` (e.g. to cross-publish `linux-x64` from
Windows) still overrides that default normally.

To use a different set of FFmpeg libraries, drop them into the published
`plugins` folder yourself (matching FFmpeg's own file-naming and versioning
conventions, e.g. `avformat-61.dll` / `libavformat.so.61` / matching
`.dylib`) -- `FfmpegNativeBinkFrameSource` points `ffmpeg.RootPath` at that
folder and does not otherwise care where the files came from.

If the libraries are absent or fail to load, `FfmpegNativeBinkFrameSource.TryOpen`
degrades gracefully: SharpEmu logs one informational line ("Bink2 bridge
could not open movie ...") and leaves the guest's own rendering path
untouched, rather than crashing.
