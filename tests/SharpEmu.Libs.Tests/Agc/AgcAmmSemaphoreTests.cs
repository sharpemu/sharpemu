// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcAmmSemaphoreTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong OutputAddress = BaseAddress + 0x8000;
    private const ulong RangeSize = 0x4000;
    private const ulong SlotSize = 32;

    public AgcAmmSemaphoreTests()
    {
        AgcExports.ResetAmmSemaphoreMemoryForTests();
    }

    public void Dispose()
    {
        AgcExports.ResetAmmSemaphoreMemoryForTests();
    }

    [Theory]
    [InlineData(0UL, RangeSize)]
    [InlineData(BaseAddress + 1, RangeSize)]
    [InlineData(BaseAddress, RangeSize - 1)]
    [InlineData(BaseAddress, RangeSize / 2)]
    public void Registration_RejectsInvalidRange(ulong baseAddress, ulong sizeBytes)
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = baseAddress;
        ctx[CpuRegister.Rsi] = sizeBytes;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetAmmSemaphoreMemory(ctx));
    }

    [Fact]
    public void Registration_RejectsUnmappedRange()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = RangeSize;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.SetAmmSemaphoreMemory(ctx));
    }

    [Fact]
    public void Registration_ClearsEachCounterAndCannotBeReplaced()
    {
        var ctx = CreateContext(out var memory);
        WriteUInt64(memory, BaseAddress, 9);
        WriteUInt64(memory, BaseAddress + RangeSize - SlotSize, 11);
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = RangeSize;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetAmmSemaphoreMemory(ctx));
        Assert.Equal(0UL, ReadUInt64(memory, BaseAddress));
        Assert.Equal(0UL, ReadUInt64(memory, BaseAddress + RangeSize - SlotSize));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS,
            AgcExports.SetAmmSemaphoreMemory(ctx));
    }

    [Fact]
    public void Registration_IsScopedToGuestMemory()
    {
        var first = CreateContext(out _);
        first[CpuRegister.Rdi] = BaseAddress;
        first[CpuRegister.Rsi] = RangeSize;
        var second = CreateContext(out _);
        second[CpuRegister.Rdi] = BaseAddress;
        second[CpuRegister.Rsi] = RangeSize;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetAmmSemaphoreMemory(first));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetAmmSemaphoreMemory(second));
    }

    [Fact]
    public void LabelLookup_RequiresRegistration()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 3;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.GetSemaphoreLabel(ctx));
    }

    [Fact]
    public void LabelLookup_ResolvesSlotAddress()
    {
        var ctx = CreateContext(out var memory);
        Register(ctx);
        ctx[CpuRegister.Rdi] = 3;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.GetSemaphoreLabel(ctx));
        Assert.Equal(BaseAddress + (3 * SlotSize), ReadUInt64(memory, OutputAddress));
    }

    [Fact]
    public void LabelLookup_RejectsSlotOutsideRange()
    {
        var ctx = CreateContext(out _);
        Register(ctx);
        ctx[CpuRegister.Rdi] = RangeSize / SlotSize;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.GetSemaphoreLabel(ctx));
    }

    [Fact]
    public void LabelLookup_ReportsOutputMemoryFault()
    {
        var ctx = CreateContext(out _);
        Register(ctx);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = BaseAddress + 0x1_0000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.GetSemaphoreLabel(ctx));
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1_0000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void Register(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = RangeSize;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetAmmSemaphoreMemory(ctx));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
