// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpEntitlementAccessExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void GetGameTrialsFlagReportsAFullGameAndOnlyWritesOneWord()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MemoryBase, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, NpEntitlementAccessExports.NpEntitlementAccessGetGameTrialsFlag(context));
        Span<byte> actual = stackalloc byte[8];
        Assert.True(memory.TryRead(MemoryBase, actual));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 5, 6, 7, 8 }, actual.ToArray());
    }

    [Theory]
    [InlineData(0UL, unchecked((int)0x817D0002))]
    [InlineData(0x2_0000_0000UL, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void GetGameTrialsFlagValidatesOutputMemory(ulong address, int expected)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        Assert.Equal(expected, NpEntitlementAccessExports.NpEntitlementAccessGetGameTrialsFlag(context));
    }

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
}
