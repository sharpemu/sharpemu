// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferAsyncReadbackTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;

    private static void Attach(CacheHarness harness, HeadlessVulkan vulkan) =>
        harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);

    private static void Detach(CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Cache.Shutdown();
        harness.Cache.AsyncReadback?.Dispose();
        harness.Cache.AsyncReadback = null;
    });

    [Fact]
    public void AGuestReadWaitsOffTheQueueThreadAndLandsTheGpuBytes()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, 0x11223344);
        });
        Assert.True(harness.Cache.HasGpuDirtyBytes(address, 0x100));

        Assert.True(harness.Cache.DownloadToCpu(address + 8, 4));

        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
        Assert.Equal((1L, 0L), (harness.Cache.PendingReadbacksApplied, harness.Cache.PendingReadbacksRetried));
        Assert.Equal(0x11223344u, BitConverter.ToUInt32(harness.Read(address + 0xF0, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    // An async readback waits only for the buffer's last recorded writer, and work recorded after it
    // can still reach the buffer. Collection must keep the buffer alive until that work completes:
    // destroying it at once let a later copy write into freed memory (ErrorDeviceLost, a write fault).
    [Fact]
    public void GarbageCollectorKeepsAnAsyncDownloadedBufferUntilSubmittedWorkCompletes()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var dirty = harness.MapBacked(0x10000, ReadWrite);
        var buffer = harness.Worker.Run(() =>
        {
            var (written, offset) = harness.Cache.ObtainBuffer(dirty, 0x100, isWritten: true);
            written.Fill(offset, 0x100, 0x0BADF00D);
            return written;
        });
        harness.Cache.SetCollectionThresholds(1, 1);
        harness.Worker.Run(() =>
        {
            harness.Cache.RunGarbageCollector();
            Assert.Equal(0, harness.Cache.BufferCount);
            Assert.NotEqual(0UL, buffer.Handle.Handle);
            harness.Scheduler.Finish();
        });
        Assert.Equal(0UL, buffer.Handle.Handle);
        Assert.Equal(0x0BADF00Du, BitConverter.ToUInt32(harness.Read(dirty + 0xFC, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void AGuestReadCountsItsWaitAndTheFrameAgeOfItsWriter()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x55667788);
        var lastTick = harness.Scheduler.CurrentTick;
        for (var frame = 0; frame < 3; frame++)
        {
            harness.Cache.FrameTicks.NoteFrameSubmitted(lastTick);
        }

        var reads = GuestBufferCache.GuestReadbacks.Total;
        var oldWriters = GuestBufferCache.GuestReadbackFrameAge[3].Total;

        Assert.True(harness.Cache.DownloadToCpu(address + 8, 4));

        Assert.True(GuestBufferCache.GuestReadbacks.Total > reads);
        Assert.True(GuestBufferCache.GuestReadbackFrameAge[3].Total > oldWriters);
        Assert.Equal(0x55667788u, BitConverter.ToUInt32(harness.Read(address + 8, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    private static void GpuWrite(CacheHarness harness, ulong address, uint value) => harness.Worker.Run(() =>
    {
        var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
        buffer.Fill(offset, 0x100, value);
    });

    private static void QueueRead(CacheHarness harness, ulong address) => harness.Worker.Run(() =>
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address, 4, SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead)));

    private static void SubmitBatch(CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Scheduler.Flush();
        harness.Cache.OnBatchSubmitted();
    });

    [Fact]
    public void AHotWindowIsReadAheadAfterTheNextGpuWrite()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x01010101);
        QueueRead(harness, address);

        GpuWrite(harness, address, 0x02020202);
        SubmitBatch(harness);
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);
        QueueRead(harness, address);

        Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
        Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void AReadAheadOverwrittenBeforeItsReadIsNeverApplied()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x01010101);
        QueueRead(harness, address);
        GpuWrite(harness, address, 0x02020202);
        SubmitBatch(harness);
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);

        GpuWrite(harness, address, 0x03030303);
        QueueRead(harness, address);

        Assert.Equal(0, harness.Cache.EagerReadbacksUsed);
        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
        Assert.Equal(0x03030303u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void AWindowWhoseReadAheadsKeepGoingStaleStopsBeingReadAhead()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x01010101);
        QueueRead(harness, address);

        GpuWrite(harness, address, 0x02020202);
        Assert.True(harness.Cache.HotWritePending);
        var value = 0x03030303u;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (harness.Cache.EagerReadbacksWasted < 3 && DateTime.UtcNow < deadline)
        {
            SubmitBatch(harness);
            GpuWrite(harness, address, value++);
        }

        Assert.Equal(3, harness.Cache.EagerReadbacksWasted);
        SubmitBatch(harness);
        var started = harness.Cache.EagerReadbacksStarted;
        GpuWrite(harness, address, 0x07070707);
        Assert.False(harness.Cache.HotWritePending);
        SubmitBatch(harness);
        SubmitBatch(harness);

        Assert.Equal(started, harness.Cache.EagerReadbacksStarted);
        QueueRead(harness, address);
        Assert.Equal(0x07070707u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    private static void CoverWithOneBuffer(CacheHarness harness, ulong address, ulong size) =>
        harness.Worker.Run(() => _ = harness.Cache.ObtainBuffer(address, size, isWritten: false));

    [Fact]
    public void AWriteElsewhereInTheBufferKeepsAReadAheadUsable()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        CoverWithOneBuffer(harness, address, 0x10000);
        GpuWrite(harness, address, 0x01010101);
        QueueRead(harness, address);
        GpuWrite(harness, address, 0x02020202);
        SubmitBatch(harness);
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);

        GpuWrite(harness, address + 0x8000, 0x07070707);
        QueueRead(harness, address);

        Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
        Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Assert.True(harness.Cache.HasGpuDirtyBytes(address + 0x8000, 0x100));
        Assert.True(harness.Cache.DownloadToCpu(address + 0x8000, 4));
        Assert.Equal(0x07070707u, BitConverter.ToUInt32(harness.Read(address + 0x8010, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void ANewerWriteKeepsOnlyItsOwnPieceProtected()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        CoverWithOneBuffer(harness, address, 0x10000);
        GpuWrite(harness, address, 0x01010101);
        GpuWrite(harness, address + 0x8000, 0x01010101);
        QueueRead(harness, address);
        GpuWrite(harness, address, 0x02020202);
        GpuWrite(harness, address + 0x8000, 0x02020202);
        SubmitBatch(harness);
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);

        GpuWrite(harness, address + 0x8000, 0x03030303);
        QueueRead(harness, address);

        Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
        Assert.Equal(1, harness.Cache.StalePiecesKept);
        Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Assert.True(harness.Cache.HasGpuDirtyBytes(address + 0x8000, 0x100));
        Assert.True(harness.Cache.MayHaveGpuDirtyPages(address + 0x8000, 0x100));
        Assert.True(harness.Cache.DownloadToCpu(address + 0x8000, 4));
        Assert.Equal(0x03030303u, BitConverter.ToUInt32(harness.Read(address + 0x8040, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void AGuestReadOffTheQueueIsReadAheadToo()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x01010101);
        Assert.True(harness.Cache.DownloadToCpu(address, 4));

        GpuWrite(harness, address, 0x02020202);
        SubmitBatch(harness);
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);
        Assert.True(harness.Cache.DownloadToCpu(address, 4));

        Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
        Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void AReadAheadDeferredForASlotStartsLater()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        const ulong window = 0x80000;
        const int windows = 5;
        var address = harness.MapBacked(window * windows, ReadWrite);
        for (var index = 0; index < windows; index++)
        {
            GpuWrite(harness, address + (ulong)index * window, 0x01010101);
            QueueRead(harness, address + (ulong)index * window);
        }

        for (var index = 0; index < windows; index++)
        {
            GpuWrite(harness, address + (ulong)index * window, 0x02020202);
        }

        SubmitBatch(harness);
        Assert.Equal(4, harness.Cache.EagerReadbacksStarted);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (harness.Cache.EagerReadbacksStarted < windows && DateTime.UtcNow < deadline)
        {
            harness.Worker.Run(() => harness.Scheduler.Finish());
            Thread.Sleep(10);
            SubmitBatch(harness);
        }

        Assert.Equal(windows, harness.Cache.EagerReadbacksStarted);
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void RepeatedGuestReadsReuseTheReadbackSlots()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        for (uint round = 0; round < 8; round++)
        {
            var value = 0xA0000000u + round;
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
                buffer.Fill(offset, 0x100, value);
            });

            Assert.True(harness.Cache.DownloadToCpu(address, 4));
            Assert.Equal(value, BitConverter.ToUInt32(harness.Read(address + 0x80, 4)));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
            Assert.Equal(((long)round + 1, 0L), (harness.Cache.PendingReadbacksApplied, harness.Cache.PendingReadbacksRetried));
        }

        Detach(harness);
        harness.Shutdown();
    }
}
