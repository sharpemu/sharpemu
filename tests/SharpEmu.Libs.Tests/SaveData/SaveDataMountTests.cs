// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[CollectionDefinition("SaveData", DisableParallelization = true)]
public sealed class SaveDataCollectionDefinition;

[Collection("SaveData")]
public sealed class SaveDataMountTests : IDisposable
{
    private const ulong MemoryBase = 0x0000_7FFF_2000_0000;
    private const ulong MountAddress = MemoryBase + 0x1000;
    private const ulong DirNameAddress = MemoryBase + 0x2000;
    private const ulong SearchCondAddress = MemoryBase + 0x3000;
    private const ulong SearchResultAddress = MemoryBase + 0x3100;
    private const ulong SearchDirNamesAddress = MemoryBase + 0x4000;
    private const ulong SearchParamsAddress = MemoryBase + 0x5000;
    private const uint MountModeCreate2 = 1u << 5;
    private readonly string? _previousSaveDataDirectory;
    private readonly string _saveDataDirectory;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x20_000);
    private readonly CpuContext _context;

    public SaveDataMountTests()
    {
        _previousSaveDataDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        _saveDataDirectory = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-tests-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _saveDataDirectory);
        SaveDataExports.ConfigureApplicationInfo("TEST00001");
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void Mount3_AllocatesDistinctSlotsAndReusesReleasedSlot()
    {
        var firstResult = MemoryBase + 0x6000;
        var secondResult = MemoryBase + 0x6100;
        var thirdResult = MemoryBase + 0x6200;
        var afterResetResult = MemoryBase + 0x6300;

        Assert.Equal(0, Mount("Alpha", firstResult));
        Assert.Equal("/savedata0", ReadCString(firstResult, 16));
        Assert.Equal(0, Mount("Beta", secondResult));
        Assert.Equal("/savedata1", ReadCString(secondResult, 16));

        Assert.Equal(0, Umount(firstResult));
        Assert.Equal(0, Mount("Gamma", thirdResult));
        Assert.Equal("/savedata0", ReadCString(thirdResult, 16));

        SaveDataExports.ConfigureApplicationInfo("TEST00001");
        Assert.Equal(0, Mount("Delta", afterResetResult));
        Assert.Equal("/savedata0", ReadCString(afterResetResult, 16));
    }

    [Fact]
    public void Mount3_ReturnsMountFullAfterSixteenLiveMounts()
    {
        for (var i = 0; i < 16; i++)
        {
            var resultAddress = MemoryBase + 0x6000 + ((ulong)i * 0x100);
            Assert.Equal(0, Mount($"Slot{i}", resultAddress));
            Assert.Equal($"/savedata{i}", ReadCString(resultAddress, 16));
        }

        Assert.Equal(unchecked((int)0x809F000C), Mount("Overflow", MemoryBase + 0x7000));
    }

    [Fact]
    public void DirNameSearch_WritesDirectoryNameToParamSubtitle()
    {
        var mountResultAddress = MemoryBase + 0x6000;
        Assert.Equal(0, Mount("WorldOne", mountResultAddress));
        Assert.Equal(0, Umount(mountResultAddress));

        Span<byte> cond = stackalloc byte[0x40];
        cond.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(cond[0x00..], 1000);
        Assert.True(_memory.TryWrite(SearchCondAddress, cond));

        Span<byte> result = stackalloc byte[0x38];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], SearchDirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x18..], SearchParamsAddress);
        Assert.True(_memory.TryWrite(SearchResultAddress, result));

        _context[CpuRegister.Rdi] = SearchCondAddress;
        _context[CpuRegister.Rsi] = SearchResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(_context));

        Assert.Equal("Saved Data", ReadCString(SearchParamsAddress, 128));
        Assert.Equal("WorldOne", ReadCString(SearchParamsAddress + 0x80, 128));
        Assert.Equal(string.Empty, ReadCString(SearchParamsAddress + 0x100, 1024));
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousSaveDataDirectory);
        if (Directory.Exists(_saveDataDirectory))
        {
            Directory.Delete(_saveDataDirectory, recursive: true);
        }
    }

    private int Mount(string dirName, ulong resultAddress)
    {
        Span<byte> mount = stackalloc byte[0x50];
        mount.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(mount[0x00..], 1000);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x08..], DirNameAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x10..], 96);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x18..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(mount[0x20..], MountModeCreate2);
        Assert.True(_memory.TryWrite(MountAddress, mount));
        _memory.WriteCString(DirNameAddress, dirName);

        _context[CpuRegister.Rdi] = MountAddress;
        _context[CpuRegister.Rsi] = resultAddress;
        return SaveDataExports.SaveDataMount3(_context);
    }

    private int Umount(ulong mountPointAddress)
    {
        _context[CpuRegister.Rdi] = mountPointAddress;
        _context[CpuRegister.Rsi] = 0;
        return SaveDataExports.SaveDataUmount2(_context);
    }

    private string ReadCString(ulong address, int capacity)
    {
        var bytes = new byte[capacity];
        Assert.True(_memory.TryRead(address, bytes));
        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes, 0, length);
    }
}
