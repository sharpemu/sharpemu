// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Gpu.Images;

// A texture streamed into its address range maps only its resident mips. A tiled chain puts
// the mip tail and small levels at the start and level 0 at the end, so the resident part is
// a backed prefix. The GPU samples only resident levels; the unmapped rest reads as zero.
internal static class ResidentImageBacking
{
    private const ulong Page = 1UL << 12;

    // SHARPEMU_IMAGE_BACKING_HOLES=1 (default off): unmapped pages anywhere after the first are zero-filled instead of
    // failing. Games sample images whose backing has a backed page after a hole, and that failure is
    // fatal.
    private static readonly bool AllowHoles =
        Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BACKING_HOLES") == "1";

    // Reads image bytes from CPU-visible backing or PRT backing. A streamed texture's image holds only its
    // resident levels and its upload reads only their bytes, so an unmapped byte inside them is an error,
    // not a level to zero; SHARPEMU_IMAGE_BACKING_HOLES=1 still zero-fills the unmapped pages instead.
    internal static bool TryRead(IGuestBackedSpace backing, ulong address, Span<byte> destination) =>
        backing.TryReadBacking(address, destination) ||
        KernelMemoryCompatExports.TryReadPrtBacking(backing, address, destination) ||
        AllowHoles && TryReadResidentPrefix(backing, address, destination, allowHoles: true);

    // Copies the backed prefix page by page and zero-fills the unmapped tail. A backed page
    // after a hole is not a mip chain, so it is rejected unless allowHoles.
    internal static bool TryReadResidentPrefix(IGuestBackedSpace backing, ulong address, Span<byte> destination) =>
        TryReadResidentPrefix(backing, address, destination, AllowHoles);

    internal static bool TryReadResidentPrefix(IGuestBackedSpace backing, ulong address, Span<byte> destination, bool allowHoles)
    {
        var size = (ulong)destination.Length;
        if (size == 0 || !backing.IsBackedRange(address, 1))
        {
            return false;
        }

        var resident = true;
        for (ulong offset = 0; offset < size;)
        {
            var chunk = Math.Min(Page - ((address + offset) & (Page - 1)), size - offset);
            var target = destination.Slice((int)offset, (int)chunk);
            var backed = backing.IsBackedRange(address + offset, chunk);
            if (backed && !resident && !allowHoles)
            {
                return false;
            }

            resident = backed && backing.TryReadBacking(address + offset, target);
            if (!resident)
            {
                target.Clear();
            }

            offset += chunk;
        }

        return true;
    }
}
