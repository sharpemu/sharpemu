// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpWebApi2ExportRegistrationTests
{
    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void TimeoutMaintenanceIsRegisteredWithoutGuestArguments(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        Assert.True(manager.TryGetExport("3Tt9zL3tkoc", out var export));
        Assert.Equal("sceNpWebApi2CheckTimeout", export.Name);
        Assert.Equal("libSceNpWebApi2", export.LibraryName);
        Assert.Equal(0, NpWebApi2Exports.NpWebApi2CheckTimeout());
    }
}
