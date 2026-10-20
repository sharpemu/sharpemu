// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("00000001,00000002")]
    public void FindImage_ReusesASameBackingLookupUntilTheIndexChanges(string? descriptor)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));

        var first = Sampled(address);
        first.TraceTextureDescriptor = descriptor;
        var firstId = harness.Find(ref first);
        var created = Sampled(address);
        created.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref created));
        var baseline = harness.Images.LookupMemoCounters;

        var repeated = Sampled(address);
        repeated.TraceTextureDescriptor = descriptor is null ? null : new string(descriptor.ToCharArray());
        Assert.Equal(firstId, harness.Find(ref repeated));
        Assert.Equal(baseline.Hits + 1, harness.Images.LookupMemoCounters.Hits);
        Assert.Equal(first.View, repeated.View);

        var stale = new ResourceSlotIdentifier(firstId.Index, firstId.Generation + 1);
        harness.Images.AddPageOwner(address, stale);
        Assert.True(harness.Images.RemovePageOwner(address, stale));
        var afterChange = Sampled(address);
        afterChange.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref afterChange));
        Assert.Equal(baseline.Hits + 1, harness.Images.LookupMemoCounters.Hits);

        var reusedAgain = Sampled(address);
        reusedAgain.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref reusedAgain));
        Assert.Equal(baseline.Hits + 2, harness.Images.LookupMemoCounters.Hits);
    }

    // The memo is direct-mapped: it pays off only if a frame's distinct textures mostly fit.
    [Fact]
    public void FindImage_RemembersTheLookupsOfAFrameWithManyTextures()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const int Textures = 512;
        var address = harness.MapBacked(Textures * 0x1000UL, ReadWrite);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var index = 0; index < Textures; index++)
            {
                var request = Sampled(address + (ulong)index * 0x1000);
                harness.Find(ref request);
            }
        }

        var baseline = harness.Images.LookupMemoCounters;
        for (var index = 0; index < Textures; index++)
        {
            var request = Sampled(address + (ulong)index * 0x1000);
            harness.Find(ref request);
        }

        Assert.True(harness.Images.LookupMemoCounters.Hits - baseline.Hits >= Textures * 4 / 5);
    }

    [Fact]
    public void FindImage_ADifferentViewIsNotReused()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));

        var first = Sampled(address);
        var firstId = harness.Find(ref first);
        var again = Sampled(address);
        harness.Find(ref again);
        var baseline = harness.Images.LookupMemoCounters;

        var swizzled = Sampled(address);
        swizzled.View = swizzled.View with { Mapping = new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.A) };
        Assert.Equal(firstId, harness.Find(ref swizzled));
        Assert.Equal(ComponentSwizzle.B, swizzled.View.Mapping.R);
        Assert.Equal(baseline.Hits, harness.Images.LookupMemoCounters.Hits);
    }
}
