// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Remoteplay;
using Xunit;

namespace SharpEmu.Libs.Tests.Remoteplay;

public sealed class RemoteplayExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    [Fact]
    public void Initialize_Succeeds()
    {
        var ctx = CreateContext(out _);

        var result = RemoteplayExports.RemoteplayInitialize(ctx);

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetConnectionStatus_WritesDisconnectedStatus()
    {
        var ctx = CreateContext(out var memory);
        ctx[CpuRegister.Rdi] = 0x1000_0000;
        ctx[CpuRegister.Rsi] = StatusAddress;
        memory.TryWrite(StatusAddress, stackalloc byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        var result = RemoteplayExports.RemoteplayGetConnectionStatus(ctx);

        Assert.Equal(0, result);
        var status = new byte[4];
        Assert.True(ctx.Memory.TryRead(StatusAddress, status));
        Assert.Equal(0, status[0]);
    }

    [Fact]
    public void GetConnectionStatus_NullOutPointerStillSucceeds()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 0x1000_0000;
        ctx[CpuRegister.Rsi] = 0;

        var result = RemoteplayExports.RemoteplayGetConnectionStatus(ctx);

        Assert.Equal(0, result);
    }
}
