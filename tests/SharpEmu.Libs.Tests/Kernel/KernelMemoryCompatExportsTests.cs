// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelMemoryCompatExportsTests
{
    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    // Helper: release a direct-memory block allocated during a test, keeping the
    // static _directAllocations table clean for subsequent tests.
    private static void ReleaseDirectMemory(ICpuMemory memory, ulong start, ulong length)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = start;
        ctx[CpuRegister.Rsi] = length;
        KernelMemoryCompatExports.KernelReleaseDirectMemory(ctx);
    }

    // Helper: allocate one direct-memory block and return its physical start offset.
    private static ulong AllocateDirectMemory(
        ICpuMemory memory, ulong outAddress, ulong searchStart, ulong length)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = searchStart;
        ctx[CpuRegister.Rsi] = 0;           // searchEnd = full range
        ctx[CpuRegister.Rdx] = length;
        ctx[CpuRegister.Rcx] = 0;           // alignment = default
        ctx[CpuRegister.R8] = 0x0C;         // memoryType = GPU_CACHEABLE
        ctx[CpuRegister.R9] = outAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(ctx));
        Span<byte> buf = stackalloc byte[8];
        Assert.True(memory.TryRead(outAddress, buf));
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    // Helper: query and return the physical start written into the info struct, or null.
    private static ulong? QueryDirectMemory(
        ICpuMemory memory, ulong infoAddress, ulong offset, ulong flags)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = offset;
        ctx[CpuRegister.Rsi] = flags;
        ctx[CpuRegister.Rdx] = infoAddress;
        ctx[CpuRegister.Rcx] = 24;
        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(ctx);
        if (result != 0)
        {
            return null;
        }

        Span<byte> info = stackalloc byte[24];
        Assert.True(memory.TryRead(infoAddress, info));
        return BinaryPrimitives.ReadUInt64LittleEndian(info[0..8]);
    }

    // Case 1: offset exactly at the start of a block → always returns that block,
    // regardless of flags. Proves the containing-block lookup takes priority.
    [Fact]
    public void DirectMemoryQuery_OffsetAtBlockStart_ReturnsContainingBlock()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong blockSize = 0x10000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        const ulong outAddress = memoryBase + 0x800;
        const ulong infoAddress = memoryBase + 0x900;

        var blockA = AllocateDirectMemory(memory, outAddress, 0, blockSize);
        try
        {
            // flags=0: exact start → containing block returned.
            Assert.Equal(blockA, QueryDirectMemory(memory, infoAddress, blockA, flags: 0));
            // flags=1: same offset is inside the block → still returns the containing block,
            // not the next one.
            Assert.Equal(blockA, QueryDirectMemory(memory, infoAddress, blockA, flags: 1));
        }
        finally
        {
            ReleaseDirectMemory(memory, blockA, blockSize);
        }
    }

    // Case 2: offset in the middle of a block → always returns that block with either flag.
    [Fact]
    public void DirectMemoryQuery_OffsetInsideBlock_ReturnsContainingBlock()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong blockSize = 0x10000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        const ulong outAddress = memoryBase + 0x800;
        const ulong infoAddress = memoryBase + 0x900;

        var blockA = AllocateDirectMemory(memory, outAddress, 0, blockSize);
        var blockB = AllocateDirectMemory(memory, outAddress, blockA + blockSize, blockSize);
        try
        {
            var midA = blockA + 0x4000;
            // flags=0: mid-block → containing.
            Assert.Equal(blockA, QueryDirectMemory(memory, infoAddress, midA, flags: 0));
            // flags=1: still inside block A → returning block B would be wrong.
            Assert.Equal(blockA, QueryDirectMemory(memory, infoAddress, midA, flags: 1));
        }
        finally
        {
            ReleaseDirectMemory(memory, blockA, blockSize);
            ReleaseDirectMemory(memory, blockB, blockSize);
        }
    }

    // Case 3: offset falls in a gap between two blocks.
    // flags=0 → NOT_FOUND; flags=1 → next block returned.
    [Fact]
    public void DirectMemoryQuery_OffsetInGap_Flags0ReturnsNotFound_Flags1ReturnsNext()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong blockSize = 0x10000;
        const ulong gapSize = 0x10000;          // intentional gap between A and B
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        const ulong outAddress = memoryBase + 0x800;
        const ulong infoAddress = memoryBase + 0x900;

        var blockA = AllocateDirectMemory(memory, outAddress, 0, blockSize);
        // Leave a gap of gapSize, then allocate B.
        var blockB = AllocateDirectMemory(memory, outAddress, blockA + blockSize + gapSize, blockSize);
        try
        {
            var gapOffset = blockA + blockSize + gapSize / 2; // somewhere in the gap
            // flags=0: gap is unallocated → NOT_FOUND.
            Assert.Null(QueryDirectMemory(memory, infoAddress, gapOffset, flags: 0));
            // flags=1: gap → first block after gap is B.
            Assert.Equal(blockB, QueryDirectMemory(memory, infoAddress, gapOffset, flags: 1));
        }
        finally
        {
            ReleaseDirectMemory(memory, blockA, blockSize);
            ReleaseDirectMemory(memory, blockB, blockSize);
        }
    }

    // Case 4: offset is past the last allocated block → NOT_FOUND for both flag values.
    [Fact]
    public void DirectMemoryQuery_OffsetPastLastBlock_ReturnsNotFound()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong blockSize = 0x10000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        const ulong outAddress = memoryBase + 0x800;
        const ulong infoAddress = memoryBase + 0x900;

        var blockA = AllocateDirectMemory(memory, outAddress, 0, blockSize);
        try
        {
            var pastEnd = blockA + blockSize; // first byte after block A
            // Neither flag finds anything past the last block.
            Assert.Null(QueryDirectMemory(memory, infoAddress, pastEnd, flags: 0));
            Assert.Null(QueryDirectMemory(memory, infoAddress, pastEnd, flags: 1));
        }
        finally
        {
            ReleaseDirectMemory(memory, blockA, blockSize);
        }
    }
}

