// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferCacheTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = GuestBufferCache.CachingPageSize;

    [Fact]
    public void ImageUploadFailureIdentifiesAHoleBetweenBackedEndpoints()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        var (address, granule, _) = harness.MapBackedSandwich();
        var size = 3 * granule;
        harness.Worker.Run(() =>
        {
            var failure = Assert.Throws<SchedulerFatalException>(() =>
                harness.Cache.ObtainBufferForImage(address, size));
            Assert.Contains("Could not read the mapped guest image backing", failure.Message);
            Assert.Contains($"address=0x{address:X16} size=0x{size:X16}", failure.Message);
            Assert.Contains("range_backed=False first_byte_backed=True last_byte_backed=True", failure.Message);
            Assert.True(harness.Memory.IsBackedRange(address, granule));
            Assert.True(harness.Memory.IsBackedRange(address + 2 * granule, granule));
            _ = harness.Cache.ObtainBufferForImage(address, granule);
            harness.Scheduler.Finish();
        });
    }

    [Fact]
    public void ImageUploadFailureDistinguishesStagingCapacityFromBackingReads()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        var address = harness.MapBacked(0x10000, ReadWrite);
        const ulong size = 512UL * 1024 * 1024 + 1;
        harness.Worker.Run(() =>
        {
            var failure = Assert.Throws<SchedulerFatalException>(() =>
                harness.Cache.ObtainBufferForImage(address, size));
            Assert.Contains("Cannot reserve image staging space", failure.Message);
            Assert.Contains($"address=0x{address:X16} size=0x{size:X16}", failure.Message);
            Assert.Contains("capacity=0x0000000020000000", failure.Message);
            Assert.DoesNotContain("Could not read", failure.Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeometryPreparationKeepsWritableGlobalBufferInTheFinalAllocation(bool indexed)
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var globals = new[] { new GuestMemoryBuffer(address, [], 0, 0x8000, false, Writable: true) };
            var vertex = new GuestVertexBuffer(0, 4, 0, 0, address + 0x4000, 16, 0, [], 0x10000, false);
            var indices = new GuestIndexBuffer([], 0x10000, false, false) { GuestAddress = address + 0x4000 };
            VulkanVideoPresenter.PrepareCachedBufferAllocations(harness.Cache, globals,
                indexed ? [] : [vertex], indexed ? indices : null);
            var (global, offset) = harness.Cache.ObtainBuffer(address, 0x8000, true);
            var (geometry, _) = harness.Cache.ObtainBuffer(address + 0x4000, 0x10000, false);
            Assert.Same(global, geometry);
            global.Fill(offset, 4, 0x12345678);
        });
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address, 4));
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, harness.Read(address, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuReadAcrossTrackingBoundaryDownloadsTheDirtyPart(bool dirtyBeforeBoundary)
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong regionSize = 0x400000;
        var address = harness.MapBacked(3 * regionSize, ReadWrite);
        var boundary = (address + regionSize) & ~(regionSize - 1);
        var dirtyAddress = dirtyBeforeBoundary ? boundary - 4 : boundary;
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(dirtyAddress, 4, isWritten: true);
            buffer.Fill(offset, 4, 0x12345678);
        });

        Assert.False(harness.Store.DownloadToCpu(boundary - 2, 4));
        Assert.True(harness.Cache.HasGpuDirtyPages(boundary - 2, 4));
        Assert.True(harness.Cache.TrySynchronizeCpuRead(boundary - 2, 4));
        Assert.False(harness.Cache.HasGpuDirtyPages(boundary - 2, 4));
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, harness.Read(dirtyAddress, 4));
    }

    [Fact]
    public void OverlappingPreparedBindingsUseTheFinalCacheAllocation()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Worker.Run(() =>
        {
            harness.Scheduler.Begin(new SubmissionContext());
            VulkanVideoPresenter.PrepareCachedBufferAllocations(harness.Cache,
            [
                new GuestMemoryBuffer(address, [], 0, 0x8000, false),
                new GuestMemoryBuffer(address + 0x4000, [], 0, 0x10000, false),
            ]);
            var first = harness.Cache.ObtainBuffer(address, 0x8000, false);
            var second = harness.Cache.ObtainBuffer(address + 0x4000, 0x10000, false);
            harness.Scheduler.Finish();
            Assert.NotEqual(0UL, first.Buffer.Handle.Handle);
            Assert.Same(first.Buffer, second.Buffer);
        });
    }

    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private readonly HeadlessVulkan? _vulkan;

    public GuestBufferCacheTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // Forwards to the real backing but refuses alias writes when asked to.
    private sealed class FailingBacking(IGuestBackedSpace inner) : IGuestBackedSpace
    {
        public bool FailWrites { get; set; }

        public bool TryHoldRange(ulong address, ulong size) => inner.TryHoldRange(address, size);

        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) =>
            inner.TryHoldRangeAtOrAbove(searchStart, size, alignment, out address);

        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) =>
            inner.TryMapBacked(address, size, backingOffset, protection, out failure);

        public bool TryUnmapBacked(ulong address, ulong size) => inner.TryUnmapBacked(address, size);

        public bool TryClearBacking(ulong offset, ulong size) => inner.TryClearBacking(offset, size);

        public bool IsBackedView(ulong address) => inner.IsBackedView(address);

        public bool IsBackedRange(ulong address, ulong size) => inner.IsBackedRange(address, size);

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => !FailWrites && inner.TryWriteBacking(address, data);

        public bool TryReadBacking(ulong address, Span<byte> data) => inner.TryReadBacking(address, data);
    }

    private static byte[] Pattern(int size, byte seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void GpuOwnedIndices_KeepGuestOffsetWithoutPublishingToCpu(int stride)
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x00020001);
        });
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            Assert.True(harness.Cache.HasGpuDirtyPages(address, 0x100));

            var indexAddress = address + 7UL * (ulong)stride;
            var indexLength = 6UL * (ulong)stride;
            var (resident, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(indexAddress, indexLength, false));
            Assert.Equal(7UL * (ulong)stride, offset);
            var gpuBytes = harness.ReadBack(resident, offset, indexLength);
            Assert.Contains(gpuBytes, value => value != 0);

            Assert.True(harness.Store.DownloadToCpu(address, 0x100));
            var cpuBytes = new byte[indexLength];
            Assert.True(harness.Memory.TryRead(indexAddress, cpuBytes));
            Assert.Equal(gpuBytes, cpuBytes);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    [Fact]
    public void ObtainBuffer_CreatesRegistersAndUploadsOnlyCpuDirtyPages()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var version1 = Pattern(0x100, 1);
        harness.Write(address + 0x40, version1);

        // Larger than one caching page, so the request bypasses the stream ring.
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x40, 0x4100, isWritten: false));
        Assert.Equal(1, harness.Cache.BufferCount);
        Assert.Equal(address, buffer.CpuAddress);
        Assert.Equal(2 * Page, buffer.Size);
        Assert.Equal(0x40UL, offset);
        Assert.Equal(buffer.CpuAddress, harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 8)).CpuAddress);
        Assert.Equal(version1, harness.ReadBack(buffer, 0x40, 0x100));
        Assert.False(harness.Cache.HasCpuDirtyPages(address, 0x4140));
        Assert.True(harness.Cache.HasCpuDirtyPages(address + 0x6000, 0x1000));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address + 0x6000));

        // The BDA table maps every 16 KiB page of the buffer to its device address.
        var slice = harness.ReadBack(harness.Cache.BdaPageTableBuffer, (address >> GuestBufferCache.CachingPageBits) * 8, 24);
        Assert.Equal(buffer.DeviceAddress, BitConverter.ToUInt64(slice, 0));
        Assert.Equal(buffer.DeviceAddress + Page, BitConverter.ToUInt64(slice, 8));
        Assert.Equal(0UL, BitConverter.ToUInt64(slice, 16));

        // A CPU write marks the page dirty again and the next obtain uploads the new bytes.
        var version2 = Pattern(0x100, 9);
        Assert.True(harness.Store.MarkCpuWrite(address + 0x40, 8));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address + 0x1000));
        harness.Write(address + 0x40, version2);
        var (again, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x40, 0x4100, isWritten: false));
        Assert.Same(buffer, again);
        Assert.Equal(version2, harness.ReadBack(buffer, 0x40, 0x100));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        harness.Shutdown();
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
    }

    [Fact]
    public void ObtainBuffer_SmallReadOfADirtyPageUsesTheStreamRing()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Pattern(0x80, 3));

        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x80, isWritten: false));

        Assert.Equal(harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream).Handle.Handle, buffer.Handle.Handle);
        Assert.Equal(Pattern(0x80, 3), buffer.Mapped.Slice((int)offset, 0x80).ToArray());
        Assert.Equal(0, harness.Cache.BufferCount);
        Assert.True(harness.Cache.HasCpuDirtyPages(address, Page));
        harness.Shutdown();
    }

    [Fact]
    public void ObtainBuffer_RewrittenLargeReadUsesCurrentBytesFromTheStreamRing()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        const int size = 0x4100;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Pattern(size, 1));
        _ = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, isWritten: false));

        MarkRangeWritten();
        harness.Write(address, Pattern(size, 2));
        _ = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, isWritten: false));

        MarkRangeWritten();
        var latest = Pattern(size, 3);
        harness.Write(address, latest);
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, isWritten: false));
        Assert.Equal(harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream).Handle.Handle, buffer.Handle.Handle);
        Assert.Equal(latest, buffer.Mapped.Slice((int)offset, size).ToArray());
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address + Page));
        harness.Shutdown();

        void MarkRangeWritten()
        {
            Assert.True(harness.Store.MarkCpuWrite(address, size));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PersistentReadTracksHotPagesAndPreservesEachUpload(bool formatted, bool entireRangeHot)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong size = 3 * Page;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var expected = Pattern((int)size, 1);
        harness.Write(address, expected);
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false, formatted));
        var writeAddress = entireRangeHot ? address : address + Page;
        var writeSize = entireRangeHot ? size : Page;

        for (var version = 2; version <= 4; version++)
        {
            Assert.True(harness.Store.MarkCpuWrite(writeAddress, writeSize));
            var changedBytes = Pattern((int)writeSize, (byte)version);
            harness.Write(writeAddress, changedBytes);
            changedBytes.CopyTo(expected, (int)(writeAddress - address));
            var current = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false, formatted));
            Assert.Same(buffer, current.Buffer);
            Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(writeAddress));
            Assert.Equal(expected, harness.ReadBack(buffer, offset, size));

            var repeated = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false, formatted));
            Assert.Same(buffer, repeated.Buffer);
            Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
        }

        harness.Shutdown();
    }

    [Fact]
    public void FullStreamRingFallsBackToTrackedStorageAndAllowsLaterStreaming()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong size = 2 * Page;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false, true));
        for (var version = 1; version <= 2; version++)
        {
            Assert.True(harness.Store.MarkCpuWrite(address, size));
            harness.Write(address, Pattern((int)size, (byte)version));
            _ = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false, true));
        }

        Assert.True(harness.Store.MarkCpuWrite(address, size));
        var expected = Pattern((int)size, 3);
        harness.Write(address, expected);
        var stream = (GpuRingBuffer)harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
        using (stream.RetainContents())
        {
            var current = harness.Worker.Run(() =>
            {
                Assert.True(stream.TryMap(stream.Size, out _));
                stream.Commit();
                return harness.Cache.ObtainBuffer(address, size, false);
            });
            Assert.Same(buffer, current.Buffer);
            Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
            Assert.Equal(expected, harness.ReadBack(current.Buffer, current.Offset, size));
        }

        Assert.True(harness.Store.MarkCpuWrite(address, size));
        expected = Pattern((int)size, 4);
        harness.Write(address, expected);
        var streamed = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false));
        Assert.Same(stream, streamed.Buffer);
        Assert.True(harness.Cache.HasCpuDirtyPages(address, size));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.Equal(expected, harness.ReadBack(streamed.Buffer, streamed.Offset, size));
        harness.Shutdown();
    }

    [Fact]
    public void WrittenObtain_MakesThePageGpuDirtyUntilACpuFaultDownloadsIt()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var hops = 0;

        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address + 0x100, 0x200, isWritten: true);
            buffer.Fill(offset, 0x200, 0x11223344);
        });
        Assert.True(harness.Cache.HasGpuDirtyPages(address, Page));
        Assert.True(harness.Cache.HasGpuDirtyBytes(address + 0x100, 0x200));
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address + Page));

        // The read fault arrives from a foreign thread and returns after the download landed.
        var faultThread = Environment.CurrentManagedThreadId;
        harness.Worker.Post(() => Interlocked.Increment(ref hops));
        Assert.True(harness.Store.DownloadToCpu(address + 0x104, 8));
        Assert.NotEqual(faultThread, harness.Worker.Relay.IsGpuQueueThread ? faultThread : 0);
        Assert.False(harness.Cache.HasGpuDirtyPages(address, Page));
        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x20000));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
        var bytes = harness.Read(address + 0x100, 0x200);
        Assert.All(Enumerable.Range(0, 0x80), i => Assert.Equal(0x11223344u, BitConverter.ToUInt32(bytes, i * 4)));
        Assert.Equal(0, harness.Read(address + 0x300, 4)[0]);

        // A second fault on the clean page needs no hop and still counts as handled.
        Assert.True(harness.Store.DownloadToCpu(address + 0x104, 8));
        Assert.True(harness.Store.MarkCpuWrite(address + 0x104, 8));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.False(harness.Store.DownloadToCpu(address + 0x40_0000, 8));
        harness.Shutdown();
    }

    [Fact]
    public void WriteFault_OnAGpuDirtyPageDownloadsThenReleasesTheWrite()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x40, isWritten: true);
            buffer.Fill(offset, 0x40, 0xCAFEF00D);
        });

        Assert.True(harness.Store.MarkCpuWrite(address + 8, 8));

        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.True(harness.Cache.HasCpuDirtyPages(address, Page));
        Assert.Equal(0xCAFEF00Du, BitConverter.ToUInt32(harness.Read(address + 0x3C, 4)));
        harness.Shutdown();
    }

    [Fact]
    public void WriteHostMemory_LandsInGuestMemoryAndEveryOverlappingBuffer()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x8000, isWritten: false));

        harness.Worker.Run(() => harness.Cache.WriteHostMemory(address + 0x1000, Pattern(0x100, 7)));

        Assert.Equal(Pattern(0x100, 7), harness.Read(address + 0x1000, 0x100));
        Assert.Equal(Pattern(0x100, 7), harness.ReadBack(buffer, 0x1000, 0x100));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ManagedWrite_InvalidatesTrackedPagesAcrossUntrackedRegions(int trackedRegionIndex, bool gpuWritten)
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong regionBytes = TrackerLayout.BlockBytes;
        var address = harness.MapBacked(3 * regionBytes, ReadWrite);
        var trackedAddress = address + (ulong)trackedRegionIndex * regionBytes + 0x10000;
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(trackedAddress, 0x8000, isWritten: gpuWritten);
            if (gpuWritten)
                buffer.Fill(offset, 0x8000, 0x11223344);
        });

        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var replacement = new byte[checked((int)(3 * regionBytes))];
            Array.Fill(replacement, (byte)0x5A);
            Assert.True(harness.Memory.TryWrite(address, replacement));
            Assert.False(harness.Cache.HasGpuDirtyPages(trackedAddress, 0x8000));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(trackedAddress));
            Assert.True(harness.Store.DownloadToCpu(trackedAddress, 8));
            Assert.Equal(0x5A5A5A5Au, BitConverter.ToUInt32(harness.Read(trackedAddress, 4)));
            var (reuploaded, offset) = harness.Worker.Run(() =>
                harness.Cache.ObtainBuffer(trackedAddress, 0x8000, isWritten: false));
            Assert.Equal(0x5A5A5A5Au, BitConverter.ToUInt32(harness.ReadBack(reuploaded, offset, 4)));
            harness.Shutdown();
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    [Fact]
    public void FillAndCopy_InvalidateImageBytesAndTexelObtainsReadTheImage()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);

        harness.Worker.Run(() => harness.Cache.FillBuffer(0x100, 0x40, 0x5A5A5A5A, isGds: true));
        var gds = harness.ReadBack(harness.Cache.GdsBuffer, 0x100, 0x40);
        Assert.All(gds, value => Assert.Equal(0x5A, value));

        // A host fill over image bytes lands in guest memory and marks the image dirty.
        harness.Write(address, Pattern(0x10, 0x01));
        var request = Color32(address, 4);
        var imageIdentifier = harness.Acquire(ref request);
        Assert.False(harness.Image(imageIdentifier).IsCpuDirty);
        harness.Worker.Run(() => harness.Cache.FillBuffer(address, 0x10, 0x11111111, isGds: false));
        Assert.True(harness.Image(imageIdentifier).IsDefinitelyCpuDirty);
        Assert.Equal(Bytes(0x11111111u, 0x11111111u, 0x11111111u, 0x11111111u), harness.Read(address, 0x10));
        Assert.Equal(imageIdentifier, harness.Acquire(ref request));
        Assert.False(harness.Image(imageIdentifier).IsCpuDirty);
        Assert.Equal(Bytes(0x11111111u, 0x11111111u, 0x11111111u, 0x11111111u), harness.ReadImageBytes(harness.Image(imageIdentifier)));

        // A GPU copy from GDS into the image bytes leaves the result in the buffer store.
        harness.Worker.Run(() => harness.Cache.CopyBuffer(address, 0x100, 0x10, dstGds: false, srcGds: true));
        Assert.True(harness.Image(imageIdentifier).IsBufferModified);
        Assert.True(harness.Cache.HasGpuDirtyBytes(address, 0x10));
        Assert.Equal(imageIdentifier, harness.Acquire(ref request));
        Assert.False(harness.Image(imageIdentifier).IsBufferModified);
        Assert.Equal(Bytes(0x5A5A5A5Au, 0x5A5A5A5Au, 0x5A5A5A5Au, 0x5A5A5A5Au), harness.ReadImageBytes(harness.Image(imageIdentifier)));

        // A GPU-written image serves a texel obtain of its range through the image store; the
        // obtain is larger than a caching page so it does not take the stream ring.
        var texelAddress = address + 0x8000;
        harness.Write(texelAddress, Pattern(0x10, 0x02));
        var texelRequest = Color32(texelAddress, 4);
        var texelImageIdentifier = harness.Acquire(ref texelRequest);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(texelAddress, 0x10, 0x22222222u)));
        Assert.True(harness.Image(texelImageIdentifier).IsGpuModified);
        var (texel, texelOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(texelAddress, 0x4100, isWritten: false, isTexelBuffer: true));
        Assert.True(harness.Image(texelImageIdentifier).IsGpuModified);
        Assert.Equal(Bytes(0x22222222u, 0x22222222u, 0x22222222u, 0x22222222u), harness.ReadBack(texel, texelOffset, 0x10));
        Assert.False(harness.Cache.HasGpuDirtyBytes(texelAddress, 0x10));
        harness.Shutdown();
    }

    [Fact]
    public void SynchronizeBuffersInRange_UploadsDirtyPagesOfEveryBufferInTheRange()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x8000, isWritten: false));
        Assert.True(harness.Store.MarkCpuWrite(address + 0x4000, 8));
        harness.Write(address + 0x4000, Pattern(0x10, 5));

        harness.Worker.Run(() => harness.Cache.PrepareBda([new GuestSpan(address, 0x20000)]));

        Assert.False(harness.Cache.HasCpuDirtyPages(address, 0x8000));
        Assert.Equal(Pattern(0x10, 5), harness.ReadBack(buffer, 0x4000, 0x10));
        harness.Shutdown();
    }

    [Fact]
    public void DeviceAddressPreparationTracksHotWritesAndPreservesStreamedReads()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        const ulong size = 0x8000;
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false));
        for (var write = 1; write <= 3; write++)
        {
            Assert.True(harness.Store.MarkCpuWrite(address, size));
            var expected = Pattern((int)size, (byte)write);
            harness.Write(address, expected);
            harness.Worker.Run(() =>
            {
                harness.Cache.PrepareBda([new GuestSpan(address, size)]);
                Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
                Assert.False(harness.Cache.HasGpuDirtyPages(address, size));
                harness.Cache.PrepareBda([new GuestSpan(address, size)]);
            });
            Assert.Equal(expected, harness.ReadBack(buffer, offset, size));
        }

        Assert.True(harness.Store.MarkCpuWrite(address, size));
        var latest = Pattern((int)size, 4);
        harness.Write(address, latest);
        var (stream, streamOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, false));
        Assert.NotSame(buffer, stream);
        Assert.True(harness.Cache.HasCpuDirtyPages(address, size));
        Assert.Equal(latest, harness.ReadBack(stream, streamOffset, size));
        harness.Shutdown();
    }

    [Fact]
    public async Task CleanDeviceAddressSweepKeepsGpuOwnershipAndObservesTheNextCpuWrite()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        const ulong size = 0x8000;
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, size, true));
        harness.Worker.Run(() =>
        {
            buffer.Fill(offset, size, 0x12345678);
            harness.Cache.PrepareBda([new GuestSpan(address, size)]);
            Assert.True(harness.Cache.HasGpuDirtyPages(address, size));
        });
        Assert.Equal(Bytes(0x12345678u), harness.ReadBack(buffer, offset, 4));
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address, size));
        harness.Worker.Run(() => harness.Cache.PrepareBda([new GuestSpan(address, size)]));

        var expected = Pattern((int)size, 7);
        await Task.Run(() =>
        {
            Assert.True(harness.Store.MarkCpuWrite(address, size));
            harness.Write(address, expected);
        });
        harness.Worker.Run(() => harness.Cache.PrepareBda([new GuestSpan(address, size)]));
        Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
        Assert.Equal(expected, harness.ReadBack(buffer, offset, size));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceAddressPreparationKeepsCleanBuffersResident(bool aggressiveCollection)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Worker.Run(() =>
        {
            harness.Cache.RunGarbageCollector();
            _ = harness.Cache.ObtainBuffer(address, 0x8000, false);
            harness.Cache.SetCollectionThresholds(1, aggressiveCollection ? 1UL : ulong.MaxValue);
            for (var collection = 0; collection < 170; collection++)
            {
                harness.Cache.PrepareBda([new GuestSpan(address, 0x20000)]);
                harness.Cache.RunGarbageCollector();
                Assert.True(harness.Cache.IsRegionRegistered(address, 0x8000));
                harness.Scheduler.Finish();
            }

            for (var collection = 0; collection < 161; collection++)
                harness.Cache.RunGarbageCollector();
            Assert.False(harness.Cache.IsRegionRegistered(address, 0x8000));
        });
        harness.Shutdown();
    }

    [Fact]
    public void GarbageCollector_DownloadsDirtyBuffersAndUntracksCleanOnes()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var clean = harness.MapBacked(0x10000, ReadWrite);
        var dirty = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            _ = harness.Cache.ObtainBuffer(clean, 0x4100, isWritten: false);
            var (buffer, offset) = harness.Cache.ObtainBuffer(dirty, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x0BADF00D);
        });
        Assert.Equal(2, harness.Cache.BufferCount);

        // Below the trigger nothing happens; above it only clean buffers retire.
        harness.Worker.Run(() => harness.Cache.RunGarbageCollector());
        Assert.Equal(2, harness.Cache.BufferCount);
        harness.Cache.SetCollectionThresholds(1, ulong.MaxValue);
        harness.Worker.Run(() =>
        {
            for (var tick = 0; tick < 161; tick++)
            {
                harness.Cache.RunGarbageCollector();
            }
        });
        Assert.Equal(1, harness.Cache.BufferCount);
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(clean));
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(dirty));

        // The critical threshold downloads dirty buffers, then erases them at once.
        harness.Cache.SetCollectionThresholds(1, 1);
        harness.Worker.Run(() => harness.Cache.RunGarbageCollector());
        Assert.Equal(0, harness.Cache.BufferCount);
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(dirty));
        Assert.Equal(0x0BADF00Du, BitConverter.ToUInt32(harness.Read(dirty + 0xFC, 4)));
        Assert.False(harness.Cache.HasGpuDirtyBytes(dirty, 0x10000));
        harness.Shutdown();
    }

    [Fact]
    public async Task Shutdown_DownloadsEveryGpuResultAndRestoresTheLedgerProtection()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var writable = harness.MapBacked(0x10000, ReadWrite);
        var readOnly = harness.MapBacked(0x10000, GuestPageProtection.Read);
        var executable = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Execute);
        var noAccess = harness.MapBacked(0x10000, GuestPageProtection.None);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(writable, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x600DF00D);
            _ = harness.Cache.ObtainBuffer(readOnly, 0x4100, isWritten: false);
            _ = harness.Cache.ObtainBuffer(executable, 0x4100, isWritten: false);
            _ = harness.Cache.ObtainBuffer(noAccess, 0x100, isWritten: true);
        });
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(writable));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(readOnly));
        Assert.Equal(HostPageProtection.ReadExecute, harness.Protection(executable));
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(noAccess));

        // A fault that races closure waits for the drain and reports coverage only afterwards.
        harness.Worker.Relay.StopAcceptingWork();
        var racing = Task.Run(() => harness.Store.DownloadToCpu(writable + 8, 8));
        Assert.False(await CompletesWithin(racing, 100));
        harness.Worker.Run(() =>
        {
            harness.Cache.Shutdown();
            harness.Gpu.AttachStores(null, null);
            harness.Scheduler.Shutdown();
            harness.Gpu.AttachGpuQueue(null, null);
        });
        Assert.True(await racing);

        Assert.Equal(0, harness.Cache.BufferCount);
        Assert.Equal(0x600DF00Du, BitConverter.ToUInt32(harness.Read(writable + 0xFC, 4)));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(writable));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(readOnly));
        Assert.Equal(HostPageProtection.ReadExecute, harness.Protection(executable));
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(noAccess));
    }

    [Fact]
    public async Task Shutdown_ThatCannotWriteTheBackingFailsAndReleasesWaitersWithFalse()
    {
        if (_vulkan is null) return;
        FailingBacking backing = null!;
        using var harness = new CacheHarness(_vulkan, memory => backing = new FailingBacking(memory));
        using var fatal = new FatalScope();
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 1);
        });

        backing.FailWrites = true;
        harness.Worker.Relay.StopAcceptingWork();
        var racing = Task.Run(() => harness.Store.DownloadToCpu(address + 8, 8));
        Assert.False(await CompletesWithin(racing, 100));
        Assert.Throws<SchedulerFatalException>(harness.Shutdown);
        Assert.Contains(fatal.Messages, message => message.StartsWith("Could not write the required direct backing", StringComparison.Ordinal));

        Assert.False(await racing);
    }

    [Fact]
    public void WrittenObtain_OnAPrivateSpanIsRefusedBeforeAnyPageChanges()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        var address = harness.MapPrivate(0x10000);

        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x100, isWritten: false));
        Assert.Equal(1, harness.Cache.BufferCount);
        harness.Worker.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Cache.ObtainBuffer(address, 0x100, isWritten: true)));

        Assert.Contains(fatal.Messages, message => message.StartsWith("Could not write the required direct backing", StringComparison.Ordinal));
        Assert.False(harness.Cache.HasGpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
        Assert.NotNull(buffer);
        harness.Shutdown();
    }

    [Fact]
    public void WrittenObtain_AcrossABackingGapIsRefusedEvenWhenBothEndsAreBacked()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        var (first, gap, last) = harness.MapBackedSandwich();
        Assert.Equal(first + gap, last - gap);

        harness.Worker.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Cache.ObtainBuffer(first + gap - 0x10, 0x20 + gap, isWritten: true)));

        Assert.Contains(fatal.Messages, message => message.StartsWith("Could not write the required direct backing", StringComparison.Ordinal));
        Assert.Equal(0, harness.Cache.BufferCount);
        Assert.False(harness.Cache.HasGpuDirtyPages(first, 3 * gap));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(first));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(last));
        harness.Shutdown();
    }

    [Fact]
    public void CpuWriteFault_HopsExactlyOnceAndTheDownloadNeverReentersTheStore()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x22222222);
        });
        var storeCalls = 0;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var hops = 0;
            harness.Worker.Post(() => hops++);
            Assert.True(harness.Store.MarkCpuWrite(address + 16, 8));
            harness.Worker.Run(() => storeCalls = hops);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        Assert.Equal(1, storeCalls);
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        Assert.Equal(0x22222222u, BitConverter.ToUInt32(harness.Read(address + 0x20, 4)));
        harness.Shutdown();
    }

    [Fact]
    public void ManagedWrite_MarksThePageDirtyAndDownloadsGpuResultsFirst()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x4100, isWritten: false));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

            // A managed write to a watched page: the store marks it dirty before the copy lands.
            Assert.True(harness.Memory.TryWrite(address + 0x40, Pattern(0x10, 2)));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
            Assert.True(harness.Cache.HasCpuDirtyPages(address, Page));
            Assert.Equal(Pattern(0x10, 2), harness.Read(address + 0x40, 0x10));
            var (again, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x4100, isWritten: false));
            Assert.Same(buffer, again);
            Assert.Equal(Pattern(0x10, 2), harness.ReadBack(buffer, 0x40, 0x10));

            // A managed write to a GPU-dirty page downloads the GPU result first, then lands.
            harness.Worker.Run(() =>
            {
                var (written, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
                written.Fill(offset, 0x100, 0x33333333);
            });
            Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));
            Assert.True(harness.Memory.TryWrite(address + 0x10, Pattern(4, 9)));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
            Assert.Equal(Pattern(4, 9), harness.Read(address + 0x10, 4));
            Assert.Equal(0x33333333u, BitConverter.ToUInt32(harness.Read(address + 0x14, 4)));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x10000));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
    }

    [Fact]
    public void Unregister_RacingAWrittenObtainDrainsThroughTheWorkerBeforeUntracking()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x44444444);
        });
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));

        harness.Gpu.Unregister(address, 0x10000);

        Assert.False(harness.Gpu.Covers(address, 0x10000));
        Assert.Equal(0x44444444u, BitConverter.ToUInt32(harness.Read(address + 0xFC, 4)));
        Assert.True(harness.Cache.HasCpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingChangeDrainsRecordedBufferWorkAndPublishesBeforeUnmapping(bool recordInsideChange)
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        ulong writeTick = 0;
        var published = false;
        void RecordWrite()
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x55555555);
            writeTick = harness.Scheduler.CurrentTick;
            harness.Scheduler.QueuePriorityCompletionAction(() => published = true);
        }

        if (!recordInsideChange)
            harness.Worker.Run(RecordWrite);
        harness.Gpu.RunMappingChange(() =>
        {
            if (recordInsideChange)
                RecordWrite();
            harness.Gpu.Unregister(address, 0x10000);
            Assert.True(published);
            Assert.True(harness.Scheduler.IsTickComplete(writeTick));
        });

        Assert.False(harness.Gpu.Covers(address, 0x10000));
        Assert.Equal(0x55555555u, BitConverter.ToUInt32(harness.Read(address + 0xFC, 4)));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        harness.Shutdown();
    }

    // Draw 1 reads version 1, the label completes only after its tick retired, the guest writes
    // version 2 after the label, draw 2 reads version 2; the earlier draw stays live until obtained.
    [Fact]
    public void OrderedActionsConsumeEachVersionInStreamOrder()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var info = _vulkan.DeviceInfo;
            using var first = new GpuBuffer(info, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x100);
            using var second = new GpuBuffer(info, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x100);
            using var labelWritten = new ManualResetEventSlim();
            using var workerBlocked = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            var labelTick = 0UL;
            var labelRetired = false;

            void Draw(GpuBuffer target)
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x4100, isWritten: false);
                target.CopyFrom(harness.Scheduler.Current, buffer, offset, 0, 0x100, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
            }

            // The guest writes version 1 after the draw was queued but before the worker obtains it.
            harness.Worker.Post(() =>
            {
                workerBlocked.Set();
                releaseWorker.Wait();
            });
            harness.Worker.Post(() => Draw(first));
            harness.Worker.Post(() =>
            {
                labelTick = harness.Scheduler.CurrentTick;
                harness.Scheduler.Finish();
                labelRetired = harness.Scheduler.IsTickComplete(labelTick);
                labelWritten.Set();
            });
            workerBlocked.Wait();
            Assert.True(harness.Memory.TryWrite(address, Pattern(0x100, 1)));
            releaseWorker.Set();

            labelWritten.Wait();
            Assert.True(labelRetired);
            Assert.True(harness.Memory.TryWrite(address, Pattern(0x100, 2)));
            harness.Worker.Run(() =>
            {
                Draw(second);
                harness.Scheduler.Finish();
            });

            first.Invalidate(0, 0x100);
            second.Invalidate(0, 0x100);
            Assert.Equal(Pattern(0x100, 1), first.Mapped[..0x100].ToArray());
            Assert.Equal(Pattern(0x100, 2), second.Mapped[..0x100].ToArray());
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
    }

    [Fact]
    public void ProcessFaultBufferPreservesRequestsBeyondOutputCapacity()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const int pageCount = 1056;
        var address = harness.MapBacked((ulong)(pageCount + 32) * Page, ReadWrite);
        var firstPage = ((address / Page) + 31) & ~31UL;
        var bitmap = Enumerable.Repeat((byte)255, pageCount / 8).ToArray();
        harness.Worker.Run(() =>
        {
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var offset = staging.Copy(bitmap, 4);
            harness.Cache.FaultBuffer.CopyFrom(harness.Scheduler.Current, staging, offset,
                firstPage / 8, (ulong)bitmap.Length, Silk.NET.Vulkan.AccessFlags.HostWriteBit);
            harness.Cache.ProcessFaultBuffer();
            harness.Scheduler.Finish();
        });

        var remaining = harness.ReadBack(harness.Cache.FaultBuffer, firstPage / 8, (ulong)bitmap.Length);
        Assert.Equal(pageCount - 1023, remaining.Sum(value => System.Numerics.BitOperations.PopCount((uint)value)));
        harness.Worker.Run(() =>
        {
            harness.Cache.ProcessFaultBuffer();
            harness.Scheduler.Finish();
        });
        Assert.All(harness.ReadBack(harness.Cache.FaultBuffer, firstPage / 8, (ulong)bitmap.Length),
            value => Assert.Equal(0, value));
        for (var page = 0; page < pageCount; page++)
            Assert.True(harness.Cache.IsRegionRegistered((firstPage + (ulong)page) * Page, Page));
        harness.Shutdown();
    }

    [Fact]
    public void ProcessFaultBuffer_CreatesBuffersForTheFlaggedPagesAndClearsTheBitmap()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        var firstPage = address >> GuestBufferCache.CachingPageBits;

        // Flag pages 0, 1 and 3 of the mapping: two coalesced ranges.
        var word = firstPage / 32;
        var bits = (1u << (int)(firstPage % 32)) | (1u << (int)((firstPage + 1) % 32)) | (1u << (int)((firstPage + 3) % 32));
        harness.Worker.Run(() =>
        {
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var offset = staging.Copy(BitConverter.GetBytes(bits), 4);
            harness.Cache.FaultBuffer.CopyFrom(harness.Scheduler.Current, staging, offset, word * 4, 4, Silk.NET.Vulkan.AccessFlags.HostWriteBit);
            harness.Cache.ProcessFaultBuffer();
            harness.Scheduler.Finish();
        });

        Assert.Equal(2, harness.Cache.BufferCount);
        Assert.True(harness.Cache.IsRegionRegistered(address, 2 * Page));
        Assert.False(harness.Cache.IsRegionRegistered(address + 2 * Page, Page));
        Assert.True(harness.Cache.IsRegionRegistered(address + 3 * Page, Page));
        Assert.All(harness.ReadBack(harness.Cache.FaultBuffer, word * 4, 4), value => Assert.Equal(0, value));
        harness.Shutdown();
    }
}
