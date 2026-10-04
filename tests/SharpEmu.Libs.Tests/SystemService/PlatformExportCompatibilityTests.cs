// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using SharpEmu.Libs.Share;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

[Collection("ImeState")]
public sealed class PlatformExportCompatibilityTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;

    [Fact]
    public void UserServiceGetUserNumber_WritesOneForPrimaryUser()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0x1000_0000;
        context[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetUserNumber(context));
        Assert.True(context.TryReadUInt32(OutputAddress, out var number));
        Assert.Equal(1U, number);
    }

    [Fact]
    public void ImeKeyboardClose_TracksOpenState()
    {
        var context = CreateContext();
        _ = ImeExports.ImeKeyboardClose(context);
        context[CpuRegister.Rdi] = 0x1000_0000;

        Assert.Equal(0, ImeExports.ImeKeyboardOpen(context));
        Assert.Equal(0, ImeExports.ImeKeyboardClose(context));
        Assert.Equal(unchecked((int)0x80BC0002), ImeExports.ImeKeyboardClose(context));
    }

    [Fact]
    public void ImeKeyboardGetInfo_ReportsClosedAndMissingKeyboardStates()
    {
        var context = CreateContext();
        ImeExports.ResetRuntimeState();
        context[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(unchecked((int)0x80BC0002), ImeExports.ImeKeyboardGetInfo(context));

        Assert.Equal(0, ImeExports.ImeKeyboardOpen(context));
        Assert.Equal(unchecked((int)0x80BC0023), ImeExports.ImeKeyboardGetInfo(context));

        context[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x80BC0031), ImeExports.ImeKeyboardGetInfo(context));
        ImeExports.ResetRuntimeState();
    }

    [Fact]
    public void ShareFeatureProhibit_ValidatesFeatureMask()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x81960002), ShareExports.ShareFeatureProhibit(context));

        context[CpuRegister.Rdi] = 2;
        Assert.Equal(0, ShareExports.ShareFeatureProhibit(context));
    }

    [Fact]
    public void MissingPlatformNids_RegisterForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "PMVehSlfZ94", "sceImeKeyboardClose", "libSceIme");
        AssertExport(manager, "VkqLPArfFdc", "sceImeKeyboardGetInfo", "libSceIme");
        AssertExport(manager, "bwFjS+bX9mA", "sceUserServiceTerminate", "libSceUserService");
        AssertExport(manager, "qbwy0Ub8b3M", "sceUserServiceGetUserNumber", "libSceUserService");
        AssertExport(manager, "5wjxESwX68I", "sceShareFeatureProhibit", "libSceShare");
        AssertExport(manager, "T64o-315wbg", "sceShareSetScreenshotOverlayImage", "libSceShare");
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static void AssertExport(ModuleManager manager, string nid, string name, string library)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal(library, export.LibraryName);
    }
}
