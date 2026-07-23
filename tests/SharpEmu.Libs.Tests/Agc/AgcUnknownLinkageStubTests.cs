// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcUnknownLinkageStubTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong OutputAddress = BaseAddress + 0x100;

    // 32 {offset,value} register pairs: the title always submits this blob
    // with count 0x20, so a shorter fill leaves garbage pairs.
    private const int BlobSize = 0x100;

    [Fact]
    public void UnknownDbOlWdppb4o_ZeroFillsExactlyTheLinkageBlob()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        var sentinel = new byte[BlobSize + 1];
        Array.Fill(sentinel, (byte)0xCC);
        Assert.True(memory.TryWrite(OutputAddress, sentinel));

        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = BaseAddress + 0x800;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.UnknownDbOlWdppb4o(ctx));

        var blob = new byte[BlobSize + 1];
        Assert.True(memory.TryRead(OutputAddress, blob));
        Assert.All(blob[..BlobSize], value => Assert.Equal(0, value));
        Assert.Equal(0xCC, blob[BlobSize]);
    }

    [Fact]
    public void UnknownDbOlWdppb4o_RejectsNullOutput()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.UnknownDbOlWdppb4o(ctx));
    }
}
