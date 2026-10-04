// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanShaderClockPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RequiresExtensionAndDeviceClockFeature(
        bool extensionAvailable,
        bool deviceClockFeature,
        bool expected) =>
        Assert.Equal(
            expected,
            VulkanShaderClockPolicy.ShouldEnable(extensionAvailable, deviceClockFeature));

    [Theory]
    [InlineData(1.0f, 3u)]
    [InlineData(2.0f, 2u)]
    [InlineData(10.0f, 0u)]
    public void ChoosesPowerOfTwoShiftTowardGuestRate(float timestampPeriod, uint expected)
    {
        Assert.True(VulkanShaderClockPolicy.TryComputeShift(timestampPeriod, out var shift));
        Assert.Equal(expected, shift);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsInvalidTimestampPeriod(float timestampPeriod)
    {
        Assert.False(VulkanShaderClockPolicy.TryComputeShift(timestampPeriod, out var shift));
        Assert.Equal(0u, shift);
    }
}
