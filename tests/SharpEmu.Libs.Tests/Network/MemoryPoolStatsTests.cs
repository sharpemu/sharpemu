// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class MemoryPoolStatsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatsAddress = MemoryBase + 0x100;

    [Fact]
    public void NetPoolStats_ReportTheCreatedPool()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = memory.WriteCString(MemoryBase + 0x200, "stats");
        ctx[CpuRegister.Rsi] = 0x4000;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NetExports.NetPoolCreate(ctx));
        var poolId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = poolId;
        ctx[CpuRegister.Rsi] = StatsAddress;
        Assert.Equal(0, NetExports.NetGetMemoryPoolStats(ctx));
        AssertStats(memory, 0x4000);

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetGetMemoryPoolStats(ctx));

        ctx[CpuRegister.Rdi] = poolId;
        Assert.Equal(0, NetExports.NetPoolDestroy(ctx));
        ctx[CpuRegister.Rsi] = StatsAddress;
        Assert.Equal(unchecked((int)0x80410109), NetExports.NetGetMemoryPoolStats(ctx));
    }

    [Fact]
    public void SslPoolStats_ReportTheContextPool()
    {
        var (memory, ctx) = CreateContext();
        ctx[CpuRegister.Rdi] = 0x28000;
        Assert.Equal(0, SslExports.SslInit(ctx));
        var contextId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = StatsAddress;
        Assert.Equal(0, SslExports.SslGetMemoryPoolStats(ctx));
        AssertStats(memory, 0x28000);

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x8095F007), SslExports.SslGetMemoryPoolStats(ctx));

        ctx[CpuRegister.Rdi] = contextId;
        Assert.Equal(0, SslExports.SslTerm(ctx));
        ctx[CpuRegister.Rsi] = StatsAddress;
        Assert.Equal(unchecked((int)0x8095F006), SslExports.SslGetMemoryPoolStats(ctx));
    }

    [Theory]
    [InlineData("rMyh97BU5pY", "sceNetGetMemoryPoolStats", "libSceNet")]
    [InlineData("-PoIzr3PEk0", "sceSslGetMemoryPoolStats", "libSceSsl")]
    public void PoolStats_RegisterPs5Nids(string nid, string name, string library)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal(library, export.LibraryName);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Assert.True(memory.TryWrite(StatsAddress, Enumerable.Repeat((byte)0xCC, 32).ToArray()));
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static void AssertStats(FakeCpuMemory memory, ulong poolSize)
    {
        var stats = new byte[32];
        Assert.True(memory.TryRead(StatsAddress, stats));
        Assert.Equal(poolSize, BitConverter.ToUInt64(stats, 0));
        Assert.All(stats[8..], value => Assert.Equal(0, value));
    }
}
