// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadRegionAllocationTests
{
    // Two host threads that each claim a nested-callback stack at the same time must get
    // different regions. Map reuses a region that already exists, so a claim that checks
    // for a free slot and maps it in two steps hands both threads the same stack: Ghost of
    // Yotei's render threads then returned into each other's host stacks.
    [Fact]
    public void ConcurrentClaims_GetDistinctRegions()
    {
        var memory = new InterleavingMemory();
        var bases = new ulong[2];
        var claims = Enumerable.Range(0, 2)
            .Select(index => new Thread(() =>
            {
                Assert.True(DirectExecutionBackend.TryMapGuestThreadRegion(
                    memory, 0x7FFF_E000_0000UL, 0x20_0000UL,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write,
                    out bases[index], out var error), error);
            }))
            .ToList();
        claims.ForEach(thread => thread.Start());
        claims.ForEach(thread => thread.Join());

        Assert.NotEqual(bases[0], bases[1]);
    }

    // Region bookkeeping only. SnapshotRegions waits briefly for the other claimer, so two
    // claims that are not serialized both look for a free slot before either maps one.
    private sealed class InterleavingMemory : IVirtualMemory
    {
        private readonly List<VirtualMemoryRegion> _regions = [];
        private readonly Barrier _bothLooking = new(2);

        public void Clear() => _regions.Clear();

        public bool IsBackedView(ulong address) => false;

        public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
        {
            lock (_regions)
            {
                if (!_regions.Any(region => region.VirtualAddress == virtualAddress))
                {
                    _regions.Add(new VirtualMemoryRegion(virtualAddress, memorySize, fileOffset, 0, protection));
                }
            }
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
        {
            _bothLooking.SignalAndWait(TimeSpan.FromMilliseconds(500));
            lock (_regions)
            {
                return [.. _regions];
            }
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
