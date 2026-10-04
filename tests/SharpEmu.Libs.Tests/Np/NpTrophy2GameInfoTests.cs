// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Stubs;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2GameInfoTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong Details = MemoryBase + 0x100;
    private const ulong Data = MemoryBase + 0x800;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public NpTrophy2GameInfoTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = Details;
        _context[CpuRegister.Rcx] = Data;
    }

    [Fact]
    public void GetGameInfo_InitializesEveryOutputByteBeforeReturningSuccess()
    {
        Assert.True(_memory.TryWrite(Details, Enumerable.Repeat((byte)0xA5, 152).ToArray()));
        Assert.True(_memory.TryWrite(Data, Enumerable.Repeat((byte)0x5A, 24).ToArray()));

        Assert.Equal(0, GameServiceStubs.NpTrophy2GetGameInfo(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);

        Span<byte> details = stackalloc byte[152];
        Assert.True(_memory.TryRead(Details, details));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(details));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(details[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(details[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(details[12..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(details[16..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(details[20..]));
        Assert.Equal("SharpEmu\0"u8.ToArray(), details.Slice(24, 9).ToArray());
        Assert.True(details[33..].ToArray().All(value => value == 0));

        Span<byte> data = stackalloc byte[24];
        Assert.True(_memory.TryRead(Data, data));
        Assert.True(data.ToArray().All(value => value == 0));
    }

    [Fact]
    public void GetGameInfo_AllowsNullOptionalOutputs()
    {
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = 0;

        Assert.Equal(0, GameServiceStubs.NpTrophy2GetGameInfo(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    [Fact]
    public void GetGameInfo_ReportsMemoryFaultForInvalidOutput()
    {
        _context[CpuRegister.Rdx] = MemoryBase + 0xFF0;

        var result = GameServiceStubs.NpTrophy2GetGameInfo(_context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            _context[CpuRegister.Rax]);
    }
}
