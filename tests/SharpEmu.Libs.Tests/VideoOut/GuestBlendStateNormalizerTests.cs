// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestBlendStateNormalizerTests
{
    private static GuestBlendState EnabledBlend => GuestBlendState.Default with { Enable = true };

    [Fact]
    public void NormalizeIntegerAttachments_DisablesBlendOnIntegerAttachments()
    {
        var blends = new[]
        {
            EnabledBlend,
            EnabledBlend,
            GuestBlendState.Default,
        };
        var integerAttachments = new[] { true, false, true };

        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            blends,
            integerAttachments,
            out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.False(normalized[0].Enable);
        Assert.True(normalized[1].Enable);
        Assert.False(normalized[2].Enable);
    }

    [Fact]
    public void NormalizeIntegerAttachments_LeavesNonIntegerUnchanged()
    {
        var blends = new[] { EnabledBlend, EnabledBlend };
        var integerAttachments = new[] { false, false };

        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            blends,
            integerAttachments,
            out var normalizedCount);

        Assert.Equal(0, normalizedCount);
        Assert.True(normalized[0].Enable);
        Assert.True(normalized[1].Enable);
        Assert.Equal(blends[0], normalized[0]);
        Assert.Equal(blends[1], normalized[1]);
    }

    [Fact]
    public void NormalizeIntegerAttachments_DoesNotCountAlreadyDisabledIntegerBlends()
    {
        var blends = new[] { GuestBlendState.Default };
        var integerAttachments = new[] { true };

        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            blends,
            integerAttachments,
            out var normalizedCount);

        Assert.Equal(0, normalizedCount);
        Assert.False(normalized[0].Enable);
    }

    [Fact]
    public void NormalizeIntegerAttachments_PreservesNonEnableFields()
    {
        var blend = GuestBlendState.Default with
        {
            Enable = true,
            ColorSrcFactor = 4,
            ColorDstFactor = 5,
            ColorFunc = 1,
            AlphaSrcFactor = 6,
            AlphaDstFactor = 7,
            AlphaFunc = 2,
            SeparateAlphaBlend = true,
            WriteMask = 0x7u,
        };

        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            [blend],
            [true],
            out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.Equal(
            blend with { Enable = false },
            normalized[0]);
    }

    [Fact]
    public void NormalizeIntegerAttachments_ThrowsOnCountMismatch()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            GuestBlendStateNormalizer.NormalizeIntegerAttachments(
                [EnabledBlend],
                [true, false],
                out _));

        Assert.Equal("integerAttachments", exception.ParamName);
    }

    [Fact]
    public void NormalizeIntegerAttachments_HandlesEmptyLists()
    {
        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            Array.Empty<GuestBlendState>(),
            Array.Empty<bool>(),
            out var normalizedCount);

        Assert.Equal(0, normalizedCount);
        Assert.Empty(normalized);
    }
}
