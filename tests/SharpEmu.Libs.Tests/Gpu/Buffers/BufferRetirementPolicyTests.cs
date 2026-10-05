// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class BufferRetirementPolicyTests
{
    [Theory]
    [InlineData(1073741823UL, false, false, 0)]
    [InlineData(1073741824UL, true, false, 32)]
    [InlineData(2147483647UL, true, false, 32)]
    [InlineData(2147483648UL, true, true, 64)]
    public void DefaultThresholdBoundariesPreserveCollectionMode(
        ulong usedMemory, bool expectedCollection, bool expectedDownloads, int expectedLimit)
    {
        var policy = new BufferRetirementPolicy();
        Assert.Equal(expectedCollection, policy.TryBeginCollection(usedMemory, out var plan));
        Assert.Equal(expectedDownloads, plan.DownloadDirtyBuffers);
        Assert.Equal(expectedLimit, plan.MaximumBufferCount);
        Assert.Equal(0UL, plan.LatestEligibleTick);
        Assert.Equal(1UL, policy.CurrentTick);
    }

    [Theory]
    [InlineData(false, 0UL, 0UL)]
    [InlineData(false, 159UL, 0UL)]
    [InlineData(false, 160UL, 0UL)]
    [InlineData(false, 161UL, 1UL)]
    [InlineData(true, 79UL, 0UL)]
    [InlineData(true, 80UL, 0UL)]
    [InlineData(true, 81UL, 1UL)]
    public void IdleCollectionCallsAdvanceAgeWithoutUnderflow(
        bool critical, ulong idleCalls, ulong expectedCutoff)
    {
        var policy = new BufferRetirementPolicy();
        policy.SetThresholds(1, 2);
        for (ulong index = 0; index < idleCalls; index++)
        {
            Assert.False(policy.TryBeginCollection(0, out _));
        }

        Assert.True(policy.TryBeginCollection(critical ? 2UL : 1UL, out var plan));
        Assert.Equal(expectedCutoff, plan.LatestEligibleTick);
        Assert.Equal(idleCalls + 1, policy.CurrentTick);
    }

    [Fact]
    public void PressureChangesDoNotResetCollectionAge()
    {
        var policy = new BufferRetirementPolicy();
        policy.SetThresholds(1, 2);
        for (var index = 0; index < 200; index++) policy.TryBeginCollection(0, out _);

        Assert.True(policy.TryBeginCollection(1, out var normal));
        Assert.Equal(new BufferRetirementPlan(40, 32, false), normal);
        Assert.True(policy.TryBeginCollection(2, out var critical));
        Assert.Equal(new BufferRetirementPlan(121, 64, true), critical);
        policy.SetThresholds(3, 4);
        Assert.False(policy.TryBeginCollection(2, out _));
        Assert.True(policy.TryBeginCollection(3, out var normalAgain));
        Assert.Equal(new BufferRetirementPlan(43, 32, false), normalAgain);
    }
}
