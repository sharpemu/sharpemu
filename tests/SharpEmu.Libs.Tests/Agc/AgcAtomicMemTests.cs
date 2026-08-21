// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcAtomicMemTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong AtomicAddress = BaseAddress + 0x200;

    [Fact]
    public void Decoder_Reads64BitReturnCompareSwapLoop()
    {
        var packet = AgcExports.DecodeAtomicMemPacket(
            0x28u | (1u << 8) | (2u << 25),
            unchecked((uint)AtomicAddress),
            (uint)(AtomicAddress >> 32),
            0x5566_7788,
            0x1122_3344,
            0xDDEE_FF00,
            0x99AA_BBCC,
            400);

        Assert.Equal(8u, packet.BaseOperation);
        Assert.True(packet.Is64Bit);
        Assert.True(packet.ReturnsData);
        Assert.True(packet.IsCompareSwap);
        Assert.Equal(1u, packet.Command);
        Assert.Equal(2u, packet.CachePolicy);
        Assert.Equal(AtomicAddress, packet.Address);
        Assert.Equal(0x1122_3344_5566_7788UL, packet.SourceData);
        Assert.Equal(0x99AA_BBCC_DDEE_FF00UL, packet.CompareData);
        Assert.Equal(400u, packet.LoopIntervalCycles);
        Assert.Equal(0u, packet.EngineSelection);
        Assert.True(packet.IsSupported);
        Assert.True(packet.HasValidEngine(usesAsyncEncoding: false));
    }

    [Fact]
    public void Decoder_RejectsGraphicsReservedEngineSelection()
    {
        var packet = AgcExports.DecodeAtomicMemPacket(
            0x4Fu | (2u << 8) | (2u << 30),
            unchecked((uint)AtomicAddress),
            (uint)(AtomicAddress >> 32),
            1,
            0,
            0,
            0,
            400);

        Assert.True(packet.IsSupported);
        Assert.False(packet.HasValidEngine(usesAsyncEncoding: false));
        Assert.False(packet.HasValidEngine(usesAsyncEncoding: true));
    }

    [Fact]
    public void CompareSwap_WritesSourceAndReturnsPriorValue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write64(memory, 0x1234);
        var packet = Packet(
            operation: 0x28,
            command: 0,
            source: 0x5678,
            compare: 0x1234);

        Assert.True(AgcExports.TryApplyAtomicMem(
            memory,
            packet,
            out var prior,
            out var current,
            out var comparePassed));
        Assert.True(comparePassed);
        Assert.Equal(0x1234UL, prior);
        Assert.Equal(0x5678UL, current);
        Assert.Equal(0x5678UL, Read64(memory));
    }

    [Fact]
    public void CompareSwap_FailedComparisonDoesNotChangeMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write64(memory, 0x1234);
        var packet = Packet(
            operation: 0x28,
            command: 1,
            source: 0x5678,
            compare: 0x9999);

        Assert.True(AgcExports.TryApplyAtomicMem(
            memory,
            packet,
            out var prior,
            out var current,
            out var comparePassed));
        Assert.False(comparePassed);
        Assert.Equal(0x1234UL, prior);
        Assert.Equal(0x1234UL, current);
        Assert.Equal(0x1234UL, Read64(memory));
    }

    [Theory]
    [InlineData(0x4Fu, 9u, 4u, 13u)]
    [InlineData(0x50u, 9u, 4u, 5u)]
    [InlineData(0x55u, 0xF0u, 0xCCu, 0xC0u)]
    [InlineData(0x56u, 0x30u, 0x0Fu, 0x3Fu)]
    [InlineData(0x57u, 0x3Cu, 0x0Fu, 0x33u)]
    [InlineData(0x58u, 3u, 3u, 0u)]
    [InlineData(0x59u, 0u, 3u, 3u)]
    public void Integer32Operations_UseDefinedSemantics(
        uint operation,
        uint initial,
        uint source,
        uint expected)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write32(memory, initial);
        var packet = Packet(operation, command: 2, source, compare: 0);

        Assert.True(AgcExports.TryApplyAtomicMem(
            memory,
            packet,
            out var prior,
            out var current,
            out _));
        Assert.Equal(initial, (uint)prior);
        Assert.Equal(expected, (uint)current);
        Assert.Equal(expected, Read32(memory));
    }

    [Fact]
    public void SignedMinimum_UsesSignedOrdering()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        Write32(memory, 1);
        var packet = Packet(
            operation: 0x51,
            command: 2,
            source: uint.MaxValue,
            compare: 0);

        Assert.True(AgcExports.TryApplyAtomicMem(
            memory,
            packet,
            out _,
            out var current,
            out _));
        Assert.Equal(uint.MaxValue, (uint)current);
    }

    [Theory]
    [InlineData(0x08u, 2u)]
    [InlineData(0x48u, 0u)]
    [InlineData(0x0Fu, 1u)]
    [InlineData(0x01u, 0u)]
    public void UnsupportedOperationOrCommand_IsRejected(
        uint operation,
        uint command)
    {
        var packet = Packet(operation, command, source: 1, compare: 0);

        Assert.False(packet.IsSupported);
    }

    [Fact]
    public void AtomicLoopReference_IgnoresHighDwordFor32BitOperation()
    {
        var packet = Packet(
            operation: 0x08,
            command: 1,
            source: 0x5678,
            compare: 0xAABB_CCDD_0000_1234);

        Assert.Equal(0x1234UL, AgcExports.GetAtomicLoopReferenceValue(packet));
    }

    [Fact]
    public void AtomicLoopWait_UsesExactEquality()
    {
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            ReferenceValue = 5,
            Mask = uint.MaxValue,
            CompareFunction = 3,
            RequiresExactEquality = true,
        };

        Assert.True(GpuWaitRegistry.Compare(waiter, 5));
        Assert.False(GpuWaitRegistry.Compare(waiter, 6));
    }

    private static AgcExports.AtomicMemPacket Packet(
        uint operation,
        uint command,
        ulong source,
        ulong compare) =>
        AgcExports.DecodeAtomicMemPacket(
            operation | (command << 8),
            unchecked((uint)AtomicAddress),
            (uint)(AtomicAddress >> 32),
            (uint)source,
            (uint)(source >> 32),
            (uint)compare,
            (uint)(compare >> 32),
            400);

    private static void Write32(FakeCpuMemory memory, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(AtomicAddress, bytes));
    }

    private static uint Read32(FakeCpuMemory memory)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(AtomicAddress, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void Write64(FakeCpuMemory memory, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(AtomicAddress, bytes));
    }

    private static ulong Read64(FakeCpuMemory memory)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(AtomicAddress, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
