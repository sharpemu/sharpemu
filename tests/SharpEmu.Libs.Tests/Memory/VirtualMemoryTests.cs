// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class VirtualMemoryTests
{
    [Fact]
    public void OutOfOrderMappingsRemainSortedAndResolveAtBoundaries()
    {
        var memory = new VirtualMemory();
        memory.Map(0x3000, 0x100, 0, [3], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(0x1000, 0x100, 0, [1], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(0x2000, 0x100, 0, [2], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        Assert.Equal([0x1000UL, 0x2000UL, 0x3000UL], memory.SnapshotRegions().Select(region => region.VirtualAddress));

        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(0x1000, value));
        Assert.Equal(1, value[0]);
        Assert.True(memory.TryRead(0x2000, value));
        Assert.Equal(2, value[0]);
        Assert.True(memory.TryRead(0x3000, value));
        Assert.Equal(3, value[0]);
        Assert.False(memory.TryRead(0x1100, value));
        Assert.False(memory.TryRead(0x0FFF, value));
    }

    [Fact]
    public void MappingRejectsOverlapWithEitherNeighbor()
    {
        var memory = new VirtualMemory();
        memory.Map(0x2000, 0x100, 0, [], ProgramHeaderFlags.Read);
        memory.Map(0x4000, 0x100, 0, [], ProgramHeaderFlags.Read);

        Assert.Throws<InvalidOperationException>(() =>
            memory.Map(0x1FFF, 2, 0, [], ProgramHeaderFlags.Read));
        Assert.Throws<InvalidOperationException>(() =>
            memory.Map(0x3FFF, 2, 0, [], ProgramHeaderFlags.Read));

        memory.Map(0x2100, 0x1F00, 0, [], ProgramHeaderFlags.Read);
        Assert.Equal(3, memory.SnapshotRegions().Count);
    }

    [Fact]
    public void ReadAndWriteSpanAdjacentRegions()
    {
        var memory = new VirtualMemory();
        const ProgramHeaderFlags protection = ProgramHeaderFlags.Read | ProgramHeaderFlags.Write;
        memory.Map(0x1000, 4, 0, [1, 2, 3, 4], protection);
        memory.Map(0x1004, 4, 0, [5, 6, 7, 8], protection);

        Span<byte> read = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1002, read));
        Assert.Equal([3, 4, 5, 6], read.ToArray());

        Assert.True(memory.TryWrite(0x1002, [9, 10, 11, 12]));
        Assert.True(memory.TryRead(0x1000, read));
        Assert.Equal([1, 2, 9, 10], read.ToArray());
        Assert.True(memory.TryRead(0x1004, read));
        Assert.Equal([11, 12, 7, 8], read.ToArray());
    }

    [Fact]
    public void AccessRequiresPermissionAcrossEntireRange()
    {
        var memory = new VirtualMemory();
        memory.Map(0x1000, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(0x1004, 4, 0, [5, 6, 7, 8], ProgramHeaderFlags.Read);
        memory.Map(0x2000, 4, 0, [1, 2, 3, 4], ProgramHeaderFlags.Execute);
        memory.Map(0x3000, 4, 0, [], ProgramHeaderFlags.Write);

        Assert.False(memory.TryWrite(0x1002, [9, 9, 9, 9]));
        Span<byte> unchanged = stackalloc byte[4];
        Assert.True(memory.TryRead(0x1000, unchanged));
        Assert.Equal([1, 2, 3, 4], unchanged.ToArray());

        Assert.False(memory.TryRead(0x2000, unchanged));
        Assert.False(memory.TryWrite(0x2000, [9]));
        Assert.False(memory.TryRead(0x3000, unchanged));
        Assert.True(memory.TryWrite(0x3000, [9]));
    }

    [Fact]
    public void TryReadAndTryWriteOnUnmappedAddressReturnFalse()
    {
        var memory = new VirtualMemory();
        memory.Map(0x1000, 0x100, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        // Addresses with no covering region fail for both reads and writes.
        Span<byte> value = stackalloc byte[1];
        Assert.False(memory.TryRead(0x2000, value));
        Assert.False(memory.TryWrite(0x2000, value));

        // With no regions mapped at all, every access fails.
        var empty = new VirtualMemory();
        Assert.False(empty.TryRead(0x1000, value));
        Assert.False(empty.TryWrite(0x1000, value));
    }

    // Zero-length operations succeed on a mapped address and touch nothing, but
    // still require a mapped address.
    [Fact]
    public void ZeroLengthOperationsOnMappedAddressReturnTrue()
    {
        var memory = new VirtualMemory();
        memory.Map(0x1000, 0x100, 0, [0x01], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        Assert.True(memory.TryRead(0x1000, []));
        Assert.True(memory.TryWrite(0x1000, []));

        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(0x1000, value));
        Assert.Equal(0x01, value[0]);

        Assert.False(memory.TryRead(0x2000, []));
        Assert.False(memory.TryWrite(0x2000, []));
    }

    // A page-aligned region must be accessible at both offset 0 and the final byte.
    [Fact]
    public void MappingAtPageBoundarySupportsOffsetZeroAndLastByte()
    {
        const ulong pageSize = 0x1000;
        var memory = new VirtualMemory();
        memory.Map(pageSize, pageSize, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        Assert.True(memory.TryWrite(pageSize, [0x5A]));
        Assert.True(memory.TryWrite(pageSize + pageSize - 1, [0xA5]));

        Span<byte> head = stackalloc byte[1];
        Span<byte> tail = stackalloc byte[1];
        Assert.True(memory.TryRead(pageSize, head));
        Assert.True(memory.TryRead(pageSize + pageSize - 1, tail));
        Assert.Equal(0x5A, head[0]);
        Assert.Equal(0xA5, tail[0]);
    }

    // A read/write that starts in one region but extends into the unmapped gap
    // between two non-adjacent regions must fail across the entire range.
    [Fact]
    public void ReadWriteAcrossGapBetweenNonAdjacentRegionsReturnsFalse()
    {
        const ulong pageSize = 0x1000;
        var memory = new VirtualMemory();
        const ProgramHeaderFlags protection = ProgramHeaderFlags.Read | ProgramHeaderFlags.Write;
        memory.Map(pageSize, pageSize, 0, [], protection);
        memory.Map(pageSize * 3, pageSize, 0, [], protection);

        Assert.False(memory.TryRead(pageSize, new byte[(int)pageSize * 2]));
        Assert.False(memory.TryWrite(pageSize, new byte[(int)pageSize * 2]));
    }
}
