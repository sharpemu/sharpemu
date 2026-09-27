// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Memory.HostViews;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

// The tracker over real host memory: host protection is read back with the platform query.
[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestPageTrackerTests : IDisposable
{
    private const ulong Page = TrackerLayout.PageBytes;
    private const ulong Region = TrackerLayout.BlockBytes;
    private const GuestPageProtection Read = GuestPageProtection.Read;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private sealed class TrackerFatalException(string message) : Exception(message);

    // Forwards to the real address space and records every protection call.
    private sealed class LoggingSpace(PhysicalVirtualMemory memory) : IGuestAddressSpace
    {
        public List<(ulong Address, ulong Size, GuestPageProtection Protection)> Protects { get; } = new();

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        {
            lock (Protects)
            {
                Protects.Add((address, size, protection));
            }

            return memory.TryProtect(address, size, protection);
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) =>
            memory.AllocateAt(desiredAddress, size, executable, allowAlternative);

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => memory.TryBackFixedRange(address, size, executable);

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) =>
            memory.TryAllocateAtOrAbove(desiredAddress, size, executable, alignment, out actualAddress);

        public bool TryEnsureRangeCommitted(ulong address, ulong size) => memory.TryEnsureRangeCommitted(address, size);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => memory.TryAllocateGuestMemory(size, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => memory.TryFreeGuestMemory(address);
    }

    private readonly Action<string> _previousFatal = PageGuard.OnFatal;
    private readonly IHostMemory _host = HostViewTestSupport.PlatformMemory;
    private readonly PhysicalVirtualMemory _memory;
    private readonly LoggingSpace _space;
    private readonly GuestGpuMemory _gpu;
    private readonly GuestPageTracker _tracker;

    public GuestPageTrackerTests()
    {
        PageGuard.OnFatal = message => throw new TrackerFatalException(message);
        _memory = new PhysicalVirtualMemory(hostMemory: _host);
        _space = new LoggingSpace(_memory);
        _gpu = new GuestGpuMemory(_space);
        _tracker = new GuestPageTracker(_gpu.Pages);
    }

    public void Dispose()
    {
        try
        {
            _gpu.Dispose();
        }
        finally
        {
            try { _memory.Dispose(); }
            finally { PageGuard.OnFatal = _previousFatal; }
        }
    }

    [NativePageProtectionFact]
    public void QueriesDoNotRequireMappedOwnership()
    {
        const ulong address = 0x2_0300_0000;

        Assert.Equal(0ul, _tracker.CountCpuWriteHotBytes(address, Page));
        Assert.False(_tracker.HasRegion(address, Page));
        Assert.True(_tracker.HasCpuDirtyPages(address, Page));
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        Assert.True(_tracker.HasRegion(address, Page));
        Assert.False(_tracker.HasRegion(address + Region, Page));
    }

    [NativePageProtectionFact]
    public void ConcurrentRegionPublicationKeepsInitialCpuOwnership()
    {
        var address = Allocate(1);
        var results = 0;
        using var start = new ManualResetEventSlim();
        var threads = new[] { Query(), Query() };
        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
        Assert.Equal(2, results);

        Thread Query() => new(() =>
        {
            start.Wait();
            if (_tracker.HasCpuDirtyPages(address, Page))
            {
                Interlocked.Increment(ref results);
            }
        });
    }

    [NativePageProtectionFact]
    public void CpuDirtyUploadArmsWriteProtectionAndExplicitDirtinessReleasesIt()
    {
        var address = Allocate(2);
        Assert.True(_tracker.HasCpuDirtyPages(address + 16, 32));

        var ranges = 0;
        var uploaded = false;
        _tracker.ForEachUploadRange(address + 16, 32, false, (uploadAddress, uploadSize) =>
        {
            Assert.Equal(address, uploadAddress);
            Assert.Equal(Page, uploadSize);
            ranges++;
        }, () => uploaded = true);

        Assert.Equal(1, ranges);
        Assert.True(uploaded);
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address));

        _tracker.MarkCpuDirtyPages(address + 16, 32);
        Assert.True(_tracker.HasCpuDirtyPages(address, Page));
        Assert.True(IsWritable(address));

        _tracker.UntrackMemory(address, Page * 2);
        Release(address, Page * 2);
    }

    // The lock-free block summary must agree with the precise page masks through every
    // CPU-dirty transition: upload, explicit mark, write-fault invalidation and GPU writes.
    [NativePageProtectionFact]
    public void CpuDirtySummaryFollowsEveryTransitionOfTheBlock()
    {
        var address = AllocateAligned(Region, Region);
        Assert.True(_tracker.HasCpuDirtyPages(address, Region));

        _tracker.ForEachUploadRange(address, Region, false, NoRange, NoUpload, preserveCpuWriteHotPages: false);
        Assert.False(_tracker.HasCpuDirtyPages(address, Region));
        Assert.False(_tracker.HasCpuDirtyPages(address + Region / 2, Page));

        _tracker.MarkCpuDirtyPages(address + Page * 3, 8);
        Assert.True(_tracker.HasCpuDirtyPages(address, Region));
        Assert.True(_tracker.HasCpuDirtyPages(address + Page * 3, Page));
        // A clean page inside a block that has a dirty page takes the precise path.
        Assert.False(_tracker.HasCpuDirtyPages(address + Page * 9, Page));

        _tracker.ForEachUploadRange(address, Region, false, NoRange, NoUpload, preserveCpuWriteHotPages: false);
        Assert.False(_tracker.HasCpuDirtyPages(address, Region));

        Assert.True(_tracker.InvalidateRegion(address + Page * 5, 4, () => { }));
        Assert.True(_tracker.HasCpuDirtyPages(address + Page * 5, Page));

        _tracker.ForEachUploadRange(address, Region, false, NoRange, NoUpload, preserveCpuWriteHotPages: false);
        _tracker.ForEachUploadRange(address + Page, Page, true, NoRange, NoUpload);
        Assert.False(_tracker.HasCpuDirtyPages(address, Region));
        Assert.True(_tracker.HasGpuDirtyPages(address + Page, Page));
        _tracker.ForEachDownloadRange(address + Page, Page, clear: true, null, NoRange);

        _tracker.UntrackMemory(address, Region);
        Assert.True(_tracker.HasCpuDirtyPages(address, Region));
        Release(address, Region);
    }

    [NativePageProtectionFact]
    public void RangeInvalidationBatchesOwnershipTransferAcrossRegions()
    {
        const ulong size = Region * 2;
        var address = AllocateAligned(size, Region);
        _tracker.ForEachUploadRange(address, size, true, NoRange, NoUpload);
        Assert.True(_tracker.HasGpuDirtyPages(address, size));
        Assert.False(IsWritable(address));

        var flushes = 0;
        _tracker.InvalidateRegion(address + 16, size - 32, () =>
        {
            flushes++;
            _tracker.ForEachDownloadRange(address + 16, size - 32, clear: true, null, NoRange);
            _tracker.MarkCpuDirtyPages(address + 16, size - 32);
        });

        Assert.Equal(1, flushes);
        Assert.False(_tracker.HasGpuDirtyPages(address, size));
        Assert.True(_tracker.HasCpuDirtyPages(address, size));
        Assert.True(IsWritable(address));
        Assert.True(IsWritable(address + size - 1));

        _tracker.InvalidateRegion(address + 16, size - 32, () => flushes++);
        Assert.Equal(1, flushes);

        _tracker.UntrackMemory(address, size);
        Release(address, size);
    }

    [NativePageProtectionFact]
    public void InvalidationAcceptsANewGenerationOfGpuOwnership()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));

        var flushes = 0;
        var uploads = 0;
        using var reacquire = new SemaphoreSlim(0);
        using var reacquired = new SemaphoreSlim(0);
        var publisher = new Thread(() =>
        {
            reacquire.Wait();
            _tracker.ForEachUploadRange(address + 16, 32, true, (_, _) => uploads++, NoUpload);
            reacquired.Release();
        });
        publisher.Start();
        _tracker.InvalidateRegion(address + 16, 32, () =>
        {
            flushes++;
            _tracker.ForEachDownloadRange(address + 16, 32, clear: true, null, NoRange);
            _tracker.MarkCpuDirtyPages(address + 16, 32);
            reacquire.Release();
            reacquired.Wait();
        });
        publisher.Join();

        Assert.Equal(1, flushes);
        Assert.Equal(1, uploads);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));
        Assert.False(IsWritable(address));

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionFact]
    public void GpuDirtyBitsStayInsideTheRequestedRange()
    {
        var address = Allocate(2);
        _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);

        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(_tracker.HasGpuDirtyPages(address + Page, Page));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address));

        _tracker.ClearGpuDirtyPages(address, Page);
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address));

        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page * 2);
        Release(address, Page * 2);
    }

    [NativePageProtectionFact]
    public void ExactDirtyIntervalsShareOneTrackerPage()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        var exactDirty = new SpanSet();
        exactDirty.Add(address + 64, 16);
        exactDirty.Add(address + 192, 32);

        ResetLog();
        _tracker.MarkGpuDirtyPages(address + 64, 16);
        _tracker.MarkGpuDirtyPages(address + 192, 32);
        Assert.Single(_space.Protects);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address));

        exactDirty.Remove(address + 64, 16);
        if (!exactDirty.Overlaps(address, Page))
        {
            _tracker.ClearGpuDirtyPages(address, Page);
        }

        Assert.Single(_space.Protects);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address));

        exactDirty.Remove(address + 192, 32);
        if (!exactDirty.Overlaps(address, Page))
        {
            _tracker.ClearGpuDirtyPages(address, Page);
        }

        Assert.Equal(2, _space.Protects.Count);
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address));

        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionFact]
    public void DownloadsMirrorTheProtectionOfEachSide()
    {
        var address = Allocate(4);
        _tracker.ForEachUploadRange(address, Page * 4, false, NoRange, NoUpload);
        _tracker.MarkGpuDirtyPages(address + 16, 32);
        _tracker.MarkGpuDirtyPages(address + Page * 2 + 16, 32);

        var visited = new List<(ulong Address, ulong Size)>();
        ResetLog();
        _tracker.ForEachDownloadRange(address, Page * 3, clear: false, null, (rangeAddress, rangeSize) => visited.Add((rangeAddress, rangeSize)));
        Assert.Equal(new[] { (address, Page), (address + Page * 2, Page) }, visited);
        Assert.Empty(_space.Protects);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page * 3));

        visited.Clear();
        _tracker.ForEachDownloadRange(address + 16, 32, clear: true, null, (rangeAddress, rangeSize) => visited.Add((rangeAddress, rangeSize)));
        Assert.Equal(new[] { (address, Page) }, visited);
        Assert.Equal(new[] { (address, Page, Read) }, _space.Protects);
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        Assert.True(_tracker.HasGpuDirtyPages(address + Page * 2, Page));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address + Page * 2));

        visited.Clear();
        ResetLog();
        _tracker.ForEachDownloadRange(address + 16, 32, clear: true, null, (rangeAddress, rangeSize) => visited.Add((rangeAddress, rangeSize)));
        Assert.Empty(visited);
        Assert.Empty(_space.Protects);
        Assert.True(_tracker.HasGpuDirtyPages(address + Page * 2, Page));

        _tracker.ClearGpuDirtyPages(address, Page * 3);
        Assert.False(_tracker.HasGpuDirtyPages(address, Page * 3));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address + Page * 2));

        ResetLog();
        _tracker.MarkCpuDirtyPages(address + 16, 32);
        Assert.Equal(new[] { (address, Page, ReadWrite) }, _space.Protects);
        Assert.True(IsWritable(address));
        Assert.False(IsWritable(address + Page));

        _tracker.UntrackMemory(address, Page * 4);
        Release(address, Page * 4);
    }

    [NativePageProtectionFact]
    public void CrossRegionUploadClearsAndProtectsBothRegions()
    {
        var address = AllocateAligned(Region * 2, Region);
        var boundary = address + Region;
        var ranges = 0;
        _tracker.ForEachUploadRange(boundary - Page, Page * 2, false, (_, _) => ranges++, NoUpload);

        Assert.Equal(2, ranges);
        Assert.False(_tracker.HasCpuDirtyPages(boundary - Page, Page * 2));
        Assert.False(IsWritable(boundary - Page));
        Assert.False(IsWritable(boundary));

        _tracker.MarkCpuDirtyPages(boundary - Page, Page * 2);
        _tracker.UntrackMemory(address, Region * 2);
        Release(address, Region * 2);
    }

    [NativePageProtectionFact]
    public void UploadDoesNotSerializeADisjointRegion()
    {
        var address = AllocateAligned(Region * 2, Region);
        var secondRegion = address + Region;
        Assert.True(_tracker.HasCpuDirtyPages(address, Page));
        Assert.True(_tracker.HasCpuDirtyPages(secondRegion, Page));

        using var uploadEntered = new SemaphoreSlim(0);
        using var finishUpload = new SemaphoreSlim(0);
        using var queryFinished = new SemaphoreSlim(0);
        var queryResult = false;
        var uploader = new Thread(() => _tracker.ForEachUploadRange(address, Page, true, NoRange, () =>
        {
            uploadEntered.Release();
            finishUpload.Wait();
        }));
        uploader.Start();
        uploadEntered.Wait();
        var query = new Thread(() =>
        {
            queryResult = _tracker.HasCpuDirtyPages(secondRegion, Page);
            queryFinished.Release();
        });
        query.Start();

        var completedWhileUploadBlocked = queryFinished.Wait(TimeSpan.FromSeconds(5));
        finishUpload.Release();
        uploader.Join();
        query.Join();

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Region * 2);
        Release(address, Region * 2);
        Assert.True(completedWhileUploadBlocked);
        Assert.True(queryResult);
    }

    [NativePageProtectionFact]
    public void DownloadDoesNotSerializeADisjointRegion()
    {
        var address = AllocateAligned(Region * 2, Region);
        var secondRegion = address + Region;
        _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);
        _tracker.ForEachUploadRange(secondRegion, Page, false, NoRange, NoUpload);

        using var downloadEntered = new SemaphoreSlim(0);
        using var finishDownload = new SemaphoreSlim(0);
        using var mutationFinished = new SemaphoreSlim(0);
        var downloader = new Thread(() => _tracker.ForEachDownloadRange(address, Page, clear: false, null, (_, _) =>
        {
            downloadEntered.Release();
            finishDownload.Wait();
        }));
        downloader.Start();
        downloadEntered.Wait();
        var mutation = new Thread(() =>
        {
            _tracker.MarkGpuDirtyPages(secondRegion, Page);
            mutationFinished.Release();
        });
        mutation.Start();

        var completedWhileDownloadBlocked = mutationFinished.Wait(TimeSpan.FromSeconds(5));
        finishDownload.Release();
        downloader.Join();
        mutation.Join();

        var bothGpuOwned = _tracker.HasGpuDirtyPages(address, Page) && _tracker.HasGpuDirtyPages(secondRegion, Page);
        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.ClearGpuDirtyPages(secondRegion, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(secondRegion, Page);
        _tracker.UntrackMemory(address, Region * 2);
        Release(address, Region * 2);
        Assert.True(completedWhileDownloadBlocked);
        Assert.True(bothGpuOwned);
    }

    [NativePageProtectionFact]
    public void GpuUnmarkUsesOneRegionMaskPerRegion()
    {
        var address = AllocateAligned(Region * 2, Region);
        var sparseBegin = address + Page;
        _tracker.ForEachUploadRange(sparseBegin, Page * 3, false, NoRange, NoUpload);
        _tracker.MarkGpuDirtyPages(sparseBegin, Page);
        _tracker.MarkGpuDirtyPages(sparseBegin + Page * 2, Page);

        ResetLog();
        _tracker.ClearGpuDirtyPages(sparseBegin, Page * 3);
        Assert.Equal(new[] { (sparseBegin, Page * 3, Read) }, _space.Protects);
        Assert.False(_tracker.HasGpuDirtyPages(sparseBegin, Page * 3));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(sparseBegin));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(sparseBegin + Page));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(sparseBegin + Page * 2));

        ResetLog();
        _tracker.ClearGpuDirtyPages(sparseBegin, Page * 3);
        Assert.Empty(_space.Protects);

        var boundary = address + Region;
        var crossBegin = boundary - Page;
        _tracker.ForEachUploadRange(crossBegin, Page * 2, false, NoRange, NoUpload);
        _tracker.MarkGpuDirtyPages(crossBegin, Page * 2);
        ResetLog();
        _tracker.ClearGpuDirtyPages(crossBegin, Page * 2);
        Assert.Equal(new[] { (crossBegin, Page, Read), (boundary, Page, Read) }, _space.Protects);
        Assert.False(_tracker.HasGpuDirtyPages(crossBegin, Page * 2));

        _tracker.UntrackMemory(address, Region * 2);
        Release(address, Region * 2);
    }

    [NativePageProtectionFact]
    public void FullRegionGpuUnmarkUsesOneProtectionRequest()
    {
        var address = AllocateAligned(Region, Region);
        _tracker.ForEachUploadRange(address, Region, false, NoRange, NoUpload);
        _tracker.MarkGpuDirtyPages(address, Region);
        Assert.True(_tracker.HasGpuDirtyPages(address, Region));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address + Region - Page));

        ResetLog();
        _tracker.ClearGpuDirtyPages(address, Region);
        Assert.Equal(new[] { (address, Region, Read) }, _space.Protects);
        Assert.False(_tracker.HasGpuDirtyPages(address, Region));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address));
        Assert.Equal(HostPageProtection.ReadOnly, Protection(address + Region - Page));

        _tracker.UntrackMemory(address, Region);
        Release(address, Region);
    }

    [NativePageProtectionFact]
    public void ExplicitCpuDirtinessOnGpuDirtyMemoryIsFatal()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);

        var fatal = Assert.Throws<TrackerFatalException>(() => _tracker.MarkCpuDirtyPages(address, Page));
        Assert.Equal("Cannot mark GPU-dirty pages as CPU-dirty.", fatal.Message);

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionFact]
    public void CleanUploadStillRunsCallbackAndTransfersWritableOwnership()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        var callbacks = 0;
        var ranges = 0;
        _tracker.ForEachUploadRange(address, Page, false, (_, _) => ranges++, () => callbacks++);
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        _tracker.ForEachUploadRange(address, Page, true, (_, _) => ranges++, () => callbacks++);
        Assert.Equal(0, ranges);
        Assert.Equal(2, callbacks);
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionFact]
    public void RepeatedCpuWritesKeepReadOnlyHotPagesWritableAndDirty()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);
        Assert.False(_tracker.IsCpuWriteHotRange(address, Page));

        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);
        Assert.True(_tracker.IsCpuWriteHotRange(address, Page));
        Assert.Equal(Page, _tracker.CountCpuWriteHotBytes(address, Page));
        Assert.Equal(123ul, _tracker.CountCpuWriteHotBytes(address + 7, 123));

        var uploads = 0;
        _tracker.ForEachUploadRange(address, Page, false, (_, _) => uploads++, NoUpload);
        _tracker.ForEachUploadRange(address, Page, false, (_, _) => uploads++, NoUpload);
        Assert.Equal(2, uploads);
        Assert.True(_tracker.HasCpuDirtyPages(address, Page));
        Assert.True(IsWritable(address));

        _tracker.ForEachUploadRange(address, Page, true, (_, _) => uploads++, NoUpload);
        Assert.Equal(3, uploads);
        Assert.False(_tracker.IsCpuWriteHotRange(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));
        Assert.Equal(0ul, _tracker.CountCpuWriteHotBytes(address, Page));
        Assert.True(_tracker.HasGpuDirtyPages(address, Page));

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrackedReadOnlyUploadRearmsHotPagesAndRetriesFailures(bool failUpload)
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);
        Assert.True(_tracker.IsCpuWriteHotRange(address, Page));

        if (failUpload)
        {
            Assert.Throws<InvalidOperationException>(() => _tracker.ForEachUploadRange(
                address, Page, false, NoRange, () => throw new InvalidOperationException(),
                preserveCpuWriteHotPages: false));
            Assert.True(_tracker.HasCpuDirtyPages(address, Page));
            Assert.True(IsWritable(address));
        }

        var uploads = 0;
        for (var repeat = 0; repeat < 3; repeat++)
            _tracker.ForEachUploadRange(address, Page, false, (_, _) => uploads++, NoUpload,
                preserveCpuWriteHotPages: false);
        Assert.Equal(1, uploads);
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));
        Assert.False(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(IsWritable(address));
        Assert.True(_tracker.IsCpuWriteHotRange(address, Page));

        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.ForEachUploadRange(address, Page, false, (_, _) => uploads++, NoUpload,
            preserveCpuWriteHotPages: false);
        Assert.Equal(2, uploads);
        Assert.False(IsWritable(address));

        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionFact]
    public void ReadOnlyUploadClearsColdPagesAndPreservesHotPages()
    {
        var address = Allocate(2);
        _tracker.ForEachUploadRange(address, Page * 2, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.ForEachUploadRange(address, Page, false, NoRange, NoUpload);
        _tracker.MarkCpuDirtyPages(address, Page);

        var uploads = new List<(ulong Address, ulong Size)>();
        _tracker.ForEachUploadRange(address, Page * 2, false, (rangeAddress, rangeSize) => uploads.Add((rangeAddress, rangeSize)), NoUpload);
        Assert.Single(uploads);
        Assert.Equal((address, Page), uploads[0]);
        Assert.True(_tracker.HasCpuDirtyPages(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address + Page, Page));
        Assert.Equal(Page, _tracker.CountCpuWriteHotBytes(address, Page * 2));
        Assert.Equal(7ul, _tracker.CountCpuWriteHotBytes(address + Page - 7, 14));
        Assert.True(IsWritable(address));
        Assert.False(IsWritable(address + Page));

        _tracker.UntrackMemory(address, Page * 2);
        Release(address, Page * 2);
    }

    [NativePageProtectionFact]
    public void ReenteringTheTrackerFromAnUploadCallbackIsFatal()
    {
        var address = Allocate(1);

        var fatal = Assert.Throws<TrackerFatalException>(() =>
            _tracker.ForEachUploadRange(address, Page, false, NoRange, () => _tracker.HasCpuDirtyPages(address, Page)));
        Assert.Equal("Cannot enter the memory tracker from its upload callback.", fatal.Message);

        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    [NativePageProtectionTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void FailedUploadReturnsTheRunsToTheCpuAndReleasesEveryLock(bool isWritten, bool failInRange)
    {
        var address = AllocateAligned(Region * 2, Region);
        var boundary = address + Region;
        var ranges = 0;

        // The second region has already been cleared when the callback fails.
        var failure = Assert.Throws<InvalidOperationException>(() => _tracker.ForEachUploadRange(
            boundary - Page, Page * 2, isWritten,
            (_, _) =>
            {
                if (failInRange && ++ranges == 2)
                {
                    throw new InvalidOperationException("range");
                }
            },
            () => throw new InvalidOperationException("upload")));
        Assert.Equal(failInRange ? "range" : "upload", failure.Message);

        Assert.True(_tracker.HasCpuDirtyPages(boundary - Page, Page * 2));
        Assert.False(_tracker.HasGpuDirtyPages(boundary - Page, Page * 2));
        Assert.True(IsWritable(boundary - Page));
        Assert.True(IsWritable(boundary));

        // Both region locks are free again: the same thread can upload the range once more.
        var retried = 0;
        _tracker.ForEachUploadRange(boundary - Page, Page * 2, false, (_, _) => retried++, NoUpload);
        Assert.Equal(2, retried);
        _tracker.MarkCpuDirtyPages(boundary - Page, Page * 2);

        _tracker.UntrackMemory(address, Region * 2);
        Release(address, Region * 2);
    }

    [NativePageProtectionFact]
    public void FailedReadOnlyUploadKeepsAnOwnershipInstalledAfterItsLockOpened()
    {
        var address = Allocate(2);
        using var released = new SemaphoreSlim(0);
        using var proceed = new SemaphoreSlim(0);
        var writer = new Thread(() =>
        {
            released.Wait();
            _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);
            proceed.Release();
        });
        writer.Start();

        // The read-only upload has released its region lock when uploadFunc runs; the writer
        // takes only the first of the two cleared pages.
        Assert.Throws<InvalidOperationException>(() => _tracker.ForEachUploadRange(address, Page * 2, false, NoRange, () =>
        {
            released.Release();
            proceed.Wait();
            throw new InvalidOperationException("upload");
        }));
        writer.Join();

        Assert.True(_tracker.HasGpuDirtyPages(address, Page));
        Assert.False(_tracker.HasCpuDirtyPages(address, Page));
        Assert.Equal(HostPageProtection.NoAccess, Protection(address));
        Assert.False(_tracker.HasGpuDirtyPages(address + Page, Page));
        Assert.True(_tracker.HasCpuDirtyPages(address + Page, Page));
        Assert.True(IsWritable(address + Page));

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page * 2);
        Release(address, Page * 2);
    }

    [NativePageProtectionFact]
    public void UntrackingGpuDirtyMemoryIsFatal()
    {
        var address = Allocate(1);
        _tracker.ForEachUploadRange(address, Page, true, NoRange, NoUpload);

        var fatal = Assert.Throws<TrackerFatalException>(() => _tracker.UntrackMemory(address, Page));
        Assert.Equal("Cannot remove tracking while the memory is GPU-dirty.", fatal.Message);

        _tracker.ClearGpuDirtyPages(address, Page);
        _tracker.MarkCpuDirtyPages(address, Page);
        _tracker.UntrackMemory(address, Page);
        Release(address, Page);
    }

    private static void NoRange(ulong address, ulong size)
    {
    }

    private static void NoUpload()
    {
    }

    private ulong Allocate(ulong pages) => AllocateAligned(pages * Page, Page);

    // Allocate inside the low 2^40 bytes covered by the tracker.
    private ulong AllocateAligned(ulong size, ulong alignment)
    {
        Assert.True(_memory.TryAllocateAtOrAbove(0x2_0000_0000, size, executable: false, alignment, out var address));
        Assert.True(address % alignment == 0 && address + size < TrackerLayout.SpaceBytes);
        _gpu.Register(address, size, ReadWrite);
        return address;
    }

    private void Release(ulong address, ulong size) => _gpu.Unregister(address, size);

    private HostPageProtection Protection(ulong address)
    {
        Assert.True(_host.Query(address, out var info));
        return info.Protection;
    }

    private bool IsWritable(ulong address) => Protection(address) == HostPageProtection.ReadWrite;

    private void ResetLog()
    {
        lock (_space.Protects)
        {
            _space.Protects.Clear();
        }
    }
}
