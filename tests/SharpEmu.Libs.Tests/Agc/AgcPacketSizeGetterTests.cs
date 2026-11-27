// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Psml;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// The packet size getters a title uses to size command buffers, and the small exports that report the
// console as a retail one.
public sealed class AgcPacketSizeGetterTests
{
    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(0x2_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void ConditionalExecuteAndRewindSizesMatchTheirPackets()
    {
        var ctx = NewContext();
        Assert.Equal(20, AgcExports.DcbCondExecGetSize(ctx));
        Assert.Equal(20, AgcExports.AcbCondExecGetSize(ctx));
        Assert.Equal(8, AgcExports.AcbRewindGetSize(ctx));
        Assert.Equal(8, AgcExports.DcbRewindGetSize(ctx));
        Assert.Equal(36, AgcExports.DcbAtomicMemGetSize(ctx));
        Assert.Equal(36, AgcExports.AcbAtomicMemGetSize(ctx));
    }

    [Theory]
    [InlineData(0x07, 8)]
    [InlineData(0x16, 8)]
    [InlineData(0x38, 16)]
    [InlineData(0x39, 16)]
    public void AcbEventWriteSizeFollowsTheEventType(int eventType, int expected)
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = (ulong)eventType;
        Assert.Equal(expected, AgcExports.AcbEventWriteGetSize(ctx));
    }

    [Fact]
    public void Mfsr2InitReportsAnUnsupportedConsole()
    {
        var ctx = NewContext();
        Assert.Equal(unchecked((int)0x8A8A0012), PsmlExports.PsmlMfsr2Init(ctx));
    }

    [Fact]
    public void SetGlobalBlendSpaceRejectsAnUnknownHandle()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0x7FFF;
        Assert.Equal(unchecked((int)0x8029000B), VideoOutExports.VideoOutSetGlobalBlendSpace(ctx));
    }
}
