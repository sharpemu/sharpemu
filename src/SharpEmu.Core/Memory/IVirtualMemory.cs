// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Memory;

public interface IVirtualMemory : ICpuMemory
{
    void Clear();

    bool IsBackedView(ulong address);

    void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection);

    IReadOnlyList<VirtualMemoryRegion> SnapshotRegions();
}
