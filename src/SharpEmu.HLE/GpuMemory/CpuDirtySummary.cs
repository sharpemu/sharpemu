// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// One bit per tracked block: set while the block has at least one CPU-dirty page.
// Regions update their bit under their own lock whenever their CPU-dirty mask
// changes, so a clear bit lets a reader skip the block without taking any lock.
// A reader that must observe a CPU write is ordered after it by the guest's own
// submission, which happens after the write fault published the bit.
public sealed class CpuDirtySummary
{
    private const int WordBits = 64;
    private readonly long[] _words = new long[(TrackerLayout.BlockCount + WordBits - 1) / WordBits];

    public void Set(ulong block, bool dirty)
    {
        var bit = 1L << (int)(block % WordBits);
        ref var word = ref _words[block / WordBits];
        if (dirty)
        {
            Interlocked.Or(ref word, bit);
        }
        else
        {
            Interlocked.And(ref word, ~bit);
        }
    }

    public bool IsDirty(ulong block) => (Volatile.Read(ref _words[block / WordBits]) & (1L << (int)(block % WordBits))) != 0;
}
