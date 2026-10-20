// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// A page the CPU and shaders both write, on an owner the host cannot read through a coherent
// mapping: the shader's bytes sit in the GPU copy only, and the merge into guest memory and the
// shadow has to read them back from the device.
[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class GuestBufferSharedPageMergeTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Tracker = TrackerLayout.PageBytes;
    private const ulong Baseline = 0x10;
    private const ulong CpuValue = 0x40;
    private const ulong Reset = 0x50;
    private const ulong Elsewhere = 0x60;
    private const uint BaselineValue = 0xCAFEF00D;
    private const uint CpuWord = 0x11223344;
    private const uint StaleShaderWord = 0x00810081;
    private const uint ShaderWord = 0x7E7E7E7E;

    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;

    // The merge reads the device only when the owner is not host-coherent; on a machine whose
    // device-local heap is, the report merges at once and the pending steps below do not exist.
    private static bool Deferred(GpuBuffer buffer) => buffer.MappedPointer == null || !buffer.IsCoherent;

    private static void Attach(CacheHarness harness, HeadlessVulkan vulkan) =>
        harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);

    private static void Detach(CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Cache.Shutdown();
        harness.Cache.AsyncReadback?.Dispose();
        harness.Cache.AsyncReadback = null;
    });

    // A shadowed page whose GPU copy, guest memory and shadow all agree: the shader wrote the first
    // dword, the page was downloaded (taking the shadow) and the CPU's rewrite was uploaded.
    private static (GpuBuffer Buffer, ulong Offset) EstablishShadow(CacheHarness harness, ulong address) =>
        harness.Worker.Run(() =>
        {
            _ = harness.Cache.FindBuffer(address, 0x10000);
            var obtained = harness.Cache.ObtainBufferForImage(address, 256);
            obtained.Buffer.Fill(obtained.Offset, 4, BaselineValue);
            harness.Cache.NoteDeviceAddressWrites(address, Tracker);
            Assert.True(harness.Cache.WriteDownloaded(address, BitConverter.GetBytes(BaselineValue)));
            harness.Cache.Tracker.ClearGpuDirtyPages(address, Tracker);
            harness.Cache.Tracker.MarkCpuDirtyPages(address, Tracker);
            _ = harness.Cache.ObtainBufferForImage(address, 256);
            return obtained;
        });

    private static uint Word(byte[] bytes, ulong offset) => BitConverter.ToUInt32(bytes, (int)offset);

    private static void WaitUntilReadsComplete(CacheHarness harness)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!harness.Cache.SharedMergeReadsComplete())
        {
            Assert.True(DateTime.UtcNow < deadline, "The shared page readback never completed.");
            Thread.Sleep(1);
        }
    }

    // Sweeps without waiting until the shader's bytes reach guest memory; a sweep that finds the
    // copy still running leaves it in flight.
    private static void SweepUntilMerged(CacheHarness harness, ulong address)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Word(harness.Read(address, 0x100), Elsewhere) != ShaderWord)
        {
            Assert.True(DateTime.UtcNow < deadline, "The sweeps never merged the shader's bytes.");
            harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));
            Thread.Sleep(1);
        }
    }

    // The CPU dirties the page and writes CpuValue; shaders store through device addresses at
    // CpuValue, Reset and Elsewhere, and the GPU reports the page afterwards.
    private static void CpuWritesThenShadersWrite(CacheHarness harness, ulong address, GpuBuffer buffer, ulong offset)
    {
        harness.Write(address + CpuValue, Bytes(CpuWord));
        harness.Worker.Run(() =>
        {
            harness.Cache.Tracker.MarkCpuDirtyPages(address, Tracker);
            buffer.Fill(offset + CpuValue, 4, StaleShaderWord);
            buffer.Fill(offset + Reset, 4, StaleShaderWord);
            buffer.Fill(offset + Elsewhere, 4, ShaderWord);
            // The fault scan reports a write once the scan that saw it has completed.
            harness.Scheduler.Finish();
            harness.Cache.NoteDeviceAddressWrites(address, Tracker);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShaderByteTheCpuLaterRewritesToTheShadowValueIsOverwrittenOnTheGpu(bool asyncReadback)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        if (asyncReadback) Attach(harness, _vulkan!);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        Assert.Equal(harness.Read(address, 0x100), harness.ReadBack(buffer, offset, 0x100));

        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        // The next fault scan's pass, or any use of the page, merges the shader's bytes first.
        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: true));

        // Guest memory takes the shader's bytes where the CPU left the page alone; its own write stays.
        var merged = harness.Read(address, 0x100);
        Assert.Equal(CpuWord, Word(merged, CpuValue));
        Assert.Equal(StaleShaderWord, Word(merged, Reset));
        Assert.Equal(ShaderWord, Word(merged, Elsewhere));

        // The CPU resets the value to the one the shadow held before the shader's write.
        harness.Write(address + Reset, Bytes(0u));
        harness.Worker.Run(() =>
        {
            harness.Cache.Tracker.MarkCpuDirtyPages(address, Tracker);
            _ = harness.Cache.ObtainBufferForImage(address, 256);
        });

        var gpu = harness.ReadBack(buffer, offset, 0x100);
        Assert.Equal(0u, Word(gpu, Reset));
        Assert.Equal(CpuWord, Word(gpu, CpuValue));
        Assert.Equal(ShaderWord, Word(gpu, Elsewhere));
        Assert.Equal(harness.Read(address, 0x100), gpu);
        if (asyncReadback) Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void APendingMergeLandsBeforeTheUploadFilterEvenWhenTheCpuWroteNothingNew()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);

        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        if (Deferred(buffer))
        {
            // Nothing has read the GPU copy yet: guest memory does not know the shader's bytes.
            Assert.Equal(0u, Word(harness.Read(address, 0x100), Elsewhere));
        }

        // The synchronization that uploads the CPU's write resolves the merge first.
        harness.Worker.Run(() => _ = harness.Cache.ObtainBufferForImage(address, 256));

        var guest = harness.Read(address, 0x100);
        Assert.Equal(CpuWord, Word(guest, CpuValue));
        Assert.Equal(StaleShaderWord, Word(guest, Reset));
        Assert.Equal(ShaderWord, Word(guest, Elsewhere));
        Assert.Equal(guest, harness.ReadBack(buffer, offset, 0x100));
        harness.Shutdown();
    }

    [Fact]
    public void ACpuWriteMadeBeforeTheMergeResolvesLeavesTheGpuCopyEqualToGuestMemory()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);

        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        // The CPU resets a value before the merge resolved: the shadow's old value, so the write
        // cannot be told from none. Wherever the byte ends up, both copies must agree on it.
        harness.Write(address + Reset, Bytes(0u));
        harness.Worker.Run(() =>
        {
            harness.Cache.Tracker.MarkCpuDirtyPages(address, Tracker);
            _ = harness.Cache.ObtainBufferForImage(address, 256);
        });

        var gpu = harness.ReadBack(buffer, offset, 0x100);
        Assert.Equal(harness.Read(address, 0x100), gpu);
        Assert.Equal(CpuWord, Word(gpu, CpuValue));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMergeWithoutWaitingNeedsTheReadbackQueueAndCompletedInputs(bool asyncReadback)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        if (asyncReadback) Attach(harness, _vulkan!);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        harness.Worker.Run(() => harness.Scheduler.Finish());

        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));

        if (asyncReadback)
        {
            // The sweep starts the copy and returns; a later sweep merges it once it has completed.
            SweepUntilMerged(harness, address);
        }

        var shaderBytesReachedGuest = Word(harness.Read(address, 0x100), Elsewhere) == ShaderWord;
        // Without a readback queue the download would drain the scheduler, which a sweep must not do.
        Assert.Equal(asyncReadback || !Deferred(buffer), shaderBytesReachedGuest);
        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: true));
        Assert.Equal(ShaderWord, Word(harness.Read(address, 0x100), Elsewhere));
        if (asyncReadback) Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void ASynchronizedCpuReadResolvesThePendingMerge()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);

        Assert.True(harness.Cache.TrySynchronizeCpuRead(address + Elsewhere, 4));

        Assert.Equal(ShaderWord, Word(harness.Read(address, 0x100), Elsewhere));
        harness.Shutdown();
    }

    [Fact]
    public void AMergeAfterTheOwnerWasReplacedReadsTheReplacement()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        var start = harness.MapBacked(0x30000, ReadWrite);
        var address = start + 0x10000;
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);

        // A replacement covers the page; the old owner is copied into it and retired.
        var replacement = harness.Worker.Run(() =>
        {
            var wider = harness.Cache.GetBuffer(harness.Cache.FindBuffer(start, 0x30000));
            harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: true);
            return wider;
        });

        Assert.NotSame(buffer, replacement);
        Assert.Equal(ShaderWord, Word(harness.Read(address, 0x100), Elsewhere));
        harness.Shutdown();
    }

    [Fact]
    public void AReadbackIssuedEarlyAndCompletedIsMergedAtSynchronizationWithoutWaiting()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan!);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        if (!Deferred(buffer))
        {
            Detach(harness);
            harness.Shutdown();
            return;
        }

        // The submitted tick the page waits for is enough: the copy starts at once.
        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));
        Assert.Equal(1, harness.Cache.SharedMergesIssued);
        Assert.Equal(0u, Word(harness.Read(address, 0x100), Elsewhere));
        WaitUntilReadsComplete(harness);

        // The synchronization that uploads the CPU's write finds the snapshot done and merges it.
        harness.Worker.Run(() => _ = harness.Cache.ObtainBufferForImage(address, 256));

        Assert.Equal(1, harness.Cache.SharedMergesMergedWithoutWait);
        Assert.Equal(0, harness.Cache.SharedMergesWaitedInFlight);
        Assert.Equal(0, harness.Cache.SharedMergesIssuedAndWaited);
        Assert.Equal(0, harness.Cache.SharedMergesForcedFlush);
        Assert.Equal(0, harness.Cache.SharedMergesReissued);
        var guest = harness.Read(address, 0x100);
        Assert.Equal(CpuWord, Word(guest, CpuValue));
        Assert.Equal(StaleShaderWord, Word(guest, Reset));
        Assert.Equal(ShaderWord, Word(guest, Elsewhere));
        Assert.Equal(guest, harness.ReadBack(buffer, offset, 0x100));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void ASynchronizationNeedingAPageInFlightWaitsForItsReadbackOnly()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan!);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        if (!Deferred(buffer))
        {
            Detach(harness);
            harness.Shutdown();
            return;
        }

        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));
        Assert.Equal(1, harness.Cache.SharedMergesIssued);
        harness.Worker.Run(() => _ = harness.Cache.ObtainBufferForImage(address, 256));

        // Whether or not the copy had finished by then, it was the only thing waited for.
        Assert.Equal(1, harness.Cache.SharedMergesMergedWithoutWait + harness.Cache.SharedMergesWaitedInFlight);
        Assert.Equal(1, harness.Cache.SharedMergesIssued);
        Assert.Equal(0, harness.Cache.SharedMergesForcedFlush);
        Assert.Equal(ShaderWord, Word(harness.Read(address, 0x100), Elsewhere));
        Assert.Equal(harness.Read(address, 0x100), harness.ReadBack(buffer, offset, 0x100));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void APageWhoseTickIsSubmittedIsReadWithoutSubmittingAnything()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan!);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        if (!Deferred(buffer))
        {
            Detach(harness);
            harness.Shutdown();
            return;
        }

        var tick = harness.Scheduler.CurrentTick;
        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: true));

        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        Assert.Equal(1, harness.Cache.SharedMergesIssued);
        Assert.Equal(1, harness.Cache.SharedMergesIssuedAndWaited);
        Assert.Equal(0, harness.Cache.SharedMergesForcedFlush);
        Assert.Equal(ShaderWord, Word(harness.Read(address, 0x100), Elsewhere));
        Detach(harness);
        harness.Shutdown();
    }

    [Fact]
    public void ASnapshotOlderThanAnUploadStagedAfterItIsReissuedNotMerged()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan!);
        using var fatal = new FatalScope();
        Attach(harness, _vulkan!);
        var start = harness.MapBacked(0x30000, ReadWrite);
        var address = start + 0x10000;
        var (buffer, offset) = EstablishShadow(harness, address);
        CpuWritesThenShadersWrite(harness, address, buffer, offset);
        if (!Deferred(buffer))
        {
            Detach(harness);
            harness.Shutdown();
            return;
        }

        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));
        Assert.Equal(1, harness.Cache.SharedMergesIssued);
        // A replacement buffer copies the old owner into the open command buffer: the page's shadow
        // now expects bytes the snapshot, taken before that, does not hold.
        harness.Worker.Run(() => _ = harness.Cache.GetBuffer(harness.Cache.FindBuffer(start, 0x30000)));
        WaitUntilReadsComplete(harness);

        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: false));

        Assert.Equal(1, harness.Cache.SharedMergesReissued);
        Assert.Equal(0, harness.Cache.SharedMergesMergedWithoutWait);
        Assert.Equal(0u, Word(harness.Read(address, 0x100), Elsewhere));

        harness.Worker.Run(() => harness.Cache.ResolvePendingSharedMerges(address, Tracker, wait: true));

        Assert.Equal(2, harness.Cache.SharedMergesIssued);
        Assert.True(harness.Cache.SharedMergesForcedFlush >= 1);
        var guest = harness.Read(address, 0x100);
        Assert.Equal(CpuWord, Word(guest, CpuValue));
        Assert.Equal(ShaderWord, Word(guest, Elsewhere));
        Detach(harness);
        harness.Shutdown();
    }
}
