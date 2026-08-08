// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

internal static class VulkanStorageFeedbackTargetResolver
{
    /// <summary>
    /// This method replaces one zero-write color attachment when the attachment
    /// aliases a storage image. The method uses the private compatibility
    /// attachment for color output. The method keeps the guest surface bound
    /// only as a storage image. This configuration is valid in Vulkan and keeps
    /// the ImageLoad and ImageStore operations.
    /// </summary>
    public static bool TryResolve(
        IReadOnlyList<GuestRenderTarget> targets,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestBlendState> blends,
        out GuestRenderTarget[] resolvedTargets,
        out bool usedCompatibilityAttachment,
        out ulong unsupportedAddress)
    {
        if (targets.Count != blends.Count)
        {
            throw new ArgumentException(
                "color attachment and blend-state counts must match",
                nameof(blends));
        }

        resolvedTargets = targets.ToArray();
        usedCompatibilityAttachment = false;
        unsupportedAddress = 0;

        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            if (target.Address == 0 ||
                !textures.Any(texture =>
                    texture.IsStorage &&
                    texture.Address == target.Address))
            {
                continue;
            }

            // Use one private attachment only for a storage-output pass with one
            // target. Multiple aliased MRT slots require separate transient
            // images and render-pass views.
            if (targets.Count != 1 || blends[targetIndex].WriteMask != 0)
            {
                unsupportedAddress = target.Address;
                return false;
            }

            resolvedTargets[targetIndex] = target with { Address = 0 };
            usedCompatibilityAttachment = true;
        }

        return true;
    }
}
