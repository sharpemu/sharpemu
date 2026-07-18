// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Sparse host backing whose byte ranges can be mapped at more than one
/// virtual address. Implementations own the native section/file descriptor.
/// </summary>
public interface IHostSharedMemory : IDisposable
{
    ulong Size { get; }
}
