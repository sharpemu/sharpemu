// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Orders an intervening GPU claim at the new admission boundary. GPU recovery happens
// before any CPU reservation, and the real TryCopy/TryWrite performs the payload copy.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferManagedCopyReadbackTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;
    private const ulong WindowBytes = 512 * 1024;
    private const int PointerCount = 52;
    private const int PayloadBytes = PointerCount * sizeof(ulong);
    private const uint NeighbourGpuValue = 0xCAFEF00D;

    // The read-only variants exercise the landed-notification behavior that already
    // works. The written variants expose a newer CPU copy hidden by broad GPU ownership.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedCopySurvivesAnUploadBetweenPremarkAndBackingCopy(bool writtenUpload, bool asynchronous) =>
        RunManagedWriteRace(writtenUpload, asynchronous, useCopy: true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManagedWriteSurvivesAnUploadBetweenPremarkAndBackingCopy(bool writtenUpload, bool asynchronous) =>
        RunManagedWriteRace(writtenUpload, asynchronous, useCopy: false);

    private void RunManagedWriteRace(bool writtenUpload, bool asynchronous, bool useCopy)
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        using var fatal = new FatalScope();
        Assert.Null(GuestGpuMemoryHook.Current);

        var mapping = harness.MapBacked(2 * WindowBytes, ReadWrite);
        var window = (mapping + WindowBytes - 1) & ~(WindowBytes - 1);
        var destination = window + 0x1A680;
        var neighbour = window + 0x18000;
        var source = harness.MapBacked(0x10000, ReadWrite);
        var oldBytes = Pointers(0x0000001000000000UL);
        var newBytes = Pointers(0x0000002000000000UL);
        // Synthetic stale payload; this is not a capture of the game's bad writer.
        BinaryPrimitives.WriteUInt64LittleEndian(oldBytes.AsSpan(20 * sizeof(ulong)), 0x3F800000UL);
        harness.Write(destination, oldBytes);
        harness.Write(source, newBytes);
        harness.Worker.Run(() =>
        {
            // A persistent resident buffer exists before the CPU premark.
            _ = harness.Cache.ObtainBuffer(window, WindowBytes, isWritten: false);
            harness.Scheduler.Finish();
            if (asynchronous)
                harness.Cache.AsyncReadback = new VulkanAsyncReadback(
                    _vulkan.DeviceInfo, harness.Scheduler, _vulkan.Queue, _vulkan.QueueFamily);
            harness.Cache.EagerReadbacksEnabled = false;
        });

        var attempts = 0;
        var reservations = 0;
        harness.Gpu.BeforeManagedWriteReservationForTest = retry =>
        {
            attempts++;
            if (retry != 0) return;
            harness.Worker.Run(() =>
            {
                _ = harness.Cache.ObtainBuffer(window, WindowBytes, isWritten: writtenUpload);
                // Actual GPU output is on another page of the readback window.
                // The pointer array itself is not written by this command.
                var (target, offset) = harness.Cache.ObtainBuffer(neighbour, sizeof(uint), isWritten: true);
                target.Fill(offset, sizeof(uint), NeighbourGpuValue);
            });
            Assert.Equal(writtenUpload, harness.Cache.HasGpuDirtyBytes(destination, PayloadBytes));
        };
        harness.Gpu.ManagedWriteReservedForTest = () => reservations++;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            // The written case must retry premark and recover the intervening GPU claim
            // BEFORE copying. The read-only control needs only its landed re-dirty.
            var before = harness.Cache.PendingReadbacksApplied;
            var copied = useCopy ? harness.Memory.TryCopy(destination, source, PayloadBytes)
                : harness.Memory.TryWrite(destination, newBytes);
            Assert.True(copied);
            Assert.Equal(writtenUpload ? 2 : 1, attempts);
            Assert.Equal(1, reservations);
            Assert.True(harness.Cache.HasCpuDirtyPages(destination, PayloadBytes));
            Assert.False(harness.Cache.HasGpuDirtyBytes(destination, PayloadBytes));
            Assert.Equal(newBytes, ReadBacking(harness, destination, PayloadBytes));

            Assert.True(harness.Cache.TrySynchronizeCpuRead(neighbour, sizeof(uint)));
            if (asynchronous)
                Assert.True(harness.Cache.PendingReadbacksApplied > before,
                    "This variant must exercise asynchronous landing; SHARPEMU_ASYNC_GUEST_READBACK must not be 0.");
            Assert.Equal(NeighbourGpuValue,
                BinaryPrimitives.ReadUInt32LittleEndian(ReadBacking(harness, neighbour, sizeof(uint))));

            // The CPU copy was newer than the upload. Downloading the neighbour
            // must preserve it, including the cell carrying the synthetic stale bits.
            Assert.Equal(newBytes, ReadBacking(harness, destination, PayloadBytes));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
            harness.Gpu.BeforeManagedWriteReservationForTest = null;
            harness.Gpu.ManagedWriteReservedForTest = null;
            harness.Worker.Run(() =>
            {
                harness.Cache.Shutdown();
                harness.Cache.AsyncReadback?.Dispose();
                harness.Cache.AsyncReadback = null;
            });
            harness.Shutdown();
        }
    }

    private static byte[] Pointers(ulong first)
    {
        var result = new byte[PayloadBytes];
        for (var index = 0; index < PointerCount; index++)
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(index * sizeof(ulong)), first + (ulong)index * 0x100);
        return result;
    }

    private static byte[] ReadBacking(CacheHarness harness, ulong address, int count)
    {
        // Inspect publication without triggering another managed readback.
        var result = new byte[count];
        Assert.True(harness.Memory.TryReadBacking(address, result));
        return result;
    }

}
