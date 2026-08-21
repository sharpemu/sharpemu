// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcMemSemaphoreTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong SemaphoreAddress = BaseAddress + 0x200;
    private const ulong CommandBufferAddress = BaseAddress + 0x400;
    private const ulong PacketAddress = BaseAddress + 0x800;
    private const ulong PacketEndAddress = PacketAddress + 0x100;

    [Fact]
    public void Decoder_ReadsSignalLayout()
    {
        var packet = AgcExports.DecodeMemSemaphorePacket(
            0x207,
            1,
            (1u << 16) | (1u << 20) | (6u << 29));

        Assert.Equal(SemaphoreAddress, packet.Address);
        Assert.True(packet.WaitForMailbox);
        Assert.True(packet.WriteSignal);
        Assert.True(packet.IsSignal);
        Assert.False(packet.IsWait);
        Assert.True(packet.IsSupported);
    }

    [Fact]
    public void Decoder_RejectsUnknownSelection()
    {
        var packet = AgcExports.DecodeMemSemaphorePacket(
            0x200,
            1,
            5u << 29);

        Assert.False(packet.IsSupported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Builder_WritesDirectAddressSignalPacket(bool asyncCompute)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = SemaphoreAddress;
        ctx[CpuRegister.Rdx] = 6;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = 1;

        Assert.Equal(
            0,
            asyncCompute
                ? AgcExports.AcbMemSemaphore(ctx)
                : AgcExports.DcbMemSemaphore(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(Pm4Header(4, 0x39), ReadUInt32(memory, PacketAddress));
        Assert.Equal(unchecked((uint)SemaphoreAddress), ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(
            unchecked((uint)(SemaphoreAddress >> 32)),
            ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(
            (6u << 29) | (1u << 20) | (1u << 16),
            ReadUInt32(memory, PacketAddress + 12));
    }

    [Fact]
    public void Builder_WritesDirectAddressWaitPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = SemaphoreAddress;
        ctx[CpuRegister.Rdx] = 7;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;

        Assert.Equal(0, AgcExports.DcbMemSemaphore(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(7u << 29, ReadUInt32(memory, PacketAddress + 12));
    }

    [Theory]
    [InlineData(0UL, SemaphoreAddress, 6u, 0u, 0u)]
    [InlineData(CommandBufferAddress, 0UL, 6u, 0u, 0u)]
    [InlineData(CommandBufferAddress, SemaphoreAddress + 4, 6u, 0u, 0u)]
    [InlineData(CommandBufferAddress, SemaphoreAddress, 5u, 0u, 0u)]
    [InlineData(CommandBufferAddress, SemaphoreAddress, 6u, 2u, 0u)]
    [InlineData(CommandBufferAddress, SemaphoreAddress, 6u, 0u, 2u)]
    public void Builder_RejectsInvalidArguments(
        ulong commandBufferAddress,
        ulong semaphoreAddress,
        uint operation,
        uint signalType,
        uint mailbox)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory);
        ctx[CpuRegister.Rdi] = commandBufferAddress;
        ctx[CpuRegister.Rsi] = semaphoreAddress;
        ctx[CpuRegister.Rdx] = operation;
        ctx[CpuRegister.Rcx] = signalType;
        ctx[CpuRegister.R8] = mailbox;

        Assert.Equal(0, AgcExports.DcbMemSemaphore(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void IncrementSignal_AddsOne()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write(memory, 4);

        Assert.True(AgcExports.TrySignalMemSemaphore(
            memory,
            SemaphoreAddress,
            writeSignal: false,
            out var value));
        Assert.Equal(5UL, value);
        Assert.Equal(5UL, Read(memory));
    }

    [Fact]
    public void WriteSignal_StoresOne()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write(memory, 19);

        Assert.True(AgcExports.TrySignalMemSemaphore(
            memory,
            SemaphoreAddress,
            writeSignal: true,
            out var value));
        Assert.Equal(1UL, value);
        Assert.Equal(1UL, Read(memory));
    }

    [Fact]
    public void Wait_ConsumesOneToken()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write(memory, 2);

        Assert.True(AgcExports.TryConsumeMemSemaphore(
            memory,
            SemaphoreAddress,
            out var prior));
        Assert.Equal(2UL, prior);
        Assert.Equal(1UL, Read(memory));
    }

    [Fact]
    public void Wait_DoesNotUnderflowAtZero()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);

        Assert.True(AgcExports.TryConsumeMemSemaphore(
            memory,
            SemaphoreAddress,
            out var prior));
        Assert.Equal(0UL, prior);
        Assert.Equal(0UL, Read(memory));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(SemaphoreAddress + 4)]
    public void Operations_RejectInvalidAddresses(ulong address)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);

        Assert.False(AgcExports.TryConsumeMemSemaphore(memory, address, out _));
        Assert.False(AgcExports.TrySignalMemSemaphore(
            memory,
            address,
            writeSignal: false,
            out _));
    }

    private static void Write(FakeCpuMemory memory, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(SemaphoreAddress, bytes));
    }

    private static ulong Read(FakeCpuMemory memory)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(SemaphoreAddress, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void InitializeCommandBuffer(FakeCpuMemory memory)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketEndAddress);
    }

    private static uint Pm4Header(uint dwords, uint opcode) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8);

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
