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
public sealed class GpuRingBufferTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GpuRingBufferTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void NormalizeReservation_RoundsToAtomsOnlyWhenNotCoherent()
    {
        var size = 100UL;
        var alignment = 0UL;
        Assert.True(GpuRingBuffer.NormalizeReservation(coherent: true, atom: 64, ref size, ref alignment));
        Assert.Equal((100UL, 0UL), (size, alignment));

        Assert.True(GpuRingBuffer.NormalizeReservation(coherent: false, atom: 64, ref size, ref alignment));
        Assert.Equal((128UL, 64UL), (size, alignment));

        alignment = 48;
        Assert.True(GpuRingBuffer.NormalizeReservation(coherent: false, atom: 64, ref size, ref alignment));
        Assert.Equal(192UL, alignment);

        size = ulong.MaxValue - 1;
        Assert.False(GpuRingBuffer.NormalizeReservation(coherent: false, atom: 64, ref size, ref alignment));
    }

    [Fact]
    public void Map_WrapsAndWaitsForTheWatchThatCoversTheReusedBytes()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var ring = new GpuRingBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Upload, 0x1000);

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            var tick = worker.Scheduler.CurrentTick;

            Assert.True(ring.TryMap(0xC00, out var first));
            Assert.Equal(0UL, first);
            ring.Commit();

            // The tail of the ring is free, but the wrap would reuse bytes tick 1 may still read.
            Assert.True(ring.TryMap(0x200, out var second, 0x100));
            Assert.Equal(0xC00UL, second);
            ring.Commit();
            Assert.False(ring.TryMap(0xC00, out _, 0, allowWait: false));

            worker.Scheduler.Finish();
            Assert.True(ring.TryMap(0xC00, out var third, 0, allowWait: false));
            Assert.Equal(0UL, third);
            ring.Commit();
            Assert.Contains($"wait {tick}", worker.Device.Log);

            Assert.False(ring.TryMap(0x2000, out _));
            worker.Scheduler.Shutdown();
        });
    }

    [Fact]
    public void Map_OnTheDownloadRingWaitsPriorityOperationsOfTheWatchedTick()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var ring = new GpuRingBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Download, 0x1000);
        var ran = new List<string>();

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            Assert.True(ring.TryMap(0x1000, out _));
            ring.Commit();
            worker.Scheduler.QueuePriorityCompletionAction(() =>
            {
                Thread.Sleep(50);
                lock (ran)
                {
                    ran.Add("urgent");
                }
            });

            Assert.True(ring.TryMap(0x10, out _));
            lock (ran)
            {
                Assert.Equal(new[] { "urgent" }, ran);
            }

            ring.Commit();
            worker.Scheduler.Shutdown();
        });
    }

    [Fact]
    public void PreparedBindingSurvivesRetirementBeforeItsConsumer()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var ring = new GpuRingBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Stream, 0x1000);
        using var readback = new GpuBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Download,
            0, GpuBuffer.AllFlags, 0x100);
        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            var original = Enumerable.Repeat((byte)0x37, 0x1000).ToArray();
            var offset = ring.Copy(original);
            using var retention = ring.RetainContents();
            worker.Scheduler.Finish();

            if (ring.TryMap(0x1000, out var replacement, allowWait: false))
            {
                ring.Mapped.Slice((int)replacement, 0x1000).Fill(0xA9);
                ring.Commit();
            }

            readback.CopyFrom(worker.Scheduler.Current, ring, offset, 0, 0x100,
                AccessFlags.HostWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit, AccessFlags.HostReadBit);
            worker.Scheduler.Finish();
            readback.Invalidate(0, 0x100);
            Assert.Equal(original[..0x100], readback.Mapped[..0x100].ToArray());
            retention.Dispose();
            retention.Dispose();
            Assert.True(ring.TryMap(0x1000, out var reused, allowWait: false));
            Assert.Equal(0UL, reused);
            worker.Scheduler.Shutdown();
        });
    }

    [Fact]
    public void RetentionAllowsTailAllocationAndRequiresEveryConsumerToRelease()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var ring = new GpuRingBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Stream, 0x1000);
        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            ring.Copy(new byte[0x800]);
            using var first = ring.RetainContents();
            using var second = ring.RetainContents();
            Assert.True(ring.TryMap(0x800, out var tail, allowWait: false));
            Assert.Equal(0x800UL, tail);
            ring.Commit();
            worker.Scheduler.Finish();
            first.Dispose();
            first.Dispose();
            Assert.False(ring.TryMap(0x1000, out _, allowWait: false));
            second.Dispose();
            Assert.True(ring.TryMap(0x1000, out var reused, allowWait: false));
            Assert.Equal(0UL, reused);
            worker.Scheduler.Shutdown();
        });
    }

    [Fact]
    public void Copy_ReturnsTheOffsetOfTheCommittedBytes()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var ring = new GpuRingBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.Stream, 0x1000);

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            var first = ring.Copy(new byte[] { 1, 2, 3 }, 4);
            var second = ring.Copy(new byte[] { 4, 5 }, 4);
            Assert.Equal(0UL, first);
            Assert.Equal(4UL, second);
            Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5 }, ring.Mapped[..6].ToArray());
            worker.Scheduler.Shutdown();
        });
    }
}
