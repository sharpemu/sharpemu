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

    [Theory]
    [InlineData(5000ul, 20, 2500ul)]
    [InlineData(5000ul, 34, 2500ul)]
    [InlineData(5000ul, 35, 5000ul)]
    [InlineData(5000ul, 60, 5000ul)]
    [InlineData(5000ul, -1, 5000ul)]
    [InlineData(10000ul, 0, 5000ul)]
    [InlineData(11000ul, 0, 11000ul)]
    [InlineData(100000ul, 0, 100000ul)]
    public void AProducerSleepBetweenGrainsIsHalvedOnlyWhileTheDeviceRunsShort(
        ulong microseconds, int queuedMilliseconds, ulong expected)
    {
        Assert.Equal(expected, AudioOut2Exports.ShortenedProducerSleep(microseconds, 256, 48000, queuedMilliseconds));
    }

    [Fact]
    public void TheThresholdStaysFarBelowTheHostQueueCap()
    {
        const double hostCapMilliseconds = 128 * 1024 * 1000.0 / (48000 * 4);
        Assert.True(AudioOut2Exports.DeviceSlotFreeThreshold(256, 48000, 2) < hostCapMilliseconds / 10);
    }
}
