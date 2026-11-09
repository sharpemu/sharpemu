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

    [Theory]
    [InlineData("sceNpGetAccountLanguage2")]
    [InlineData("sceNpGetAccountAge")]
    public void AccountRequests_ReportSignedOutDirectlyAndThroughPolling(string name)
    {
        Func<CpuContext, int> query = name == "sceNpGetAccountAge"
            ? NpManagerExports.NpGetAccountAge
            : NpManagerExports.NpGetAccountLanguage2;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var requestId = NpManagerExports.NpCreateAsyncRequest(ctx);
        var outputAddress = MemoryBase + 0x200;
        Assert.True(memory.TryWrite(outputAddress, Enumerable.Repeat((byte)0xCC, 48).ToArray()));

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = outputAddress;
        Assert.Equal(unchecked((int)0x80550006), query(ctx));
        Span<byte> output = stackalloc byte[48];
        Assert.True(memory.TryRead(outputAddress, output));
        Assert.True(output.IndexOfAnyExcept((byte)0xCC) < 0);

        var resultAddress = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(0, NpManagerExports.NpPollAsync(ctx));
        Span<byte> resultBytes = stackalloc byte[4];
        Assert.True(memory.TryRead(resultAddress, resultBytes));
        Assert.Equal(unchecked((int)0x80550006), BinaryPrimitives.ReadInt32LittleEndian(resultBytes));

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, NpManagerExports.NpDeleteRequest(ctx));
    }

    [Theory]
    [InlineData(0, 1, 0x1_0000_0200UL)]
    [InlineData(1, -1, 0x1_0000_0200UL)]
    [InlineData(1, 1, 0UL)]
    public void AccountRequests_RejectInvalidArguments(int requestId, int userId, ulong outputAddress)
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        foreach (var query in new Func<CpuContext, int>[] { NpManagerExports.NpGetAccountLanguage2, NpManagerExports.NpGetAccountAge })
        {
            ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
            ctx[CpuRegister.Rsi] = unchecked((uint)userId);
            ctx[CpuRegister.Rdx] = outputAddress;
            Assert.Equal(unchecked((int)0x80550003), query(ctx));
        }
    }

    [Theory]
    [InlineData("3Tcz5bNCfZQ", "sceNpGetAccountLanguage2")]
    [InlineData("+4DegjBqV1g", "sceNpGetAccountAge")]
    public void AccountRequests_RegisterPs5Nids(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
    }

    [Fact]
    public void CheckNpReachability_RejectsAnInvalidRequest()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80550003), NpManagerExports.NpCheckNpReachability(ctx));
    }
}
