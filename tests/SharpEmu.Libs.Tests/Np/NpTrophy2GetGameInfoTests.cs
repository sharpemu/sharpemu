// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Stubs;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2GetGameInfoTests
{
    // SceNpTrophy2GameDetails is six uint32 counters and a 128 byte title;
    // SceNpTrophy2GameData is six uint32 counters.
    private const int GameDetailsSize = 6 * sizeof(uint) + 128;
    private const int GameDataSize = 6 * sizeof(uint);

    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x400;
    private const ulong DetailsAddress = MemoryBase + 0x100;
    private const ulong DataAddress = MemoryBase + 0x200;
    private const byte Stale = 0xCD;

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _context;

    public NpTrophy2GetGameInfoTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        var stale = new byte[MemorySize];
        Array.Fill(stale, Stale);
        Assert.True(_memory.TryWrite(MemoryBase, stale));
    }

    [Fact]
    public void ReportsAnEmptyTrophySetInBothOutputs()
    {
        Assert.Equal(0, GetGameInfo(DetailsAddress, DataAddress));

        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        AssertZeroedBetweenIntactGuards(DetailsAddress, GameDetailsSize);
        AssertZeroedBetweenIntactGuards(DataAddress, GameDataSize);
    }

    [Fact]
    public void AcceptsAnOmittedDataOutput()
    {
        Assert.Equal(0, GetGameInfo(DetailsAddress, 0));

        AssertZeroedBetweenIntactGuards(DetailsAddress, GameDetailsSize);
        AssertFilled(DataAddress - 8, GameDataSize + 16, Stale);
    }

    [Fact]
    public void ReportsAFaultWhenAnOutputCannotBeWritten()
    {
        var detailsPastTheMapping = MemoryBase + MemorySize - 8;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            GetGameInfo(detailsPastTheMapping, DataAddress));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            _context[CpuRegister.Rax]);
    }

    // sceNpTrophy2GetGameInfo(context, handle, details, data)
    private int GetGameInfo(ulong details, ulong data)
    {
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = details;
        _context[CpuRegister.Rcx] = data;
        return GameServiceStubs.NpTrophy2GetGameInfo(_context);
    }

    private void AssertZeroedBetweenIntactGuards(ulong address, int size)
    {
        AssertFilled(address - 8, 8, Stale);
        AssertFilled(address, size, 0);
        AssertFilled(address + (ulong)size, 8, Stale);
    }

    private void AssertFilled(ulong address, int size, byte expected)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        Assert.All(bytes, value => Assert.Equal(expected, value));
    }
}
