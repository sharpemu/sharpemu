// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcAtomicMemRuntimeTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x1_0000;
    private const ulong GraphicsCommandAddress = BaseAddress + 0x1000;
    private const ulong ComputeCommandAddress = BaseAddress + 0x2000;
    private const ulong LaterGraphicsCommandAddress = BaseAddress + 0x3000;
    private const ulong GraphicsSubmitAddress = BaseAddress + 0x4000;
    private const ulong ComputeSubmitAddress = BaseAddress + 0x4100;
    private const ulong AtomicAddress = BaseAddress + 0x8000;
    private const ulong ResultAddress = BaseAddress + 0x8100;

    private const uint ItWriteData = 0x37;
    private const uint ItCopyData = 0x40;
    private const uint ItAtomicMem = 0x1E;

    public AgcAtomicMemRuntimeTests()
    {
        GpuWaitRegistry.Clear();
    }

    public void Dispose()
    {
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ConfirmedAdd_PrecedesLaterMemoryCopyInTheSameQueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, AtomicAddress, 5);
        WriteAtomic32(
            memory,
            GraphicsCommandAddress,
            operation: 0x4F,
            command: 2,
            AtomicAddress,
            source: 3,
            compare: 0);
        WriteCopyMemory32(
            memory,
            GraphicsCommandAddress + (9 * sizeof(uint)),
            AtomicAddress,
            ResultAddress);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, 15);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, ResultAddress) == 8,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(8u, ReadUInt32(memory, AtomicAddress));
    }

    [Fact]
    public void AsyncConfirmedAdd_Executes64BitOperation()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, AtomicAddress, 10);
        WriteAtomic64(
            memory,
            ComputeCommandAddress,
            operation: 0x6F,
            command: 2,
            AtomicAddress,
            source: 3,
            compare: 0);

        SubmitAcb(ctx, memory, ComputeCommandAddress, 9, owner: 2);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt64(memory, AtomicAddress) == 13,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CompareSwapLoop_SuspendsUntilTheComparisonCanPass()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, AtomicAddress, 1);
        WriteAtomic32(
            memory,
            GraphicsCommandAddress,
            operation: 8,
            command: 1,
            AtomicAddress,
            source: 9,
            compare: 5);
        WriteWriteData(
            memory,
            GraphicsCommandAddress + (9 * sizeof(uint)),
            ResultAddress,
            0xCAFE_BABE);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, 14);
        Assert.True(SpinWait.SpinUntil(
            () => GpuWaitRegistry.Count == 1,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(1u, ReadUInt32(memory, AtomicAddress));
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));

        WriteCopyImmediate32(
            memory,
            ComputeCommandAddress,
            value: 5,
            AtomicAddress);
        SubmitAcb(ctx, memory, ComputeCommandAddress, 6, owner: 1);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, ResultAddress) == 0xCAFE_BABE,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(9u, ReadUInt32(memory, AtomicAddress));
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void UnsupportedAtomic_StopsCurrentAndLaterQueueWork()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, AtomicAddress, 5);
        WriteAtomic32(
            memory,
            GraphicsCommandAddress,
            operation: 1,
            command: 0,
            AtomicAddress,
            source: 3,
            compare: 5);
        WriteWriteData(
            memory,
            GraphicsCommandAddress + (9 * sizeof(uint)),
            ResultAddress,
            0x1111_1111);

        SubmitDcb(ctx, memory, GraphicsCommandAddress, 14);
        Assert.Equal(5u, ReadUInt32(memory, AtomicAddress));
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));

        WriteWriteData(
            memory,
            LaterGraphicsCommandAddress,
            ResultAddress,
            0x2222_2222);
        SubmitDcb(ctx, memory, LaterGraphicsCommandAddress, 5);
        Assert.Equal(0u, ReadUInt32(memory, ResultAddress));
    }

    private static void WriteAtomic32(
        FakeCpuMemory memory,
        ulong packetAddress,
        uint operation,
        uint command,
        ulong targetAddress,
        uint source,
        uint compare)
    {
        WriteDwords(
            memory,
            packetAddress,
            Pm4Header(9, ItAtomicMem),
            operation | (command << 8),
            unchecked((uint)targetAddress),
            unchecked((uint)(targetAddress >> 32)),
            source,
            0,
            compare,
            0,
            400);
    }

    private static void WriteAtomic64(
        FakeCpuMemory memory,
        ulong packetAddress,
        uint operation,
        uint command,
        ulong targetAddress,
        ulong source,
        ulong compare)
    {
        WriteDwords(
            memory,
            packetAddress,
            Pm4Header(9, ItAtomicMem),
            operation | (command << 8),
            unchecked((uint)targetAddress),
            unchecked((uint)(targetAddress >> 32)),
            unchecked((uint)source),
            unchecked((uint)(source >> 32)),
            unchecked((uint)compare),
            unchecked((uint)(compare >> 32)),
            400);
    }

    private static void WriteCopyMemory32(
        FakeCpuMemory memory,
        ulong packetAddress,
        ulong sourceAddress,
        ulong destinationAddress)
    {
        WriteDwords(
            memory,
            packetAddress,
            Pm4Header(6, ItCopyData),
            2u | (2u << 8),
            unchecked((uint)sourceAddress),
            unchecked((uint)(sourceAddress >> 32)),
            unchecked((uint)destinationAddress),
            unchecked((uint)(destinationAddress >> 32)));
    }

    private static void WriteCopyImmediate32(
        FakeCpuMemory memory,
        ulong packetAddress,
        uint value,
        ulong destinationAddress)
    {
        WriteDwords(
            memory,
            packetAddress,
            Pm4Header(6, ItCopyData),
            5u | (2u << 8),
            value,
            0,
            unchecked((uint)destinationAddress),
            unchecked((uint)(destinationAddress >> 32)));
    }

    private static void WriteWriteData(
        FakeCpuMemory memory,
        ulong packetAddress,
        ulong destinationAddress,
        uint value)
    {
        WriteDwords(
            memory,
            packetAddress,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)destinationAddress),
            unchecked((uint)(destinationAddress >> 32)),
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

    private static uint Pm4Header(uint dwords, uint opcode) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8);

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
