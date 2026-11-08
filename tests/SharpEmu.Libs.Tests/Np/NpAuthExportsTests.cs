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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnlineOperation_CompletesSignedOutWithoutProducingCredentials(bool async, bool idToken)
    {
        NpAuthExports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = MemoryBase;
        Assert.True(ctx.TryWriteUInt64(MemoryBase, 24));
        var requestId = async
            ? NpAuthExports.NpAuthCreateAsyncRequest(ctx)
            : NpAuthExports.NpAuthCreateRequest(ctx);
        Assert.True(requestId > 0);

        var outputAddress = MemoryBase + 0x100;
        var issuerAddress = MemoryBase + 0x1800;
        var credentials = new byte[idToken ? 4104 : 136];
        Array.Fill(credentials, (byte)0xAA);
        Assert.True(memory.TryWrite(outputAddress, credentials));
        Assert.True(ctx.TryWriteUInt32(issuerAddress, uint.MaxValue));
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rdx] = outputAddress;
        ctx[CpuRegister.Rcx] = issuerAddress;

        var result = idToken
            ? NpAuthExports.NpAuthGetIdTokenV3(ctx)
            : NpAuthExports.NpAuthGetAuthorizationCodeV3(ctx);
        Assert.Equal(async ? 0 : unchecked((int)0x80550006), result);
        Assert.True(memory.TryRead(outputAddress, credentials));
        Assert.All(credentials, value => Assert.Equal((byte)0, value));
        Assert.True(ctx.TryReadUInt32(issuerAddress, out var issuer));
        Assert.Equal(idToken ? uint.MaxValue : 0u, issuer);

        var resultAddress = MemoryBase + 0x1880;
        ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, NpAuthExports.NpAuthPollAsync(ctx));
        Assert.True(ctx.TryReadUInt32(resultAddress, out var completionResult));
        Assert.Equal(0x80550006u, completionResult);

        Assert.Equal(unchecked((int)0x80550301), NpAuthExports.NpAuthGetAuthorizationCodeV3(ctx));
    }
}
