// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GpuBufferTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GpuBufferTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void FixtureDisposalFreesRetainedSlabMemory()
    {
        var fixture = new HeadlessVulkanFixture();
        if (fixture.Vulkan is not { } vulkan) return;
        var info = vulkan.DeviceInfo;
        try
        {
            using (var worker = new CacheWorker(vulkan))
            using (var buffer = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal,
                0, GpuBuffer.AllFlags, 0x1000, allowSlab: true))
            {
                Assert.True(info.LiveAllocations > 0);
            }
            Assert.True(info.LiveAllocations > 0);
        }
        finally { fixture.Dispose(); }
        Assert.Equal(0, info.LiveAllocations);
    }

    [Fact]
    public void IsInBounds_IsOverflowSafe()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var buffer = new GpuBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0x10000, GpuBuffer.AllFlags, 0x4000);

        Assert.True(buffer.IsInBounds(0x10000, 0x4000));
        Assert.True(buffer.IsInBounds(0x13FFF, 1));
        Assert.False(buffer.IsInBounds(0x13FFF, 2));
        Assert.False(buffer.IsInBounds(0xFFFF, 1));
        Assert.False(buffer.IsInBounds(0x10000, ulong.MaxValue));
        Assert.Equal(0x1000UL, buffer.Offset(0x11000));
        Assert.True(buffer.Mapped.IsEmpty);
    }

    [Fact]
    public void HostCachedStream_PrefersCachedHostMemoryAndFallsBackToUploadRanking()
    {
        if (_vulkan is null) return;
        var info = _vulkan.DeviceInfo;
        var allTypes = info.MemoryTypeCount >= 32 ? uint.MaxValue : (1u << (int)info.MemoryTypeCount) - 1;
        var cachedTypes = 0u;
        for (var index = 0; index < info.MemoryTypeCount; index++)
        {
            if ((info.GetMemoryTypeFlags((uint)index) & (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit)) ==
                (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit))
            {
                cachedTypes |= 1u << index;
            }
        }

        var ranked = GpuBuffer.RankMemoryTypes(info, allTypes, GpuBufferUsage.HostCachedStream).ToArray();
        Assert.NotEmpty(ranked);
        Assert.NotEqual(MemoryPropertyFlags.None, info.GetMemoryTypeFlags(ranked[0]) & MemoryPropertyFlags.HostVisibleBit);
        if (cachedTypes != 0)
        {
            Assert.All(ranked, index => Assert.NotEqual(0u, cachedTypes & (1u << (int)index)));
        }

        var uncachedTypes = allTypes & ~cachedTypes;
        Assert.Equal(
            GpuBuffer.RankMemoryTypes(info, uncachedTypes, GpuBufferUsage.Upload),
            GpuBuffer.RankMemoryTypes(info, uncachedTypes, GpuBufferUsage.HostCachedStream));
        Assert.Equal(GpuBufferUsage.Stream, GpuMemoryPlacement.StreamRing);
    }

    [Fact]
    public void MappedDeviceLocal_RanksOnlyHostVisibleDeviceMemoryAndBudgetsNothingWithoutIt()
    {
        if (_vulkan is null) return;
        var info = _vulkan.DeviceInfo;
        const MemoryPropertyFlags mapped = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.DeviceLocalBit;
        var allTypes = info.MemoryTypeCount >= 32 ? uint.MaxValue : (1u << (int)info.MemoryTypeCount) - 1;
        var ranked = GpuBuffer.RankMemoryTypes(info, allTypes, GpuBufferUsage.MappedDeviceLocal).ToArray();

        Assert.All(ranked, index => Assert.Equal(mapped, info.GetMemoryTypeFlags(index) & mapped));
        if (ranked.Length == 0)
        {
            Assert.Equal(0UL, info.MappedDeviceLocalBudget);
            return;
        }

        Assert.True(info.MappedDeviceLocalBudget <= info.GetMemoryHeapSize(info.GetMemoryHeapIndex(ranked[0])) / 4);
    }

    [Fact]
    public void SlabBuffers_ShareAChunkAndRecycleZeroedOnlyAfterForeignReads()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        var info = _vulkan.DeviceInfo;
        const BufferUsageFlags flags = GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit;
        using var download = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x1000);
        GpuBuffer? recycled = null;
        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            var first = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, flags, 0x1000, allowSlab: true);
            var allocations = info.LiveAllocations;
            using var second = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, flags, 0x1000, allowSlab: true);
            Assert.Equal(allocations, info.LiveAllocations);
            first.Fill(0, 0x1000, 0xA5A5A5A5);
            worker.Scheduler.Finish();

            // A readback may still copy from the released block; it must not be handed out yet.
            var firstAddress = first.DeviceAddress;
            using var during = new ForeignReadBuffers();
            using (info.Slabs.BeginForeignRead())
            {
                first.Dispose();
                during.Buffer = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, flags, 0x1000, allowSlab: true);
                Assert.NotEqual(firstAddress, during.Buffer.DeviceAddress);
            }

            recycled = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, flags, 0x1000, allowSlab: true);
            Assert.Equal(firstAddress, recycled.DeviceAddress);
            download.CopyFrom(worker.Scheduler.Current, recycled, 0, 0, 0x1000, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit, AccessFlags.HostReadBit);
            worker.Scheduler.Finish();
            download.Invalidate(0, 0x1000);
        });

        Assert.All(download.Mapped.ToArray(), value => Assert.Equal(0, value));
        recycled!.Dispose();
        worker.Run(() => worker.Scheduler.Shutdown());
    }

    private sealed class ForeignReadBuffers : IDisposable
    {
        public GpuBuffer? Buffer;

        public void Dispose() => Buffer?.Dispose();
    }

    [Fact]
    public void FillAndCopyFrom_ReachTheHostThroughADownloadBuffer()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        var info = _vulkan.DeviceInfo;
        using var device = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 0x1000);
        using var upload = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, 0x1000);
        using var download = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x1000);
        Assert.False(upload.Mapped.IsEmpty);
        Assert.False(download.Mapped.IsEmpty);

        var pattern = new byte[0x100];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 3);
        }

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            device.Fill(0, 0x1000, 0xA5A5A5A5);
            upload.Write(0, pattern);
            device.CopyFrom(worker.Scheduler.Current, upload, 0, 0x200, 0x100, AccessFlags.HostWriteBit);
            download.CopyFrom(worker.Scheduler.Current, device, 0, 0, 0x1000, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit, AccessFlags.HostReadBit);
            worker.Scheduler.Finish();
            download.Invalidate(0, 0x1000);
        });

        var bytes = download.Mapped.ToArray();
        Assert.All(bytes[..0x200], value => Assert.Equal(0xA5, value));
        Assert.Equal(pattern, bytes[0x200..0x300]);
        Assert.All(bytes[0x300..], value => Assert.Equal(0xA5, value));

        using var fatal = new FatalScope();
        worker.Run(() => Assert.Throws<SchedulerFatalException>(() => device.Fill(2, 4, 0)));
        Assert.Contains("The buffer fill range must be aligned to four bytes.", fatal.Messages);
        worker.Run(() => Assert.Throws<SchedulerFatalException>(() => device.CopyFrom(worker.Scheduler.Current, device, 0, 0x80, 0x100)));
        Assert.Contains("Cannot copy overlapping ranges of the same buffer.", fatal.Messages);
        worker.Run(() => worker.Scheduler.Shutdown());
    }

    [Fact]
    public void CopyRun_RecordsIndependentCopiesBetweenOnePairOfBarriers()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        var info = _vulkan.DeviceInfo;
        using var device = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 0x1000);
        using var upload = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, 0x1000);
        using var download = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x1000);
        var pattern = Enumerable.Range(0, 0x300).Select(index => (byte)(index * 7 + 1)).ToArray();
        var run = new GpuBuffer.CopyRun();

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            device.Fill(0, 0x1000, 0);
            upload.Write(0, pattern);
            run.Begin(worker.Scheduler.Current, AccessFlags.HostWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.HostReadBit);
            run.Add(upload, 0x000, device, 0x800, 0x100);
            run.Add(upload, 0x100, device, 0x100, 0x100);
            run.Add(upload, 0x200, download, 0xF00, 0x100);
            Assert.Equal(3, run.Count);
            run.End();
            Assert.Equal(1, run.Flushes);
            Assert.NotEqual(0UL, device.LastGpuWriteTickIn(0x800, 0x100));

            run.Begin(worker.Scheduler.Current, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit, AccessFlags.HostReadBit);
            run.Add(device, 0x000, download, 0x000, 0xF00);
            run.End();
            Assert.Equal(2, run.Flushes);
            worker.Scheduler.Finish();
            download.Invalidate(0, 0x1000);
        });

        var bytes = download.Mapped.ToArray();
        Assert.Equal(pattern[0x100..0x200], bytes[0x100..0x200]);
        Assert.Equal(pattern[0x000..0x100], bytes[0x800..0x900]);
        Assert.Equal(pattern[0x200..0x300], bytes[0xF00..0x1000]);
        Assert.All(bytes[0x200..0x800], value => Assert.Equal(0, value));
        Assert.Contains("copy_runs=", GpuBuffer.CopyRun.TakeReport(), StringComparison.Ordinal);
        worker.Run(() => worker.Scheduler.Shutdown());
    }

    [Fact]
    public void CopyRun_ACopyReadingAPendingDestinationRecordsTheRunFirst()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        var info = _vulkan.DeviceInfo;
        using var device = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 0x1000);
        using var upload = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, 0x1000);
        using var download = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x1000);
        var pattern = Enumerable.Range(0, 0x100).Select(index => (byte)(index ^ 0x5A)).ToArray();
        var run = new GpuBuffer.CopyRun();

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            upload.Write(0, pattern);
            run.Begin(worker.Scheduler.Current, AccessFlags.HostWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.HostReadBit);
            run.Add(upload, 0, device, 0x200, 0x100);
            run.Add(device, 0x280, download, 0x000, 0x80);
            Assert.Equal(1, run.Flushes);
            run.Add(upload, 0, device, 0x600, 0x100);
            run.Add(upload, 0x80, device, 0x640, 0x10);
            Assert.Equal(2, run.Flushes);
            run.Add(device, 0x000, upload, 0x800, 0x100);
            Assert.Equal(2, run.Flushes);
            run.End();
            Assert.Equal(3, run.Flushes);
            worker.Scheduler.Finish();
            download.Invalidate(0, 0x1000);
        });

        Assert.Equal(pattern[0x80..0x100], download.Mapped.ToArray()[..0x80]);

        using var fatal = new FatalScope();
        worker.Run(() =>
        {
            run.Begin(worker.Scheduler.Current);
            Assert.Throws<SchedulerFatalException>(() => run.Add(device, 0, device, 0x80, 0x100));
            run.End();
        });
        Assert.Contains("Cannot copy overlapping ranges of the same buffer.", fatal.Messages);
        worker.Run(() => worker.Scheduler.Shutdown());
    }
}
