// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[CollectionDefinition("SaveData", DisableParallelization = true)]
public sealed class SaveDataCollectionDefinition;

[Collection("SaveData")]
public sealed class SaveDataExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong MountAddress = MemoryBase + 0x100;
    private const ulong DirNameAddress = MemoryBase + 0x200;
    private const ulong MountResultAddress = MemoryBase + 0x300;
    private const ulong ParamAddress = MemoryBase + 0x1000;
    private const ulong SearchCondAddress = MemoryBase + 0x2000;
    private const ulong SearchResultAddress = MemoryBase + 0x2100;
    private const ulong SearchDirNamesAddress = MemoryBase + 0x2200;
    private const ulong SearchParamsAddress = MemoryBase + 0x2300;
    private const ulong DeleteAddress = MemoryBase + 0x3000;

    [Fact]
    public void MountSetParamSearchAndDelete_RoundTripsSaveMetadata()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-{Guid.NewGuid():N}");
        var previousSaveRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var memory = new FakeCpuMemory(MemoryBase, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", saveRoot);
            SaveDataExports.ConfigureApplicationInfo("PPSA02929");
            memory.WriteCString(DirNameAddress, "slot0");

            Span<byte> mount = stackalloc byte[0x30];
            mount.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(mount, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(mount[0x08..], DirNameAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(mount[0x10..], 96);
            BinaryPrimitives.WriteUInt32LittleEndian(mount[0x20..], 1u << 5);
            Assert.True(memory.TryWrite(MountAddress, mount));

            context[CpuRegister.Rdi] = MountAddress;
            context[CpuRegister.Rsi] = MountResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataMount3(context));
            Assert.EndsWith(
                Path.Combine("PPSA02929", "slot0", "data.bin"),
                KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));

            var expectedParam = new byte[0x530];
            WriteAscii(expectedParam.AsSpan(0x00, 128), "Dreaming Sarah");
            WriteAscii(expectedParam.AsSpan(0x80, 128), "slot0");
            WriteAscii(expectedParam.AsSpan(0x100, 1024), "New game");
            BinaryPrimitives.WriteUInt32LittleEndian(expectedParam.AsSpan(0x500), 42);
            BinaryPrimitives.WriteInt64LittleEndian(expectedParam.AsSpan(0x508), 1_784_000_000);
            Assert.True(memory.TryWrite(ParamAddress, expectedParam));

            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = (ulong)expectedParam.Length;
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));

            var shortTitle = Encoding.ASCII.GetBytes("Sarah\0");
            Assert.True(memory.TryWrite(ParamAddress, shortTitle));
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = (ulong)shortTitle.Length;
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));
            WriteAscii(expectedParam.AsSpan(0x00, 128), "Sarah");

            Span<byte> searchCond = stackalloc byte[0x20];
            searchCond.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(searchCond, 1);
            Assert.True(memory.TryWrite(SearchCondAddress, searchCond));

            Span<byte> searchResult = stackalloc byte[0x28];
            searchResult.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(searchResult[0x08..], SearchDirNamesAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(searchResult[0x10..], 1);
            BinaryPrimitives.WriteUInt64LittleEndian(searchResult[0x18..], SearchParamsAddress);
            Assert.True(memory.TryWrite(SearchResultAddress, searchResult));

            context[CpuRegister.Rdi] = SearchCondAddress;
            context[CpuRegister.Rsi] = SearchResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));

            var actualParam = new byte[expectedParam.Length];
            Assert.True(memory.TryRead(SearchParamsAddress, actualParam));
            Assert.Equal(expectedParam, actualParam);

            WriteDeleteRequest(memory, 1, DirNameAddress, DeleteAddress);
            context[CpuRegister.Rdi] = DeleteAddress;
            Assert.Equal(unchecked((int)0x809F0003), SaveDataExports.SaveDataDelete(context));
            Assert.True(Directory.Exists(Path.Combine(saveRoot, "1", "PPSA02929", "slot0")));

            context[CpuRegister.Rdi] = MountResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataUmount2(context));
            Assert.Equal("/savedata0/data.bin", KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));

            WriteDeleteRequest(memory, 1, DirNameAddress, DeleteAddress);
            context[CpuRegister.Rdi] = DeleteAddress;
            Assert.Equal(0, SaveDataExports.SaveDataDelete(context));
            Assert.False(Directory.Exists(Path.Combine(saveRoot, "1", "PPSA02929", "slot0")));
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", previousSaveRoot);
            if (Directory.Exists(saveRoot))
            {
                Directory.Delete(saveRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Delete_RejectsParentDirectoryTraversal()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-{Guid.NewGuid():N}");
        var previousSaveRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var memory = new FakeCpuMemory(MemoryBase, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", saveRoot);
            SaveDataExports.ConfigureApplicationInfo("PPSA02929");
            var sentinelPath = Path.Combine(saveRoot, "1", "sentinel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sentinelPath)!);
            File.WriteAllText(sentinelPath, "must survive");
            memory.WriteCString(DirNameAddress, "..");
            WriteDeleteRequest(memory, 1, DirNameAddress, DeleteAddress);

            context[CpuRegister.Rdi] = DeleteAddress;
            Assert.Equal(unchecked((int)0x809F0000), SaveDataExports.SaveDataDelete(context));
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", previousSaveRoot);
            if (Directory.Exists(saveRoot))
            {
                Directory.Delete(saveRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void FirstPartialSetParam_PreservesDirectoryMtime()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-{Guid.NewGuid():N}");
        var previousSaveRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var memory = new FakeCpuMemory(MemoryBase, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", saveRoot);
            SaveDataExports.ConfigureApplicationInfo("PPSA02929");
            memory.WriteCString(DirNameAddress, "slot0");

            Span<byte> mount = stackalloc byte[0x30];
            mount.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(mount, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(mount[0x08..], DirNameAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(mount[0x20..], 1u << 5);
            Assert.True(memory.TryWrite(MountAddress, mount));
            context[CpuRegister.Rdi] = MountAddress;
            context[CpuRegister.Rsi] = MountResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataMount3(context));

            var savePath = Path.Combine(saveRoot, "1", "PPSA02929", "slot0");
            var expectedMtime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
            Directory.SetLastWriteTimeUtc(savePath, expectedMtime);
            var shortTitle = Encoding.ASCII.GetBytes("Sarah\0");
            Assert.True(memory.TryWrite(ParamAddress, shortTitle));
            context[CpuRegister.Rdi] = MountResultAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = ParamAddress;
            context[CpuRegister.Rcx] = (ulong)shortTitle.Length;
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(context));

            var storedParam = File.ReadAllBytes(Path.Combine(saveRoot, "1", "PPSA02929", "sce_params", "slot0.bin"));
            var actualMtime = BinaryPrimitives.ReadInt64LittleEndian(storedParam.AsSpan(0x508));
            Assert.Equal(new DateTimeOffset(expectedMtime).ToUnixTimeSeconds(), actualMtime);
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", previousSaveRoot);
            if (Directory.Exists(saveRoot))
            {
                Directory.Delete(saveRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void DirNameSearch_SortsByStoredSaveMtime()
    {
        var saveRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-{Guid.NewGuid():N}");
        var previousSaveRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var memory = new FakeCpuMemory(MemoryBase, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", saveRoot);
            SaveDataExports.ConfigureApplicationInfo("PPSA02929");
            var titleRoot = Path.Combine(saveRoot, "1", "PPSA02929");
            var newerHostPath = Directory.CreateDirectory(Path.Combine(titleRoot, "stored-oldest")).FullName;
            var olderHostPath = Directory.CreateDirectory(Path.Combine(titleRoot, "stored-newest")).FullName;
            Directory.SetLastWriteTimeUtc(newerHostPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(olderHostPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var metadataRoot = Directory.CreateDirectory(Path.Combine(titleRoot, "sce_params")).FullName;
            WriteStoredMtime(Path.Combine(metadataRoot, "stored-oldest.bin"), 100);
            WriteStoredMtime(Path.Combine(metadataRoot, "stored-newest.bin"), 200);

            Span<byte> searchCond = stackalloc byte[0x20];
            searchCond.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(searchCond, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(searchCond[0x18..], 3);
            Assert.True(memory.TryWrite(SearchCondAddress, searchCond));

            Span<byte> searchResult = stackalloc byte[0x28];
            searchResult.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(searchResult[0x08..], SearchDirNamesAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(searchResult[0x10..], 2);
            Assert.True(memory.TryWrite(SearchResultAddress, searchResult));

            context[CpuRegister.Rdi] = SearchCondAddress;
            context[CpuRegister.Rsi] = SearchResultAddress;
            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal("stored-oldest", ReadFixedAscii(memory, SearchDirNamesAddress, 32));
            Assert.Equal("stored-newest", ReadFixedAscii(memory, SearchDirNamesAddress + 32, 32));
        }
        finally
        {
            SaveDataExports.ConfigureApplicationInfo(null);
            Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", previousSaveRoot);
            if (Directory.Exists(saveRoot))
            {
                Directory.Delete(saveRoot, recursive: true);
            }
        }
    }

    private static void WriteDeleteRequest(
        FakeCpuMemory memory,
        int userId,
        ulong dirNameAddress,
        ulong deleteAddress)
    {
        Span<byte> delete = stackalloc byte[0x40];
        delete.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(delete, userId);
        BinaryPrimitives.WriteUInt64LittleEndian(delete[0x10..], dirNameAddress);
        Assert.True(memory.TryWrite(deleteAddress, delete));
    }

    private static void WriteStoredMtime(string path, long seconds)
    {
        var param = new byte[0x530];
        BinaryPrimitives.WriteInt64LittleEndian(param.AsSpan(0x508), seconds);
        File.WriteAllBytes(path, param);
    }

    private static string ReadFixedAscii(FakeCpuMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        var encoded = Encoding.ASCII.GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, destination.Length - 1)).CopyTo(destination);
    }
}
