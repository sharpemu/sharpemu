// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// One storage buffer view: the device-aligned offset to bind, the range to bind from it, and
// the byte distance from that offset to the guest base, which the shader adds to every address.
internal readonly record struct StorageBufferView(ulong AlignedOffset, ulong Range, uint MemoryOffset);

// Turns the cache buffer offset of a guest range into a bindable storage buffer view.
internal static class StorageBufferViewPlanner
{
    // The memory offset travels as one byte per buffer in the shader data.
    public const uint MaxMemoryOffsetAdjustment = 256;

    public static bool TryPlan(
        ulong offset,
        ulong size,
        ulong alignment,
        ulong maxRange,
        ulong bufferSize,
        bool dwordAddressed,
        out StorageBufferView view,
        out string error)
    {
        view = default;
        error = string.Empty;
        if (alignment == 0 || size > maxRange)
        {
            error = $"The storage buffer range or the device alignment is unsupported: size=0x{size:X} alignment={alignment}.";
            return false;
        }

        var alignedOffset = offset - offset % alignment;
        var adjustment = offset - alignedOffset;
        // Dword accesses address the bound words directly, so only a buffer reached through
        // byte, short or formatted accesses may start mid-dword.
        if ((adjustment % sizeof(uint) != 0 && dwordAddressed) ||
            adjustment >= MaxMemoryOffsetAdjustment ||
            size > maxRange - adjustment)
        {
            error = $"A storage buffer offset adjustment is unsupported: adjustment={adjustment} dword_addressed={dwordAddressed}.";
            return false;
        }

        var range = size + adjustment;
        // The shader indexes a uint[], whose runtime length drops a partial final word. The
        // hardware range-checks an access by its first byte, so a dword load or store that
        // starts inside a range ending mid-word is performed whole, and byte or formatted
        // accesses may start inside it too. Pad every view to whole words when the cache
        // buffer has the room; otherwise the exact range is kept.
        if ((range & 3) != 0)
        {
            var padded = (range + 3) & ~3UL;
            if (padded <= maxRange && alignedOffset + padded <= bufferSize)
            {
                range = padded;
            }
        }

        view = new StorageBufferView(alignedOffset, range, (uint)adjustment);
        return true;
    }
}
