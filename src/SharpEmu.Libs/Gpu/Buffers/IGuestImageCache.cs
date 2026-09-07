// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public readonly record struct ImageRegionInfo(bool ImageBytes, bool GpuImageBytes);

// Image-cache operations used by the buffer cache.
public interface IGuestImageCache
{
    ImageRegionInfo QueryRegion(ulong address, ulong size);

    bool ClearMeta(ulong address);

    void InvalidateMemory(ulong address, ulong size);

    void InvalidateMemoryFromGpu(ulong address, ulong size);

    bool TrySynchronizeBufferFromImage(GpuBuffer buffer, ulong address, ulong size);
}
