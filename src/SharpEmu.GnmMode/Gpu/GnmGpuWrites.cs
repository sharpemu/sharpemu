// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.GnmMode.Gpu;

internal static class GnmGpuWrites
{
    internal static GuestBufferCache? Buffers => GuestGpuMemoryHook.Current?.Buffers as GuestBufferCache;

    internal static bool Overlap(ulong address, ulong size) =>
        GuestGpuMemoryHook.IsWithinGpuAddressSpace(address, size) && Buffers is { } buffers &&
        (buffers.HasGpuDirtyBytes(address, size) ||
            buffers.MayHaveGpuDirtyPages(address, size) && buffers.HasGpuDirtyPages(address, size));

    internal static bool MayOverlap(ulong address, ulong size) =>
        GuestGpuMemoryHook.IsWithinGpuAddressSpace(address, size) && Buffers?.MayHaveGpuDirtyPages(address, size) == true;
}
