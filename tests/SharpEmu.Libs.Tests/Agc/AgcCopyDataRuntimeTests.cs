// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcCopyDataRuntimeTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x1_0000;
    private const ulong GraphicsCommandAddress = BaseAddress + 0x1000;
    private const ulong ComputeCommandAddress = BaseAddress + 0x2000;
    private const ulong LaterComputeCommandAddress = BaseAddress + 0x3000;
    private const ulong GraphicsSubmitAddress = BaseAddress + 0x4000;
    private const ulong ComputeSubmitAddress = BaseAddress + 0x4100;
    private const ulong LabelAddress = BaseAddress + 0x8000;
    private const ulong SourceAddress = BaseAddress + 0x8100;
    private const ulong DestinationAddress = BaseAddress + 0x8200;
    private const ulong ResultAddress = BaseAddress + 0x8300;

    private const uint ItNop = 0x10;
    private const uint ItWriteData = 0x37;
    private const uint ItCopyData = 0x40;
    private const uint RWaitMem32 = 0x0A;

    public AgcCopyDataRuntimeTests()
    {
        GpuWaitRegistry.Clear();
    }

    public void Dispose()
    {
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ImmediateCopy_ReleasesWaitingGraphicsSubmission()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const uint expected = 0x1122_3344;
        var graphicsDwords = WriteWaitThenResult(memory, LabelAddress, expected);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, graphicsDwords);
        Assert.Equal(1, GpuWaitRegistry.Count);
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));

        WriteCopyData(
            memory,
            ComputeCommandAddress,
            control: 5u | (2u << 8),
            source: expected,
            destination: LabelAddress);
        SubmitAcb(ctx, memory, ComputeCommandAddress, 6, owner: 1);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, ResultAddress) == 0xCAFE_BABE,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, ReadUInt32(memory, LabelAddress));
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void MemoryCopy_ReadsAndWritesGuestMemoryInQueueOrder()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, SourceAddress, 0xA5A5_5A5A);
        WriteCopyData(
            memory,
            GraphicsCommandAddress,
            control: 2u | (2u << 8) | (1u << 30),
            source: SourceAddress,
            destination: DestinationAddress);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, 6);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, DestinationAddress) == 0xA5A5_5A5A,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Copy64_ReleasesWaiterOnHighDword()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const uint highDword = 0xAABB_CCDD;
        var graphicsDwords = WriteWaitThenResult(memory, LabelAddress + sizeof(uint), highDword);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, graphicsDwords);
        Assert.Equal(1, GpuWaitRegistry.Count);

        const ulong value = ((ulong)highDword << 32) | 0x1122_3344u;
        WriteCopyData(
            memory,
            ComputeCommandAddress,
            control: 5u | (2u << 8) | (1u << 16),
            source: value,
            destination: LabelAddress);
        SubmitAcb(ctx, memory, ComputeCommandAddress, 6, owner: 3);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, ResultAddress) == 0xCAFE_BABE,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(value, ReadUInt64(memory, LabelAddress));
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void AtomicReturnCopy_StopsCurrentAndLaterQueueWork()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteCopyData(
            memory,
            ComputeCommandAddress,
            control: 6u | (2u << 8),
            source: 0,
            destination: DestinationAddress);
        WriteWriteData(memory, ComputeCommandAddress + (6 * sizeof(uint)), ResultAddress, 0x1111_1111);

        SubmitAcb(ctx, memory, ComputeCommandAddress, 11, owner: 4);
        Assert.Equal(0u, ReadUInt32(memory, DestinationAddress));
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));

        WriteWriteData(memory, LaterComputeCommandAddress, ResultAddress, 0x2222_2222);
        SubmitAcb(ctx, memory, LaterComputeCommandAddress, 5, owner: 4);
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));
    }

    private static uint WriteWaitThenResult(
        FakeCpuMemory memory,
        ulong waitAddress,
        uint reference)
    {
        WriteDwords(
            memory,
            GraphicsCommandAddress,
            Pm4Header(7, ItNop, RWaitMem32),
            unchecked((uint)waitAddress),
            unchecked((uint)(waitAddress >> 32)),
            uint.MaxValue,
            reference,
            0x0400_0053,
            0,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)ResultAddress),
            unchecked((uint)(ResultAddress >> 32)),
            0xCAFE_BABE);
        return 12;
    }

    private static void WriteCopyData(
        FakeCpuMemory memory,
        ulong commandAddress,
        uint control,
        ulong source,
        ulong destination)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(6, ItCopyData),
            control,
            unchecked((uint)source),
            unchecked((uint)(source >> 32)),
            unchecked((uint)destination),
            unchecked((uint)(destination >> 32)));
    }

    private static void WriteWriteData(
        FakeCpuMemory memory,
        ulong commandAddress,
        ulong destination,
        uint value)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)destination),
            unchecked((uint)(destination >> 32)),
            value);
    }

    private static void SubmitDcb(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong commandAddress,
        uint dwordCount)
    {
        WriteSubmit(memory, GraphicsSubmitAddress, commandAddress, dwordCount);
        ctx[CpuRegister.Rdi] = GraphicsSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(ctx));
    }

    private static void SubmitAcb(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong commandAddress,
        uint dwordCount,
        uint owner)
    {
        WriteSubmit(memory, ComputeSubmitAddress, commandAddress, dwordCount);
        ctx[CpuRegister.Rdi] = owner;
        ctx[CpuRegister.Rsi] = ComputeSubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitAcb(ctx));
    }

    private static void WriteSubmit(
        FakeCpuMemory memory,
        ulong submitAddress,
        ulong commandAddress,
        uint dwordCount)
    {
        WriteUInt64(memory, submitAddress, commandAddress);
        WriteUInt32(memory, submitAddress + sizeof(ulong), dwordCount);
    }

    private static uint Pm4Header(uint dwords, uint opcode, uint register = 0) =>
        0xC000_0000u |
        ((dwords - 2) << 16) |
        (opcode << 8) |
        ((register & 0x3Fu) << 2);

    private static void WriteDwords(
        FakeCpuMemory memory,
        ulong address,
        params uint[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            WriteUInt32(memory, address + ((ulong)index * sizeof(uint)), values[index]);
        }
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

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
