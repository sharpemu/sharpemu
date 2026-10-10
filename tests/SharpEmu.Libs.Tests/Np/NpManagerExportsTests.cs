// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpManagerExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void CheckNpReachability_ReportsSignedOutDirectlyAndThroughPolling()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var requestId = NpManagerExports.NpCreateAsyncRequest(ctx);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(unchecked((int)0x80550006), NpManagerExports.NpCheckNpReachability(ctx));

        var resultAddress = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, NpManagerExports.NpPollAsync(ctx));
        Span<byte> resultBytes = stackalloc byte[4];
        Assert.True(memory.TryRead(resultAddress, resultBytes));
        Assert.Equal(unchecked((int)0x80550006), BinaryPrimitives.ReadInt32LittleEndian(resultBytes));

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, NpManagerExports.NpDeleteRequest(ctx));
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, NpManagerExports.NpPollAsync(ctx));
        Assert.True(memory.TryRead(resultAddress, resultBytes));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(resultBytes));
    }

    [Fact]
    public void CheckNpReachability_RejectsAnInvalidRequest()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80550003), NpManagerExports.NpCheckNpReachability(ctx));
    }
}
