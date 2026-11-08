// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.SaveData;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmSaveDataDialogTests : IDisposable
{
    private const ulong Address = 0x1_0000_0000;
    private readonly CpuContext _context = new(new FakeCpuMemory(Address, 0x200), Generation.Gen4);

    public GnmSaveDataDialogTests()
    {
        GnmSaveDataDialogExports.Terminate(_context);
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogInitialize(_context));
        _context[CpuRegister.Rdi] = Address;
        Assert.True(_context.TryWriteUInt64(Address + 0x70, 0x123456789ABCDEF0));
    }

    public void Dispose() => GnmSaveDataDialogExports.Terminate(_context);

    [Fact]
    public void ProgressDialogStaysRunningUntilTheSaveWorkerClosesIt()
    {
        var exports = GnmExportRegistry.CreateExports();
        Assert.True(_context.TryWriteUInt32(Address + 0x34, 5));
        Assert.Equal(0, Assert.Single(exports, export => export.Nid == "4tPhsP6FpDI").Function(_context));
        foreach (var nid in new[] { "ERKzksauAJA", "KK3Bdg1RWK0" })
        {
            var status = Assert.Single(exports, export => export.Nid == nid);
            Assert.Equal(Generation.Gen4, status.Target);
            for (var poll = 0; poll < 10; poll++)
                Assert.Equal(2, status.Function(_context));
        }
        _context[CpuRegister.Rdi] = Address + 0x100;
        Assert.Equal(unchecked((int)0x80B80005), SaveDataDialogExports.SaveDataDialogGetResult(_context));
        Assert.Equal(0, GnmSaveDataDialogExports.Close(_context));
        Assert.Equal(3, GnmSaveDataDialogExports.GetStatus(_context));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogGetResult(_context));
        Assert.True(_context.TryReadUInt32(Address + 0x100, out var mode));
        Assert.Equal(5u, mode);
        Assert.True(_context.TryReadUInt64(Address + 0x120, out var userData));
        Assert.Equal(0x123456789ABCDEF0UL, userData);
        Assert.Equal(unchecked((int)0x80B8000B), GnmSaveDataDialogExports.Close(_context));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void UserPromptsStillCompleteWithoutAHostDialog(uint mode)
    {
        Assert.True(_context.TryWriteUInt32(Address + 0x34, mode));
        Assert.Equal(0, GnmSaveDataDialogExports.Open(_context));
        Assert.Equal(3, GnmSaveDataDialogExports.GetStatus(_context));
        _context[CpuRegister.Rdi] = Address + 0x100;
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogGetResult(_context));
        Assert.True(_context.TryReadUInt32(Address + 0x108, out var button));
        Assert.Equal(1u, button);
    }

    [Fact]
    public void FailedOpenDoesNotDismissTheCurrentProgressDialogAndTerminateClearsIt()
    {
        Assert.True(_context.TryWriteUInt32(Address + 0x34, 5));
        Assert.Equal(0, GnmSaveDataDialogExports.Open(_context));
        Assert.True(_context.TryWriteUInt32(Address + 0x34, 3));
        Assert.NotEqual(0, GnmSaveDataDialogExports.Open(_context));
        _context[CpuRegister.Rdi] = 0;
        Assert.NotEqual(0, GnmSaveDataDialogExports.Open(_context));
        _context[CpuRegister.Rdi] = Address + 0x1000;
        Assert.NotEqual(0, GnmSaveDataDialogExports.Open(_context));
        Assert.Equal(2, GnmSaveDataDialogExports.GetStatus(_context));
        Assert.Equal(0, GnmSaveDataDialogExports.Terminate(_context));
        Assert.Equal(0, GnmSaveDataDialogExports.GetStatus(_context));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogInitialize(_context));
        _context[CpuRegister.Rdi] = Address;
        Assert.Equal(0, GnmSaveDataDialogExports.Open(_context));
        Assert.Equal(3, GnmSaveDataDialogExports.GetStatus(_context));
    }

    [Fact]
    public void Gen5RetainsTheSharedDialogImplementations()
    {
        var gnm = GnmExportRegistry.CreateExports();
        foreach (var export in SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
                     .Where(export => export.LibraryName == "libSceSaveDataDialog"))
        {
            var adapted = Assert.Single(gnm, item => item.Nid == export.Nid);
            if (export.Name is "sceSaveDataDialogOpen" or "sceSaveDataDialogGetStatus" or
                "sceSaveDataDialogUpdateStatus" or "sceSaveDataDialogClose" or "sceSaveDataDialogTerminate")
            {
                Assert.Equal(Generation.Gen4, adapted.Target);
                Assert.NotEqual(export.Function, adapted.Function);
            }
            else
            {
                Assert.Equal(export.Target, adapted.Target);
                Assert.Equal(export.Function, adapted.Function);
            }
        }
    }
}
