// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GpuRenderStateSmokeTests
{
    [Fact]
    public void VulkanGuestDepthStencilState_Assembly_HasType()
    {
        var type = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter).Assembly
            .GetType("SharpEmu.Libs.VideoOut.VulkanGuestDepthStencilState");
        Assert.NotNull(type);
    }

    [Fact]
    public void VulkanGuestRasterizerState_Assembly_HasType()
    {
        var type = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter).Assembly
            .GetType("SharpEmu.Libs.VideoOut.VulkanGuestRasterizerState");
        Assert.NotNull(type);
    }

    [Fact]
    public void VulkanGuestRenderState_Assembly_HasType()
    {
        var type = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter).Assembly
            .GetType("SharpEmu.Libs.VideoOut.VulkanGuestRenderState");
        Assert.NotNull(type);
    }

    [Fact]
    public void VulkanVideoPresenter_DecodeDepthZFormatExists()
    {
        var method = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter)
            .GetMethod("DecodeDepthZFormat",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(Silk.NET.Vulkan.Format), method!.ReturnType);
    }
}
