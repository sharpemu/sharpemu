// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using LibAtrac9;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Atrac9ConfigTests
{
    [Fact]
    public void StandardStereoConfigIsParsed()
    {
        var config = new Atrac9Config([0xFE, 0x74, 0x0B, 0xF0]);

        Assert.Equal(2, config.ChannelCount);
        Assert.Equal(48000, config.SampleRate);
        Assert.Equal(96, config.FrameBytes);
        Assert.Equal(384, config.SuperframeBytes);
        Assert.Equal(1024, config.SuperframeSamples);
        Assert.Equal(0, config.IndependentBlockSuperframeBytes);
    }

    // Demon's Souls ships first-, second- and third-order ambisonic streams
    // (4, 9 and 16 channels) with a 0x30 config header.
    [Theory]
    [InlineData(new byte[] { 0x30, 0x70, 0xC1, 0x7E }, 4, 1536)]
    [InlineData(new byte[] { 0x30, 0x72, 0x01, 0x7E }, 9, 3456)]
    [InlineData(new byte[] { 0x30, 0x73, 0xC1, 0x7E }, 16, 6144)]
    public void IndependentMonoConfigIsParsed(byte[] configData, int channels, int superframeBytes)
    {
        var config = new Atrac9Config(configData);

        Assert.Equal(channels, config.ChannelCount);
        Assert.Equal(channels, config.ChannelConfig.BlockCount);
        Assert.All(config.ChannelConfig.BlockTypes, type => Assert.Equal(BlockType.Mono, type));
        Assert.Equal(48000, config.SampleRate);
        Assert.Equal(4, config.FramesPerSuperframe);
        Assert.Equal(1024, config.SuperframeSamples);
        Assert.Equal(superframeBytes, config.SuperframeBytes);
        Assert.Equal(384, config.IndependentBlockSuperframeBytes);
    }

    [Theory]
    [InlineData(new byte[] { 0x31, 0x73, 0xC1, 0x7E })]
    [InlineData(new byte[] { 0xFE, 0x7C, 0x0B, 0xF0 })]
    [InlineData(new byte[] { 0xFE, 0x75, 0x0B, 0xF0 })]
    public void InvalidConfigIsRejected(byte[] configData)
    {
        Assert.Throws<InvalidDataException>(() => new Atrac9Config(configData));
    }
}
