// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

// The PRT commands of AMM: MapAsPrt declares a partially resident range, AllocatePaForPrt backs one
// chunk of it. Both only encode the range here; the pages are mapped when the buffer is submitted.
[Collection("AmprFileRegistry")]
public sealed class AmprAmmPrtTests
{
    private const ulong MemoryBase = 0x3_1000_0000;
    private const ulong BufferObject = MemoryBase + 0x100;
    private const ulong Records = MemoryBase + 0x200;
    private const ulong AmmStart = 0x80_0000_0000;
    private const int KernelEinval = unchecked((int)0x80020016);

    [Fact]
    public void MapAsPrtEncodesTheApertureWithoutBackingPages()
    {
        // Titles reserve their streamed world this way: tens of GiB, backed chunk by chunk.
        const ulong size = 0x6_0000_0000UL;
        var context = CreateContext();
        context[CpuRegister.Rdi] = BufferObject;
        context[CpuRegister.Rsi] = AmmStart;
        context[CpuRegister.Rdx] = size;

        Assert.Equal(0, AmprExports.AmmCommandBufferMapAsPrt(context));

        Assert.Equal(3u, ReadUInt32(context, Records));
        Assert.Equal(AmmStart, ReadUInt64(context, Records + 0x08));
        Assert.Equal(size, ReadUInt64(context, Records + 0x10));
    }

    [Fact]
    public void AllocatePaForPrtEncodesTheChunkWithReadGrants()
    {
        const int protection = 0xF2;
        const ulong size = 0x1900_0000UL;
        var context = CreateContext();
        context[CpuRegister.Rdi] = BufferObject;
        context[CpuRegister.Rsi] = AmmStart + 0x4000;
        context[CpuRegister.Rdx] = size;
        context[CpuRegister.Rcx] = 12;
        context[CpuRegister.R8] = protection;

        Assert.Equal(0, AmprExports.AmmCommandBufferAllocatePaForPrt(context));

        Assert.Equal(1u, ReadUInt32(context, Records));
        Assert.Equal((uint)(protection | ((protection >> 1) & 0x141)), ReadUInt32(context, Records + 0x04));
        Assert.Equal(AmmStart + 0x4000, ReadUInt64(context, Records + 0x08));
        Assert.Equal(size, ReadUInt64(context, Records + 0x10));
    }

    [Theory]
    [InlineData(10, 0xF2)]
    [InlineData(21, 0xF2)]
    [InlineData(12, 0x1000)]
    public void AllocatePaForPrtRejectsUnknownTypesAndProtections(int memoryType, int protection)
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = BufferObject;
        context[CpuRegister.Rsi] = AmmStart;
        context[CpuRegister.Rdx] = 0x4000;
        context[CpuRegister.Rcx] = unchecked((ulong)memoryType);
        context[CpuRegister.R8] = unchecked((ulong)protection);

        Assert.Equal(KernelEinval, AmprExports.AmmCommandBufferAllocatePaForPrt(context));
        Assert.Equal(unchecked((ulong)(long)KernelEinval), context[CpuRegister.Rax]);
    }

    [Fact]
    public void PrtCommandsRejectRangesOutsideTheAmmArea()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = BufferObject;
        context[CpuRegister.Rsi] = AmmStart - 0x4000;
        context[CpuRegister.Rdx] = 0x4000;

        Assert.Equal(KernelEinval, AmprExports.AmmCommandBufferMapAsPrt(context));
        Assert.Equal(KernelEinval, AmprExports.AmmCommandBufferAllocatePaForPrt(context));
    }

    private static CpuContext CreateContext()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = BufferObject;
        context[CpuRegister.Rsi] = Records;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));
        return context;
    }

    private static uint ReadUInt32(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt32(address, out var value));
        return value;
    }

    private static ulong ReadUInt64(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}
