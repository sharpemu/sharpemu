// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// The image lock call-chain rules and the single host-protection authority.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestImageCacheLockTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GuestImageCacheLockTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // The worker waits inside Finish with the lock held: pending GPU work and a completion action
    // drain first, then the guest writer that spun on the lock completes.
    [Fact]
    public void GuestWrite_WaitsWhileTheWorkerDrainsInsideFinishUnderTheImageLock()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = Color32(address);
        var imageIdentifier = harness.Acquire(ref request);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address, 4, 0x5a5a5a5au)));
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);

        using var inFinish = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var completionRan = false;
        var writeSeenByCompletion = true;
        var write = new Task<bool>(() => harness.ImageStore.MarkCpuWrite(address, 4));
        harness.Worker.Post(() =>
        {
            harness.Scheduler.QueueCompletionAction(() =>
            {
                inFinish.Set();
                release.Wait();
                writeSeenByCompletion = write.IsCompleted;
                completionRan = true;
            });
            using var scope = harness.Images.HoldLockForTest();
            harness.Scheduler.Finish();
        });
        // A failed assertion must still release the worker, or the harness disposal waits on it forever.
        try
        {
            Assert.True(inFinish.Wait(5000));
            write.Start();
            Assert.False(write.Wait(250));
        }
        finally
        {
            release.Set();
        }

        Assert.True(write.Wait(5000));
        Assert.True(write.Result);
        harness.Worker.Run(() => { });
        Assert.True(completionRan);
        Assert.False(writeSeenByCompletion);
        Assert.True(harness.Image(imageIdentifier).IsDefinitelyCpuDirty);
        Assert.Equal(Bytes(0x5a5a5a5au), harness.ReadImageBytes(harness.Image(imageIdentifier)));
        harness.Shutdown();
    }

    [Fact]
    public void CompletionActionThatTakesTheImageLock_IsCaughtByTheRecursionFatal()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var fatal = Assert.Throws<SchedulerFatalException>(() => harness.Worker.Run(() =>
        {
            harness.Scheduler.QueueCompletionAction(() => harness.Images.QueryRegion(address, 4));
            using var scope = harness.Images.HoldLockForTest();
            harness.Scheduler.Finish();
        }));
        Assert.Equal("Cannot acquire the region lock twice on the same thread.", fatal.Message);
        harness.Shutdown();
    }

    [Fact]
    public void ManagedGuestWriteUnderTheImageLock_IsCaughtByTheRecursionFatal()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = Color32(address);
        var imageIdentifier = harness.Acquire(ref request);
        harness.MarkGpuWritten(imageIdentifier);
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var fatal = Assert.Throws<SchedulerFatalException>(() => harness.Worker.Run(() =>
            {
                using var scope = harness.Images.HoldLockForTest();
                _ = harness.Memory.TryWrite(address, Bytes(1u));
            }));
            Assert.Equal("Cannot acquire the region lock twice on the same thread.", fatal.Message);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
    }

    [Fact]
    public void HostProtection_FollowsTheSharedPageThroughImageAndBufferWatches()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));

        var request = Color32(address);
        var imageIdentifier = harness.Acquire(ref request);
        harness.MarkGpuWritten(imageIdentifier);
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        harness.Worker.Run(() => Assert.NotNull(harness.Cache.ObtainBuffer(address + 0x100, 0x4100, isWritten: false).Buffer));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        // The image leaves first; the buffer watch keeps the page read-only.
        harness.Worker.Run(() => harness.ImageStore.Unregister(address, 4));
        Assert.False(harness.Images.Contains(imageIdentifier));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address + 0x100, 4, isWritten: true);
            buffer.Fill(offset, 4, 0x11223344u);
        });
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));

        // A new image on the GPU-dirty page cannot loosen the buffer's read watch.
        var second = Color32(address + 0x200);
        var secondId = harness.Acquire(ref second);
        harness.MarkGpuWritten(secondId);
        Assert.Equal(HostPageProtection.NoAccess, harness.Protection(address));

        Assert.True(harness.ReadFault(address + 0x100));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
        harness.Shutdown();
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
    }
}
