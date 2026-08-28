// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;

internal static class VulkanFramebufferExtentResolver
{
    internal static Extent2D Resolve(
        ReadOnlySpan<Extent2D> colorAttachments,
        Extent2D? depthAttachment = null)
    {
        if (colorAttachments.IsEmpty)
        {
            return depthAttachment ?? default;
        }

        var width = colorAttachments[0].Width;
        var height = colorAttachments[0].Height;
        for (var index = 1; index < colorAttachments.Length; index++)
        {
            width = Math.Min(width, colorAttachments[index].Width);
            height = Math.Min(height, colorAttachments[index].Height);
        }

        if (depthAttachment is { } depth)
        {
            width = Math.Min(width, depth.Width);
            height = Math.Min(height, depth.Height);
        }

        return new Extent2D(width, height);
    }
}
