// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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

    // Without a readback queue the main queue carries the download and the guest thread waits
    // for that submission; the worker records the copies and moves on.
    [Fact]
    public void AGuestReadWithoutAReadbackQueueStillWaitsOffTheQueueThread()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Assert.Null(harness.Cache.AsyncReadback);
        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x55667788);
        Assert.True(harness.Cache.HasGpuDirtyBytes(address, 0x100));

        Assert.True(harness.Cache.DownloadToCpu(address + 8, 4));

        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
        Assert.Equal((1L, 0L), (harness.Cache.PendingReadbacksApplied, harness.Cache.PendingReadbacksRetried));
        Assert.Equal(0x55667788u, BitConverter.ToUInt32(harness.Read(address + 0xF0, 4)));

        // A second write after the first read is picked up by the next read, not served stale.
        GpuWrite(harness, address, 0x99AABBCCu);
        Assert.True(harness.Cache.DownloadToCpu(address, 4));
        Assert.Equal(0x99AABBCCu, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        Assert.Equal((2L, 0L), (harness.Cache.PendingReadbacksApplied, harness.Cache.PendingReadbacksRetried));
        harness.Shutdown();
    }

    // On a unified-memory device a read of a retired GPU write copies straight out of the buffer's
    // own mapping: no copy command, no pending readback.
    [Fact]
    public void AReadOfARetiredWriteComesFromTheUnifiedMapping()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        if (!harness.Cache.UnifiedBuffers)
        {
            harness.Shutdown();
            return;
        }

        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x0BADF00D);
        harness.Worker.Run(() => harness.Scheduler.Wait(harness.Scheduler.Flush()));

        Assert.True(harness.Cache.DownloadToCpu(address + 8, 4));

        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
        Assert.Equal(0x0BADF00Du, BitConverter.ToUInt32(harness.Read(address + 0x80, 4)));
        Assert.Equal(1L, harness.Cache.MappedReadbacks);
        Assert.Equal(0L, harness.Cache.PendingReadbacksApplied);
        harness.Shutdown();
    }

    // A read while the writer is submitted but still running waits for that submission only, then
    // reads the mapping; a write recorded after the read was issued is picked up by the next read.
    [Fact]
    public void AReadOfASubmittedWriteWaitsForItAndReadsTheUnifiedMapping()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        if (!harness.Cache.UnifiedBuffers)
        {
            harness.Shutdown();
            return;
        }

        var address = harness.MapBacked(0x10000, ReadWrite);
        GpuWrite(harness, address, 0x12345678);
        SubmitBatch(harness);

        Assert.True(harness.Cache.DownloadToCpu(address, 4));

        Assert.Equal(0x12345678u, BitConverter.ToUInt32(harness.Read(address + 0xFC, 4)));
        Assert.Equal(1L, harness.Cache.MappedReadbacks);
        Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));

        GpuWrite(harness, address, 0x9ABCDEF0);
        SubmitBatch(harness);
        Assert.True(harness.Cache.DownloadToCpu(address, 4));
        Assert.Equal(0x9ABCDEF0u, BitConverter.ToUInt32(harness.Read(address + 0x10, 4)));
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
