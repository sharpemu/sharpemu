// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEventWritePacketTests
{
    private const ulong BaseAddress = 0x2_3000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;

    [Fact]
    public void DcbEventWrite_AddressedOcclusionEvent_EncodesAndExecutes()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = CreateContext(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x39;
        ctx[CpuRegister.Rdx] = StreamRunner.LabelAddress + 7;

        Assert.Equal(0, AgcExports.DcbEventWrite(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));

        var packet = ReadDwords(memory, PacketAddress, 4);
        Assert.Equal(0xC002_4600u, packet[0]);
        Assert.Equal(0x139u, packet[1]);
        Assert.Equal(unchecked((uint)StreamRunner.LabelAddress), packet[2]);
        Assert.Equal((uint)(StreamRunner.LabelAddress >> 32), packet[3]);

        var runner = new StreamRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(packet));
        Assert.Equal(1UL << 63, runner.Host.ReadQword(StreamRunner.LabelAddress));
        Assert.Equal(1UL << 63, runner.Host.ReadQword(StreamRunner.LabelAddress + 15 * 16));
    }

    [Theory]
    [InlineData(0x07u, 0x407u)]
    [InlineData(0x0Fu, 0x40Fu)]
    [InlineData(0x10u, 0x410u)]
    public void DcbEventWrite_FlushEventsUseIndexFour(uint eventType, uint eventControl)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = CreateContext(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = eventType;
        ctx[CpuRegister.Rdx] = StreamRunner.LabelAddress;

        Assert.Equal(0, AgcExports.DcbEventWrite(ctx));
        Assert.Equal(new[] { 0xC000_4600u, eventControl }, ReadDwords(memory, PacketAddress, 2));
        Assert.Equal(PacketAddress + 8, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0x07u, 0x407u)]
    [InlineData(0x39u, 0x039u)]
    public void AcbEventWrite_AlwaysEmitsTwoDwords(uint eventType, uint eventControl)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = CreateContext(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = eventType;
        ctx[CpuRegister.Rdx] = StreamRunner.LabelAddress;

        Assert.Equal(0, AgcExports.AcbEventWrite(ctx));
        Assert.Equal(new[] { 0xC000_4600u, eventControl }, ReadDwords(memory, PacketAddress, 2));
        Assert.Equal(PacketAddress + 8, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0x38u, 16)]
    [InlineData(0x39u, 16)]
    [InlineData(0x07u, 8)]
    public void DcbEventWriteGetSize_DependsOnAddressedEventType(uint eventType, int expectedSize)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = eventType;

        Assert.Equal(expectedSize, AgcExports.DcbEventWriteGetSize(ctx));
        Assert.Equal((ulong)expectedSize, ctx[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(FakeCpuMemory memory)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static uint[] ReadDwords(FakeCpuMemory memory, ulong address, int count)
    {
        var values = new uint[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = ReadUInt32(memory, address + ((ulong)index * sizeof(uint)));
        }

        return values;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
