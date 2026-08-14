// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageAliasTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(24u)]
    [InlineData(27u)]
    public void MatchingTileModeCanReusePhysicalImage(uint tileMode)
    {
        Assert.True(
            VulkanVideoPresenter.HasCompatibleGuestImageTileMode(
                tileMode,
                tileMode));
    }

    [Theory]
    [InlineData(0u, 27u)]
    [InlineData(24u, 27u)]
    [InlineData(27u, 0u)]
    public void DifferentTileModeRequiresDifferentPhysicalImage(
        uint requestedTileMode,
        uint existingTileMode)
    {
        Assert.False(
            VulkanVideoPresenter.HasCompatibleGuestImageTileMode(
                requestedTileMode,
                existingTileMode));
    }

    [Theory]
    [InlineData(Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R8G8B8A8Srgb)]
    public void SrgbAndUnormCounterpartsShareOneGuestImage(
        Format existing,
        Format requested)
    {
        Assert.True(
            VulkanVideoPresenter.IsAliasableGuestImageFormat(existing, requested));
    }

    [Theory]
    [InlineData(Format.R8G8B8A8Unorm)]
    [InlineData(Format.R8G8B8A8Srgb)]
    [InlineData(Format.R16G16B16A16Sfloat)]
    public void IdenticalFormatsUseTheExactMatchPath(Format format)
    {
        // Equal formats are accepted by the exact-match condition; the alias
        // helper only reports genuinely different identities.
        Assert.False(
            VulkanVideoPresenter.IsAliasableGuestImageFormat(format, format));
    }

    [Theory]
    [InlineData(Format.R32Uint, Format.R8G8B8A8Unorm)]
    [InlineData(Format.R32Sint, Format.R8G8B8A8Unorm)]
    [InlineData(Format.R32Sfloat, Format.R8G8B8A8Unorm)]
    [InlineData(Format.R16G16Sfloat, Format.R8G8B8A8Unorm)]
    [InlineData(Format.A2B10G10R10UnormPack32, Format.R8G8B8A8Unorm)]
    [InlineData(Format.B10G11R11UfloatPack32, Format.R8G8B8A8Srgb)]
    public void SameClassNumericReinterpretationKeepsRecreatePath(
        Format existing,
        Format requested)
    {
        // These pairs are legal Vulkan view aliases (same 32-bit class), but
        // accepting them would attach fragment shaders translated for the
        // other numeric encoding to the existing attachment format.
        Assert.True(
            VulkanVideoPresenter.IsCompatibleGuestImageViewFormat(existing, requested));
        Assert.False(
            VulkanVideoPresenter.IsAliasableGuestImageFormat(existing, requested));
    }

    [Theory]
    [InlineData(Format.BC3SrgbBlock, Format.BC3UnormBlock)]
    public void CounterpartsOutsideTheViewClassTableAreNotAliased(
        Format existing,
        Format requested)
    {
        // sRGB/UNORM counterparts that the view-compatibility table cannot
        // alias must keep the recreate path: the shared image could never
        // legally serve the second identity through an alias view.
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGuestImageViewFormat(existing, requested));
        Assert.False(
            VulkanVideoPresenter.IsAliasableGuestImageFormat(existing, requested));
    }

    [Fact]
    public void R8SrgbAndR8UnormShareOneCompatibilityClass()
    {
        Assert.True(
            VulkanVideoPresenter.IsCompatibleGuestImageViewFormat(
                Format.R8Srgb,
                Format.R8Unorm));
    }

    [Fact]
    public void AliasedPairStaysWithinOneCompatibilityClass()
    {
        // The alias accept creates identity views of both formats on one
        // mutable-format image; this guards the compatibility table entries
        // the accept relies on.
        Assert.True(
            VulkanVideoPresenter.IsCompatibleGuestImageViewFormat(
                Format.R8G8B8A8Srgb,
                Format.R8G8B8A8Unorm));
    }
}
