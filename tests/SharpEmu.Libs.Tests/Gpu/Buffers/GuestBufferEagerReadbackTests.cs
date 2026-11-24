// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferEagerReadbackTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;

    // The pump leaves a read-ahead alone until its writer's tick completes, then starts it and lands it without a guest read.
    [Fact]
    public void AReadAheadWaitsForItsWriterAndLandsWithoutAGuestRead()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        try
        {
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x01010101);
            QueueRead(harness, address);

            GpuWrite(harness, address, 0x02020202);
            harness.Worker.Run(() => harness.Cache.PumpEagerReadbacks());
            Assert.Equal(0, harness.Cache.EagerReadbacksStarted);

            harness.Worker.Run(() => harness.Scheduler.Finish());
            Assert.True(WaitUntil(() =>
            {
                harness.Worker.Run(() => harness.Cache.PumpEagerReadbacks());
                return harness.Cache.EagerReadbacksStarted == 1;
            }));
            Assert.True(WaitUntil(() =>
            {
                harness.Worker.Run(() => harness.Cache.PumpEagerReadbacks());
                return !harness.Cache.HasGpuDirtyBytes(address, 0x100);
            }));

            Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        }
        finally
        {
            Detach(harness);
            harness.Shutdown();
        }
    }

    // A small buffer has one hot entry: a write in another readback window of the same buffer still reads it ahead.
    [Fact]
    public void AWriteAnywhereInASmallHotBufferReadsItAhead()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        try
        {
            const ulong window = 0x80000;
            var mapping = harness.MapBacked(0x100000, ReadWrite);
            var boundary = (mapping + 0x4000 + window - 1) & ~(window - 1);
            var start = boundary - 0x4000;
            harness.Worker.Run(() => _ = harness.Cache.ObtainBuffer(start, 0x8000, isWritten: false));
            GpuWrite(harness, start, 0x01010101);
            QueueRead(harness, start);

            GpuWrite(harness, boundary, 0x02020202);
            SubmitBatchAfterItsWork(harness);
            Assert.Equal(1, harness.Cache.EagerReadbacksStarted);
            QueueRead(harness, boundary);

            Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
            Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(boundary + 0x40, 4)));
        }
        finally
        {
            Detach(harness);
            harness.Shutdown();
        }
    }

    // A hot entry outlives a thousand batches without a read.
    [Fact]
    public void AHotWindowStaysHotAcrossManyBatches()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan);
        try
        {
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x01010101);
            QueueRead(harness, address);
            harness.Worker.Run(() =>
            {
                for (var batch = 0; batch < 1000; batch++)
                {
                    harness.Cache.OnBatchSubmitted();
                }
            });

            GpuWrite(harness, address, 0x02020202);
            SubmitBatchAfterItsWork(harness);
            Assert.Equal(1, harness.Cache.EagerReadbacksStarted);
            QueueRead(harness, address);

            Assert.Equal(1, harness.Cache.EagerReadbacksUsed);
            Assert.Equal(0x02020202u, BitConverter.ToUInt32(harness.Read(address + 0x40, 4)));
        }
        finally
        {
            Detach(harness);
            harness.Shutdown();
        }
    }

    private static void Attach(CacheHarness harness, HeadlessVulkan vulkan) =>
        harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);

    private static void Detach(CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Cache.Shutdown();
        harness.Cache.AsyncReadback?.Dispose();
        harness.Cache.AsyncReadback = null;
    });

    private static void GpuWrite(CacheHarness harness, ulong address, uint value) => harness.Worker.Run(() =>
    {
        var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
        buffer.Fill(offset, 0x100, value);
    });

    private static void QueueRead(CacheHarness harness, ulong address) => harness.Worker.Run(() =>
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address, 4, GuestMemoryProfile.ReadbackSource.ShaderResourceRead)));

    private static void SubmitBatchAfterItsWork(CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Scheduler.Finish();
        harness.Cache.OnBatchSubmitted();
    });
}
