// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestFlipWaitTests
{
    [Fact]
    public void NoPreviousFlipIsSafe()
    {
        var tracker = new VulkanGuestFlipCompletionTracker();

        Assert.True(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 0));
    }

    [Fact]
    public void PendingFlipIsNotSafe()
    {
        var tracker = new VulkanGuestFlipCompletionTracker();
        tracker.Register(handle: 1, bufferIndex: 0, version: 2);
        tracker.MarkSafe(handle: 1, bufferIndex: 0, version: 1);

        Assert.False(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 2));
    }

    [Fact]
    public void CompletedFlipsAdvanceInRegistrationOrder()
    {
        var tracker = new VulkanGuestFlipCompletionTracker();
        tracker.Register(handle: 1, bufferIndex: 0, version: 2);
        tracker.Register(handle: 1, bufferIndex: 0, version: 3);
        tracker.MarkSafe(handle: 1, bufferIndex: 0, version: 2);
        tracker.MarkSafe(handle: 1, bufferIndex: 0, version: 3);

        Assert.True(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 3));
        Assert.True(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 2));
    }

    [Fact]
    public void LaterOutOfOrderCompletionDoesNotReleaseEarlierFlip()
    {
        var tracker = new VulkanGuestFlipCompletionTracker();
        tracker.Register(handle: 1, bufferIndex: 0, version: 10);
        tracker.Register(handle: 1, bufferIndex: 0, version: 12);
        tracker.MarkSafe(handle: 1, bufferIndex: 0, version: 12);

        Assert.False(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 10));
        Assert.True(tracker.IsSafe(handle: 1, bufferIndex: 0, version: 12));
    }

    [Fact]
    public void CompletionDoesNotApplyToAnotherBuffer()
    {
        var tracker = new VulkanGuestFlipCompletionTracker();
        tracker.Register(handle: 1, bufferIndex: 0, version: 2);
        tracker.Register(handle: 1, bufferIndex: 1, version: 2);
        tracker.MarkSafe(handle: 1, bufferIndex: 0, version: 2);

        Assert.False(tracker.IsSafe(handle: 1, bufferIndex: 1, version: 2));
    }
}
