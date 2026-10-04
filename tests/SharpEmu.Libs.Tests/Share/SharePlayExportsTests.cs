// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Share;
using Xunit;

namespace SharpEmu.Libs.Tests.Share;

public sealed class SharePlayExportsTests : IDisposable
{
    public SharePlayExportsTests() => SharePlayExports.ResetForTests();

    public void Dispose() => SharePlayExports.ResetForTests();

    [Fact]
    public void ProhibitControllerRetainsBooleanState()
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

        ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, SharePlayExports.SharePlayProhibitController(ctx));
        Assert.True(SharePlayExports.IsControllerProhibited);

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, SharePlayExports.SharePlayProhibitController(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.False(SharePlayExports.IsControllerProhibited);
    }

    [Fact]
    public void ProhibitControllerRejectsNonBooleanStateWithoutChangingCurrentState()
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, SharePlayExports.SharePlayProhibitController(ctx));

        ctx[CpuRegister.Rdi] = 2;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            SharePlayExports.SharePlayProhibitController(ctx));
        Assert.True(SharePlayExports.IsControllerProhibited);
    }
}
