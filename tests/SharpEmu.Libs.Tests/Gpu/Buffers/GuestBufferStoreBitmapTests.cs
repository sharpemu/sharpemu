// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// A store a shader makes through a run-time device address changes the cache buffer alone; the scan of the store
// bitmap gives the page to the GPU so that no later upload puts the older guest bytes back.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferStoreBitmapTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = GuestBufferCache.CachingPageSize;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private readonly HeadlessVulkan? _vulkan;

    public GuestBufferStoreBitmapTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // Sets the bit of each page in one bitmap of the fault buffer (0 fault, 1 store) and runs the scan.
    private static void ScanBitmap(CacheHarness harness, uint bitmap, params ulong[] pages)
    {
        var bitmapBytes = GuestBufferCache.CachingPageCount / 8;
        var word = pages[0] / 32;
        var bits = new uint[(pages[^1] / 32 - word) + 1];
        foreach (var page in pages)
        {
            bits[(page / 32) - word] |= 1u << (int)(page % 32);
        }

        harness.Worker.Run(() =>
        {
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var offset = staging.Copy(MemoryMarshal.AsBytes<uint>(bits), 4);
            harness.Cache.FaultBuffer.CopyFrom(harness.Scheduler.Current, staging, offset, bitmap * bitmapBytes + word * 4,
                (ulong)bits.Length * 4, Silk.NET.Vulkan.AccessFlags.HostWriteBit);
            harness.Cache.ProcessFaultBuffer();
            harness.Scheduler.Finish();
        });
    }

    // Writes bytes into a cache buffer without telling the host, as a shader store does.
    private static void StoreIntoBuffer(CacheHarness harness, GpuBuffer buffer, ulong offset, byte[] bytes) =>
        harness.Worker.Run(() =>
        {
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var source = staging.Copy(bytes, 4);
            buffer.CopyFrom(harness.Scheduler.Current, staging, source, offset, (ulong)bytes.Length, Silk.NET.Vulkan.AccessFlags.HostWriteBit);
        });

    // The report gives the page to the GPU: the guest CPU then reads the stored bytes, and the scan clears the bit.
    [Fact]
    public void AStoreBitmapPageTurnsGpuOwned_AndTheGuestCpuReadsTheStoredBytes()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address + 0x100, [1, 2, 3, 4]);
        // Two pages: a single clean page would be served from the stream buffer, without a cache buffer.
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));
        StoreIntoBuffer(harness, buffer, offset + 0x100, [0x78, 0x56, 0x34, 0x12]);
        var page = address >> GuestBufferCache.CachingPageBits;
        Assert.False(harness.Cache.HasGpuDirtyPages(address, Page));

        ScanBitmap(harness, DeviceAddressPaging.StoreBitmap, page);

        Assert.Equal(1, harness.Cache.StoredBufferPages);
        Assert.Equal((long)(Page / TrackerLayout.PageBytes), harness.Cache.StoredPagesOwned);
        Assert.Equal(0, harness.Cache.StoredPagesShared);
        Assert.Equal(0, harness.Cache.StoredPagesLost);
        Assert.True(harness.Cache.HasGpuDirtyPages(address, Page));
        Assert.False(harness.Cache.HasGpuDirtyPages(address + Page, Page));
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address + 0x100, 4));
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, harness.Read(address + 0x100, 4));
        Assert.All(harness.ReadBack(harness.Cache.FaultBuffer,
            DeviceAddressPaging.StoreBitmap * (GuestBufferCache.CachingPageCount / 8) + page / 32 * 4, 4), value => Assert.Equal(0, value));
        harness.Shutdown();
    }

    // The guest CPU wrote a tracker page after its last upload: nothing says which bytes the shader changed, so
    // that page stays with the CPU and is counted; the other tracker pages of the reported page turn GPU-owned.
    [Fact]
    public void AStoreBitmapPageTheCpuAlsoWroteKeepsTheCpuBytesThere_AndIsCounted()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));
        harness.Cache.InvalidateMemory(address + TrackerLayout.PageBytes, 4);
        Assert.True(harness.Cache.HasCpuDirtyPages(address + TrackerLayout.PageBytes, 4));

        ScanBitmap(harness, DeviceAddressPaging.StoreBitmap, address >> GuestBufferCache.CachingPageBits);

        Assert.Equal(1, harness.Cache.StoredPagesShared);
        Assert.Equal((long)(Page / TrackerLayout.PageBytes) - 1, harness.Cache.StoredPagesOwned);
        Assert.True(harness.Cache.HasCpuDirtyPages(address + TrackerLayout.PageBytes, TrackerLayout.PageBytes));
        Assert.False(harness.Cache.HasGpuDirtyPages(address + TrackerLayout.PageBytes, TrackerLayout.PageBytes));
        Assert.True(harness.Cache.HasGpuDirtyPages(address, TrackerLayout.PageBytes));
        Assert.True(harness.Cache.HasGpuDirtyPages(address + 2 * TrackerLayout.PageBytes, 2 * TrackerLayout.PageBytes));
        harness.Shutdown();
    }

    // A second report of a page the GPU already owns changes nothing, and a report of a page whose buffer is gone
    // is counted as lost.
    [Fact]
    public void AStoreBitmapPageIsTakenOnce_AndAPageWithoutABufferIsCountedLost()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));
        var page = address >> GuestBufferCache.CachingPageBits;

        ScanBitmap(harness, DeviceAddressPaging.StoreBitmap, page);
        ScanBitmap(harness, DeviceAddressPaging.StoreBitmap, page, page + 3);

        Assert.Equal(3, harness.Cache.StoredBufferPages);
        Assert.Equal((long)(Page / TrackerLayout.PageBytes), harness.Cache.StoredPagesOwned);
        Assert.Equal(1, harness.Cache.StoredPagesLost);
        Assert.False(harness.Cache.IsRegionRegistered(address + 3 * Page, Page));
        harness.Shutdown();
    }

    // A fault-bitmap page is not a store: the scan makes its buffer and takes no page for the GPU.
    [Fact]
    public void AFaultBitmapPageIsNotReportedAsAStore()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);

        ScanBitmap(harness, 0, address >> GuestBufferCache.CachingPageBits);

        Assert.True(harness.Cache.IsRegionRegistered(address, Page));
        Assert.Equal(0, harness.Cache.StoredBufferPages);
        Assert.Equal(0, harness.Cache.StoredPagesOwned);
        Assert.False(harness.Cache.HasGpuDirtyPages(address, Page));
        harness.Shutdown();
    }
}
