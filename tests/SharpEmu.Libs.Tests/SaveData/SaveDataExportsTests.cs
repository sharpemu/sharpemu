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

    private const int NoEvent = unchecked((int)0x809F0008);
    private const int ParameterError = unchecked((int)0x809F0000);
    private const uint MountModeCreate = 1u << 2;

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

    private int Mount(uint mountMode = MountModeCreate)
    {
        WriteAscii(DirNamePtr, DirName);
        Span<byte> param = stackalloc byte[0x30];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x08..], DirNamePtr);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x20..], mountMode);
        Assert.True(_memory.TryWrite(MountParam, param));
        return SaveDataExports.SaveDataMount3(Reg(rdi: MountParam, rsi: MountResult));
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
    public void Umount_RemovesMountTracking()
    {
        Assert.Equal(0, Mount());
        WriteAscii(MountPointStr, MountPoint);
        Assert.Equal(0, SaveDataExports.SaveDataUmount2(Reg(rdi: MountPointStr)));

        Assert.Equal(0, SaveDataExports.SaveDataIsMounted(Reg(rsi: EventOut)));
        Assert.True(_ctx.TryReadUInt32(EventOut, out var mounted));
        Assert.Equal(0u, mounted);
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
    public void CreateTransactionResource_WithoutOutPointer_DoesNotProbeStaleRegisters()
    {
        const uint sentinel = 0xA5A5A5A5;
        Assert.True(_ctx.TryWriteUInt32(TransactionOut, sentinel));
        Assert.True(_ctx.TryWriteUInt32(StaleR8, sentinel));
        Assert.True(_ctx.TryWriteUInt32(StaleR9, sentinel));

        var ctx = Reg(rdi: UserId, rdx: 0, rcx: TransactionOut);
        ctx[CpuRegister.R8] = StaleR8;
        ctx[CpuRegister.R9] = StaleR9;

        Assert.Equal(0, SaveDataExports.SaveDataCreateTransactionResource(ctx));
        Assert.True(_ctx.TryReadUInt32(TransactionOut, out var rcxValue));
        Assert.True(_ctx.TryReadUInt32(StaleR8, out var r8Value));
        Assert.True(_ctx.TryReadUInt32(StaleR9, out var r9Value));
        Assert.Equal(sentinel, rcxValue);
        Assert.Equal(sentinel, r8Value);
        Assert.Equal(sentinel, r9Value);
    }

    [Fact]
    public void CreateTransactionResource_WithOutPointerFlag_WritesOnlyRcx()
    {
        const uint sentinel = 0xA5A5A5A5;
        Assert.True(_ctx.TryWriteUInt32(TransactionOut, 0));
        Assert.True(_ctx.TryWriteUInt32(StaleR8, sentinel));
        Assert.True(_ctx.TryWriteUInt32(StaleR9, sentinel));

        var ctx = Reg(rdi: UserId, rdx: 1, rcx: TransactionOut);
        ctx[CpuRegister.R8] = StaleR8;
        ctx[CpuRegister.R9] = StaleR9;

        Assert.Equal(0, SaveDataExports.SaveDataCreateTransactionResource(ctx));
        Assert.True(_ctx.TryReadUInt32(TransactionOut, out var resource));
        Assert.True(_ctx.TryReadUInt32(StaleR8, out var r8Value));
        Assert.True(_ctx.TryReadUInt32(StaleR9, out var r9Value));
        Assert.NotEqual(0u, resource);
        Assert.Equal(sentinel, r8Value);
        Assert.Equal(sentinel, r9Value);
    }

    [Fact]
    public void CreateTransactionResource_WithLegacyOutPointer_WritesOnlyRdx()
    {
        const uint sentinel = 0xA5A5A5A5;
        Assert.True(_ctx.TryWriteUInt32(TransactionOut, 0));
        Assert.True(_ctx.TryWriteUInt32(StaleR8, sentinel));

        Assert.Equal(
            0,
            SaveDataExports.SaveDataCreateTransactionResource(
                Reg(rdi: UserId, rdx: TransactionOut, rcx: StaleR8)));

        Assert.True(_ctx.TryReadUInt32(TransactionOut, out var resource));
        Assert.True(_ctx.TryReadUInt32(StaleR8, out var rcxValue));
        Assert.NotEqual(0u, resource);
        Assert.Equal(sentinel, rcxValue);
    }
}
