// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    private static ImageRequest GrowthRequest(ulong address, uint layers, GuestTileMode tile)
    {
        var request = LinearRequest(address, 65536UL * layers, Format.R32Uint, GuestPixelFormat.Bits32UInt,
            GuestImageType.Color2D, new Extent3D(128, 128, 1), layers, 4, 1);
        request.Description.TileMode = tile;
        return request;
    }

    [Theory]
    [InlineData(GuestTileMode.Linear, false)]
    [InlineData(GuestTileMode.RenderTarget, false)]
    [InlineData(GuestTileMode.Linear, true)]
    [InlineData(GuestTileMode.RenderTarget, true)]
    public unsafe void ArrayGrowth_PreservesNativePrefixAndUploadsNewLayer(GuestTileMode tile, bool cpuWrite)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(8 * 65536, ReadWrite);
        var bytes = Enumerable.Repeat((byte)0x33, 3 * 65536).ToArray();
        for (var index = 2 * 65536; index < bytes.Length; index++) bytes[index] = (byte)(index * 37 + (index >> 8));
        harness.Write(address, bytes);
        harness.Write(address + 4 * 65536, bytes);
        var reference = GrowthRequest(address + 4 * 65536, 3, tile);
        var referenceId = harness.Acquire(ref reference);
        var expected = harness.ReadImageBytes(harness.Image(referenceId));
        var original = GrowthRequest(address, 2, tile);
        var sourceId = harness.Acquire(ref original);
        var source = harness.Image(sourceId);
        harness.Worker.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            source.Transition(AccessFlags.TransferWriteBit, null, command);
            var clear = new ClearColorValue { Uint32_0 = 0x77777777 };
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 2);
            harness.Vulkan.Vk.CmdClearColorImage(command, source.Backing.Handle, ImageLayout.General, &clear, 1, &range);
        });
        harness.MarkGpuWritten(sourceId);
        if (cpuWrite)
        {
            for (ulong offset = 0; offset < 2 * 65536; offset += 4096) harness.WriteFault(address + offset);
            harness.Write(address, Enumerable.Repeat((byte)0x55, 2 * 65536).ToArray());
        }

        var grown = GrowthRequest(address, 3, tile);
        Assert.True(GuestImageCache.CanUploadArrayGrowthSuffix(original.Description, grown.Description));
        var grownId = harness.Acquire(ref grown);
        Assert.NotEqual(sourceId, grownId);
        Assert.False(source.Registered);
        Assert.True(source.Backing.Exists);
        var pixels = harness.ReadImageBytes(harness.Image(grownId));
        Assert.Equal(3 * 65536, pixels.Length);
        Assert.True(pixels.Take(2 * 65536).All(value => value == (cpuWrite ? (byte)0x55 : (byte)0x77)));
        Assert.True(pixels.AsSpan(2 * 65536).SequenceEqual(expected.AsSpan(2 * 65536)));
        harness.Finish();
        Assert.False(source.Backing.Exists);
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void ArrayGrowth_RejectsUnprovenLayouts()
    {
        var source = GrowthRequest(0x10000, 2, GuestTileMode.RenderTarget).Description;
        var target = GrowthRequest(0x10000, 3, GuestTileMode.RenderTarget).Description;
        Assert.True(GuestImageCache.CanUploadArrayGrowthSuffix(source, target));
        var changed = target;
        changed.Pitch++;
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.Resources = changed.Resources with { Levels = 2 };
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.TileMode = GuestTileMode.Linear;
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.Metadata.Kind = MetadataKind.Dcc; changed.Metadata.Range = new(0x80000, 4096);
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.Type = GuestImageType.Color3D;
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.Samples = 2;
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
        changed = target; changed.Data = new(target.Data.Address, target.Data.Size - 4);
        Assert.False(GuestImageCache.CanUploadArrayGrowthSuffix(source, changed));
    }

    [Fact]
    public void ArrayGrowth_FailedInitializationKeepsSourceRegistered()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(4 * 65536, ReadWrite);
        var request = GrowthRequest(address, 2, GuestTileMode.Linear);
        var sourceId = harness.Acquire(ref request);
        var source = harness.Image(sourceId);
        harness.MarkGpuWritten(sourceId);
        source.Binding.IsBound = true;
        var bad = GrowthRequest(address, 3, GuestTileMode.Linear).Description;
        bad.GuestFormat = (GuestPixelFormat)uint.MaxValue;
        var grow = typeof(GuestImageCache).GetMethod("GrowImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Throws<TargetInvocationException>(() => harness.Worker.Run(() => grow.Invoke(harness.Images, [bad, sourceId])));
        Assert.True(source.Registered);
        Assert.True(source.IsGpuModified);
        Assert.True(source.Backing.Exists);
        Assert.False(source.Binding.NeedsRebind);
        Assert.Equal(sourceId, harness.Find(ref request));
        harness.Finish();
        _vulkan.AssertNoValidationMessages();
    }
}
