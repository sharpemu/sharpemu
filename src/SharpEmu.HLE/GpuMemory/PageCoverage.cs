// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public sealed class PageCoverage
{
    internal sealed class Block
    {
        public PageMask Pages;
        public int Version;
        public int CleanRegionVersion = -1;
        public int CleanCoverageVersion = -1;
    }

    private readonly Block?[] _blocks = new Block?[TrackerLayout.BlockCount];
    private readonly ulong[] _blockMask = new ulong[(TrackerLayout.BlockCount + 63) / 64];

    public bool Complete { get; private set; } = true;

    internal ulong BlockWord(int word) => _blockMask[word];

    internal Block? BlockAt(ulong block) => _blocks[block];

    public void Set(ulong vaddr, ulong size, bool covered)
    {
        if (size == 0 || !new GuestSpan(vaddr, size).IsValid)
        {
            Complete &= !covered;
            return;
        }

        var end = vaddr + size;
        var last = (end - 1) / TrackerLayout.BlockBytes;
        for (var index = vaddr / TrackerLayout.BlockBytes; index <= last; index++)
        {
            var blockStart = index * TrackerLayout.BlockBytes;
            var first = (int)((Math.Max(vaddr, blockStart) - blockStart) / TrackerLayout.PageBytes);
            var stop = (int)((Math.Min(end, blockStart + TrackerLayout.BlockBytes) - blockStart + TrackerLayout.PageBytes - 1) / TrackerLayout.PageBytes);
            var block = _blocks[index];
            if (block is null)
            {
                if (!covered)
                {
                    continue;
                }

                block = _blocks[index] = new Block();
            }

            if (covered)
            {
                block.Pages.SetRange(first, stop);
                _blockMask[index / 64] |= 1UL << (int)(index % 64);
            }
            else
            {
                block.Pages.UnsetRange(first, stop);
                if (block.Pages.None)
                {
                    _blockMask[index / 64] &= ~(1UL << (int)(index % 64));
                }
            }

            block.Version++;
        }
    }
}
