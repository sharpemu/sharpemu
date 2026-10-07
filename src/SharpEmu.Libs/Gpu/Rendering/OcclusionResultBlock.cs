// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.Rendering;

// The 256-byte block a guest occlusion query reads: per depth block, a begin counter at
// 16 * db and an end counter at 16 * db + 8, each valid once bit 63 is set. The guest sums
// end - begin over the depth blocks, so host segment k is published as depth block k's count.
internal static class OcclusionResultBlock
{
    public const int Bytes = 256;
    public const int DepthBlocks = 16;
    public const ulong ReadyBit = 1UL << 63;

    // Reported when the samples could not be counted, so the guest keeps the object visible.
    public const ulong ConservativeCount = 0x00FF_FFFF;

    public static byte[] Build(int segmentCount, bool conservative, ReadOnlySpan<ulong> counts)
    {
        if (segmentCount is < 0 or > DepthBlocks || (!conservative && counts.Length < segmentCount))
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount));
        }

        var block = new byte[Bytes];
        var slots = MemoryMarshal.Cast<byte, ulong>(block.AsSpan());
        slots.Fill(ReadyBit);
        if (conservative)
        {
            slots[1] = ReadyBit | ConservativeCount;
            return block;
        }

        for (var segment = 0; segment < segmentCount; segment++)
        {
            slots[segment * 2 + 1] = ReadyBit | (counts[segment] & (ReadyBit - 1));
        }

        return block;
    }
}
