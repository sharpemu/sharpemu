// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthAttachmentPolicyTests
{
    [Theory]
    [InlineData(ImageAspectFlags.None)]
    [InlineData(ImageAspectFlags.DepthBit)]
    [InlineData(ImageAspectFlags.StencilBit)]
    [InlineData(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)]
    public void StoreAccessIncludesAttachmentWriteEvenWhenTheGuestStateIsReadOnly(ImageAspectFlags guestWriteAspects)
    {
        Assert.Equal(
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            VulkanVideoPresenter.DepthAttachmentStoreAccess(guestWriteAspects));
    }
}
