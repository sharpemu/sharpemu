// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(65)]
    public void RangeQueriesKeepAllAliasesAndInvalidateAcrossOwnerPages(int count)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x400000, ReadWrite);
        var firstPage = (address + 0xfffff) & ~0xfffffUL;
        var range = Ownership(firstPage + 0xff000, 0x2000);
        var identifiers = new List<ResourceSlotIdentifier>();
        for (var index = 0; index < count; index++) identifiers.Add(harness.Images.InsertImageForTest(range));
        Assert.Equal(identifiers, harness.ImagesInRange(range.Data.Address, range.Data.Size));
        harness.Images.QueryEpoch = uint.MaxValue;
        Assert.Equal(identifiers, harness.ImagesInRange(range.Data.Address, range.Data.Size));
        foreach (var identifier in identifiers) harness.Image(identifier).MarkGpuModified();
        Assert.True(harness.Images.HasGpuModifiedImageBytes(range.Data.Address, range.Data.Size));
        Assert.True(((IGuestImageStore)harness.Images).MarkCpuWrite(range.Data.Address, range.Data.Size));
        foreach (var identifier in identifiers) Assert.True(harness.Image(identifier).IsCpuDirty);
        if (count <= 16)
        {
            Assert.Equal(0, MeasureImageQueryAllocations(() =>
                ((IGuestImageStore)harness.Images).MarkCpuWrite(range.Data.Address, range.Data.Size)));
        }
        Assert.Equal(identifiers, harness.ImagesInRange(range.Data.Address, range.Data.Size));
        foreach (var identifier in identifiers) harness.Image(identifier).ClearGpuModified();
        ((IGuestImageStore)harness.Images).Unregister(range.Data.Address, range.Data.Size);
        Assert.Empty(harness.ImagesInRange(range.Data.Address, range.Data.Size));
        harness.Shutdown();
    }

    [Fact]
    public void DirtyImageQueryMatchesRegionQueryAndDoesNotAllocate()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var owner = harness.Images.InsertImageForTest(Ownership(address + 0x100, 0x100));
        var image = harness.Image(owner);
        Assert.False(harness.Images.HasGpuModifiedImageBytes(address + 0x100, 4));
        image.MarkGpuModified();
        var stale = new ResourceSlotIdentifier(owner.Index, owner.Generation + 1);
        harness.Images.AddPageOwner(address, stale);
        foreach (var queryAddress in new[] { address, address + 0x100, address + 0x1fc, address + 0x200, address + 0x1000 })
            Assert.Equal(harness.Images.QueryRegion(queryAddress, 4).GpuImageBytes,
                harness.Images.HasGpuModifiedImageBytes(queryAddress, 4));
        Assert.False(harness.Images.HasGpuModifiedImageBytes(address, 0));
        Assert.False(harness.Images.HasGpuModifiedImageBytes(ulong.MaxValue, 4));
        image.DepthOwner = owner;
        Assert.False(harness.Images.HasGpuModifiedImageBytes(address + 0x100, 4));
        image.DepthOwner = ResourceSlotIdentifier.Invalid;
        Assert.Equal(0, MeasureImageQueryAllocations(Query));
        Assert.True(harness.Images.RemovePageOwner(address, stale));
        image.ClearGpuModified();
        harness.Shutdown();

        void Query()
        {
            _ = harness.Images.QueryRegion(address + 0x100, 4);
            _ = harness.Images.QueryRegion(address + 0x2000, 4);
            _ = harness.Images.HasGpuModifiedImageBytes(address + 0x100, 4);
            _ = harness.Images.HasGpuModifiedImageBytes(address + 0x2000, 4);
            _ = ((IGuestImageStore)harness.Images).MarkCpuWrite(address + 0x2000, 4);
        }
    }

    // Use a dedicated thread so test-runner work cannot enter the allocation sample.
    private static long MeasureImageQueryAllocations(Action query)
    {
        long allocated = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (var warmup = 0; warmup < 100; warmup++) query();
                allocated = 0;
                for (var iteration = 0; iteration < 1000; iteration++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    query();
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return allocated;
    }
}
