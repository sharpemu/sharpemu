// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using SharpEmu.Libs.Stubs;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

// The counts come from TrophyService, which reads the title's trophy package once.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class NpTrophy2GetGameInfoTests : IDisposable
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
        TrophyService.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
        var stale = new byte[MemorySize];
        Array.Fill(stale, Stale);
        Assert.True(_memory.TryWrite(MemoryBase, stale));
    }

    public void Dispose() => TrophyService.ResetForTests();

    [Fact]
    public void ReportsAnEmptyTrophySetWhenTheTitleShipsNone()
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

    // The counts describe the set the trophy service reads from the title's package, the one
    // sceNpTrophy2GetTrophyInfoArray returns.
    [Fact]
    public void CountsTheTrophiesOfThePackageByGradeAndUnlock()
    {
        var trophies = TrophyService.ReadTrophies(BuildPackage(
            """{"trophies":[{"id":0,"grade":"P"},{"id":1,"grade":"G"},{"id":2,"grade":"B"},{"id":3,"grade":"B"}]}"""));
        trophies[3].Unlocked = true;
        var details = new byte[GameDetailsSize];
        var data = new byte[GameDataSize];

        GameServiceStubs.FillTrophy2GameInfo(details, data, trophies);

        // numGroups, numTrophies, numPlatinum, numGold, numSilver, numBronze, then a zero title.
        Assert.Equal([0u, 4u, 1u, 1u, 0u, 2u], Counters(details));
        Assert.All(details[(6 * sizeof(uint))..], value => Assert.Equal(0, value));
        // unlockedTrophies, unlockedPlatinum, unlockedGold, unlockedSilver, unlockedBronze, progressPercentage
        Assert.Equal([1u, 0u, 0u, 0u, 1u, 0u], Counters(data));
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

    private static uint[] Counters(byte[] bytes) =>
        [.. Enumerable.Range(0, 6).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint))))];

    // The package TrophyService reads: a 0x40-byte header (big-endian magic, the file count at 0x10, the
    // table offset at 0x14 counted from 0x20), 0x40-byte entries (name, offset, length), then the data.
    private static byte[] BuildPackage(string tropconf)
    {
        var json = Encoding.UTF8.GetBytes(tropconf);
        const int headerSize = 0x40;
        const int entrySize = 0x40;
        var dataOffset = headerSize + entrySize;
        var package = new byte[dataOffset + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(package, 0xB228C60Au);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x14), headerSize - 0x20);
        var entry = package.AsSpan(headerSize, entrySize);
        Encoding.UTF8.GetBytes("tropconf.json").CopyTo(entry);
        BinaryPrimitives.WriteUInt64BigEndian(entry[0x20..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(entry[0x28..], (ulong)json.Length);
        json.CopyTo(package.AsSpan(dataOffset));
        return package;
    }

    private void AssertFilled(ulong address, int size, byte expected)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        Assert.All(bytes, value => Assert.Equal(expected, value));
    }
}
