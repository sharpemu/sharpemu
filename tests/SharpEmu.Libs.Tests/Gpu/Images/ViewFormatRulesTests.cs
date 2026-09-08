// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class ViewFormatRulesTests
{
    [Theory]
    [InlineData(Format.R8G8B8A8Unorm, Format.B8G8R8A8Srgb, true)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R32Uint, true)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R16G16B16A16Sfloat, false)]
    [InlineData(Format.BC1RgbaUnormBlock, Format.R32G32Uint, true)]
    [InlineData(Format.R32G32Uint, Format.BC1RgbaUnormBlock, false)]
    [InlineData(Format.BC7UnormBlock, Format.R32G32B32A32Uint, true)]
    [InlineData(Format.D32Sfloat, Format.D32Sfloat, true)]
    [InlineData(Format.D32Sfloat, Format.R32Sfloat, false)]
    [InlineData(Format.R8Unorm, Format.Undefined, false)]
    public void Compatibility_IsTheSubsetRule(Format baseFormat, Format viewFormat, bool expected) =>
        Assert.Equal(expected, ViewFormatRules.AreCompatible(baseFormat, viewFormat));

    [Fact]
    public void Aspects_FollowTheFormat()
    {
        Assert.Equal(ImageAspectFlags.DepthBit, ViewFormatRules.DepthAspects(Format.D32Sfloat));
        Assert.Equal(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, ViewFormatRules.DepthAspects(Format.D24UnormS8Uint));
        Assert.Equal(ImageAspectFlags.StencilBit, ViewFormatRules.FullAspects(Format.S8Uint));
        Assert.Equal(ImageAspectFlags.DepthBit, ViewFormatRules.FullAspects(Format.X8D24UnormPack32));
        Assert.Equal(ImageAspectFlags.ColorBit, ViewFormatRules.FullAspects(Format.R8G8B8A8Unorm));

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ViewFormatRules.DepthAspects(Format.R8Unorm));
        Assert.Contains(fatal.Messages, message => message.Contains("not a depth/stencil format"));
    }

    [Fact]
    public void Swizzles_DecodeToComponentMappings()
    {
        var identity = ViewFormatRules.PackDestinationSelect(4, 5, 6, 7);
        Assert.True(ViewFormatRules.IsValidSwizzle(identity));
        Assert.False(ViewFormatRules.IsValidSwizzle(ViewFormatRules.PackDestinationSelect(2, 5, 6, 7)));
        Assert.False(ViewFormatRules.IsValidSwizzle(0x1000));
        Assert.Equal(5u, ViewFormatRules.DestinationSelect(identity, 1));

        var mapping = ViewFormatRules.ComponentMapping(ViewFormatRules.PackDestinationSelect(6, 5, 4, 1));
        Assert.Equal((ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.One), (mapping.R, mapping.G, mapping.B, mapping.A));
        Assert.True(ViewFormatRules.IsComponentSwizzle(ComponentSwizzle.A));
        Assert.False(ViewFormatRules.IsComponentSwizzle((ComponentSwizzle)7));
    }

    [Fact]
    public void ColorViews_AreSelectedOrRejected()
    {
        var identity = ViewFormatRules.PackDestinationSelect(4, 5, 6, 7);
        Assert.Equal(identity, ViewFormatRules.SelectSampledColorView(Format.R8G8B8A8Unorm, Format.B8G8R8A8Unorm, identity));
        Assert.Equal(Format.R8G8B8A8Unorm, ViewFormatRules.SrgbStorageFormat(Format.B8G8R8A8Srgb));
        Assert.Equal(Format.Undefined, ViewFormatRules.SrgbStorageFormat(Format.R8G8B8A8Unorm));
        ViewFormatRules.ValidateStorageColorView(Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm, identity);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ViewFormatRules.SelectSampledColorView(Format.R8G8B8A8Unorm, Format.R16G16B16A16Sfloat, identity));
        Assert.Throws<SchedulerFatalException>(() => ViewFormatRules.SelectSampledColorView(Format.R8G8B8A8Unorm, Format.R8G8B8A8Unorm, 0x1000));
        Assert.Throws<SchedulerFatalException>(() => ViewFormatRules.ValidateStorageColorView(Format.R8G8B8A8Unorm, Format.R16Unorm, identity));
        Assert.Equal(3, fatal.Messages.Count);
        Assert.Contains(fatal.Messages, message => message.Contains("sampled color view is not supported"));
        Assert.Contains(fatal.Messages, message => message.Contains("storage color view is not supported"));
    }

    [Fact]
    public void DepthViews_AcceptOnlyTheReplicatedSwizzles()
    {
        var replicated = ViewFormatRules.PackDestinationSelect(4, 4, 4, 4);
        Assert.True(ViewFormatRules.IsSupportedSampledDepthView(Format.D32Sfloat, Format.R32Sfloat, replicated));
        Assert.True(ViewFormatRules.IsSupportedSampledDepthView(Format.D16Unorm, Format.R16Unorm, ViewFormatRules.PackDestinationSelect(4, 0, 0, 1)));
        Assert.False(ViewFormatRules.IsSupportedSampledDepthView(Format.D32Sfloat, Format.R32Sfloat, ViewFormatRules.PackDestinationSelect(4, 5, 6, 7)));
        Assert.False(ViewFormatRules.IsSupportedSampledDepthView(Format.R32Sfloat, Format.R32Sfloat, replicated));
        Assert.Equal(replicated, ViewFormatRules.SelectSampledDepthView(Format.D32SfloatS8Uint, Format.R32Uint, replicated));
        Assert.True(ViewFormatRules.IsDepthCompatible(Format.R16Unorm));
        Assert.True(ViewFormatRules.IsStencilViewFormat(Format.R8Uint));

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ViewFormatRules.SelectSampledDepthView(Format.D32Sfloat, Format.R8Unorm, replicated));
        Assert.Contains(fatal.Messages, message => message.Contains("sampled depth view is not supported"));
    }

    [Fact]
    public void ViewDescriptions_CompareEveryField()
    {
        var mapping = new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.G, ComponentSwizzle.B, ComponentSwizzle.A);
        var left = ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm, Mapping = mapping };
        var same = ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm, Mapping = mapping };
        var swizzled = left with { Mapping = new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.A) };
        Assert.Equal(left, same);
        Assert.Equal(left.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(left, swizzled);
        Assert.NotEqual(left, left with { LevelCount = 2 });
        Assert.NotEqual(left, left with { Usage = ImageUsageFlags.StorageBit });
    }
}
