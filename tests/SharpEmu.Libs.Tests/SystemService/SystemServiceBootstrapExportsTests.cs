// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

public sealed class SystemServiceBootstrapExportsTests
{
    [Fact]
    public void DisableNoticeScreenSkipFlagAutoSetIsRegisteredOnlyForGen5()
    {
        var gen4 = new ModuleManager();
        gen4.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4.TryGetExport("8Lo6Zv94aho", out _));

        var gen5 = new ModuleManager();
        gen5.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5.TryGetExport("8Lo6Zv94aho", out var export));
        Assert.Equal("sceSystemServiceDisableNoticeScreenSkipFlagAutoSet", export.Name);
        Assert.Equal("libSceSystemService", export.LibraryName);
    }

    [Fact]
    public void DisableNoticeScreenSkipFlagAutoSetSucceedsWithoutGuestMemory()
    {
        var context = new CpuContext(
            new FakeCpuMemory(0x1_0000_0000, 0x1000),
            Generation.Gen5);

        Assert.Equal(
            0,
            SystemServiceExports.SystemServiceDisableNoticeScreenSkipFlagAutoSet(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
