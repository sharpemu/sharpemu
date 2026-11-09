// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcUpdatePrimStateTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong CxAddress = MemoryBase + 0x100;
    private const ulong UcAddress = MemoryBase + 0x200;

    [Fact]
    public void RegistryResolvesUpdatePrimState()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("Y3ymLfZ1384", out var export));
        Assert.Equal("sceAgcUpdatePrimState", export.Name);
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(3u, 1u)]
    [InlineData(4u, 2u)]
    [InlineData(6u, 2u)]
    [InlineData(7u, 3u)]
    [InlineData(17u, 4u)]
    [InlineData(18u, 1u)]
    public void UpdatesOnlyThePrimitiveFields(uint primitive, uint outputPrimitive)
    {
        var ctx = Context();
        Assert.True(ctx.TryWriteUInt32(CxAddress, 0x2D5));
        Assert.True(ctx.TryWriteUInt32(CxAddress + 4, 0x400000));
        Assert.True(ctx.TryWriteUInt32(CxAddress + 8, 0x29B));
        Assert.True(ctx.TryWriteUInt32(CxAddress + 12, 0x1234567F));
        Assert.True(ctx.TryWriteUInt32(UcAddress + 16, 0x242));
        Assert.True(ctx.TryWriteUInt32(UcAddress + 20, 0xFEDCBA9F));
        ctx[CpuRegister.Rdx] = primitive;

        Assert.Equal(0, AgcExports.UpdatePrimState(ctx));
        AssertWord(ctx, CxAddress, 0x2D5);
        AssertWord(ctx, CxAddress + 4, 0x400000);
        AssertWord(ctx, CxAddress + 8, 0x29B);
        AssertWord(ctx, CxAddress + 12, 0x12345678 | outputPrimitive);
        AssertWord(ctx, UcAddress + 16, 0x242);
        AssertWord(ctx, UcAddress + 20, 0xFEDCBA80 | primitive);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(32u)]
    [InlineData(36u)]
    public void PreservesShaderOwnedOutputPrimitive(uint stages)
    {
        var ctx = Context();
        Assert.True(ctx.TryWriteUInt32(CxAddress + 4, stages));
        Assert.True(ctx.TryWriteUInt32(CxAddress + 12, 0xDEADBEEF));
        ctx[CpuRegister.Rdx] = 6;

        Assert.Equal(0, AgcExports.UpdatePrimState(ctx));
        AssertWord(ctx, CxAddress + 12, 0xDEADBEEF);
        AssertWord(ctx, UcAddress + 20, 6);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AcceptsNullRegisterBlocks(bool cx, bool uc)
    {
        var ctx = Context();
        ctx[CpuRegister.Rdi] = cx ? CxAddress : 0;
        ctx[CpuRegister.Rsi] = uc ? UcAddress : 0;
        ctx[CpuRegister.Rdx] = 6;

        Assert.Equal(0, AgcExports.UpdatePrimState(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportsUnmappedRegisterMemory(bool cx)
    {
        var ctx = Context();
        ctx[cx ? CpuRegister.Rdi : CpuRegister.Rsi] = MemoryBase + 0x2000;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, AgcExports.UpdatePrimState(ctx));
    }

    private static CpuContext Context()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = CxAddress;
        ctx[CpuRegister.Rsi] = UcAddress;
        return ctx;
    }

    private static void AssertWord(CpuContext ctx, ulong address, uint expected)
    {
        Assert.True(ctx.TryReadUInt32(address, out var value));
        Assert.Equal(expected, value);
    }
}
