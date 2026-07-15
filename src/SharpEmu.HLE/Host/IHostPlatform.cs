// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Aggregates the host-OS primitives the native execution engine depends on.
/// Each supported platform provides one implementation; consumers reach the
/// process-wide instance through <see cref="HostPlatform.Current"/> or accept
/// one by injection.
/// </summary>
public interface IHostPlatform
{
    IHostMemory Memory { get; }

    IHostThreading Threading { get; }

    IHostSymbolResolver Symbols { get; }

    IHostAudioOutput Audio { get; }

    IHostInput Input { get; }
}
