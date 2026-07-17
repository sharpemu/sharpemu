// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

internal static class GuestBlendStateNormalizer
{
    public static GuestBlendState[] NormalizeIntegerAttachments(
        IReadOnlyList<GuestBlendState> blends,
        IReadOnlyList<bool> integerAttachments,
        out int normalizedCount)
    {
        if (blends.Count != integerAttachments.Count)
        {
            throw new ArgumentException(
                "color attachment and blend-state counts must match",
                nameof(integerAttachments));
        }

        var normalized = new GuestBlendState[blends.Count];
        normalizedCount = 0;
        for (var index = 0; index < blends.Count; index++)
        {
            var blend = blends[index];
            if (integerAttachments[index] && blend.Enable)
            {
                blend = blend with { Enable = false };
                normalizedCount++;
            }

            normalized[index] = blend;
        }

        return normalized;
    }
}
