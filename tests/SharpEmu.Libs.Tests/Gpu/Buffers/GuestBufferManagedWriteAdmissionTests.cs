// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferManagedWriteAdmissionTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PremarkCanReadBackBeforeReservationAndGpuClaimWaitsForCopy(bool asynchronous)
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        Assert.Null(GuestGpuMemoryHook.Current);
        const ulong size = 0x8000;
        var destination = harness.MapBacked(0x10000, ReadWrite);
        var source = harness.MapBacked(0x10000, ReadWrite);
        var expected = Enumerable.Repeat((byte)0x79, (int)size).ToArray();
        harness.Write(source, expected);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(destination, size, isWritten: true);
            buffer.Fill(offset, size, 0x11111111);
            harness.Cache.EagerReadbacksEnabled = false;
            if (asynchronous)
                harness.Cache.AsyncReadback = new VulkanAsyncReadback(
                    _vulkan.DeviceInfo, harness.Scheduler, _vulkan.Queue, _vulkan.QueueFamily);
        });
        Assert.True(harness.Cache.HasGpuDirtyBytes(destination, size));
        var premarkReturned = Signal();
        var reserved = Signal();
        var gpuBlocked = Signal();
        var gpuAdmitted = Signal();
        using var releaseCopy = new ManualResetEventSlim();
        harness.Gpu.BeforeManagedWriteReservationForTest = _ =>
        {
            Assert.False(harness.Cache.HasGpuDirtyBytes(destination, size));
            premarkReturned.TrySetResult(true);
        };
        harness.Gpu.ManagedWriteReservedForTest = () =>
        {
            reserved.TrySetResult(true);
            if (!releaseCopy.Wait(Deadline)) throw new TimeoutException("The test did not release the CPU copy.");
        };
        harness.Gpu.Pages.ManagedWrites.GpuClaimWaitingForTest = () => gpuBlocked.TrySetResult(true);
        GuestGpuMemoryHook.Attach(harness.Gpu);
        Task<bool>? cpu = null;
        Task? gpu = null;
        try
        {
            var readbacksBefore = harness.Cache.PendingReadbacksApplied;
            cpu = Task.Run(() => harness.Memory.TryCopy(destination, source, size));
            await reserved.Task.WaitAsync(Deadline);
            Assert.True(premarkReturned.Task.IsCompletedSuccessfully);
            if (asynchronous)
                Assert.True(harness.Cache.PendingReadbacksApplied > readbacksBefore,
                    "The premark must exercise the async path; SHARPEMU_ASYNC_GUEST_READBACK must not be 0.");

            gpu = Task.Run(() => harness.Worker.Run(() =>
            {
                _ = harness.Cache.ObtainBuffer(destination, size, isWritten: true);
                gpuAdmitted.TrySetResult(true);
            }));
            await gpuBlocked.Task.WaitAsync(Deadline);
            Assert.False(cpu.IsCompleted);
            Assert.False(gpuAdmitted.Task.IsCompleted);
            releaseCopy.Set();
            Assert.True(await cpu.WaitAsync(Deadline));
            await gpu.WaitAsync(Deadline);
            Assert.True(gpuAdmitted.Task.IsCompletedSuccessfully);
            Assert.True(harness.Cache.TrySynchronizeCpuRead(destination, size));
            var actual = new byte[(int)size];
            Assert.True(harness.Memory.TryReadBacking(destination, actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            releaseCopy.Set();
            try
            {
                if (cpu != null) await cpu.WaitAsync(Deadline);
                if (gpu != null) await gpu.WaitAsync(Deadline);
            }
            finally
            {
                GuestGpuMemoryHook.Attach(null);
                harness.Gpu.BeforeManagedWriteReservationForTest = null;
                harness.Gpu.ManagedWriteReservedForTest = null;
                harness.Gpu.Pages.ManagedWrites.GpuClaimWaitingForTest = null;
                harness.Worker.Run(() =>
                {
                    harness.Cache.Shutdown();
                    harness.Cache.AsyncReadback?.Dispose();
                    harness.Cache.AsyncReadback = null;
                });
                harness.Shutdown();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBackingCopyReleasesOuterReservationBeforeStagedWrites(bool sourceBacked)
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        Assert.Null(GuestGpuMemoryHook.Current);
        const ulong size = 0x10020;
        var source = sourceBacked ? harness.MapBacked(0x20000, ReadWrite) : harness.MapPrivate(0x20000);
        var destination = sourceBacked ? harness.MapPrivate(0x20000) : harness.MapBacked(0x20000, ReadWrite);
        var expected = Enumerable.Range(0, (int)size).Select(index => (byte)(index * 19 + 7)).ToArray();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            Assert.True(harness.Memory.TryWrite(source, expected));
            Assert.True(harness.Memory.TryCopy(destination, source, size));
            var actual = new byte[(int)size];
            Assert.True(harness.Memory.TryRead(destination, actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
        harness.Shutdown();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
