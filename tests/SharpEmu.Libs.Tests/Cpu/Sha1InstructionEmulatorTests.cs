// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Sha1InstructionEmulatorTests
{
    private static readonly Sha1Vector Destination = new(
        0x0123_4567u, 0x89AB_CDEFu, 0x0F1E_2D3Cu, 0x4B5A_6978u);
    private static readonly Sha1Vector Source = new(
        0xFEDC_BA98u, 0x7654_3210u, 0xF0E1_D2C3u, 0xB4A5_9687u);

    [Fact]
    public void MessageSchedule1_MatchesIntelLaneSemantics()
    {
        Assert.Equal(
            new Sha1Vector(0xF1C2_97A4u, 0x3D0E_5B68u, 0x0E3D_685Bu, 0xC2F1_A497u),
            Sha1InstructionEmulator.MessageSchedule1(Destination, Source));
    }

    [Fact]
    public void MessageSchedule2_MatchesIntelLaneSemantics()
    {
        Assert.Equal(
            new Sha1Vector(0xECA8_6420u, 0xEEEE_EEEEu, 0xF294_3E58u, 0x7777_7777u),
            Sha1InstructionEmulator.MessageSchedule2(Destination, Source));
    }

    [Fact]
    public void NextE_MatchesIntelLaneSemantics()
    {
        Assert.Equal(
            new Sha1Vector(0xFEDC_BA98u, 0x7654_3210u, 0xF0E1_D2C3u, 0xC77C_30E5u),
            Sha1InstructionEmulator.NextE(Destination, Source));
    }

    [Theory]
    [InlineData(0, 0x20E8_2326u, 0x911F_2CA8u, 0xECE0_593Fu, 0x0C1C_11FBu)]
    [InlineData(1, 0x4598_D5B9u, 0x7BA0_0411u, 0x464E_3C51u, 0xF514_1B52u)]
    [InlineData(2, 0xEE0E_73F6u, 0x2509_A67Bu, 0x26C6_85CCu, 0x0097_5845u)]
    [InlineData(3, 0x9C7B_0B46u, 0xAEC8_EB49u, 0x8FD5_A2B7u, 0xFD49_9AECu)]
    public void FourRounds_MatchesAllFourSha1Functions(
        byte function,
        uint lane0,
        uint lane1,
        uint lane2,
        uint lane3)
    {
        Assert.Equal(
            new Sha1Vector(lane0, lane1, lane2, lane3),
            Sha1InstructionEmulator.FourRounds(Destination, Source, function));
    }
}
