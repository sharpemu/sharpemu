// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcMemSemaphoreRuntimeTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x1_0000;
    private const ulong FirstWaitAddress = BaseAddress + 0x1000;
    private const ulong SecondWaitAddress = BaseAddress + 0x2000;
    private const ulong SignalAddress = BaseAddress + 0x3000;
    private const ulong GraphicsSubmitAddress = BaseAddress + 0x5000;
    private const ulong ComputeSubmitAddress = BaseAddress + 0x5100;
    private const ulong MultiAddressArray = BaseAddress + 0x5200;
    private const ulong MultiSizeArray = BaseAddress + 0x5300;
    private const ulong SemaphoreAddress = BaseAddress + 0x8000;
    private const ulong FirstResultAddress = BaseAddress + 0x8100;
    private const ulong SecondResultAddress = BaseAddress + 0x8200;
    private const ulong ThirdResultAddress = BaseAddress + 0x8300;

    private const uint ItWriteData = 0x37;
    private const uint ItMemSemaphore = 0x39;

    public AgcMemSemaphoreRuntimeTests()
    {
        GpuWaitRegistry.Clear();
    }

    public void Dispose()
    {
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void Signals_ReleaseOneWaitingSubmissionEach()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var firstDwords = WriteWaitThenResult(
            memory,
            FirstWaitAddress,
            FirstResultAddress,
            0x1111_1111);
        var secondDwords = WriteWaitThenResult(
            memory,
            SecondWaitAddress,
            SecondResultAddress,
            0x2222_2222);
        var signalDwords = WriteSignal(memory, SignalAddress);

        SubmitDcb(ctx, memory, FirstWaitAddress, firstDwords);
        SubmitAcb(ctx, memory, SecondWaitAddress, secondDwords, owner: 1);
        Assert.Equal(0u, ReadUInt32(memory, FirstResultAddress));
        Assert.Equal(0u, ReadUInt32(memory, SecondResultAddress));
        Assert.Equal(2, GpuWaitRegistry.Count);

        SubmitAcb(ctx, memory, SignalAddress, signalDwords, owner: 2);
        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, FirstResultAddress) == 0x1111_1111 &&
                  GpuWaitRegistry.Count == 1,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0u, ReadUInt32(memory, SecondResultAddress));
        Assert.Equal(0UL, ReadUInt64(memory, SemaphoreAddress));

        SubmitAcb(ctx, memory, SignalAddress, signalDwords, owner: 2);
        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, SecondResultAddress) == 0x2222_2222,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0UL, ReadUInt64(memory, SemaphoreAddress));
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void Wait_ConsumesStoredTokenAtItsOrderedPosition()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, SemaphoreAddress, 1);
        var dwords = WriteWaitThenResult(
            memory,
            FirstWaitAddress,
            FirstResultAddress,
            0xABCD_1234);

        SubmitDcb(ctx, memory, FirstWaitAddress, dwords);

        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, FirstResultAddress) == 0xABCD_1234,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0UL, ReadUInt64(memory, SemaphoreAddress));
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void UnsupportedPacket_StopsCurrentAndLaterSubmissions()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteDwords(
            memory,
            FirstWaitAddress,
            Pm4Header(4, ItMemSemaphore),
            unchecked((uint)SemaphoreAddress),
            unchecked((uint)(SemaphoreAddress >> 32)),
            5u << 29,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)FirstResultAddress),
            unchecked((uint)(FirstResultAddress >> 32)),
            0x1111_1111);

        SubmitDcb(ctx, memory, FirstWaitAddress, 9);
        Assert.Equal(0u, ReadUInt32(memory, FirstResultAddress));

        var laterDwords = WriteResult(
            memory,
            SecondWaitAddress,
            SecondResultAddress,
            0x2222_2222);
        SubmitDcb(ctx, memory, SecondWaitAddress, laterDwords);
        Assert.Equal(0u, ReadUInt32(memory, SecondResultAddress));

        var multiCommandAddress = BaseAddress + 0x6000;
        var multiDwords = WriteResult(
            memory,
            multiCommandAddress,
            ThirdResultAddress,
            0x3333_3333);
        SubmitMultiDcb(ctx, memory, multiCommandAddress, multiDwords);
        Assert.Equal(0u, ReadUInt32(memory, ThirdResultAddress));
    }

    [Fact]
    public void FaultAfterWait_DropsSubmissionThatWasAlreadyQueued()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var waitingDwords = WriteWaitResultThenUnsupported(memory, FirstWaitAddress);
        var pendingDwords = WriteResult(
            memory,
            SecondWaitAddress,
            SecondResultAddress,
            0x2222_2222);
        var signalDwords = WriteSignal(memory, SignalAddress);

        SubmitDcb(ctx, memory, FirstWaitAddress, waitingDwords);
        SubmitDcb(ctx, memory, SecondWaitAddress, pendingDwords);
        Assert.Equal(1, GpuWaitRegistry.Count);
        Assert.Equal(0u, ReadUInt32(memory, FirstResultAddress));
        Assert.Equal(0u, ReadUInt32(memory, SecondResultAddress));

        SubmitAcb(ctx, memory, SignalAddress, signalDwords, owner: 1);
        Assert.True(SpinWait.SpinUntil(
            () => ReadUInt32(memory, FirstResultAddress) == 0x1111_1111,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, GpuWaitRegistry.Count);
        Assert.Equal(0u, ReadUInt32(memory, SecondResultAddress));
    }

    private static uint WriteWaitThenResult(
        FakeCpuMemory memory,
        ulong commandAddress,
        ulong resultAddress,
        uint result)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(4, ItMemSemaphore),
            unchecked((uint)SemaphoreAddress),
            unchecked((uint)(SemaphoreAddress >> 32)),
            7u << 29,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)resultAddress),
            unchecked((uint)(resultAddress >> 32)),
            result);
        return 9;
    }

    private static uint WriteSignal(FakeCpuMemory memory, ulong commandAddress)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(4, ItMemSemaphore),
            unchecked((uint)SemaphoreAddress),
            unchecked((uint)(SemaphoreAddress >> 32)),
            6u << 29);
        return 4;
    }

    private static uint WriteWaitResultThenUnsupported(
        FakeCpuMemory memory,
        ulong commandAddress)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(4, ItMemSemaphore),
            unchecked((uint)SemaphoreAddress),
            unchecked((uint)(SemaphoreAddress >> 32)),
            7u << 29,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)FirstResultAddress),
            unchecked((uint)(FirstResultAddress >> 32)),
            0x1111_1111,
            Pm4Header(4, ItMemSemaphore),
            unchecked((uint)SemaphoreAddress),
            unchecked((uint)(SemaphoreAddress >> 32)),
            5u << 29);
        return 13;
    }

    private static uint WriteResult(
        FakeCpuMemory memory,
        ulong commandAddress,
        ulong resultAddress,
        uint result)
    {
        WriteDwords(
            memory,
            commandAddress,
            Pm4Header(5, ItWriteData),
            2u << 8,
            unchecked((uint)resultAddress),
            unchecked((uint)(resultAddress >> 32)),
            result);
        return 5;
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

    private static void SubmitMultiDcb(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong commandAddress,
        uint dwordCount)
    {
        WriteUInt64(memory, MultiAddressArray, commandAddress);
        WriteUInt32(memory, MultiSizeArray, dwordCount);
        ctx[CpuRegister.Rdi] = MultiAddressArray;
        ctx[CpuRegister.Rsi] = MultiSizeArray;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AgcExports.DriverSubmitMultiDcbs(ctx));
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
