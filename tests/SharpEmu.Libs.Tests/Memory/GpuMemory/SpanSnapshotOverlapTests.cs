// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class SpanSnapshotOverlapTests
{
    // The stateful walk visits the same parts in the same order as the closure walk.
    [Fact]
    public void TheStatefulWalkMatchesTheClosureWalk()
    {
        var snapshot = new SpanSnapshot([0x1000, 0x4000, 0x6000], [0x2000, 0x5000, 0x8000]);
        ulong[] addresses = [0, 0xFFF, 0x1000, 0x1800, 0x1FFF, 0x2000, 0x3000, 0x3FFF, 0x4800, 0x5000, 0x5FFF, 0x7FFF,
            0x8000, 0x9000];
        ulong[] sizes = [1, 4, 0x800, 0x1000, 0x3000, 0x8000];
        foreach (var address in addresses)
        foreach (var size in sizes)
        {
            var closure = new List<(ulong, ulong)>();
            snapshot.ForEachOverlap(address, size, (part, partSize) => closure.Add((part, partSize)));
            var stateful = new List<(ulong, ulong)>();
            snapshot.ForEachOverlap(address, size, stateful, static (list, part, partSize) => list.Add((part, partSize)));
            Assert.Equal(closure, stateful);
        }
    }
}
