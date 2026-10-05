// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class PacketHeaderTests
{
    [Theory]
    [InlineData(2u, 0x10u, 0u, 0xC000_1000u)]
    [InlineData(8u, 0x10u, 0x18u, 0xC006_1060u)]
    [InlineData(6u, 0x47u, 0u, 0xC004_4700u)]
    [InlineData(7u, 0x10u, 0x06u, 0xC005_1018u)]
    public void Make_ProducesTheReferenceHeaders(uint dwords, uint opcode, uint customCode, uint expected)
    {
        var header = PacketHeader.Make(dwords, opcode, customCode);

        Assert.Equal(expected, header);
        Assert.Equal(dwords, PacketHeader.Length(header));
        Assert.Equal(opcode, PacketHeader.Opcode(header));
        Assert.Equal(customCode, PacketHeader.CustomCode(header));
        Assert.Equal(3u, PacketHeader.PacketType(header));
        Assert.False(PacketHeader.IsPredicated(header));
    }

    [Fact]
    public void PredicateBit_IsTheLowestHeaderBit()
    {
        var header = PacketHeader.Make(3, PacketOpcode.DrawIndexAuto) | 1u;

        Assert.True(PacketHeader.IsPredicated(header));
        Assert.Equal(3u, PacketHeader.Length(header));
    }

    [Fact]
    public void InternalDataPacket_MatchesOnlyTheLibraryShapes()
    {
        var beginHeader = PacketHeader.Make(4, PacketOpcode.SetUserConfigRegister, 1);
        var endHeader = PacketHeader.Make(3, PacketOpcode.SetUserConfigRegister, 1);

        Assert.True(InternalDataPacket.Matches(beginHeader, new uint[] { 0x342, 0xC801_0000, 0 }));
        Assert.True(InternalDataPacket.Matches(beginHeader, new uint[] { 0x342, 0xC802_1234, 0 }));
        Assert.True(InternalDataPacket.Matches(endHeader, new uint[] { 0x342, 0xC800_0000 }));
        Assert.True(InternalDataPacket.Matches(endHeader, new uint[] { 0x342, 0xC600_0008 }));
        Assert.False(InternalDataPacket.Matches(endHeader, new uint[] { 0x343, 0xC800_0000 }));
        Assert.False(InternalDataPacket.Matches(PacketHeader.Make(3, PacketOpcode.SetUserConfigRegister, 0), new uint[] { 0x342, 0xC800_0000 }));
        Assert.False(InternalDataPacket.Matches(beginHeader, new uint[] { 0x342, 0xC800_0000, 0 }));
    }
}
