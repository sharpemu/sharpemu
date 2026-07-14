// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Implemented by memories that decorate another <see cref="ICpuMemory"/>
/// (e.g. access trackers) so capability lookups can unwrap to the real
/// implementation without reflection.
/// </summary>
public interface ICpuMemoryWrapper
{
    ICpuMemory Inner { get; }
}
