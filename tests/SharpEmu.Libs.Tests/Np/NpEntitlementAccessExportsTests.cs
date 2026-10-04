// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpEntitlementAccessExportsTests
{
    public NpEntitlementAccessExportsTests() => NpEntitlementAccessExports.ResetForTests();

    [Fact]
    public void GetEntitlementKey_MatchesOfflineNoEntitlementContract()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong labelAddress = memoryBase + 0x100;
        const ulong keyAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = labelAddress;
        ctx[CpuRegister.Rdx] = keyAddress;
        Assert.Equal(
            unchecked((int)0x817D0007),
            NpEntitlementAccessExports.NpEntitlementAccessGetEntitlementKey(ctx));

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            unchecked((int)0x817D0002),
            NpEntitlementAccessExports.NpEntitlementAccessGetEntitlementKey(ctx));
    }

    [Fact]
    public void GetEntitlementKey_RegistersWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("5LiMEPuW0DQ", out var export));
        Assert.Equal("sceNpEntitlementAccessGetEntitlementKey", export.Name);
        Assert.Equal("libSceNpEntitlementAccess", export.LibraryName);
    }

    [Fact]
    public void UnifiedEntitlementRequest_CompletesEmptyAndSupportsAbortAndDelete()
    {
        const ulong parameterAddress = MemoryBase + 0x100;
        const ulong requestIdAddress = MemoryBase + 0x180;
        const ulong resultAddress = MemoryBase + 0x200;
        const ulong hitNumAddress = MemoryBase + 0x208;
        const ulong nextOffsetAddress = MemoryBase + 0x210;
        const ulong previousOffsetAddress = MemoryBase + 0x218;
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(parameterAddress, new byte[0x20]));

        context[CpuRegister.Rdi] = 0x1000_0000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = parameterAddress;
        context[CpuRegister.R9] = requestIdAddress;

        Assert.Equal(
            0,
            NpEntitlementAccessExports.NpEntitlementAccessRequestUnifiedEntitlementInfoList(context));

        Span<byte> requestIdBytes = stackalloc byte[sizeof(long)];
        Assert.True(memory.TryRead(requestIdAddress, requestIdBytes));
        var requestId = BinaryPrimitives.ReadInt64LittleEndian(requestIdBytes);
        Assert.Equal(0x1000_0001L, requestId);

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        context[CpuRegister.Rsi] = resultAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = hitNumAddress;
        context[CpuRegister.R9] = nextOffsetAddress;
        context.SetImportStackArguments(previousOffsetAddress, 0, 0, 0, 0, 0);

        Assert.Equal(
            0,
            NpEntitlementAccessExports.NpEntitlementAccessPollUnifiedEntitlementInfoList(context));
        Assert.Equal(0, ReadInt32(memory, resultAddress));
        Assert.Equal(0, ReadInt32(memory, hitNumAddress));
        Assert.Equal(-1, ReadInt32(memory, nextOffsetAddress));
        Assert.Equal(-1, ReadInt32(memory, previousOffsetAddress));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessAbortRequest(context));
        context[CpuRegister.Rsi] = resultAddress;
        context[CpuRegister.R8] = hitNumAddress;
        context[CpuRegister.R9] = nextOffsetAddress;
        Assert.Equal(
            0,
            NpEntitlementAccessExports.NpEntitlementAccessPollUnifiedEntitlementInfoList(context));
        Assert.Equal(unchecked((int)0x817D0016), ReadInt32(memory, resultAddress));

        context[CpuRegister.Rdi] = unchecked((ulong)requestId);
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessDeleteRequest(context));
        context[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(
            unchecked((int)0x817D0015),
            NpEntitlementAccessExports.NpEntitlementAccessPollUnifiedEntitlementInfoList(context));
    }

    [Fact]
    public void UnifiedEntitlementExports_RegisterWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "uCZf2L27th8", "sceNpEntitlementAccessRequestUnifiedEntitlementInfoList");
        AssertExport(manager, "nAEqawEZG5s", "sceNpEntitlementAccessPollUnifiedEntitlementInfoList");
        AssertExport(manager, "HFcQl9TMcFQ", "sceNpEntitlementAccessAbortRequest");
        AssertExport(manager, "Z0eQj8m7XA8", "sceNpEntitlementAccessDeleteRequest");
    }

    private const ulong MemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData("GHOST2APP0000000")]
    [InlineData("GHOST2BASE000000")]
    public void GhostOfYoteiMainEntitlement_IsReportedAsInstalled(string label)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        var labelAddress = MemoryBase + 0x100;
        var infoAddress = MemoryBase + 0x200;
        memory.WriteCString(labelAddress, label);
        context[CpuRegister.Rsi] = labelAddress;
        context[CpuRegister.Rdx] = infoAddress;

        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessGetAddcontEntitlementInfo(context));

        Span<byte> info = stackalloc byte[0x1C];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.Equal(label, ReadCString(info[..17]));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(info[20..]));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
    }

    [Fact]
    public void GhostOfYoteiOptionalEntitlement_RemainsUnowned()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1_000);
        var context = new CpuContext(memory, Generation.Gen5);
        var labelAddress = MemoryBase + 0x100;
        var infoAddress = MemoryBase + 0x200;
        memory.WriteCString(labelAddress, "GHOST2DDE0000000");
        context[CpuRegister.Rsi] = labelAddress;
        context[CpuRegister.Rdx] = infoAddress;

        Assert.Equal(
            unchecked((int)0x817D0007),
            NpEntitlementAccessExports.NpEntitlementAccessGetAddcontEntitlementInfo(context));
    }

    private static string ReadCString(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(value[..(length < 0 ? value.Length : length)]);
    }

    private static int ReadInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNpEntitlementAccess", export.LibraryName);
    }
}
