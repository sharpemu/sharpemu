// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

// Exercises the mount / event / param / delete exports end to end against a
// temp save root. Shares the environment-pinning collection so it never runs
// alongside other tests that mutate SHARPEMU_SAVEDATA_DIR.
[Collection("SaveDataMemoryState")]
public sealed class SaveDataExportsTests : IDisposable
{
    private const ulong Base = 0x2_0000_0000;
    private const int UserId = 0x1001;
    private const string TitleId = "SDEXPORTTEST";
    private const string MountPoint = "/savedata0";
    private const string DirName = "SAVE0000";

    private const ulong MountParam = Base + 0x100;
    private const ulong MountResult = Base + 0x200;
    private const ulong DirNamePtr = Base + 0x300;
    private const ulong MountPointStr = Base + 0x340;
    private const ulong EventOut = Base + 0x400;
    private const ulong ParamStruct = Base + 0x500;
    private const ulong DeleteParam = Base + 0xC00;
    private const ulong SyncParam = Base + 0xC80;
    private const ulong SetupParam = Base + 0xD00;
    private const ulong SetupResult = Base + 0xD80;
    private const ulong TransactionOut = Base + 0xE00;
    private const ulong StaleR8 = Base + 0xE08;
    private const ulong StaleR9 = Base + 0xE10;
    private const ulong PrepareParam = Base + 0xE20;
    private const ulong CommitParam = Base + 0xE60;
    private const ulong SecondMountParam = Base + 0xF00;
    private const ulong SecondMountResult = Base + 0xF80;
    private const ulong SecondDirNamePtr = Base + 0x1000;
    private const ulong MountInfo = Base + 0x1080;
    private const ulong SearchCond = Base + 0x1100;
    private const ulong SearchResult = Base + 0x1140;
    private const ulong SearchDirNames = Base + 0x1180;
    private const ulong SearchInfos = Base + 0x1200;
    private const ulong BackupParam = Base + 0x1300;
    private const ulong BackupTitleId = Base + 0x1380;
    private const ulong BackupDirName = Base + 0x13C0;
    private const ulong Unmapped = Base + 0x2_0000;

    private const int NoEvent = unchecked((int)0x809F0008);
    private const int ParameterError = unchecked((int)0x809F0000);
    private const int MemoryFault = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    private const int ResourceBusy = unchecked((int)0x809F001B);
    private const uint MountModeCreate = 1u << 2;
    private const string BackupTitle = "PPSA10067";
    private const string BackupDirectory = "SLOT_WITH_32_CHARACTERS_12345678";

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sdexport-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SlotDir => Path.Combine(_root, TitleId, DirName);

    private void WriteAscii(ulong address, string value)
    {
        var bytes = new byte[value.Length + 1];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private CpuContext Reg(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0)
    {
        _ctx[CpuRegister.Rdi] = rdi;
        _ctx[CpuRegister.Rsi] = rsi;
        _ctx[CpuRegister.Rdx] = rdx;
        _ctx[CpuRegister.Rcx] = rcx;
        return _ctx;
    }

    private int Mount(
        uint mountMode = MountModeCreate,
        string dirName = DirName,
        ulong mountParam = MountParam,
        ulong mountResult = MountResult,
        ulong dirNamePointer = DirNamePtr)
    {
        WriteAscii(dirNamePointer, dirName);
        Span<byte> param = stackalloc byte[0x30];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x08..], dirNamePointer);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x20..], mountMode);
        Assert.True(_memory.TryWrite(mountParam, param));
        return SaveDataExports.SaveDataMount3(Reg(rdi: mountParam, rsi: mountResult));
    }

    [Fact]
    public void GetEventResult_WhenNoEvents_ReportsNoEvent()
    {
        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Fact]
    public void GetEventResult_NullOut_ReturnsParameterError()
    {
        Assert.Equal(ParameterError, SaveDataExports.SaveDataGetEventResult(Reg(rsi: 0)));
    }

    [Fact]
    public void Backup_QueuesCompleteBackupEndEvent_ThenDrains()
    {
        WriteAscii(BackupTitleId, BackupTitle);
        WriteAscii(BackupDirName, BackupDirectory);
        WriteBackupParam(BackupTitleId, BackupDirName);

        Assert.Equal(0, SaveDataExports.SaveDataBackup(Reg(rdi: BackupParam)));

        var initialized = new byte[0x70];
        Array.Fill(initialized, (byte)0xCC);
        Assert.True(_memory.TryWrite(EventOut, initialized));
        Assert.Equal(0, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));

        var ev = new byte[0x70];
        Assert.True(_memory.TryRead(EventOut, ev));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(ev));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(ev.AsSpan(0x04)));
        Assert.Equal(UserId, BinaryPrimitives.ReadInt32LittleEndian(ev.AsSpan(0x08)));
        Assert.Equal(BackupTitle, Encoding.ASCII.GetString(ev, 0x10, 0x10).TrimEnd('\0'));
        Assert.Equal(BackupDirectory, Encoding.ASCII.GetString(ev, 0x20, 0x20).TrimEnd('\0'));
        Assert.All(ev[0x1A..0x20], value => Assert.Equal((byte)0, value));
        Assert.All(ev[0x40..0x68], value => Assert.Equal((byte)0, value));
        Assert.All(ev[0x68..0x70], value => Assert.Equal((byte)0xCC, value));
        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Fact]
    public void Backup_NullParameterOrDirectory_ReturnsParameterWithoutEvent()
    {
        Assert.Equal(ParameterError, SaveDataExports.SaveDataBackup(Reg(rdi: 0)));

        WriteBackupParam(titleIdAddress: 0, dirNameAddress: 0);
        Assert.Equal(ParameterError, SaveDataExports.SaveDataBackup(Reg(rdi: BackupParam)));
        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Backup_InvalidNestedPointer_ReturnsMemoryFaultWithoutEvent(bool invalidTitle)
    {
        WriteAscii(BackupTitleId, BackupTitle);
        WriteAscii(BackupDirName, BackupDirectory);
        WriteBackupParam(
            invalidTitle ? Unmapped : BackupTitleId,
            invalidTitle ? BackupDirName : Unmapped);

        Assert.Equal(MemoryFault, SaveDataExports.SaveDataBackup(Reg(rdi: BackupParam)));
        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Fact]
    public void Backup_InvalidStructurePointer_ReturnsMemoryFaultWithoutEvent()
    {
        Assert.Equal(MemoryFault, SaveDataExports.SaveDataBackup(Reg(rdi: Unmapped)));
        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Fact]
    public void SyncSaveDataMemory_PostsSyncEndEvent_DrainedOnce()
    {
        // Setup the memory blob so sync succeeds.
        Span<byte> setup = stackalloc byte[0x10];
        setup.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(setup[0x04..], UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(setup[0x08..], 0x1000);
        Assert.True(_memory.TryWrite(SetupParam, setup));
        Assert.Equal(0, SaveDataExports.SaveDataSetupSaveDataMemory2(Reg(rdi: SetupParam, rdx: SetupResult)));

        Span<byte> sync = stackalloc byte[0x10];
        sync.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(sync, UserId);
        Assert.True(_memory.TryWrite(SyncParam, sync));
        Assert.Equal(0, SaveDataExports.SaveDataSyncSaveDataMemory(Reg(rdi: SyncParam)));

        // The queued SAVE_DATA_MEMORY_SYNC_END (type 3) event is delivered once.
        Assert.Equal(0, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
        Assert.True(_ctx.TryReadUInt32(EventOut + 0x00, out var type));
        Assert.Equal(3u, type);
        Assert.True(_ctx.TryReadInt32(EventOut + 0x04, out var errorCode));
        Assert.Equal(0, errorCode);

        var ev = new byte[0x68];
        Assert.True(_memory.TryRead(EventOut, ev));
        Assert.Equal(TitleId[..10], Encoding.ASCII.GetString(ev, 0x10, 0x10).TrimEnd('\0'));
        Assert.Equal("sce_sdmemory", Encoding.ASCII.GetString(ev, 0x20, 0x20).TrimEnd('\0'));

        Assert.Equal(NoEvent, SaveDataExports.SaveDataGetEventResult(Reg(rsi: EventOut)));
    }

    [Fact]
    public void Mount_CreatesSlotDirectory_AndReportsMounted()
    {
        Assert.Equal(0, Mount());
        Assert.True(Directory.Exists(SlotDir));

        Assert.Equal(0, SaveDataExports.SaveDataIsMounted(Reg(rsi: EventOut)));
        Assert.True(_ctx.TryReadUInt32(EventOut, out var mounted));
        Assert.Equal(1u, mounted);
    }

    [Fact]
    public void GetMountInfo_ReportsBlockCapacityAndRemainingBlocks()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        File.WriteAllBytes(Path.Combine(SlotDir, "payload.bin"), new byte[65537]);

        Assert.Equal(
            0,
            SaveDataExports.SaveDataGetMountInfo(
                Reg(rdi: MountPointStr, rsi: MountInfo)));
        Assert.True(_ctx.TryReadUInt64(MountInfo, out var blocks));
        Assert.True(_ctx.TryReadUInt64(MountInfo + 0x08, out var freeBlocks));
        Assert.Equal(16384UL, blocks);
        Assert.Equal(16382UL, freeBlocks);
    }

    [Fact]
    public void GetMountInfo_WritesOnlyTheGuestStructure()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        Span<byte> sentinel = stackalloc byte[0x10];
        sentinel.Fill(0xCD);
        Assert.True(_memory.TryWrite(MountInfo + 0x30, sentinel));

        Assert.Equal(
            0,
            SaveDataExports.SaveDataGetMountInfo(
                Reg(rdi: MountPointStr, rsi: MountInfo)));

        Span<byte> after = stackalloc byte[0x10];
        Assert.True(_memory.TryRead(MountInfo + 0x30, after));
        Assert.True(after.SequenceEqual(sentinel));
    }

    [Fact]
    public void DirNameSearch_ReportsBlockCapacityAndRemainingBlocks()
    {
        Directory.CreateDirectory(SlotDir);
        File.WriteAllBytes(Path.Combine(SlotDir, "payload.bin"), new byte[65537]);

        Span<byte> cond = stackalloc byte[0x40];
        cond.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(cond, UserId);
        Assert.True(_memory.TryWrite(SearchCond, cond));

        Span<byte> result = stackalloc byte[0x40];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], SearchDirNames);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x20..], SearchInfos);
        Assert.True(_memory.TryWrite(SearchResult, result));

        Assert.Equal(
            0,
            SaveDataExports.SaveDataDirNameSearch(
                Reg(rdi: SearchCond, rsi: SearchResult)));
        Assert.True(_ctx.TryReadUInt64(SearchInfos, out var blocks));
        Assert.True(_ctx.TryReadUInt64(SearchInfos + 0x08, out var freeBlocks));
        Assert.Equal(16384UL, blocks);
        Assert.Equal(16382UL, freeBlocks);
    }

    [Fact]
    public void Umount_RemovesMountTracking()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        Assert.Equal(0, SaveDataExports.SaveDataUmount2(Reg(rsi: MountPointStr)));

        Assert.Equal(0, SaveDataExports.SaveDataIsMounted(Reg(rsi: EventOut)));
        Assert.True(_ctx.TryReadUInt32(EventOut, out var mounted));
        Assert.Equal(0u, mounted);
    }

    [Fact]
    public void Mount_UsesDistinctSlotsForConcurrentDirectories()
    {
        Assert.Equal(0, Mount());
        Assert.Equal(
            0,
            Mount(
                dirName: "SAVE0001",
                mountParam: SecondMountParam,
                mountResult: SecondMountResult,
                dirNamePointer: SecondDirNamePtr));

        var first = new byte[16];
        var second = new byte[16];
        Assert.True(_memory.TryRead(MountResult, first));
        Assert.True(_memory.TryRead(SecondMountResult, second));
        Assert.Equal(MountPoint, Encoding.ASCII.GetString(first).TrimEnd('\0'));
        Assert.Equal("/savedata1", Encoding.ASCII.GetString(second).TrimEnd('\0'));
    }

    [Fact]
    public void SetParam_ThenGetParam_RoundTripsThroughMetadata()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);

        Span<byte> param = stackalloc byte[0x530];
        param.Clear();
        Encoding.ASCII.GetBytes("My Save").CopyTo(param);                 // +0x00 title
        Encoding.ASCII.GetBytes("Biome 2").CopyTo(param[0x80..]);         // +0x80 subtitle
        Encoding.ASCII.GetBytes("2h 30m").CopyTo(param[0x100..]);         // +0x100 detail
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x500..], 7);      // +0x500 userParam
        Assert.True(_memory.TryWrite(ParamStruct, param));
        Assert.Equal(0, SaveDataExports.SaveDataSetParam(Reg(rdi: MountPointStr, rdx: ParamStruct)));

        Assert.True(File.Exists(SaveDataStorage.ParamPath(SlotDir)));
        var meta = SaveDataStorage.ReadMetadata(SlotDir);
        Assert.Equal("My Save", meta.Title);
        Assert.Equal("Biome 2", meta.SubTitle);
        Assert.Equal("2h 30m", meta.Detail);
        Assert.Equal(7u, meta.UserParam);

        // Read it back through the export into a fresh buffer.
        Span<byte> zero = stackalloc byte[0x530];
        zero.Clear();
        Assert.True(_memory.TryWrite(ParamStruct, zero));
        Assert.Equal(0, SaveDataExports.SaveDataGetParam(Reg(rdi: MountPointStr, rdx: ParamStruct)));
        var title = new byte[7];
        Assert.True(_memory.TryRead(ParamStruct, title));
        Assert.Equal("My Save", Encoding.ASCII.GetString(title));
    }

    [Fact]
    public void SetParam_WithoutMount_ReturnsBadMounted()
    {
        WriteAscii(MountPointStr, "/notmounted");
        Assert.True(_memory.TryWrite(ParamStruct, new byte[0x530]));
        Assert.Equal(
            unchecked((int)0x809F0013),
            SaveDataExports.SaveDataSetParam(Reg(rdi: MountPointStr, rdx: ParamStruct)));
    }

    [Fact]
    public void Delete_RemovesTheSlotDirectory()
    {
        Assert.Equal(0, Mount());
        Assert.True(Directory.Exists(SlotDir));

        WriteAscii(DirNamePtr, DirName);
        Span<byte> del = stackalloc byte[0x10];
        del.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(del, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(del[0x08..], DirNamePtr);
        Assert.True(_memory.TryWrite(DeleteParam, del));

        Assert.Equal(0, SaveDataExports.SaveDataDelete(Reg(rdi: DeleteParam)));
        Assert.False(Directory.Exists(SlotDir));
    }

    [Fact]
    public void Delete_MissingSlot_ReturnsNotFound()
    {
        WriteAscii(DirNamePtr, "NOSUCHSLOT");
        Span<byte> del = stackalloc byte[0x10];
        del.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(del, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(del[0x08..], DirNamePtr);
        Assert.True(_memory.TryWrite(DeleteParam, del));

        Assert.Equal(
            unchecked((int)0x809F0008),
            SaveDataExports.SaveDataDelete(Reg(rdi: DeleteParam)));
    }

    [Fact]
    public void CreateTransactionResource_ReturnsResourceAndDoesNotWriteStaleRegisters()
    {
        const uint sentinel = 0xA5A5A5A5;
        Assert.True(_ctx.TryWriteUInt32(TransactionOut, sentinel));
        Assert.True(_ctx.TryWriteUInt32(StaleR8, sentinel));
        Assert.True(_ctx.TryWriteUInt32(StaleR9, sentinel));

        var ctx = Reg(rdi: 0x02000000, rdx: TransactionOut, rcx: StaleR8);
        ctx[CpuRegister.R8] = StaleR8;
        ctx[CpuRegister.R9] = StaleR9;

        var resource = SaveDataExports.SaveDataCreateTransactionResource(ctx);
        Assert.True(resource > 0);
        Assert.True(_ctx.TryReadUInt32(TransactionOut, out var rcxValue));
        Assert.True(_ctx.TryReadUInt32(StaleR8, out var r8Value));
        Assert.True(_ctx.TryReadUInt32(StaleR9, out var r9Value));
        Assert.Equal(sentinel, rcxValue);
        Assert.Equal(sentinel, r8Value);
        Assert.Equal(sentinel, r9Value);
    }

    [Fact]
    public void CreateTransactionResource_ReturnsDistinctResources()
    {
        var first = SaveDataExports.SaveDataCreateTransactionResource(Reg(rdi: 0x1000));
        var second = SaveDataExports.SaveDataCreateTransactionResource(Reg(rdi: 0x2000));

        Assert.True(first > 0);
        Assert.True(second > 0);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Prepare_ReadsResourceFromParameterStructure()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        var resource = SaveDataExports.SaveDataCreateTransactionResource(Reg(rdi: 0x2000));
        Assert.True(resource > 0);

        Span<byte> param = stackalloc byte[0x28];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, resource);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        Assert.True(_memory.TryWrite(PrepareParam, param));

        Assert.Equal(
            0,
            SaveDataExports.SaveDataPrepare(
                Reg(rdi: MountPointStr, rsi: PrepareParam, rdx: 4)));
    }

    [Fact]
    public void Commit_ReleasesOnlyTheNamedPreparedResource()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        var first = SaveDataExports.SaveDataCreateTransactionResource(Reg(rdi: 0x1000));
        var second = SaveDataExports.SaveDataCreateTransactionResource(Reg(rdi: 0x1000));

        Prepare(first);
        Prepare(second);

        Span<byte> commit = stackalloc byte[0x28];
        commit.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(commit, first);
        BinaryPrimitives.WriteUInt32LittleEndian(commit[0x04..], 0);
        Assert.True(_memory.TryWrite(CommitParam, commit));

        Assert.Equal(0, SaveDataExports.SaveDataCommit(Reg(rdi: CommitParam)));
        Assert.Equal(0, SaveDataExports.SaveDataDeleteTransactionResource(Reg(rdi: unchecked((uint)first))));
        Assert.Equal(
            ResourceBusy,
            SaveDataExports.SaveDataDeleteTransactionResource(Reg(rdi: unchecked((uint)second))));
    }

    private void Prepare(int resource)
    {
        Span<byte> param = stackalloc byte[0x28];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, resource);
        Assert.True(_memory.TryWrite(PrepareParam, param));
        Assert.Equal(
            0,
            SaveDataExports.SaveDataPrepare(Reg(rdi: MountPointStr, rsi: PrepareParam)));
    }

    private void WriteBackupParam(ulong titleIdAddress, ulong dirNameAddress)
    {
        Span<byte> backup = stackalloc byte[0x40];
        backup.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(backup, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(backup[0x08..], titleIdAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(backup[0x10..], dirNameAddress);
        Assert.True(_memory.TryWrite(BackupParam, backup));
    }
}
