// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Guest faults on image-watched pages reach the image store through the real host fault handler.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestImageFaultTests
{
    private const string WorkerEnvironmentVariable = "SHARPEMU_IMAGE_FAULT_WORKER";

    [Fact]
    public async Task GuestFaultsRecoverThroughTheImageStore()
    {
        if (!GuestFaultWorker.CanRun)
        {
            return;
        }

        if (GuestFaultWorker.IsWorker(WorkerEnvironmentVariable))
        {
            RunGuest();
            return;
        }

        await GuestFaultWorker.RunIsolatedAsync(WorkerEnvironmentVariable, typeof(GuestImageFaultTests), nameof(GuestFaultsRecoverThroughTheImageStore));
    }

    private static void RunGuest()
    {
        using var vulkan = HeadlessVulkan.TryCreate();
        if (!GatePrerequisites.Ready(vulkan))
        {
            GuestFaultWorker.Report(GuestFaultWorker.Skipped, "no Vulkan device");
            return;
        }

        using var harness = new CacheHarness(vulkan);
        using var fatal = new FatalScope();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            using var guest = new SyntheticGuest(harness, "image-fault-worker");
            var resolved = 0;

            // Write fault on an image page: the image turns dirty and the next use re-uploads it.
            var address = harness.MapBacked(0x10000, ReadWrite);
            harness.Write(address, Bytes(1u, 2u, 3u, 4u));
            var request = Color32(address, 4);
            var imageIdentifier = harness.Acquire(ref request);
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));
            guest.Write(address + 4, 0x22u);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
            Assert.True(harness.Image(imageIdentifier).IsDefinitelyCpuDirty);
            Assert.Contains($"faults_resolved={++resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(imageIdentifier, harness.Acquire(ref request));
            Assert.False(harness.Image(imageIdentifier).IsCpuDirty);
            Assert.Equal(Bytes(1u, 0x22u, 0u, 4u), harness.ReadImageBytes(harness.Image(imageIdentifier)));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

            // Writes beside a two-page image unwatch its head, then its tail; the edge hash then
            // proves the image clean, and later dirty, without a byte-range record.
            var edged = harness.MapBacked(0x10000, ReadWrite);
            var edgedAddress = edged + 0x800;
            harness.Write(edgedAddress, Enumerable.Range(0, 0x400).SelectMany(index => Bytes((uint)index)).ToArray());
            var edgedRequest = Color32(edgedAddress, 0x400);
            var edgedId = harness.Acquire(ref edgedRequest);
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(edged));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(edged + 0x1000));
            guest.Write(edged + 0x10, 0xAA);
            Assert.Contains($"faults_resolved={++resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(edged));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(edged + 0x1000));
            Assert.False(harness.Image(edgedId).IsCpuDirty);
            guest.Write(edged + 0x1900, 0xBB);
            Assert.Contains($"faults_resolved={++resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(edged + 0x1000));
            Assert.True(harness.Image(edgedId).IsCpuDirty);
            Assert.False(harness.Image(edgedId).IsDefinitelyCpuDirty);
            Assert.Equal(edgedId, harness.Acquire(ref edgedRequest));
            Assert.False(harness.Image(edgedId).IsCpuDirty);
            Assert.Equal(0x0000_0001_0000_0000UL, BitConverter.ToUInt64(harness.ReadImageBytes(harness.Image(edgedId)), 0));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(edged));

            guest.Write(edged + 0x10, 0xAC);
            guest.Write(edged + 0x1900, 0xBD);
            resolved += 2;
            Assert.Contains($"faults_resolved={resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.True(harness.Image(edgedId).IsCpuDirty);
            guest.Write(edgedAddress, 0x0000_00AB_0000_00CDUL);
            Assert.Contains($"faults_resolved={resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(edgedId, harness.Acquire(ref edgedRequest));
            Assert.False(harness.Image(edgedId).IsCpuDirty);
            Assert.Equal(0x0000_00AB_0000_00CDUL, BitConverter.ToUInt64(harness.ReadImageBytes(harness.Image(edgedId)), 0));

            // A page watched by an image and a buffer notifies both stores on one fault.
            var shared = harness.MapBacked(0x10000, ReadWrite);
            var sharedRequest = Color32(shared, 4);
            var sharedId = harness.Acquire(ref sharedRequest);
            harness.Worker.Run(() => harness.Cache.ObtainBuffer(shared + 0x100, 0x100, isWritten: false));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(shared));
            guest.Write(shared + 0x108, 0x3333);
            Assert.Contains($"faults_resolved={++resolved} ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(shared));
            Assert.True(harness.Cache.HasCpuDirtyPages(shared, 0x1000));
            Assert.True(harness.Image(sharedId).IsCpuDirty);
            Assert.Equal(0x3333UL, guest.Read(shared + 0x108));

            // Two guest threads writing one image page through the live handler both resume.
            var raced = harness.MapBacked(0x10000, ReadWrite);
            var racedRequest = Color32(raced, 4);
            var racedId = harness.Acquire(ref racedRequest);
            harness.MarkGpuWritten(racedId);
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(raced));
            var outcomes = guest.Race(2, write: true, raced + 8, slot => 0x1000UL + (ulong)slot);
            Assert.All(outcomes, outcome => Assert.True(outcome.Ok, outcome.Error));
            var landed = guest.Read(raced + 8);
            Assert.True(landed == 0x1000 || landed == 0x1001);
            Assert.True(harness.Image(racedId).IsDefinitelyCpuDirty);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(raced));
            Assert.Contains("faults_resolved=", GuestGpuMemoryHook.GetSummary());
            resolved = ResolvedCount();

            // Unmapping a watched image range deletes the image and drops its watch.
            var unmapped = harness.MapBacked(0x10000, ReadWrite);
            var unmappedRequest = Color32(unmapped, 4);
            var unmappedId = harness.Acquire(ref unmappedRequest);
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(unmapped));
            harness.Gpu.Unregister(unmapped, 0x10000);
            Assert.False(harness.Images.Contains(unmappedId));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(unmapped));
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Write, unmapped + 8));
            Assert.Equal(resolved, ResolvedCount());

            // A texel obtain copies the GPU result into the buffer but the image keeps ownership:
            // the guest reads its own copy without a fault until a scheduled readback publishes.
            var owned = harness.MapBacked(0x10000, ReadWrite);
            harness.Write(owned, Bytes(9u, 9u, 9u, 9u));
            var ownedRequest = Color32(owned, 4);
            var ownedId = harness.Acquire(ref ownedRequest);
            Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(owned, 16, 0x5A5A5A5Au)));
            Assert.True(harness.Image(ownedId).IsGpuModified);
            var (texel, texelOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(owned, 0x4100, isWritten: false, isTexelBuffer: true));
            Assert.Equal(Bytes(0x5A5A5A5Au, 0x5A5A5A5Au), harness.ReadBack(texel, texelOffset, 8));
            Assert.True(harness.Image(ownedId).IsGpuModified);
            Assert.False(harness.Cache.HasGpuDirtyBytes(owned, 16));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(owned));
            Assert.Equal(0x0000_0009_0000_0009UL, guest.Read(owned + 8));
            Assert.Equal(resolved, ResolvedCount());
            harness.Images.SetLinearReadback(true);
            harness.Worker.Run(() =>
            {
                harness.Images.ScheduleReadbackForTest(ownedId);
                harness.Images.FlushScheduledReadbacks();
            });
            harness.Finish();
            Assert.Equal(0x5A5A5A5A5A5A5A5AUL, guest.Read(owned + 8));
            Assert.Equal(resolved, ResolvedCount());

            harness.Shutdown();
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(owned));
            GuestFaultWorker.Report(GuestFaultWorker.Completed, GuestGpuMemoryHook.GetSummary());
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        static int ResolvedCount()
        {
            var summary = GuestGpuMemoryHook.GetSummary();
            const string key = "faults_resolved=";
            var start = summary.IndexOf(key, StringComparison.Ordinal) + key.Length;
            var end = summary.IndexOf(' ', start);
            return int.Parse(summary.AsSpan(start, end - start));
        }
    }
}
