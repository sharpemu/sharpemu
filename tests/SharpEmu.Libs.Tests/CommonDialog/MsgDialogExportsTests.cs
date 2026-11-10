// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.CommonDialog;

[CollectionDefinition("MsgDialogState", DisableParallelization = true)]
public sealed class MsgDialogStateCollection
{
    public const string Name = "MsgDialogState";
}

[Collection(MsgDialogStateCollection.Name)]
public sealed class MsgDialogExportsTests : IDisposable
{
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorBusy = unchecked((int)0x80B80007);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);
    private const int ResultSize = 0x20;
    private const int StatusFinished = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong ResultAddress = MemoryBase + 0x300;
    private const ulong FaultingAddress = MemoryBase + 0x1000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public MsgDialogExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        ResetDialog();
    }

    [Theory]
    [InlineData("lDqxaY1UbEo", "sceMsgDialogInitialize")]
    [InlineData("ePw-kqZmelo", "sceMsgDialogTerminate")]
    [InlineData("b06Hh0DPEaE", "sceMsgDialogOpen")]
    [InlineData("CWVW78Qc3fI", "sceMsgDialogGetStatus")]
    [InlineData("6fIC3XKt2k0", "sceMsgDialogUpdateStatus")]
    [InlineData("Lr8ovHH9l6A", "sceMsgDialogGetResult")]
    [InlineData("HTrcDKlFKuM", "sceMsgDialogClose")]
    public void RegistryResolvesDialogLifecycleExports(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceMsgDialog", export.LibraryName);
    }

    [Fact]
    public void PollAfterOpenWritesAffirmativeResultAndTerminateResetsService()
    {
        Initialize();
        Open();

        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));
        Assert.Equal((ulong)StatusFinished, _ctx[CpuRegister.Rax]);

        Assert.True(_memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, ResultSize).ToArray()));
        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogGetResult(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        var result = new byte[ResultSize];
        Assert.True(_memory.TryRead(ResultAddress, result));
        var expected = new byte[ResultSize];
        expected[0x08] = 1;
        Assert.Equal(expected, result);

        Assert.Equal(0, MsgDialogExports.MsgDialogTerminate(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorNotInitialized);
    }

    [Fact]
    public void CloseAfterOpenFinishesDialogBeforeTheFirstPoll()
    {
        Initialize();
        Open();

        Assert.Equal(0, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogUpdateStatus(_ctx));

        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogGetResult(_ctx));

        Assert.Equal(ErrorNotRunning, MsgDialogExports.MsgDialogClose(_ctx));
        AssertReturn(ErrorNotRunning);
    }

    [Fact]
    public void OperationsRejectInvalidStatesAndNullPointers()
    {
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorNotInitialized);

        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogTerminate(_ctx));
        AssertReturn(ErrorNotInitialized);

        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(ErrorNotFinished, MsgDialogExports.MsgDialogGetResult(_ctx));
        AssertReturn(ErrorNotFinished);

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(ErrorArgNull, MsgDialogExports.MsgDialogGetResult(_ctx));
        AssertReturn(ErrorArgNull);

        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(ErrorArgNull, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorArgNull);

        Open();
        Assert.Equal(ErrorBusy, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorBusy);

        Assert.Equal(ErrorNotFinished, MsgDialogExports.MsgDialogGetResult(_ctx));
        AssertReturn(ErrorNotFinished);
    }

    [Fact]
    public void OpenWithUnreadableNonNullParameterStillRunsDialog()
    {
        Initialize();

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogOpen(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void GetResultWithUnreadableOutputReturnsMemoryFault()
    {
        Initialize();
        Open();
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            MsgDialogExports.MsgDialogGetResult(_ctx));
        AssertReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    public void Dispose() => ResetDialog();

    private void Initialize()
    {
        Assert.Equal(0, MsgDialogExports.MsgDialogInitialize(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private void Open()
    {
        Assert.True(_memory.TryWrite(ParamAddress, new byte[0xA0]));
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogOpen(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private void ResetDialog()
    {
        _ = MsgDialogExports.MsgDialogTerminate(_ctx);
    }

    private void AssertReturn(int expected)
    {
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }
}
