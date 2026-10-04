// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong Stack = MemoryBase + 0x100;
    private const ulong Details = MemoryBase + 0x400;
    private const ulong Data = MemoryBase + 0xA00;
    private const ulong Count = MemoryBase + 0xB00;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2000);
    private readonly CpuContext _context;

    public NpTrophy2ExportsTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        _context[CpuRegister.Rsp] = Stack;
        Assert.True(_context.TryWriteUInt64(Stack + sizeof(ulong), Count));
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = 6;
        _context[CpuRegister.R8] = Details;
        _context[CpuRegister.R9] = Data;
    }

    [Fact]
    public void GetTrophyInfoArray_WritesOneCompatibleEntryAndCount()
    {
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);

        Assert.True(_context.TryReadUInt32(Count, out var count));
        Assert.Equal(1u, count);

        Span<byte> details = stackalloc byte[1312];
        Assert.True(_memory.TryRead(Details, details));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(details));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(details[4..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(details[8..]));
        Assert.Equal("Trophy\0"u8.ToArray(), details.Slice(32, 7).ToArray());
        Assert.Equal("Trophy\0"u8.ToArray(), details.Slice(160, 7).ToArray());
        Assert.True(details[1184..].ToArray().All(value => value == 0));

        Span<byte> data = stackalloc byte[32];
        Assert.True(_memory.TryRead(Data, data));
        Assert.True(data.ToArray().All(value => value == 0));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 6u)]
    public void GetTrophyInfoArray_EmptyPageWritesZeroCountWithoutTouchingArrays(
        uint offset,
        uint limit)
    {
        Assert.True(_memory.TryWrite(Details, Enumerable.Repeat((byte)0xA5, 1312).ToArray()));
        Assert.True(_memory.TryWrite(Data, Enumerable.Repeat((byte)0x5A, 32).ToArray()));
        _context[CpuRegister.Rdx] = offset;
        _context[CpuRegister.Rcx] = limit;

        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(_context));
        Assert.True(_context.TryReadUInt32(Count, out var count));
        Assert.Equal(0u, count);

        Span<byte> detailPrefix = stackalloc byte[4];
        Span<byte> dataPrefix = stackalloc byte[4];
        Assert.True(_memory.TryRead(Details, detailPrefix));
        Assert.True(_memory.TryRead(Data, dataPrefix));
        Assert.True(detailPrefix.ToArray().All(value => value == 0xA5));
        Assert.True(dataPrefix.ToArray().All(value => value == 0x5A));
    }

    [Fact]
    public void GetTrophyInfoArray_ReportsMemoryFaultForInvalidOutput()
    {
        _context[CpuRegister.R8] = MemoryBase + 0x1F00;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(_context));
    }

    [Fact]
    public void GetTrophyInfoArray_IsRegisteredForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("y3zHpdZO6ME", out var export));
        Assert.Equal("sceNpTrophy2GetTrophyInfoArray", export.Name);
        Assert.Equal("libSceNpTrophy2", export.LibraryName);
    }
}
