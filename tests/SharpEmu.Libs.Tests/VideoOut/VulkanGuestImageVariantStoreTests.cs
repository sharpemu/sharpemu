// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageVariantStoreTests
{
    [Fact]
    public void MissingKey_AddsIncomingResource()
    {
        var incoming = new object();

        var action = VulkanVideoPresenter.DecideGuestImageVariantStore(null, incoming);

        Assert.Equal(VulkanVideoPresenter.GuestImageVariantStoreAction.Add, action);
    }

    [Fact]
    public void SameResource_KeepsExistingOwnership()
    {
        var resource = new object();

        var action = VulkanVideoPresenter.DecideGuestImageVariantStore(resource, resource);

        Assert.Equal(
            VulkanVideoPresenter.GuestImageVariantStoreAction.KeepExisting,
            action);
    }

    [Fact]
    public void DifferentResource_RequiresFlushBeforeReplacement()
    {
        var stored = new object();
        var incoming = new object();

        var action = VulkanVideoPresenter.DecideGuestImageVariantStore(stored, incoming);

        Assert.Equal(
            VulkanVideoPresenter.GuestImageVariantStoreAction.FlushAndReplace,
            action);
    }
}
