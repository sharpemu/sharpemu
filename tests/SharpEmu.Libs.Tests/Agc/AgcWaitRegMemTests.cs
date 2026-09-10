// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcWaitRegMemTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong StackAddress = BaseAddress + 0x800;

    [Theory]
    [InlineData(1u, 0x0000_0115u)]
    [InlineData(2u, 0x0000_0015u)]
    public void CbCondWrite_EmitsDocumentedPacketLayout(
        uint writeSpace,
        uint expectedControl)
    {
        var memory = CreateMemory(out var ctx);
        var writeAddress = BaseAddress + 0xD04;
        var readAddress = BaseAddress + 0xC04;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 5;
        ctx[CpuRegister.Rdx] = writeSpace;
        ctx[CpuRegister.Rcx] = writeAddress;
        ctx[CpuRegister.R8] = 0xCAFE_BABE;
        ctx[CpuRegister.R9] = readAddress;
        WriteUInt32(memory, StackAddress + 8, 0x1122_3344);
        WriteUInt32(memory, StackAddress + 16, 0xFFFF_00FF);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbCondWrite(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC007_4500u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(expectedControl, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal((uint)readAddress, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0xFFFF_00FFu, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal((uint)writeAddress, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 28));
        Assert.Equal(0xCAFE_BABEu, ReadUInt32(memory, PacketAddress + 32));
        Assert.Equal(PacketAddress + 36, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void CbCondWriteGetSize_ReturnsNineDwords()
    {
        _ = CreateMemory(out var ctx);

        Assert.Equal(9 * sizeof(uint), AgcExports.CbCondWriteGetSize(ctx));
        Assert.Equal(9u * sizeof(uint), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void CbCondWrite_AllowsNullAddressForScratchWrite()
    {
        var memory = CreateMemory(out var ctx);
        var readAddress = BaseAddress + 0xC04;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 5;
        ctx[CpuRegister.Rdx] = 2;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 1;
        ctx[CpuRegister.R9] = readAddress;
        WriteUInt32(memory, StackAddress + 8, 0x1122_3344);
        WriteUInt32(memory, StackAddress + 16, uint.MaxValue);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbCondWrite(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 28));
    }

    [Theory]
    [InlineData(4u, false)]
    [InlineData(5u, true)]
    [InlineData(6u, true)]
    public void CbReleaseMem_EncodesQueuedInterruptModes(
        uint interrupt,
        bool preservesCondition)
    {
        var memory = CreateMemory(out var ctx);
        var conditionAddress = BaseAddress + 0xC08;
        const ulong data = 0x1122_3344_5566_7788UL;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = conditionAddress;
        WriteUInt64(memory, StackAddress + 8, 2);
        WriteUInt64(memory, StackAddress + 16, data);
        WriteUInt64(memory, StackAddress + 24, 0);
        WriteUInt64(memory, StackAddress + 32, 0);
        WriteUInt64(memory, StackAddress + 40, interrupt);
        WriteUInt64(memory, StackAddress + 48, 0xFFFF_FFFF);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbReleaseMem(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC006_1060u, ReadUInt32(memory, PacketAddress));
        Assert.Equal((2u << 16) | (interrupt << 24), ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(
            preservesCondition ? (uint)conditionAddress : 0u,
            ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(
            preservesCondition ? (uint)(conditionAddress >> 32) : 0u,
            ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(
            preservesCondition ? unchecked((uint)data) : 0u,
            ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(
            preservesCondition ? (uint)(data >> 32) : 0u,
            ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(0x07FF_FFFFu, ReadUInt32(memory, PacketAddress + 28));
    }

    [Fact]
    public void CbReleaseMem_RejectsContextInterruptWithCacheOperations()
    {
        var memory = CreateMemory(out var ctx);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.R9] = BaseAddress + 0xC08;
        WriteUInt64(memory, StackAddress + 8, 0);
        WriteUInt64(memory, StackAddress + 16, 0);
        WriteUInt64(memory, StackAddress + 24, 0);
        WriteUInt64(memory, StackAddress + 32, 0);
        WriteUInt64(memory, StackAddress + 40, 4);
        WriteUInt64(memory, StackAddress + 48, 1);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbReleaseMem(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DcbWaitRegMem32_EmitsGen5PacketLayout()
    {
        var memory = CreateMemory(out var ctx);
        var waitAddress = BaseAddress + 0xC03;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 4;
        ctx[CpuRegister.R8] = 2;
        ctx[CpuRegister.R9] = waitAddress;
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 0xAABB_CCDD_EEFF_0011);
        WriteUInt32(memory, StackAddress + 24, 0x123456);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWaitRegMem(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC005_1028u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0000_0C00u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xEEFF_0011u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x0400_0053u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(0xFFFFu, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(PacketAddress + 28, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbWaitRegMem64_EmitsGen5PacketLayout()
    {
        var memory = CreateMemory(out var ctx);
        var waitAddress = BaseAddress + 0xC07;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 6;
        ctx[CpuRegister.Rcx] = 4;
        ctx[CpuRegister.R8] = 1;
        ctx[CpuRegister.R9] = waitAddress;
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 0xAABB_CCDD_EEFF_0011);
        WriteUInt32(memory, StackAddress + 24, 0x320);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWaitRegMem(ctx));
        Assert.Equal(0xC007_1058u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0000_0C00u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xEEFF_0011u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0xAABB_CCDDu, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(0x0200_0096u, ReadUInt32(memory, PacketAddress + 28));
        Assert.Equal(0x32u, ReadUInt32(memory, PacketAddress + 32));
    }

    [Theory]
    [InlineData(1u, 2u, 0xFFFF_FFFFu, 1u, true)]
    [InlineData(2u, 2u, 0xFFFF_FFFFu, 2u, true)]
    [InlineData(0x12u, 0x2u, 0xFu, 3u, true)]
    [InlineData(0x12u, 0x22u, 0xFu, 3u, false)]
    [InlineData(0x12u, 0x22u, 0xFu, 4u, true)]
    [InlineData(4u, 5u, 0xFFFF_FFFFu, 5u, false)]
    [InlineData(6u, 6u, 0xFFFF_FFFFu, 5u, true)]
    public void ConditionalComparison_AppliesMaskOnlyToObservedValue(
        uint value,
        uint reference,
        uint mask,
        uint compareFunction,
        bool expected) =>
        Assert.Equal(
            expected,
            WaitOperation.TryCompare(value, reference, mask, compareFunction, out var satisfied) && satisfied);

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, true)]
    [InlineData(5u, false)]
    public void WaitOperationValidation_AcceptsOnlyDocumentedValues(
        uint operation,
        bool expected) =>
        Assert.Equal(expected, WaitOperation.IsValid(operation));

    [Theory]
    [InlineData(0u, false, true)]
    [InlineData(1u, false, true)]
    [InlineData(4u, false, false)]
    [InlineData(4u, true, true)]
    public void ConditionalWait_UsesScratchState(
        uint operation,
        bool scratchEnabled,
        bool expected) =>
        Assert.Equal(
            expected,
            WaitOperation.ShouldExecute(operation, scratchEnabled));

    [Theory]
    [InlineData(0x0400_0013u, false, 0u)]
    [InlineData(0x0400_0113u, false, 1u)]
    [InlineData(0x0400_0053u, false, 4u)]
    [InlineData(0x0200_0013u, true, 0u)]
    [InlineData(0x0200_0113u, true, 1u)]
    [InlineData(0x0200_0093u, true, 4u)]
    public void WaitOperationDecoder_ReadsGen5ControlFields(
        uint control,
        bool is64Bit,
        uint expected) =>
        Assert.Equal(expected, WaitOperation.Decode(control, is64Bit));

    [Theory]
    [InlineData(0u, false, 1u, false, 0UL, 0UL, true, false)]
    [InlineData(1u, false, 1u, false, 0UL, 0UL, false, true)]
    [InlineData(1u, true, 1u, false, 0UL, 0UL, true, true)]
    [InlineData(1u, true, 0u, false, 0UL, 0UL, false, true)]
    [InlineData(2u, false, 2u, false, 0UL, 0UL, true, true)]
    [InlineData(3u, false, 1u, false, 0UL, 0UL, true, false)]
    [InlineData(4u, false, 2u, false, 0UL, 0UL, false, true)]
    [InlineData(5u, false, 1u, false, 0UL, 1UL, false, false)]
    [InlineData(5u, false, 1u, true, 2UL, 3UL, false, true)]
    [InlineData(5u, false, 1u, true, 4UL, 3UL, false, false)]
    [InlineData(5u, false, 1u, true, 0x1_0000_0002UL, 3UL, false, true)]
    [InlineData(6u, false, 2u, true, 2UL, 3UL, false, true)]
    [InlineData(6u, false, 2u, true, 4UL, 3UL, false, false)]
    [InlineData(7u, false, 1u, true, 0UL, 0UL, false, false)]
    public void QueuedInterruptDecision_FollowsDocumentedModes(
        uint interrupt,
        bool isAsyncCompute,
        uint dataSelection,
        bool conditionReadable,
        ulong conditionValue,
        ulong data,
        bool expectedWrite,
        bool expectedInterrupt)
    {
        var decision = InterruptDecision.Evaluate(
            interrupt,
            isAsyncCompute,
            dataSelection,
            conditionReadable,
            conditionValue,
            data);

        Assert.Equal(expectedWrite, decision.WritesData);
        Assert.Equal(expectedInterrupt, decision.RaisesInterrupt);
    }

    [Fact]
    public void WaitRegMemPatchFunctions_UseGen5Fields()
    {
        var memory = CreateMemory(out var ctx);
        WriteUInt32(memory, PacketAddress, 0xC005_1028);
        WriteUInt32(memory, PacketAddress + 20, 0x0400_0153);

        ctx[CpuRegister.Rdi] = PacketAddress;
        ctx[CpuRegister.Rsi] = BaseAddress + 0xD07;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchAddress(ctx));
        Assert.Equal(0x0000_0D04u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));

        ctx[CpuRegister.Rsi] = 5;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchCompareFunction(ctx));
        Assert.Equal(0x0400_0155u, ReadUInt32(memory, PacketAddress + 20));

        ctx[CpuRegister.Rsi] = 0xDEAD_BEEF;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchReference(ctx));
        Assert.Equal(0xDEAD_BEEFu, ReadUInt32(memory, PacketAddress + 16));
    }

    [Theory]
    [InlineData(0u, 56)]
    [InlineData(1u, 64)]
    [InlineData(2u, 0)]
    public void DcbWaitOnAddressGetSize_ReturnsNativePacketSize(uint size, int expected)
    {
        CreateMemory(out var ctx);
        ctx[CpuRegister.Rdi] = size;

        Assert.Equal(expected, AgcExports.DcbWaitOnAddressGetSize(ctx));
        Assert.Equal((ulong)expected, ctx[CpuRegister.Rax]);
    }

    private static FakeCpuMemory CreateMemory(out CpuContext ctx)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return memory;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
