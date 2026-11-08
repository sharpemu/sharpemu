// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcAcquireMemTests
{
    private const ulong BaseAddress = 0x2_3000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong StackAddress = BaseAddress + 0xC00;
    private const uint GcrControl = 0xD000;
    private const uint PollCycles = 400;

    // The SDK's label sync acquires the 32 bytes of a label; the packet covers the 256-byte unit that holds them.
    [Fact]
    public void DcbAcquireMem_ALabelSizedRange_EmitsThePacketWithTheEnclosingUnit()
    {
        var (memory, ctx) = CreateContext();
        var packet = DcbAcquireMem(ctx, memory, baseAddress: 0x12_3456_7820, sizeBytes: 0x20);

        Assert.Equal(PacketAddress, packet);
        Assert.Equal(PacketHeader.Make(8, PacketOpcode.Nop, PacketCustomCode.AcquireMemory), ReadUInt32(memory, PacketAddress));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x12_3456_78u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(GcrControl, ReadUInt32(memory, PacketAddress + 28));
    }

    [Fact]
    public void AcbAcquireMem_ALabelSizedRange_EmitsThePacketWithTheEnclosingUnit()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = GcrControl;
        ctx[CpuRegister.Rdx] = 0x12_3456_7820;
        ctx[CpuRegister.Rcx] = 0x20;
        ctx[CpuRegister.R8] = PollCycles;
        AgcExports.AcbAcquireMem(ctx);

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x12_3456_78u, ReadUInt32(memory, PacketAddress + 16));
    }

    // Bytes 0x10F0..0x1110 touch two units, 0x1000 and 0x1100.
    [Fact]
    public void DcbAcquireMem_ARangeAcrossAUnitBoundary_CoversBothUnits()
    {
        var (memory, ctx) = CreateContext();
        Assert.Equal(PacketAddress, DcbAcquireMem(ctx, memory, baseAddress: 0x10F0, sizeBytes: 0x20));
        Assert.Equal(2u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0x10u, ReadUInt32(memory, PacketAddress + 16));
    }

    [Theory]
    [InlineData(0x1000UL, 0x300UL, 3u, 0x10u)]
    [InlineData(0x1000UL, ulong.MaxValue, 0u, 0x10u)]
    [InlineData(0x1000UL, 0UL, 0u, 0x10u)]
    [InlineData(0xFF_FFFF_FF00UL, 0x100UL, 1u, 0xFFFF_FFFFu)]
    public void TryEncloseAcquireRange_KeepsTheEncodingOfAlignedRangesAndTheWholeMemory(
        ulong baseAddress, ulong sizeBytes, uint expectedSizeUnits, uint expectedBaseUnits)
    {
        Assert.True(AgcExports.TryEncloseAcquireRange(baseAddress, sizeBytes, out var baseUnits, out var sizeUnits));
        Assert.Equal(expectedSizeUnits, sizeUnits);
        Assert.Equal(expectedBaseUnits, baseUnits);
    }

    [Theory]
    [InlineData(0x100_0000_0000UL, 0x20UL)]
    [InlineData(0xFF_FFFF_FFF0UL, 0x20UL)]
    [InlineData(0UL, 0x100_0000_0000UL)]
    public void TryEncloseAcquireRange_RejectsRangesPastOneTebibyte(ulong baseAddress, ulong sizeBytes)
    {
        Assert.False(AgcExports.TryEncloseAcquireRange(baseAddress, sizeBytes, out _, out _));
    }

    private static ulong DcbAcquireMem(CpuContext ctx, ICpuMemory memory, ulong baseAddress, ulong sizeBytes)
    {
        WriteUInt64(memory, StackAddress + 8, PollCycles);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = GcrControl;
        ctx[CpuRegister.R8] = baseAddress;
        ctx[CpuRegister.R9] = sizeBytes;
        ctx[CpuRegister.Rsp] = StackAddress;
        AgcExports.DcbAcquireMem(ctx);
        return ctx[CpuRegister.Rax];
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static uint ReadUInt32(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt64(ICpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
