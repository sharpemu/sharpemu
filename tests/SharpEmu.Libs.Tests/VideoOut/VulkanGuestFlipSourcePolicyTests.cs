// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestFlipSourcePolicyTests
{
    [Fact]
    public void RegistrationAloneCannotCaptureAFlip()
    {
        Assert.False(VulkanGuestFlipSourcePolicy.CanCapture(
            registered: true,
            materialized: false,
            hasQueuedWriter: false));
    }

    [Fact]
    public void MaterializedImageCanCaptureAFlip()
    {
        Assert.True(VulkanGuestFlipSourcePolicy.CanCapture(
            registered: true,
            materialized: true,
            hasQueuedWriter: false));
    }

    [Fact]
    public void QueuedWriterCanCaptureAFlipBeforeMaterialization()
    {
        Assert.True(VulkanGuestFlipSourcePolicy.CanCapture(
            registered: true,
            materialized: false,
            hasQueuedWriter: true));
    }

    [Fact]
    public void UnregisteredImageCannotCaptureAFlip()
    {
        Assert.False(VulkanGuestFlipSourcePolicy.CanCapture(
            registered: false,
            materialized: true,
            hasQueuedWriter: true));
    }
}
