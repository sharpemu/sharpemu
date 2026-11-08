// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2MasteringExportsTests
{
    [Fact]
    public void MasteringSetParam_AcceptsOpaqueParameters()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0100;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rax] = ulong.MaxValue;

        var result = AudioOut2Exports.AudioOut2MasteringSetParam(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void MasteringSetParam_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("v8iOE+j8a5o", out var export));
        Assert.Equal("sceAudioOut2MasteringSetParam", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }
}
