// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class BindlessImageHeapCapacityTests
{
    private static BindlessImageHeap.Limits Limits(uint images = uint.MaxValue, uint samplers = uint.MaxValue, uint total = uint.MaxValue) =>
        new(images, images, samplers, images, images, samplers, images, images, samplers, total);

    [Fact]
    public void SamplerLimits_BoundTheSamplerBinding()
    {
        // A device with few samplers per stage (MoltenVK without argument buffers reports 16).
        var (_, _, samplers) = BindlessImageHeap.Capacities(Limits(samplers: 16));

        Assert.Equal(16u, samplers);
    }

    [Fact]
    public void Samplers_CountAgainstThePoolWideTotal()
    {
        var (sampled, storage, samplers) = BindlessImageHeap.Capacities(Limits(total: 5096));

        Assert.Equal(4096u, samplers);
        Assert.Equal(5096u, sampled + storage + samplers);
        Assert.Equal(500u, sampled);
        Assert.Equal(500u, storage);
    }

    [Fact]
    public void UnboundedDevices_KeepTheStableCapacities()
    {
        // NVIDIA reports the pool-wide total as UINT_MAX.
        var (sampled, storage, samplers) = BindlessImageHeap.Capacities(Limits());

        Assert.Equal(4096u, samplers);
        Assert.Equal(128u * 1024, sampled);
        Assert.Equal(128u * 1024, storage);
    }

    [Fact]
    public void NoSamplerOrImageRoom_IsAnExplicitFailure()
    {
        using var fatal = new FatalScope();

        Assert.Throws<SchedulerFatalException>(() => BindlessImageHeap.Capacities(Limits(samplers: 0)));
        Assert.Throws<SchedulerFatalException>(() => BindlessImageHeap.Capacities(Limits(total: 4096)));
    }
}
