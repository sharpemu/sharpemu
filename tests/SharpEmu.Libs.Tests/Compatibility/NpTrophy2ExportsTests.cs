// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Compatibility;

public sealed class NpTrophy2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int TrophyDataSize = 0x20;

    [Fact]
    public void GetTrophyInfoArray_RegistersForGen5()
    {
        var manager = CreateRegisteredManager();

        Assert.True(manager.TryGetExport("y3zHpdZO6ME", out var export));
        Assert.Equal("sceNpTrophy2GetTrophyInfoArray", export.Name);
        Assert.Equal("libSceNpTrophy2", export.LibraryName);
    }

    [Fact]
    public void GetTrophyInfoArray_WritesLockedRecordsWithoutCrossingBuffer()
    {
        const int trophyCount = 54;
        var manager = CreateRegisteredManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var stackAddress = MemoryBase + 0x100;
        var outCountAddress = MemoryBase + 0x200;
        var dataAddress = MemoryBase + 0x400;
        var dataLength = trophyCount * TrophyDataSize;

        Assert.True(memory.TryWrite(dataAddress - 1, stackalloc byte[] { 0xA5 }));
        Assert.True(memory.TryWrite(dataAddress, Enumerable.Repeat((byte)0xCC, dataLength).ToArray()));
        Assert.True(memory.TryWrite(dataAddress + (ulong)dataLength, stackalloc byte[] { 0x5A }));
        ConfigureCall(ctx, stackAddress, outCountAddress, dataAddress, trophyCount);

        Assert.True(manager.TryDispatch("y3zHpdZO6ME", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt32(outCountAddress, out var writtenCount));
        Assert.Equal((uint)trophyCount, writtenCount);

        var data = new byte[dataLength];
        Assert.True(memory.TryRead(dataAddress, data));
        for (var trophyId = 0; trophyId < trophyCount; trophyId++)
        {
            var record = data.AsSpan(trophyId * TrophyDataSize, TrophyDataSize);
            Assert.Equal((uint)trophyId, BinaryPrimitives.ReadUInt32LittleEndian(record));
            Assert.Equal(0, record[4]);
            Assert.Equal(new byte[TrophyDataSize - sizeof(uint)], record[sizeof(uint)..].ToArray());
        }

        Assert.True(ctx.TryReadByte(dataAddress - 1, out var leadingCanary));
        Assert.True(ctx.TryReadByte(dataAddress + (ulong)dataLength, out var trailingCanary));
        Assert.Equal(0xA5, leadingCanary);
        Assert.Equal(0x5A, trailingCanary);
    }

    [Fact]
    public void GetTrophyInfoArray_RejectsDetailsModeAndClearsCount()
    {
        var manager = CreateRegisteredManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var outCountAddress = MemoryBase + 0x200;
        ConfigureCall(ctx, MemoryBase + 0x100, outCountAddress, MemoryBase + 0x400, 1);
        ctx[CpuRegister.R8] = MemoryBase + 0x300;
        Assert.True(ctx.TryWriteUInt32(outCountAddress, uint.MaxValue));

        Assert.True(manager.TryDispatch("y3zHpdZO6ME", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, result);
        Assert.True(ctx.TryReadUInt32(outCountAddress, out var writtenCount));
        Assert.Equal(0u, writtenCount);
    }

    [Fact]
    public void GetTrophyInfoArray_RejectsOversizedCapacityAndClearsCount()
    {
        var manager = CreateRegisteredManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var outCountAddress = MemoryBase + 0x200;
        ConfigureCall(ctx, MemoryBase + 0x100, outCountAddress, MemoryBase + 0x400, 129);
        Assert.True(ctx.TryWriteUInt32(outCountAddress, uint.MaxValue));

        Assert.True(manager.TryDispatch("y3zHpdZO6ME", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.True(ctx.TryReadUInt32(outCountAddress, out var writtenCount));
        Assert.Equal(0u, writtenCount);
    }

    [Fact]
    public void GetTrophyInfoArray_ReturnsMemoryFaultForUnmappedData()
    {
        var manager = CreateRegisteredManager();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var outCountAddress = MemoryBase + 0x200;
        ConfigureCall(ctx, MemoryBase + 0x100, outCountAddress, MemoryBase + 0x1000, 1);

        Assert.True(manager.TryDispatch("y3zHpdZO6ME", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.True(ctx.TryReadUInt32(outCountAddress, out var writtenCount));
        Assert.Equal(0u, writtenCount);
    }

    private static void ConfigureCall(
        CpuContext ctx,
        ulong stackAddress,
        ulong outCountAddress,
        ulong dataAddress,
        int capacity)
    {
        ctx[CpuRegister.Rsp] = stackAddress;
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = (uint)capacity;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = dataAddress;
        Assert.True(ctx.TryWriteUInt64(stackAddress, 0x8_0000_0000));
        Assert.True(ctx.TryWriteUInt64(stackAddress + sizeof(ulong), outCountAddress));
    }

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(NpTrophy2Exports).Assembly, Generation.Gen5);
        return manager;
    }
}
