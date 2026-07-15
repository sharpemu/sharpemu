// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new PosixHostMemory();

    public IHostThreading Threading { get; } = new PosixHostThreading();

    public IHostSymbolResolver Symbols { get; } = new PosixHostSymbolResolver();

    public IHostAudioOutput Audio { get; } = new PosixHostAudio();

    public IHostInput Input { get; } = new PosixHostInput();
}
