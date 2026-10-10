// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

public sealed class TickOwnedResourcesTests
{
    private sealed class Feedback : IDisposable
    {
        public int Value;
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void CompletingEarlierTick_DoesNotConsumeOrResetLaterFeedback()
    {
        using var resources = new TickOwnedResources<Feedback>();
        var first = resources.Acquire(10, () => new Feedback());
        Assert.Same(first, resources.Acquire(10, () => throw new InvalidOperationException()));
        var second = resources.Acquire(11, () => new Feedback());
        Assert.NotSame(first, second);
        first.Value = 123;
        second.Value = 456;
        resources.Complete(10, feedback =>
        {
            Assert.Equal(123, feedback.Value);
            feedback.Value = 0;
        });
        Assert.Equal(1, first.Disposals);
        Assert.Equal(456, second.Value);
        Assert.Equal(0, second.Disposals);
        resources.Complete(10, _ => throw new InvalidOperationException());
        resources.Complete(11, feedback => Assert.Equal(456, feedback.Value));
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public void FailedRead_StillReleasesResource()
    {
        using var resources = new TickOwnedResources<Feedback>();
        var feedback = resources.Acquire(1, () => new Feedback());
        Assert.Throws<InvalidOperationException>(() => resources.Complete(1, _ => throw new InvalidOperationException()));
        Assert.Equal(1, feedback.Disposals);
    }
}
