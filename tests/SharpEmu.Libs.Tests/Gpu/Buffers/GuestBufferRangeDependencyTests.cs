// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferRangeDependencyTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadAheadIsInvalidatedOnlyByWritesToItsRange(bool overlapping)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var harness = new CacheHarness(vulkan);
        using var fatal = new FatalScope();
        harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);
        var address = harness.MapBacked(0x200000, ReadWrite);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x200000, isWritten: false));
        void Write(ulong at, uint value) => harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(at, 0x100, isWritten: true);
            buffer.Fill(offset, 0x100, value);
        });
        void Read() => harness.Worker.Run(() => Assert.True(harness.Cache.TrySynchronizeCpuRead(address, 4,
            SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead)));

        Write(address, 0x11111111);
        Read();
        Assert.Equal(0x11111111u, BitConverter.ToUInt32(harness.Read(address, 4)));
        Write(address, 0x22222222);
        harness.Worker.Run(() =>
        {
            harness.Scheduler.Flush();
            harness.Cache.OnBatchSubmitted();
        });
        Assert.Equal(1, harness.Cache.EagerReadbacksStarted);
        Write(overlapping ? address : address + 0x100000, 0x33333333);
        Read();
        Assert.Equal(overlapping ? 0x33333333u : 0x22222222u, BitConverter.ToUInt32(harness.Read(address, 4)));
        Assert.Equal(overlapping ? 0 : 1, harness.Cache.EagerReadbacksUsed);
        harness.Worker.Run(() =>
        {
            harness.Cache.AsyncReadback.Dispose();
            harness.Cache.AsyncReadback = null;
        });
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadOfARetiredRangeWaitsOnlyForAnOverlappingWriter(bool overlapping)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var gate = vulkan.CreateTimelineSemaphore();
        var unrelatedSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = 0;
        var hooks = new SchedulerHooks(new RecordingRenderingState(), bundle =>
        {
            if (++submissions == 2)
            {
                bundle.AddWait(gate);
                unrelatedSubmitted.SetResult();
            }
        }, null);
        try
        {
            using var harness = new CacheHarness(vulkan, hooks: hooks);
            using var fatal = new FatalScope();
            harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);
            var address = harness.MapBacked(0x200000, ReadWrite);
            harness.Worker.Run(() =>
            {
                var (whole, _) = harness.Cache.ObtainBuffer(address, 0x200000, isWritten: false);
                var (first, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
                Assert.Same(whole, first);
                first.Fill(offset, 0x100, 0x11223344);
                harness.Scheduler.Wait(harness.Scheduler.Flush());
                harness.Scheduler.RunCompletedOperations();
                var (later, laterOffset) = harness.Cache.ObtainBuffer(overlapping ? address : address + 0x100000, 0x100, isWritten: true);
                Assert.Same(first, later);
                later.Fill(laterOffset, 0x100, 0x55667788);
            });

            var read = Task.Run(() => harness.Cache.DownloadToCpu(address, 4));
            var completedWithoutUnrelatedSubmission = false;
            try
            {
                var first = await Task.WhenAny(read, unrelatedSubmitted.Task).WaitAsync(TimeSpan.FromSeconds(10));
                completedWithoutUnrelatedSubmission = ReferenceEquals(first, read);
            }
            finally
            {
                Signal(vulkan, gate);
            }

            Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(overlapping ? 0x55667788u : 0x11223344u, BitConverter.ToUInt32(harness.Read(address, 4)));
            var laterAddress = overlapping ? address : address + 0x100000;
            Assert.True(harness.Cache.DownloadToCpu(laterAddress, 4));
            Assert.Equal(0x55667788u, BitConverter.ToUInt32(harness.Read(laterAddress, 4)));
            harness.Worker.Run(() =>
            {
                harness.Cache.AsyncReadback.Dispose();
                harness.Cache.AsyncReadback = null;
            });
            harness.Shutdown();
            Assert.Equal(!overlapping, completedWithoutUnrelatedSubmission);
        }
        finally
        {
            Signal(vulkan, gate);
            vulkan.DestroySemaphore(gate);
        }
    }

    private static unsafe void Signal(HeadlessVulkan vulkan, ulong handle)
    {
        if (vulkan.ReadSemaphore(handle) != 0) return;
        var signal = new SemaphoreSignalInfo
        {
            SType = StructureType.SemaphoreSignalInfo,
            Semaphore = new Silk.NET.Vulkan.Semaphore(handle),
            Value = 1,
        };
        Assert.Equal(Result.Success, vulkan.Vk.SignalSemaphore(vulkan.Device, &signal));
    }
}
