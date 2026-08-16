// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuLabelHostPublicationQueueTests
{
    [Fact]
    public void HostValueIsNotPublishedBeforeItsProducerCompletes()
    {
        var queue = new GpuLabelHostPublicationQueue();
        var published = false;

        queue.Register(new GuestGpuLabelDependency(4, 0), () => published = true);

        Assert.False(published);
        Assert.Equal(1, queue.Count);
        queue.Complete(new GuestGpuLabelDependency(3, 0));
        Assert.False(published);
        queue.Complete(new GuestGpuLabelDependency(4, 0));
        Assert.True(published);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CrossQueueValueWaitsForBothDependencies()
    {
        var queue = new GpuLabelHostPublicationQueue();
        var published = false;

        queue.Register(new GuestGpuLabelDependency(4, 7), () => published = true);
        queue.Complete(new GuestGpuLabelDependency(4, 0));
        Assert.False(published);
        queue.Complete(new GuestGpuLabelDependency(0, 7));
        Assert.True(published);
    }

    [Fact]
    public void PublicationsKeepPacketOrder()
    {
        var queue = new GpuLabelHostPublicationQueue();
        var order = new List<int>();

        queue.Register(new GuestGpuLabelDependency(2, 0), () => order.Add(1));
        queue.Register(new GuestGpuLabelDependency(2, 0), () => order.Add(2));
        queue.Complete(new GuestGpuLabelDependency(2, 0));

        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void EmptyDependencyPublishesImmediately()
    {
        var queue = new GpuLabelHostPublicationQueue();
        var published = false;

        queue.Register(default, () => published = true);

        Assert.True(published);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CancelDropsUnfinishedPublications()
    {
        var queue = new GpuLabelHostPublicationQueue();
        var published = false;
        queue.Register(new GuestGpuLabelDependency(1, 0), () => published = true);

        queue.Cancel();
        queue.Complete(new GuestGpuLabelDependency(1, 0));

        Assert.False(published);
        Assert.Equal(0, queue.Count);
    }
}
