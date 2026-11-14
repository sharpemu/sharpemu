// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Stubs;

// Imports a title calls during start-up that only need to be accepted.
public sealed class MortalKombatStubTests
{
    [Theory]
    [InlineData("54phPH2LZls", "sceVoiceStart", "libSceVoice")]
    [InlineData("k1V1djYpk7k", "sceNetShowIfconfig", "libSceNet")]
    [InlineData("Yn8KT+VTZaE", "sceVideoOutColorSpaceConversionSettingsSetSdrToHdrConversion_", "libSceVideoOut")]
    [InlineData("dYDAfB+cJEU", "sceVideoOutAdjustColorSpaceConversion_", "libSceVideoOut")]
    public void StartupImport_IsRegisteredAndAccepted(string nid, string name, string library)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(name, export.Name);
        Assert.Equal(library, export.LibraryName);

        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rax] = 0xFFFF;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
