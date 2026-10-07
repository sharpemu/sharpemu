// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

// Keeps a storage image out of the Vulkan color-attachment list when the guest
// disabled color writes. A storage image with active color writes still needs a
// pass split; silently binding both views would make the result undefined.
internal static class VulkanStorageFeedbackTargetResolver
{
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
            if (target.Address == 0 || !textures.Any(texture =>
                    texture.IsStorage && texture.Address == target.Address))
            {
                continue;
            }

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
