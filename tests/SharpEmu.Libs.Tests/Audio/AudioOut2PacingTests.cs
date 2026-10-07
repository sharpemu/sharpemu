// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2PacingTests
{
    [Theory]
    [InlineData(256u, 48000u, 1u, 40.0)]
    [InlineData(256u, 48000u, 2u, 45.333)]
    [InlineData(256u, 48000u, 4u, 56.0)]
    [InlineData(1024u, 48000u, 4u, 104.0)]
    public void ABlockingPushWaitsUntilTheDeviceHoldsTheCushionAndOneSlotLessThanTheQueue(
        uint grainSamples, uint frequency, uint queueDepth, double expected)
    {
        Assert.Equal(expected, AudioOut2Exports.DeviceSlotFreeThreshold(grainSamples, frequency, queueDepth), 3);
    }

    [Fact]
    public void TheThresholdStaysFarBelowTheHostQueueCap()
    {
        const double hostCapMilliseconds = 128 * 1024 * 1000.0 / (48000 * 4);
        Assert.True(AudioOut2Exports.DeviceSlotFreeThreshold(256, 48000, 2) < hostCapMilliseconds / 10);
    }
}
