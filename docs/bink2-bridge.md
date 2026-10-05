<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Bink 2 bridge

Demon's Souls plays Bink 2 (.bk2) files through a Bink implementation linked
directly into eboot.bin. It does not use libSceVideodec, therefore an HLE video
decoder cannot observe or replace those frames.

By default, SharpEmu leaves Bink decoding to the implementation linked into
the game (`SHARPEMU_BINK_MODE=guest`).

Set `SHARPEMU_BINK_MODE=native` to use the optional host bridge. It observes
guest .bk2 opens and presents decoded frames at the guest-flip boundary. The
host path decodes by calling FFmpeg's C API directly from managed
code (`src/SharpEmu.Libs/Bink/FfmpegNativeBinkFrameSource.cs`, via the
[FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) P/Invoke
bindings) against a custom FFmpeg build
(`github.com/sharpemu/ffmpeg-core`, LGPL-2.1) that adds a Bink 2 decoder to
FFmpeg 7.1.2; see "Supplying the FFmpeg libraries" below for where those
libraries come from. No proprietary RAD SDK is needed to build or run
SharpEmu, and there is no C/C++ code of SharpEmu's own involved in decoding
-- SharpEmu.CLI.csproj only downloads a prebuilt release archive.

Set `SHARPEMU_BINK_MODE=skip` only when explicitly testing a title whose
cinematics are optional.

Set SHARPEMU_BINK_MODE=dummy to retain the open and show a built-in,
non-decoded placeholder frame. This requires no SDK, but is a visual diagnostic
only; it does not decode the movie or alter its game logic.
`SHARPEMU_BINK_MODE=ffmpeg` is an alias for `native`.

## Supplying the FFmpeg libraries

Both `dotnet build` and `dotnet publish` fetch a prebuilt release of
`github.com/sharpemu/ffmpeg-core` (the tag is pinned in
`SharpEmu.CLI.csproj`'s `FfmpegRuntimeTag`, matched to the `FFmpeg.AutoGen`
package version in `Directory.Packages.props` -- both need to agree on the
same FFmpeg ABI) and copy its dynamically linked libraries into a `plugins`
folder next to the resulting executable (`artifacts/bin/...` for build,
`artifacts/publish/...` for publish). No C toolchain is required to build
SharpEmu; both just download a zip once (cached under
`$(BaseIntermediateOutputPath)ffmpeg-runtime/`, so later builds/publishes
reuse it instead of re-fetching). `plugins` is a loose, unpacked folder
rather than something embedded in the single-file bundle, so the OS loader
can resolve the libraries' own inter-dependencies (`avcodec` depends on
`avutil`, etc.) itself.

A plain `dotnet build`/`dotnet publish` with no `-r` still works: it defaults
to the host machine's own RID (see `Directory.Build.props`), so it fetches
the matching `ffmpeg-core` archive and populates `plugins` without any extra
flags. Passing an explicit `-r <rid>` (e.g. to cross-publish `linux-x64` from
Windows) still overrides that default normally.

To use a different set of FFmpeg libraries, drop them into the build or
published `plugins` folder yourself (matching FFmpeg's own file-naming and versioning
conventions, e.g. `avformat-61.dll` / `libavformat.so.61` / matching
`.dylib`) -- `FfmpegNativeBinkFrameSource` points `ffmpeg.RootPath` at that
folder and does not otherwise care where the files came from.

If the libraries are absent or fail to load, `FfmpegNativeBinkFrameSource.TryOpen`
degrades gracefully: SharpEmu logs one informational line ("Bink2 bridge
could not open movie ...") and leaves the guest's own rendering path
untouched, rather than crashing.
