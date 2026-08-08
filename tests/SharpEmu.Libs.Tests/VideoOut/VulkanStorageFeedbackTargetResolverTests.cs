// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanStorageFeedbackTargetResolverTests
{
    private const ulong TargetAddress = 0x1044_500000;

    [Fact]
    public void ZeroWriteSingleTargetAliasUsesCompatibilityAttachment()
    {
        var target = Target();

        var supported = VulkanStorageFeedbackTargetResolver.TryResolve(
            [target],
            [Texture(TargetAddress, isStorage: true)],
            [Blend(writeMask: 0)],
            out var resolved,
            out var usedCompatibilityAttachment,
            out var unsupportedAddress);

        Assert.True(supported);
        Assert.True(usedCompatibilityAttachment);
        Assert.Equal(0UL, unsupportedAddress);
        Assert.Equal(0UL, resolved[0].Address);
        Assert.Equal(target.Width, resolved[0].Width);
        Assert.Equal(target.Height, resolved[0].Height);
        Assert.Equal(target.Format, resolved[0].Format);
        Assert.Equal(target.NumberType, resolved[0].NumberType);
    }

    [Fact]
    public void WritableColorAliasRemainsUnsupported()
    {
        var supported = VulkanStorageFeedbackTargetResolver.TryResolve(
            [Target()],
            [Texture(TargetAddress, isStorage: true)],
            [Blend(writeMask: 0xF)],
            out var resolved,
            out var usedCompatibilityAttachment,
            out var unsupportedAddress);

        Assert.False(supported);
        Assert.False(usedCompatibilityAttachment);
        Assert.Equal(TargetAddress, unsupportedAddress);
        Assert.Equal(TargetAddress, resolved[0].Address);
    }

    [Fact]
    public void SampledAliasDoesNotNeedCompatibilityAttachment()
    {
        var supported = VulkanStorageFeedbackTargetResolver.TryResolve(
            [Target()],
            [Texture(TargetAddress, isStorage: false)],
            [Blend(writeMask: 0xF)],
            out var resolved,
            out var usedCompatibilityAttachment,
            out var unsupportedAddress);

        Assert.True(supported);
        Assert.False(usedCompatibilityAttachment);
        Assert.Equal(0UL, unsupportedAddress);
        Assert.Equal(TargetAddress, resolved[0].Address);
    }

    [Fact]
    public void AliasedMrtRemainsUnsupportedEvenWhenColorWritesAreDisabled()
    {
        const ulong otherAddress = 0x1042_F00000;

        var supported = VulkanStorageFeedbackTargetResolver.TryResolve(
            [Target(), Target(otherAddress)],
            [Texture(TargetAddress, isStorage: true)],
            [Blend(writeMask: 0), Blend(writeMask: 0)],
            out _,
            out var usedCompatibilityAttachment,
            out var unsupportedAddress);

        Assert.False(supported);
        Assert.False(usedCompatibilityAttachment);
        Assert.Equal(TargetAddress, unsupportedAddress);
    }

    private static GuestRenderTarget Target(ulong address = TargetAddress) =>
        new(address, 2048, 2048, Format: 10, NumberType: 6);

    private static GuestDrawTexture Texture(ulong address, bool isStorage) =>
        new(
            address,
            2048,
            2048,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: isStorage);

    private static GuestBlendState Blend(uint writeMask) =>
        GuestBlendState.Default with { WriteMask = writeMask };
}
