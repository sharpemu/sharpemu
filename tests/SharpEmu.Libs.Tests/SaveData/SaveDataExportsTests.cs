// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[CollectionDefinition("SaveDataState", DisableParallelization = true)]
public sealed class SaveDataStateCollection
{
    public const string Name = "SaveDataState";
}

[Collection(SaveDataStateCollection.Name)]
public sealed class SaveDataExportsTests : IDisposable
{
    private const int SaveDataErrorNotMounted = unchecked((int)0x809F0004);
    private const int SaveDataErrorParameter = unchecked((int)0x809F0000);
    private const int SaveDataErrorBusy = unchecked((int)0x809F0003);
    private const int SaveDataErrorMountFull = unchecked((int)0x809F000C);
    private const int SaveDataErrorInternal = unchecked((int)0x809F000B);
    private const int MemoryFault = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong MountAddress = MemoryBase + 0x100;
    private const ulong DirNameAddress = MemoryBase + 0x200;
    private const ulong MountResultAddress = MemoryBase + 0x300;
    private const ulong SecondMountAddress = MemoryBase + 0x400;
    private const ulong SecondDirNameAddress = MemoryBase + 0x500;
    private const ulong SecondMountResultAddress = MemoryBase + 0x600;
    private const ulong ParamAddress = MemoryBase + 0x1000;
    private const ulong SearchCondAddress = MemoryBase + 0x2000;
    private const ulong SearchResultAddress = MemoryBase + 0x2100;
    private const ulong SearchDirNamesAddress = MemoryBase + 0x2200;
    private const ulong SearchParamsAddress = MemoryBase + 0x2300;
    private readonly string? _originalSaveRoot;
    private readonly string _saveRoot;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _context;

    public SaveDataExportsTests()
    {
        _originalSaveRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        _saveRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-savedata-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _saveRoot);
        SaveDataExports.ConfigureApplicationInfo("PPSA03525");
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void CreateTransactionResource_ReturnsHandleWithoutWritingNonArguments()
    {
        var candidateAddresses = new[]
        {
            MemoryBase + 0x100,
            MemoryBase + 0x200,
            MemoryBase + 0x300,
            MemoryBase + 0x400,
        };
        var sentinel = new byte[] { 0xA5, 0x5A, 0xC3, 0x3C };
        foreach (var address in candidateAddresses)
        {
            Assert.True(_memory.TryWrite(address, sentinel));
        }

        _context[CpuRegister.Rdi] = 0xC0000;
        _context[CpuRegister.Rdx] = candidateAddresses[0];
        _context[CpuRegister.Rcx] = candidateAddresses[1];
        _context[CpuRegister.R8] = candidateAddresses[2];
        _context[CpuRegister.R9] = candidateAddresses[3];

        Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(_context));
        Assert.Equal(1UL, _context[CpuRegister.Rax]);

        foreach (var address in candidateAddresses)
        {
            var actual = new byte[sentinel.Length];
            Assert.True(_memory.TryRead(address, actual));
            Assert.Equal(sentinel, actual);
        }
    }

    [Fact]
    public void CreateTransactionResource_AllocatesMonotonicHandlesAndResetsPerApplication()
    {
        _context[CpuRegister.Rdi] = 0x1000;
        Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(_context));
        Assert.Equal(2, SaveDataExports.SaveDataCreateTransactionResource(_context));

        SaveDataExports.ConfigureApplicationInfo("PPSA00000");

        Assert.Equal(1, SaveDataExports.SaveDataCreateTransactionResource(_context));
    }

    [Fact]
    public void SetParam_TitleFieldPersistsOutsideMountedSaveAndFeedsSearch()
    {
        MountSave("slot0");
        Assert.EndsWith(
            Path.Combine("PPSA03525", "slot0", "data.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));

        var title = new byte[128];
        Encoding.ASCII.GetBytes("Grand Theft Auto: San Andreas\0").CopyTo(title, 0);
        Assert.True(_memory.TryWrite(ParamAddress, title));
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = ParamAddress;
        _context[CpuRegister.Rcx] = (ulong)title.Length;
        Assert.Equal(0, SaveDataExports.SaveDataSetParam(_context));

        var metadataPath = Path.Combine(
            _saveRoot,
            "1",
            "PPSA03525",
            "sce_params",
            "slot0.bin");
        var stored = File.ReadAllBytes(metadataPath);
        Assert.Equal(0x530, stored.Length);
        Assert.Equal(title, stored[..128]);
        Assert.False(File.Exists(Path.Combine(_saveRoot, "1", "PPSA03525", "slot0", "slot0.bin")));

        Span<byte> condition = stackalloc byte[0x20];
        condition.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(condition, 1);
        Assert.True(_memory.TryWrite(SearchCondAddress, condition));
        Span<byte> result = stackalloc byte[0x28];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], SearchDirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x18..], SearchParamsAddress);
        Assert.True(_memory.TryWrite(SearchResultAddress, result));
        _context[CpuRegister.Rdi] = SearchCondAddress;
        _context[CpuRegister.Rsi] = SearchResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(_context));
        var searched = new byte[0x530];
        Assert.True(_memory.TryRead(SearchParamsAddress, searched));
        Assert.Equal(title, searched[..128]);

        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataUmount2(_context));
        Assert.Equal(
            "/savedata0/data.bin",
            KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = ParamAddress;
        _context[CpuRegister.Rcx] = (ulong)title.Length;
        Assert.Equal(SaveDataErrorNotMounted, SaveDataExports.SaveDataSetParam(_context));
    }

    [Fact]
    public void SetParam_StringFieldRequiresNullWithinDeclaredSize()
    {
        MountSave("slot0");
        Assert.True(_memory.TryWrite(ParamAddress, Enumerable.Repeat((byte)'A', 128).ToArray()));
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = ParamAddress;
        _context[CpuRegister.Rcx] = 128;

        Assert.Equal(SaveDataErrorParameter, SaveDataExports.SaveDataSetParam(_context));
    }

    [Fact]
    public void Mount3_AllocatesDistinctMountPointsForConcurrentSaves()
    {
        var first = MountSave("slot0");
        var second = MountSave(
            "slot1",
            SecondMountAddress,
            SecondDirNameAddress,
            SecondMountResultAddress);

        Assert.Equal("/savedata0", first);
        Assert.Equal("/savedata1", second);
        Assert.EndsWith(
            Path.Combine("slot0", "data.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));
        Assert.EndsWith(
            Path.Combine("slot1", "data.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/savedata1/data.bin"));
    }

    [Fact]
    public void Mount3_RejectsSaveThatIsAlreadyMounted()
    {
        MountSave("slot0");
        _memory.WriteCString(SecondDirNameAddress, "slot0");
        WriteMountRequest(SecondMountAddress, SecondDirNameAddress);
        _context[CpuRegister.Rdi] = SecondMountAddress;
        _context[CpuRegister.Rsi] = SecondMountResultAddress;

        Assert.Equal(SaveDataErrorBusy, SaveDataExports.SaveDataMount3(_context));
        Assert.Equal("/savedata0", ReadFixedAscii(MountResultAddress, 16));
        Assert.Equal(string.Empty, ReadFixedAscii(SecondMountResultAddress, 16));
    }

    [Fact]
    public void Mount3_ReturnsMountFullAfterAllSlotsAreAllocated()
    {
        for (var index = 0; index < 16; index++)
        {
            var mountAddress = MemoryBase + 0x3000UL + (ulong)(index * 0x100);
            var dirNameAddress = mountAddress + 0x40;
            var resultAddress = mountAddress + 0x80;
            Assert.Equal(
                $"/savedata{index}",
                MountSave($"slot{index}", mountAddress, dirNameAddress, resultAddress));
        }

        var overflowMountAddress = MemoryBase + 0x4000;
        var overflowDirNameAddress = overflowMountAddress + 0x40;
        var overflowResultAddress = overflowMountAddress + 0x80;
        _memory.WriteCString(overflowDirNameAddress, "overflow");
        WriteMountRequest(overflowMountAddress, overflowDirNameAddress);
        _context[CpuRegister.Rdi] = overflowMountAddress;
        _context[CpuRegister.Rsi] = overflowResultAddress;

        Assert.Equal(SaveDataErrorMountFull, SaveDataExports.SaveDataMount3(_context));
    }

    [Fact]
    public void Mount3_FaultingResultRollsBackNewDirectory()
    {
        _memory.WriteCString(DirNameAddress, "rollback");
        WriteMountRequest(MountAddress, DirNameAddress);
        _context[CpuRegister.Rdi] = MountAddress;
        _context[CpuRegister.Rsi] = MemoryBase + 0x10000;

        Assert.Equal(MemoryFault, SaveDataExports.SaveDataMount3(_context));
        Assert.False(Directory.Exists(Path.Combine(_saveRoot, "1", "PPSA03525", "rollback")));
    }

    [Fact]
    public void Mount3_CreateModeReportsCreatedStatus()
    {
        _memory.WriteCString(DirNameAddress, "created");
        WriteMountRequest(MountAddress, DirNameAddress, mountMode: 1u << 2);
        _context[CpuRegister.Rdi] = MountAddress;
        _context[CpuRegister.Rsi] = MountResultAddress;

        Assert.Equal(0, SaveDataExports.SaveDataMount3(_context));

        Span<byte> status = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(MountResultAddress + 0x1C, status));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(status));
    }

    [Fact]
    public void Umount2_ValidatesPointerAndPreservesLiveMountOnUnknownName()
    {
        MountSave("slot0");

        _context[CpuRegister.Rdi] = 0xA5;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(SaveDataErrorParameter, SaveDataExports.SaveDataUmount2(_context));
        _context[CpuRegister.Rsi] = MemoryBase + 0x10000;
        Assert.Equal(MemoryFault, SaveDataExports.SaveDataUmount2(_context));
        _memory.WriteCString(DirNameAddress, "/savedata9");
        _context[CpuRegister.Rsi] = DirNameAddress;
        Assert.Equal(SaveDataErrorNotMounted, SaveDataExports.SaveDataUmount2(_context));
        Assert.EndsWith(
            Path.Combine("slot0", "data.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));
    }

    [Fact]
    public void SetParam_AllIgnoresOutputFieldsAndUmountRefreshesMtime()
    {
        MountSave("slot0");
        var input = Enumerable.Repeat((byte)0xA5, 0x530).ToArray();
        input.AsSpan(0, 0x500).Clear();
        Encoding.ASCII.GetBytes("Synthetic save\0").CopyTo(input, 0);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0x500), 42);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0x508), 1);
        Assert.True(_memory.TryWrite(ParamAddress, input));
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = ParamAddress;
        _context[CpuRegister.Rcx] = (ulong)input.Length;

        Assert.Equal(0, SaveDataExports.SaveDataSetParam(_context));
        var metadataPath = GetMetadataPath("slot0");
        var stored = File.ReadAllBytes(metadataPath);
        Assert.Equal("Synthetic save", Encoding.ASCII.GetString(stored, 0, 14));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(0x500)));
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(stored.AsSpan(0x508)) > 1);
        Assert.All(stored[0x510..], value => Assert.Equal(0, value));

        BinaryPrimitives.WriteInt64LittleEndian(stored.AsSpan(0x508), 2);
        File.WriteAllBytes(metadataPath, stored);
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataUmount2(_context));

        stored = File.ReadAllBytes(metadataPath);
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(stored.AsSpan(0x508)) > 2);
    }

    [Fact]
    public void Commit_RefreshesOnlyThePreparedSave()
    {
        MountSave("slot0");
        MountSave(
            "slot1",
            SecondMountAddress,
            SecondDirNameAddress,
            SecondMountResultAddress);
        SetUserParam(MountResultAddress, 7);
        SetUserParam(SecondMountResultAddress, 8);
        var firstMetadataPath = GetMetadataPath("slot0");
        var secondMetadataPath = GetMetadataPath("slot1");
        var first = File.ReadAllBytes(firstMetadataPath);
        var second = File.ReadAllBytes(secondMetadataPath);
        BinaryPrimitives.WriteInt64LittleEndian(first.AsSpan(0x508), 3);
        BinaryPrimitives.WriteInt64LittleEndian(second.AsSpan(0x508), 4);
        File.WriteAllBytes(firstMetadataPath, first);
        File.WriteAllBytes(secondMetadataPath, second);

        const int resource = 17;
        var prepareAddress = MemoryBase + 0x2800;
        var commitAddress = MemoryBase + 0x2840;
        WriteTransactionParam(prepareAddress, resource, mode: 0x12);
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = prepareAddress;
        Assert.Equal(0, SaveDataExports.SaveDataPrepare(_context));
        WriteTransactionParam(commitAddress, resource, mode: 0x34);
        _context[CpuRegister.Rdi] = commitAddress;

        Assert.Equal(0, SaveDataExports.SaveDataCommit(_context));

        first = File.ReadAllBytes(firstMetadataPath);
        second = File.ReadAllBytes(secondMetadataPath);
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(first.AsSpan(0x508)) > 3);
        Assert.Equal(4, BinaryPrimitives.ReadInt64LittleEndian(second.AsSpan(0x508)));

        BinaryPrimitives.WriteInt64LittleEndian(first.AsSpan(0x508), 5);
        File.WriteAllBytes(firstMetadataPath, first);
        WriteTransactionParam(commitAddress, resource: 99, mode: 0);
        _context[CpuRegister.Rdi] = commitAddress;
        Assert.Equal(0, SaveDataExports.SaveDataCommit(_context));
        first = File.ReadAllBytes(firstMetadataPath);
        Assert.Equal(5, BinaryPrimitives.ReadInt64LittleEndian(first.AsSpan(0x508)));
    }

    [Fact]
    public void PrepareAndCommit_ValidateGuestParameterPointers()
    {
        MountSave("slot0");
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(SaveDataErrorParameter, SaveDataExports.SaveDataPrepare(_context));
        _context[CpuRegister.Rsi] = MemoryBase + 0x10000;
        Assert.Equal(MemoryFault, SaveDataExports.SaveDataPrepare(_context));

        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(SaveDataErrorParameter, SaveDataExports.SaveDataCommit(_context));
        _context[CpuRegister.Rdi] = MemoryBase + 0x10000;
        Assert.Equal(MemoryFault, SaveDataExports.SaveDataCommit(_context));
    }

    [Fact]
    public void Umount_DoesNotOverwriteMalformedMetadata()
    {
        MountSave("slot0");
        var metadataPath = GetMetadataPath("slot0");
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var malformed = new byte[] { 0xA5, 0x5A, 0xC3 };
        File.WriteAllBytes(metadataPath, malformed);
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = MountResultAddress;

        Assert.Equal(SaveDataErrorInternal, SaveDataExports.SaveDataUmount2(_context));
        Assert.Equal(malformed, File.ReadAllBytes(metadataPath));
        Assert.EndsWith(
            Path.Combine("slot0", "data.bin"),
            KernelMemoryCompatExports.ResolveGuestPath("/savedata0/data.bin"));
    }

    [Fact]
    public void Commit_PreservesMalformedMetadataAndCanRetryPreparedResource()
    {
        MountSave("slot0");
        SetUserParam(MountResultAddress, 9);
        var metadataPath = GetMetadataPath("slot0");
        var valid = File.ReadAllBytes(metadataPath);
        const int resource = 23;
        var prepareAddress = MemoryBase + 0x2800;
        var commitAddress = MemoryBase + 0x2840;
        WriteTransactionParam(prepareAddress, resource, mode: 0);
        _context[CpuRegister.Rdi] = MountResultAddress;
        _context[CpuRegister.Rsi] = prepareAddress;
        Assert.Equal(0, SaveDataExports.SaveDataPrepare(_context));

        var malformed = new byte[] { 0x11, 0x22, 0x33 };
        File.WriteAllBytes(metadataPath, malformed);
        WriteTransactionParam(commitAddress, resource, mode: 0);
        _context[CpuRegister.Rdi] = commitAddress;
        Assert.Equal(SaveDataErrorInternal, SaveDataExports.SaveDataCommit(_context));
        Assert.Equal(malformed, File.ReadAllBytes(metadataPath));

        BinaryPrimitives.WriteInt64LittleEndian(valid.AsSpan(0x508), 6);
        File.WriteAllBytes(metadataPath, valid);
        Assert.Equal(0, SaveDataExports.SaveDataCommit(_context));
        valid = File.ReadAllBytes(metadataPath);
        Assert.True(BinaryPrimitives.ReadInt64LittleEndian(valid.AsSpan(0x508)) > 6);
    }

    [Fact]
    public void Umount_MissingMetadataPersistsCanonicalSearchDefaults()
    {
        MountSave("slot0");
        _context[CpuRegister.Rdi] = 0;
        _context[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataUmount2(_context));

        Span<byte> condition = stackalloc byte[0x20];
        condition.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(condition, 1);
        Assert.True(_memory.TryWrite(SearchCondAddress, condition));
        Span<byte> result = stackalloc byte[0x28];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], SearchDirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x18..], SearchParamsAddress);
        Assert.True(_memory.TryWrite(SearchResultAddress, result));
        _context[CpuRegister.Rdi] = SearchCondAddress;
        _context[CpuRegister.Rsi] = SearchResultAddress;

        Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(_context));
        Assert.Equal("Saved Data", ReadFixedAscii(SearchParamsAddress, 128));
        Assert.Equal(string.Empty, ReadFixedAscii(SearchParamsAddress + 0x100, 1024));
    }

    [Fact]
    public void SetParam_UnknownMountPrecedesFaultingDataPointer()
    {
        _memory.WriteCString(DirNameAddress, "/savedata9");
        _context[CpuRegister.Rdi] = DirNameAddress;
        _context[CpuRegister.Rsi] = 1;
        _context[CpuRegister.Rdx] = MemoryBase + 0x10000;
        _context[CpuRegister.Rcx] = 128;

        Assert.Equal(SaveDataErrorNotMounted, SaveDataExports.SaveDataSetParam(_context));
    }

    [Fact]
    public void DirNameSearch_SortsByPersistedUserParam()
    {
        MountSave("alpha");
        MountSave(
            "beta",
            SecondMountAddress,
            SecondDirNameAddress,
            SecondMountResultAddress);
        SetUserParam(MountResultAddress, 20);
        SetUserParam(SecondMountResultAddress, 10);

        Span<byte> condition = stackalloc byte[0x20];
        condition.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(condition, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(condition[0x18..], 1);
        Assert.True(_memory.TryWrite(SearchCondAddress, condition));
        Span<byte> result = stackalloc byte[0x28];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], SearchDirNamesAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], 2);
        Assert.True(_memory.TryWrite(SearchResultAddress, result));
        _context[CpuRegister.Rdi] = SearchCondAddress;
        _context[CpuRegister.Rsi] = SearchResultAddress;

        Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(_context));
        Assert.Equal("beta", ReadFixedAscii(SearchDirNamesAddress, 32));
        Assert.Equal("alpha", ReadFixedAscii(SearchDirNamesAddress + 32, 32));
    }

    [Fact]
    public void SetParam_ExportsForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("85zul--eGXs", out var export));
            Assert.Equal("sceSaveDataSetParam", export.Name);
            Assert.Equal("libSceSaveData", export.LibraryName);
        }
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _originalSaveRoot);
        if (Directory.Exists(_saveRoot))
        {
            Directory.Delete(_saveRoot, recursive: true);
        }
    }

    private string MountSave(
        string dirName,
        ulong mountAddress = MountAddress,
        ulong dirNameAddress = DirNameAddress,
        ulong resultAddress = MountResultAddress)
    {
        _memory.WriteCString(dirNameAddress, dirName);
        WriteMountRequest(mountAddress, dirNameAddress);

        _context[CpuRegister.Rdi] = mountAddress;
        _context[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataMount3(_context));
        return ReadFixedAscii(resultAddress, 16);
    }

    private void WriteMountRequest(
        ulong mountAddress,
        ulong dirNameAddress,
        uint mountMode = 1u << 5)
    {
        Span<byte> mount = stackalloc byte[0x30];
        mount.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(mount, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x08..], dirNameAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x10..], 96);
        BinaryPrimitives.WriteUInt32LittleEndian(mount[0x20..], mountMode);
        Assert.True(_memory.TryWrite(mountAddress, mount));
    }

    private void SetUserParam(ulong mountPointAddress, int value)
    {
        Span<byte> param = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(param, value);
        Assert.True(_memory.TryWrite(ParamAddress, param));
        _context[CpuRegister.Rdi] = mountPointAddress;
        _context[CpuRegister.Rsi] = 4;
        _context[CpuRegister.Rdx] = ParamAddress;
        _context[CpuRegister.Rcx] = sizeof(int);
        Assert.Equal(0, SaveDataExports.SaveDataSetParam(_context));
    }

    private void WriteTransactionParam(ulong address, int resource, uint mode)
    {
        Span<byte> param = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(param, resource);
        BinaryPrimitives.WriteUInt32LittleEndian(param[sizeof(int)..], mode);
        Assert.True(_memory.TryWrite(address, param));
    }

    private string GetMetadataPath(string dirName) => Path.Combine(
        _saveRoot,
        "1",
        "PPSA03525",
        "sce_params",
        $"{dirName}.bin");

    private string ReadFixedAscii(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
}
