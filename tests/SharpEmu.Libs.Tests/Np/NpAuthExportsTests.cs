// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpAuthExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void CreateRequest_EnforcesMaximumRequestCount()
    {
        NpAuthExports.ResetRuntimeState();
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

        for (var i = 1; i <= 16; i++)
        {
            Assert.Equal(i, NpAuthExports.NpAuthCreateRequest(ctx));
        }

        Assert.Equal(unchecked((int)0x80550305), NpAuthExports.NpAuthCreateRequest(ctx));
    }

    [Fact]
    public void AbortRequest_CompletesWaitWithAbortedResult()
    {
        NpAuthExports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var requestId = NpAuthExports.NpAuthCreateRequest(ctx);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, NpAuthExports.NpAuthAbortRequest(ctx));

        var resultAddress = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, NpAuthExports.NpAuthWaitAsync(ctx));
        Span<byte> resultBytes = stackalloc byte[4];
        Assert.True(memory.TryRead(resultAddress, resultBytes));
        Assert.Equal(unchecked((int)0x80550304), BinaryPrimitives.ReadInt32LittleEndian(resultBytes));
    }

    [Fact]
    public void OnlineOperation_ReturnsServiceDownWithoutProducingCredentials()
    {
        NpAuthExports.ResetRuntimeState();
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var requestId = NpAuthExports.NpAuthCreateRequest(ctx);
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);

        Assert.Equal(unchecked((int)0x80550401), NpAuthExports.NpAuthGetAuthorizationCodeV3(ctx));
    }
}
