// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// The write generation lets GPU caches detect guest CPU rewrites even after
/// another cache owner consumed the (single) dirty flag: the generation is
/// monotonic and survives consume/re-arm cycles and range replacement. These
/// invariants back the presenter's stale-upload detection for CPU-rewritten
/// images (video planes, streamed font atlases).
/// </summary>
public sealed unsafe class GuestImageWriteTrackerTests
{
    // The tracker aligns to the guest's 4 KiB pages; the mprotect underneath
    // operates on host pages, which are 16 KiB on Apple Silicon (the emulator
    // itself always runs with 4 KiB host pages under Rosetta, but this test
    // host may not). Align the allocation to the largest host page size so
    // the kernel's rounding stays inside memory this test owns instead of
    // spilling onto neighbouring heap pages.
    private const nuint TrackedByteCount = 4096;
    private const nuint HostPageAlignment = 16384;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(
        nint lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    private static ulong AllocateTrackedPages(out void* allocation)
    {
        // VirtualProtect (Windows) / mprotect (POSIX) must target
        // VirtualAlloc/mmap pages. Protecting CRT heap pages poisons
        // neighbouring allocator metadata and crashes the test host.
        if (OperatingSystem.IsWindows())
        {
            var windowsAllocation = VirtualAlloc(
                0,
                HostPageAlignment,
                MemCommit | MemReserve,
                PageReadWrite);
            Assert.NotEqual(nint.Zero, windowsAllocation);
            allocation = (void*)windowsAllocation;
            return (ulong)windowsAllocation;
        }

        allocation = NativeMemory.AlignedAlloc(2 * HostPageAlignment, HostPageAlignment);
        return (ulong)allocation;
    }

    private static void FreeTrackedPages(void* allocation)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = VirtualFree((nint)allocation, 0, MemRelease);
            return;
        }

        NativeMemory.Free(allocation);
    }

    [Fact]
    public void GenerationSurvivesDirtyConsume()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(0, generation);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));

            // Consuming the dirty flag must not roll back the generation:
            // that is exactly what lets a second cache owner still observe
            // the rewrite after the first owner consumed the flag.
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationIncrementsOncePerArmedLifetime()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            // The first fault disarmed the range; later writes are free-running
            // and do not enter the handler until the owner re-arms.
            Assert.False(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);

            GuestImageWriteTracker.Rearm(address);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(2, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationCarriesAcrossRangeReplacement()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            // Re-registering the same allocation with a different size retires
            // the range object (the signal handler may still see the old
            // snapshot) but must carry the generation, otherwise a resize
            // would hide the rewrite from cache owners.
            GuestImageWriteTracker.Track(address, 2 * TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void UntrackedAddressHasNoGeneration()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Assert.False(GuestImageWriteTracker.TryGetWriteGeneration(0xDEAD_0000_0000UL, out _));
    }

    [Fact]
    public void WatchOnlyTrackDoesNotArmWriteProtection()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(
                address,
                TrackedByteCount,
                source: "test.watch-only",
                protect: false);
            Assert.True(
                GuestImageWriteTracker.TryGetProtectionState(
                    address,
                    out var protect,
                    out var armed));
            Assert.False(protect);
            Assert.False(armed);

            // Pages stay writable: a native store must not require a fault handler.
            *(byte*)address = 0xAB;
            Assert.Equal(0xAB, *(byte*)address);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void WatchOnlyManagedWriteMarksRangeDirty()
    {
        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(
                address,
                TrackedByteCount,
                source: "test.watch-only",
                protect: false);
            GuestImageWriteTracker.NotifyManagedWrite(address, sizeof(uint));
            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.True(
                GuestImageWriteTracker.TryGetProtectionState(
                    address,
                    out var protect,
                    out var armed));
            Assert.False(protect);
            Assert.False(armed);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void ReadSnapshotRejectsAnOverlappingWrite()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var allocation = HostMemory.Alloc(
            null,
            HostPageAlignment,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_READWRITE);
        Assert.NotEqual((nint)0, (nint)allocation);
        var address = (ulong)allocation;
        try
        {
            var snapshot = GuestImageWriteTracker.BeginReadSnapshot(
                address,
                TrackedByteCount,
                source: "test.snapshot");
            Assert.True(snapshot.Active);
            Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));

            var retry = GuestImageWriteTracker.BeginReadSnapshot(
                address,
                TrackedByteCount,
                source: "test.snapshot-retry");
            Assert.True(retry.Active);
            Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(retry));
            Assert.True(retry.Generation > snapshot.Generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            _ = HostMemory.Free(allocation, 0, HostMemory.MEM_RELEASE);
        }
    }

    [Fact]
    public void WatchOnlyManagedWriteIgnoresUnrelatedRange()
    {
        const ulong address = 0x0000_0002_0000_0000UL;
        try
        {
            GuestImageWriteTracker.Track(
                address,
                TrackedByteCount,
                source: "test.watch-only",
                protect: false);

            GuestImageWriteTracker.NotifyManagedWrite(
                address + (2 * (ulong)TrackedByteCount),
                sizeof(uint));

            Assert.False(GuestImageWriteTracker.PeekDirty(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(0, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
        }
    }

    [Fact]
    public void WatchOnlyManagedWriteMarksEveryOverlappingOwner()
    {
        const ulong first = 0x0000_0002_1000_0000UL;
        var second = first + 2048;
        try
        {
            GuestImageWriteTracker.Track(
                first,
                TrackedByteCount,
                source: "test.watch-only.first",
                protect: false);
            GuestImageWriteTracker.Track(
                second,
                TrackedByteCount,
                source: "test.watch-only.second",
                protect: false);

            GuestImageWriteTracker.NotifyManagedWrite(second, sizeof(uint));

            Assert.True(GuestImageWriteTracker.PeekDirty(first));
            Assert.True(GuestImageWriteTracker.PeekDirty(second));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(first);
            GuestImageWriteTracker.Untrack(second);
        }
    }

    [Fact]
    public void ProtectedTrackArmsWriteProtection()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.True(
                GuestImageWriteTracker.TryGetProtectionState(
                    address,
                    out var protect,
                    out var armed));
            Assert.True(protect);
            Assert.True(armed);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void ManagedWriterSnapshotDoesNotArmWriteProtection()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.TrackManagedWriter(
                address,
                TrackedByteCount,
                source: "test.managed-writer");

            var snapshot = GuestImageWriteTracker.BeginReadSnapshot(
                address,
                TrackedByteCount,
                source: "test.managed-writer-snapshot");

            Assert.True(snapshot.Active);
            Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
            Assert.True(
                GuestImageWriteTracker.TryGetProtectionState(
                    address,
                    out var protect,
                    out var armed));
            Assert.False(protect);
            Assert.False(armed);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void ManagedWriterNotificationInvalidatesReadSnapshot()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.TrackManagedWriter(
                address,
                TrackedByteCount,
                source: "test.managed-writer");
            var snapshot = GuestImageWriteTracker.BeginReadSnapshot(
                address,
                TrackedByteCount,
                source: "test.managed-writer-snapshot");

            GuestImageWriteTracker.NotifyManagedWrite(address, sizeof(uint));

            Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
            Assert.True(GuestImageWriteTracker.PeekDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void WriteFaultRemovesTheDirtyImagesPageWatchers()
    {
        if (!GuestImageWriteTracker.Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, 2 * TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            Assert.NotEqual(0u, HostMemory.Query((void*)address, out var firstPage));
            Assert.NotEqual(
                0u,
                HostMemory.Query(
                    (void*)(address + (ulong)TrackedByteCount),
                    out var secondPage));
            Assert.Equal(HostMemory.PAGE_READWRITE, firstPage.Protect & 0xFFu);
            Assert.Equal(HostMemory.PAGE_READWRITE, secondPage.Protect & 0xFFu);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void SharedPageStaysProtectedForAnotherImageOwner()
    {
        if (!GuestImageWriteTracker.Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        var overlapAddress = address + (ulong)TrackedByteCount;
        try
        {
            GuestImageWriteTracker.Track(address, 2 * TrackedByteCount);
            GuestImageWriteTracker.Track(overlapAddress, 2 * TrackedByteCount);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.False(GuestImageWriteTracker.PeekDirty(overlapAddress));
            Assert.NotEqual(0u, HostMemory.Query((void*)address, out var firstPage));
            Assert.NotEqual(
                0u,
                HostMemory.Query(
                    (void*)(address + (ulong)TrackedByteCount),
                    out var sharedPage));
            Assert.Equal(HostMemory.PAGE_READWRITE, firstPage.Protect & 0xFFu);
            Assert.Equal(HostMemory.PAGE_READONLY, sharedPage.Protect & 0xFFu);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            GuestImageWriteTracker.Untrack(overlapAddress);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void BoundaryPageFaultInvalidatesEveryPageOwner()
    {
        if (!GuestImageWriteTracker.Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        var page = AllocateTrackedPages(out var allocation);
        var first = page + 128;
        var second = page + 512;
        try
        {
            GuestImageWriteTracker.Track(first, 128);
            GuestImageWriteTracker.Track(second, 128);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(first));

            Assert.True(GuestImageWriteTracker.PeekDirty(first));
            Assert.True(GuestImageWriteTracker.PeekDirty(second));
            Assert.NotEqual(0u, HostMemory.Query((void*)page, out var pageInfo));
            Assert.Equal(HostMemory.PAGE_READWRITE, pageInfo.Protect & 0xFFu);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(first);
            GuestImageWriteTracker.Untrack(second);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void ProtectedTrackPreservesExecutePermission()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var allocation = HostMemory.Alloc(
            null,
            HostPageAlignment,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
            HostMemory.PAGE_EXECUTE_READWRITE);
        Assert.NotEqual((nint)0, (nint)allocation);
        var address = (ulong)allocation;
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount);
            Assert.NotEqual(0u, HostMemory.Query(allocation, out var armedInfo));
            Assert.Equal(
                HostMemory.PAGE_EXECUTE_READ,
                armedInfo.Protect & 0xFFu);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.NotEqual(0u, HostMemory.Query(allocation, out var writableInfo));
            Assert.Equal(
                HostMemory.PAGE_EXECUTE_READWRITE,
                writableInfo.Protect & 0xFFu);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            _ = HostMemory.Free(allocation, 0, HostMemory.MEM_RELEASE);
        }
    }

    [Fact]
    public void WatchOnlyTrackDoesNotDowngradeProtectedRange()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var address = AllocateTrackedPages(out var allocation);
        try
        {
            GuestImageWriteTracker.Track(address, TrackedByteCount, source: "test.rt");
            GuestImageWriteTracker.Track(
                address,
                TrackedByteCount,
                source: "test.texture-cache",
                protect: false);
            Assert.True(
                GuestImageWriteTracker.TryGetProtectionState(
                    address,
                    out var protect,
                    out var armed));
            Assert.True(protect);
            Assert.True(armed);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }
}
