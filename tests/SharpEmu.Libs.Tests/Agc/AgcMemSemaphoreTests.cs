// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcMemSemaphoreTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong SemaphoreAddress = BaseAddress + 0x200;

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
}
