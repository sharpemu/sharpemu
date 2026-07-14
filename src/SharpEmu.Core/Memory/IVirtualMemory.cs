// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Memory;

public interface IVirtualMemory : ICpuMemory
{
    void Clear();

    void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection);

    /// <summary>
    /// Releases a region previously created with <see cref="Map"/>. The region whose base equals the
    /// page-aligned <paramref name="virtualAddress"/> is unmapped and its address space is reclaimed.
    /// Returns <c>false</c> when no matching region exists.
    /// </summary>
    bool Free(ulong virtualAddress, ulong memorySize);

    IReadOnlyList<VirtualMemoryRegion> SnapshotRegions();
}
