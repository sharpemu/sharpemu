// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpManagerCompatibilityTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void HasSignedUp_WritesOneByteOfflineState()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1000_0000;
        context[CpuRegister.Rsi] = MemoryBase + 0x100;

        Assert.Equal(0, NpManagerExports.NpHasSignedUp(context));

        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase + 0x100, value));
        Assert.Equal(0, value[0]);
    }

    [Fact]
    public void HasSignedUp_IsRegisteredForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("Oad3rvY-NJQ", out var export));
        Assert.Equal("sceNpHasSignedUp", export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
    }
}
