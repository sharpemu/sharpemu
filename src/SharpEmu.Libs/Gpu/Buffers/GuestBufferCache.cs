// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

public readonly record struct DownloadPiece(GpuBuffer Buffer, ulong SourceOffset, ulong Address, ulong Size);

public readonly record struct OverlapSpan(int First, int Last, ulong Begin, ulong End, bool HasStreamLeap);

// Guest memory mirrored in device buffers: upload on use, download on CPU fault, page table for BDA.
public sealed unsafe class GuestBufferCache : IGuestBufferStore, IDisposable
{
    public const int CachingPageBits = 14;
    public const ulong CachingPageSize = 1UL << CachingPageBits;
    public const ulong CachingPageCount = 1UL << (40 - CachingPageBits);
    public const ulong BdaPageTableSize = CachingPageCount * sizeof(ulong);
    public static readonly ResourceSlotIdentifier NullBufferId = new(0, 1);

    private const ulong MiB = 1024 * 1024;
    private const ulong GdsBufferSize = 64 * 1024;
    private readonly record struct PlannedDownload(GpuBuffer Buffer, ulong Address, BufferDownloadPlacement Placement);

    private enum ShutdownOutcome
    {
        Pending,
        Drained,
        Failed,
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly IGpuQueueRelay _relay;
    private readonly GuestBufferUploader _uploader;
    private readonly IGuestBackedSpace _backing;
    private readonly ICpuMemory _guest;

    // False for addresses the guest never mapped. A shader that follows a bad pointer there
    // reads zeros, as on the console; caching that memory only fills VRAM with junk buffers.
    internal bool IsGuestMemoryMapped(ulong guestAddress, ulong size) => _guest.CanRead(guestAddress, size);
    private readonly BdaFaultProcessor _faults;
    private readonly GpuBuffer _gds;
    private readonly GpuBuffer _bdaPageTable;
    private readonly GuestBufferRegistry<GpuBuffer> _registry = new(CachingPageSize, PageOwnerTable.AddressSpaceSize);
    private readonly AvlSpanSet _gpuModifiedRanges = new();
    private long _gpuModifiedVersion;
    private readonly GuestPageTracker _tracker;
    private readonly GpuRingBuffer _staging;
    private readonly GpuRingBuffer _stream;
    private readonly GpuRingBuffer _download;
    private readonly GpuRingBuffer _deviceRing;
    private readonly object _shutdownGate = new();
    private readonly BufferRetirementPolicy _retirementPolicy = new();
    private ShutdownOutcome _outcome;
    private bool _faultProcessPending;
    private bool _disposed;

    public GuestBufferCache(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        IGpuQueueRelay relay,
        PageGuard pages,
        ICpuMemory guest,
        IGuestBackedSpace backing)
    {
        _device = device;
        _scheduler = scheduler;
        _relay = relay;
        _backing = backing;
        _guest = guest;
        _faults = new BdaFaultProcessor(device, scheduler, this, CachingPageBits, CachingPageCount);
        _gds = new GpuBuffer(device, scheduler, GpuBufferUsage.Stream, 0, GpuBuffer.AllFlags, GdsBufferSize);
        _bdaPageTable = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, BdaPageTableSize);
        _tracker = new GuestPageTracker(pages);
        _staging = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Upload, 512 * MiB);
        _uploader = new GuestBufferUploader(device, scheduler, guest, _staging);
        _stream = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Stream, 64 * MiB);
        _download = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Download, 64 * MiB);
        _deviceRing = new GpuRingBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 128 * MiB);
        StreamOffsetAlignment = device.MinUniformBufferOffsetAlignment;
        _gds.Mapped.Clear();
        _gds.Flush(0, _gds.Size);
        var nullId = _registry.AllocateBuffer(new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 16), 0, 16);
        if (nullId != NullBufferId)
        {
            throw SubmissionScheduler.Fatal("The null buffer occupies the wrong slot.");
        }
    }

    public IGuestImageCache? ImageCache { get; set; }

    // An overlaid read gets pending dump values copied in by OverlayGuestPublications instead of waiting.
    internal enum PublicationAccess { Read, OverlaidRead, Write }

    internal Action<ulong, ulong, PublicationAccess>? CompleteGuestPublications { get; set; }

    // Arguments: buffer, offset of the guest address in that buffer, guest address, size.
    internal Action<GpuBuffer, ulong, ulong, ulong>? OverlayGuestPublications { get; set; }

    public GpuBuffer GdsBuffer => _gds;

    public GpuBuffer BdaPageTableBuffer => _bdaPageTable;

    public GpuBuffer FaultBuffer => _faults.FaultBuffer;

    public ulong TotalUsedMemory => _registry.RegisteredBytes;

    public int BufferCount => _registry.RegisteredCount;

    // The stream fast path aligns to this; the presenter raises it to its descriptor alignment.
    public ulong StreamOffsetAlignment { get; set; }

    public void ForEachBuffer(Action<GpuBuffer> visit)
    {
        for (var index = 0; index < _registry.RegisteredCount; index++)
        {
            visit(_registry.GetBuffer(_registry.GetRegisteredIdentifier(index)));
        }
    }

    public GpuBuffer GetBuffer(ResourceSlotIdentifier bufferIdentifier) => _registry.GetBuffer(bufferIdentifier);

    public GpuRingBuffer GetUtilityBuffer(GpuBufferUsage usage) => usage switch
    {
        GpuBufferUsage.Upload => _staging,
        GpuBufferUsage.Stream => _stream,
        GpuBufferUsage.Download => _download,
        GpuBufferUsage.DeviceLocal => _deviceRing,
        _ => throw SubmissionScheduler.Fatal("The utility buffer usage is invalid."),
    };

    // A CPU write fault: true when the range is tracked and any GPU data reached guest memory.
    bool IGuestBufferStore.MarkCpuWrite(ulong address, ulong size)
    {
        var tracked = _tracker.InvalidateRegion(address, size, out var needsGpuFlush);
        var completed = !needsGpuFlush || ReadMemoryOrAwaitShutdown(address, size, isWrite: true,
            GuestMemoryProfile.ReadbackSource.CpuWriteInvalidation);
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-write tracked={tracked} completed={completed}");
        return tracked && completed;
    }

    public bool TrySynchronizeCpuRead(ulong address, ulong size) =>
        TrySynchronizeCpuRead(address, size, GuestMemoryProfile.ReadbackSource.CpuReadSynchronization);

    public bool TrySynchronizeCpuRead(ulong address, ulong size, GuestMemoryProfile.ReadbackSource source) =>
        ResolveSharedMergesForCpuRead(address, size) &&
        (!_tracker.MayHaveGpuDirtyPages(address, size) ||
         !_tracker.HasGpuDirtyPages(address, size) ||
         ReadMemoryOrAwaitShutdown(address, size, isWrite: false, source));

    // A CPU-dirty page keeps no GPU ownership, so a read of it takes no fault: the callers that
    // synchronize a read are the only place its pending shader bytes can reach guest memory first.
    private bool ResolveSharedMergesForCpuRead(ulong address, ulong size)
    {
        if (!HasPendingSharedMerges(address, size))
        {
            return true;
        }

        if (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation)
        {
            return true;
        }

        return _relay.TryRunOnGpuQueue(() => ResolvePendingSharedMerges(address, size, wait: true)) || AwaitShutdown();
    }

    // Lock-free on the GPU queue thread; see GuestPageTracker.MayHaveGpuDirtyPages.
    public bool MayHaveGpuDirtyPages(ulong address, ulong size) => _tracker.MayHaveGpuDirtyPages(address, size);

    // A CPU read fault: GPU-dirty pages download through the worker first.
    public bool DownloadToCpu(ulong address, ulong size)
    {
        var tracked = _tracker.HasRegion(address, size);
        var dirty = tracked && _tracker.HasGpuDirtyPages(address, size);
        var completed = tracked && (!dirty || ReadMemoryOrAwaitShutdown(address, size, isWrite: false,
            GuestMemoryProfile.ReadbackSource.StoreDownload));
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-read tracked={tracked} gpu_dirty={dirty} completed={completed}");
        return completed;
    }

    public void InvalidateMemory(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The memory invalidation range is invalid.");
        }

        _tracker.InvalidateRegion(guestAddress, size, () => ReadMemory(guestAddress, size, isWrite: true));
    }

    public void ReadMemory(ulong guestAddress, ulong size, bool isWrite = false)
    {
        if (!ReadMemoryOrAwaitShutdown(guestAddress, size, isWrite))
        {
            throw SubmissionScheduler.Fatal($"Cannot download buffer data after a failed shutdown: addr=0x{guestAddress:X16} size=0x{size:X16}");
        }
    }

    public ResourceSlotIdentifier FindBuffer(ulong guestAddress, ulong size)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferCacheLookup);
        if (guestAddress == 0)
        {
            return NullBufferId;
        }

        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The buffer lookup range is invalid.");
        }

        var owner = _registry.FindContainingBuffer(guestAddress, size);
        if (owner.IsValid)
        {
            return owner;
        }

        return CreateBuffer(guestAddress, size);
    }

    // Resolves every allocation merge the ranges can trigger, so a draw does not capture a buffer
    // handle that a later acquisition of the same draw replaces.
    public void PrepareBufferAllocations(ReadOnlySpan<GuestSpan> ranges)
    {
        // Draws have few ranges; keep that common case off the managed heap.
        Span<GuestSpan> pages = ranges.Length <= 64 ? stackalloc GuestSpan[ranges.Length] : new GuestSpan[ranges.Length];
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (!IsValidRange(range.Address, range.Size) ||
                !PageOwnerTable.TryGetPageRange(range.Address, range.Size, out var first, out var end))
                throw SubmissionScheduler.Fatal($"The prepared buffer allocation range is invalid: address=0x{range.Address:X16} size=0x{range.Size:X16}.");
            pages[index] = new GuestSpan(first * CachingPageSize, (end - first) * CachingPageSize);
        }
        pages.Sort(static (left, right) => left.Address.CompareTo(right.Address));

        for (var index = 0; index < pages.Length;)
        {
            var range = pages[index];
            var next = index + 1;
            // AcquireVertexBuffers also coalesces touching byte ranges. Reserve their
            // whole union now, so its later acquisition cannot replace a shader binding.
            while (next < pages.Length && pages[next].Address <= range.End)
            {
                range = new GuestSpan(range.Address, Math.Max(range.End, pages[next].End) - range.Address);
                next++;
            }

            // Disjoint read-only geometry can still use the stream ring, without another cache allocation.
            if (next > index + 1 || _registry.HasOverlap(range.Address, range.Size))
                _ = FindBuffer(range.Address, range.Size);
            index = next;
        }
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBuffer(ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer = false,
        ResourceSlotIdentifier bufferIdentifier = default, ulong traceShaderHash = 0, bool requiresDeviceAddress = false)
    {
        CompleteGuestPublications?.Invoke(guestAddress, size, isWritten ? PublicationAccess.Write : PublicationAccess.OverlaidRead);
        if (isWritten && ImageClearTrace.Enabled) ImageCache?.TraceGuestWrite("shader-buffer-write", guestAddress, size, shaderHash: traceShaderHash);
        if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-obtain", guestAddress, size, $"written={isWritten} texel={isTexelBuffer} hash=0x{traceShaderHash:X16}", remember: true);
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferAcquisitionChecks);
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("A buffer request requires a command buffer that is recording.");
        }

        if (isWritten && isTexelBuffer)
        {
            // Preserving a partial image write can merge this buffer with the image's whole
            // backing range. Finish that before returning a handle the shader will write.
            RequireImageCache().InvalidateMemoryFromGpu(guestAddress, size);
        }

        // A caller that needs a physical address cannot take the transient stream copy.
        if (!requiresDeviceAddress && !isWritten &&
            !_tracker.HasGpuDirtyPages(guestAddress, size) &&
            _tracker.HasCpuDirtyPages(guestAddress, size) &&
            (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size))))
        {
            using var streamProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStreamUpload);
            if (_stream.TryMap(size, out var streamOffset, StreamOffsetAlignment, allowWait: false) &&
                _backing.TryReadBacking(guestAddress, _stream.Mapped.Slice((int)streamOffset, (int)size)))
            {
                _stream.Commit();
                if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-stream-upload", guestAddress, size, $"offset=0x{streamOffset:X}");
                OverlayGuestPublications?.Invoke(_stream, streamOffset, guestAddress, size);
                return (_stream, streamOffset);
            }
        }

        // A GPU write into memory without backing could never download later; refuse it now.
        if (isWritten && !_backing.IsBackedRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{size:X16}");
        }

        var buffer = _registry.TryGetRegisteredBuffer(bufferIdentifier);
        if (buffer == null || !buffer.IsInBounds(guestAddress, size))
        {
            bufferIdentifier = FindBuffer(guestAddress, size);
            buffer = _registry.GetBuffer(bufferIdentifier);
        }

        TouchBuffer(bufferIdentifier);
        // A persistent upload must track the next CPU write instead of uploading unchanged hot pages.
        // Streaming keeps hot pages writable; the fallback must restore protection before copying.
        _ = SynchronizeBuffer(buffer, guestAddress, size, isWritten, isTexelBuffer, preserveCpuWriteHotPages: false);
        if (isWritten)
        {
            buffer.NoteGpuWrite(buffer.Offset(guestAddress), size);
            if (Diagnostics.DccWriterTrace.Enabled)
            {
                Diagnostics.DccWriterTrace.Record(RequireImageCache(), guestAddress, size);
                Diagnostics.DccWriterTrace.RecordWrite(guestAddress, size);
            }

            if (!_gpuModifiedRanges.Contains(guestAddress, size))
            {
                _gpuModifiedRanges.Add(guestAddress, size);
                Interlocked.Increment(ref _gpuModifiedVersion);
            }

            NoteHotWindowWrite(guestAddress, size);
        }
        else OverlayGuestPublications?.Invoke(buffer, buffer.Offset(guestAddress), guestAddress, size);

        return (buffer, buffer.Offset(guestAddress));
    }

    // The image cache publishes an image's GPU-only contents into the buffer over the same bytes
    // before it drops the image. The buffer then owns them, as after any GPU write.
    public GpuBuffer? ObtainBufferForImageWriteBack(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size) || !_backing.IsBackedRange(guestAddress, size))
        {
            return null;
        }

        return ObtainBuffer(guestAddress, size, isWritten: true).Buffer;
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBufferForImage(ulong guestAddress, ulong size)
    {
        CompleteGuestPublications?.Invoke(guestAddress, size, PublicationAccess.Read);
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The image source range is invalid.");
        }

        var cpuModified = _tracker.HasCpuDirtyPages(guestAddress, size);
        var gpuModified = _tracker.HasGpuDirtyPages(guestAddress, size);
        var hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(guestAddress, size);
        _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, guestAddress, size, "image source");

        var owner = FindOwner(guestAddress, size);
        if (hasDirtyBufferSource && owner == null)
        {
            if (!IsRegionRegistered(guestAddress, size))
            {
                throw SubmissionScheduler.Fatal("The GPU-dirty image source has no device buffer.");
            }

            owner = _registry.GetBuffer(FindBuffer(guestAddress, size));
        }

        if (owner != null && (!gpuModified || hasDirtyBufferSource))
        {
            TouchBuffer(owner);
            if (cpuModified)
            {
                // Tracking clears complete pages, even for a subpage image. The regular
                // uploader stages those complete runs; staging only the image would copy
                // unprepared bytes over neighbouring textures and mark them clean.
                _ = SynchronizeBuffer(owner, guestAddress, size, isWritten: false, isTexelBuffer: true,
                    preserveCpuWriteHotPages: false, readImageBacking: true);
            }

            return (owner, owner.Offset(guestAddress));
        }

        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("Cannot find the device buffer that owns the GPU-dirty image source.");
        }

        if (!_staging.TryMap(size, out var stageOffset, 16))
        {
            throw SubmissionScheduler.Fatal(
                $"Cannot reserve image staging space: address=0x{guestAddress:X16} size=0x{size:X16} capacity=0x{_staging.Size:X16} tick={_scheduler.CurrentTick}.");
        }
        if (!ResidentImageBacking.TryRead(_backing, guestAddress, _staging.Mapped.Slice((int)stageOffset, (int)size)))
        {
            throw SubmissionScheduler.Fatal(
                $"Could not read the mapped guest image backing: address=0x{guestAddress:X16} size=0x{size:X16} range_backed={_backing.IsBackedRange(guestAddress, size)} first_byte_backed={_backing.IsBackedRange(guestAddress, 1)} last_byte_backed={_backing.IsBackedRange(guestAddress + size - 1, 1)} tick={_scheduler.CurrentTick}.");
        }

        _staging.Commit();
        hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(guestAddress, size);
        owner = FindOwner(guestAddress, size);
        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("The GPU-dirty image source lost its device buffer owner.");
        }

        if (owner == null || (_tracker.HasGpuDirtyPages(guestAddress, size) && !hasDirtyBufferSource))
        {
            return (_staging, stageOffset);
        }

        TouchBuffer(owner);
        // Tracking reports whole dirty pages, which can extend beyond the image's
        // staging slice. Upload those pages through the normal buffer uploader so
        // every byte marked clean is copied from its actual guest backing.
        _ = SynchronizeBuffer(owner, guestAddress, size, isWritten: false, isTexelBuffer: true,
            preserveCpuWriteHotPages: false, readImageBacking: true);
        return (owner, owner.Offset(guestAddress));
    }

    private GuestBufferSourceReader? _tryReadImageSource;

    // Image acquisition already holds the image-cache lock. Read CPU-owned backing
    // directly instead of faulting through guest memory and reentering that cache.
    private bool TryReadImageSource(ulong address, Span<byte> destination) =>
        ResidentImageBacking.TryRead(_backing, address, destination);

    public void WriteHostMemory(ulong guestAddress, ReadOnlySpan<byte> data)
    {
        CompleteGuestPublications?.Invoke(guestAddress, (ulong)data.Length, PublicationAccess.Write);
        if (guestAddress == 0 || data.IsEmpty || (ulong)data.Length > ulong.MaxValue - guestAddress)
        {
            throw SubmissionScheduler.Fatal("The host DMA write range is invalid.");
        }

        GpuWritebackEpoch.Advance();
        if (!_backing.TryWriteBacking(guestAddress, data))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{data.Length:X16}");
        }

        // Registered buffers are ordered and disjoint: walk only the ones the write overlaps.
        var end = guestAddress + (ulong)data.Length;
        for (var index = _registry.FindFirstOverlappingIndex(guestAddress);
             index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end;
             index++)
        {
            var address = _registry.GetRegisteredAddress(index);
            var bufferIdentifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(bufferIdentifier);
            var begin = Math.Max(guestAddress, address);
            var rangeEnd = Math.Min(end, address + buffer.Size);
            if (begin >= rangeEnd)
            {
                continue;
            }

            WriteDataBuffer(buffer, begin, data.Slice((int)(begin - guestAddress), (int)(rangeEnd - begin)));
            TouchBuffer(bufferIdentifier);
        }
    }

    public void FillBuffer(ulong guestAddress, ulong size, uint value, bool isGds) =>
        FillBuffer(guestAddress, size, value, isGds, publishFill: false);

    private void FillBuffer(ulong guestAddress, ulong size, uint value, bool isGds, bool publishFill)
    {
        if (!isGds) CompleteGuestPublications?.Invoke(guestAddress, size, PublicationAccess.Write);
        if ((guestAddress & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - guestAddress)
        {
            throw SubmissionScheduler.Fatal("The fill range must be aligned to four bytes.");
        }

        if (isGds)
        {
            if (guestAddress > _gds.Size || size > _gds.Size - guestAddress)
            {
                throw SubmissionScheduler.Fatal("The GDS fill range is outside the buffer.");
            }

            _gds.Fill(guestAddress, size, value);
            return;
        }

        if (guestAddress == 0)
        {
            throw SubmissionScheduler.Fatal("The fill memory address is invalid.");
        }

        var images = RequireImageCache();
        if (ImageClearTrace.Enabled) images.TraceGuestWrite("command-fill", guestAddress, size, value);
        images.TraceMetadataFill(guestAddress, size, value);
        _ = images.ClearMetadata(guestAddress);
        var region = images.QueryRegion(guestAddress, size);
        if (!HasGpuDirtyBytes(guestAddress, size) && !region.GpuImageBytes)
        {
            if (region.ImageBytes)
            {
                images.InvalidateMemory(guestAddress, size);
            }

            // Labels and small clears dominate; fill a stack chunk once and repeat it.
            Span<uint> values = stackalloc uint[(int)Math.Min(size / sizeof(uint), 1024UL)];
            values.Fill(value);
            var bytes = MemoryMarshal.AsBytes(values);
            for (ulong offset = 0; offset < size;)
            {
                var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                WriteHostMemory(guestAddress + offset, bytes[..chunk]);
                offset += (ulong)chunk;
            }

            return;
        }

        images.InvalidateMemoryFromGpu(guestAddress, size);
        var bufferIdentifier = FindBuffer(guestAddress, size);
        if (Diagnostics.DccWriterTrace.Enabled)
        {
            Diagnostics.DccWriterTrace.CurrentKind = "fill";
        }

        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, true, true, bufferIdentifier);
        destination.Fill(destinationOffset, size, value);
        if ((publishFill || images.OverlapsDccMetadata(guestAddress, size)) && TryWriteFillToBacking(guestAddress, size, value))
        {
            _gpuModifiedRanges.Remove(guestAddress, size);
            Interlocked.Increment(ref _gpuModifiedVersion);
            ClearGpuDirtyPagesOfRemovedRange(guestAddress, size);
        }
    }

    // After a range left the GPU-modified set: the pages inside it are clean in one tracker walk,
    // and only a page the range shares at either end can still hold another GPU-modified range.
    private void ClearGpuDirtyPagesOfRemovedRange(ulong guestAddress, ulong size)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        var end = guestAddress + size;
        var firstPage = guestAddress & ~(pageBytes - 1);
        var lastPage = (end - 1) & ~(pageBytes - 1);
        var startPartial = guestAddress != firstPage;
        var endPartial = end != lastPage + pageBytes;
        var innerBegin = startPartial ? firstPage + pageBytes : firstPage;
        var innerEnd = endPartial ? lastPage : end;
        if (innerBegin < innerEnd)
        {
            _tracker.ClearGpuDirtyPages(innerBegin, innerEnd - innerBegin);
        }

        if (startPartial)
        {
            ClearGpuDirtyPageIfUnmodified(firstPage);
        }

        if (endPartial && (lastPage != firstPage || !startPartial))
        {
            ClearGpuDirtyPageIfUnmodified(lastPage);
        }
    }

    private void ClearGpuDirtyPageIfUnmodified(ulong page)
    {
        if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
        {
            _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
        }
    }

    public void FillDccMetadata(ulong guestAddress, ulong size, uint value)
    {
        if (guestAddress == 0 || (guestAddress & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - guestAddress ||
            HasGpuDirtyBytes(guestAddress, size))
        {
            FillBuffer(guestAddress, size, value, false, publishFill: true);
            return;
        }

        // Images over the range read it again; only GPU-owned image bytes need the general fill.
        // The buffer copy is filled on the GPU rather than uploaded from the written bytes.
        var images = RequireImageCache();
        var region = images.QueryRegion(guestAddress, size);
        if (region.GpuImageBytes)
        {
            FillBuffer(guestAddress, size, value, false, publishFill: true);
            return;
        }

        if (region.ImageBytes)
        {
            images.InvalidateMemory(guestAddress, size);
        }

        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, false, false, FindBuffer(guestAddress, size));
        destination.Fill(destinationOffset, size, value);
        if (!TryWriteFillToBacking(guestAddress, size, value))
        {
            FillBuffer(guestAddress, size, value, false, publishFill: true);
        }
    }

    private bool TryWriteFillToBacking(ulong guestAddress, ulong size, uint value)
    {
        // Metadata fills repeat every frame: the pattern buffer is pooled.
        var count = (int)Math.Min(size / sizeof(uint), 4096);
        var values = System.Buffers.ArrayPool<uint>.Shared.Rent(count);
        try
        {
            values.AsSpan(0, count).Fill(value);
            var bytes = MemoryMarshal.AsBytes(values.AsSpan(0, count));
            for (ulong offset = 0; offset < size;)
            {
                var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                GpuWritebackEpoch.Advance();
                if (!_backing.TryWriteBacking(guestAddress + offset, bytes[..chunk]))
                {
                    return false;
                }

                offset += (ulong)chunk;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<uint>.Shared.Return(values);
        }
    }

    public const ulong MaxHostCopyWords = 64 * 1024;

    public bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
    {
        if (destination == 0 || source == 0 || sourceWords == 0 || words == 0 || ((destination | source) & 3) != 0 ||
            words > MaxHostCopyWords || sourceWords > MaxHostCopyWords)
        {
            return false;
        }

        var size = words * sizeof(uint);
        var sourceSize = Math.Min(sourceWords, words) * sizeof(uint);
        if (size > ulong.MaxValue - destination || sourceSize > ulong.MaxValue - source ||
            (source < destination + size && destination < source + sourceSize))
        {
            return false;
        }

        var images = RequireImageCache();
        var sourceRegion = images.QueryRegion(source, sourceSize);
        var destinationRegion = images.QueryRegion(destination, size);
        if (HasGpuDirtyBytes(source, sourceSize) || HasGpuDirtyBytes(destination, size) ||
            sourceRegion.GpuImageBytes || destinationRegion.GpuImageBytes)
        {
            return false;
        }

        var pattern = System.Buffers.ArrayPool<byte>.Shared.Rent((int)sourceSize);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(size, 64UL * 1024));
        try
        {
            if (!_backing.TryReadBacking(source, pattern.AsSpan(0, (int)sourceSize)))
            {
                return false;
            }

            if (destinationRegion.ImageBytes)
            {
                images.InvalidateMemory(destination, size);
            }

            var chunkLength = (ulong)(chunk.Length - chunk.Length % sizeof(uint));
            for (ulong offset = 0; offset < size;)
            {
                var length = (int)Math.Min(size - offset, chunkLength);
                for (var filled = 0; filled < length;)
                {
                    var patternOffset = (int)((offset + (ulong)filled) % sourceSize);
                    var copied = Math.Min(length - filled, (int)sourceSize - patternOffset);
                    pattern.AsSpan(patternOffset, copied).CopyTo(chunk.AsSpan(filled, copied));
                    filled += copied;
                }

                WriteHostMemory(destination + offset, chunk.AsSpan(0, length));
                offset += (ulong)length;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pattern);
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public void CopyBuffer(ulong dstVaddr, ulong srcVaddr, ulong size, bool dstGds, bool srcGds)
    {
        if (!dstGds) CompleteGuestPublications?.Invoke(dstVaddr, size, PublicationAccess.Write);
        if (!srcGds) CompleteGuestPublications?.Invoke(srcVaddr, size, PublicationAccess.Read);
        var dstMemory = !dstGds;
        var srcMemory = !srcGds;
        if ((dstMemory && dstVaddr == 0) || (srcMemory && srcVaddr == 0) || size == 0 ||
            ((dstGds || srcGds) && ((dstVaddr | srcVaddr | size) & 3) != 0) ||
            size > ulong.MaxValue - dstVaddr || size > ulong.MaxValue - srcVaddr || (dstGds && srcGds) ||
            (dstGds && (dstVaddr > _gds.Size || size > _gds.Size - dstVaddr)) ||
            (srcGds && (srcVaddr > _gds.Size || size > _gds.Size - srcVaddr)))
        {
            throw SubmissionScheduler.Fatal(
                $"The buffer copy range is invalid: src=0x{srcVaddr:X16} dst=0x{dstVaddr:X16} size=0x{size:X16} src_gds={(srcGds ? 1 : 0)} dst_gds={(dstGds ? 1 : 0)}");
        }

        var images = RequireImageCache();
        if (ImageClearTrace.Enabled && dstMemory) images.TraceGuestWrite("command-copy", dstVaddr, size, srcGds ? 0 : srcVaddr);
        var srcRegion = srcMemory ? images.QueryRegion(srcVaddr, size) : default;
        var dstRegion = dstMemory ? images.QueryRegion(dstVaddr, size) : default;
        if (srcMemory && dstMemory && !HasGpuDirtyBytes(srcVaddr, size) && !HasGpuDirtyBytes(dstVaddr, size) &&
            !srcRegion.GpuImageBytes && !dstRegion.GpuImageBytes)
        {
            if (dstRegion.ImageBytes)
            {
                images.InvalidateMemory(dstVaddr, size);
            }

            var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(size, 64UL * 1024));
            try
            {
                for (ulong offset = 0; offset < size;)
                {
                    var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                    if (!_backing.TryReadBacking(srcVaddr + offset, bytes.AsSpan(0, chunk)))
                    {
                        throw SubmissionScheduler.Fatal("The host DMA source has no direct backing.");
                    }

                    WriteHostMemory(dstVaddr + offset, bytes.AsSpan(0, chunk));
                    offset += (ulong)chunk;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            }

            return;
        }

        var command = _scheduler.Current;
        if (dstMemory)
        {
            images.InvalidateMemoryFromGpu(dstVaddr, size);
        }

        var srcId = srcMemory ? FindBuffer(srcVaddr, size) : default;
        var dstId = dstMemory ? FindBuffer(dstVaddr, size) : default;
        var (src, srcOffset) = srcMemory ? ObtainBuffer(srcVaddr, size, false, true, srcId) : (_gds, srcVaddr);
        if (Diagnostics.DccWriterTrace.Enabled)
        {
            Diagnostics.DccWriterTrace.CurrentKind = $"copy_from_0x{srcVaddr:X}";
        }

        var (dst, dstOffset) = dstMemory ? ObtainBuffer(dstVaddr, size, true, true, dstId) : (_gds, dstVaddr);
        if (ReferenceEquals(src, dst) && srcOffset < dstOffset + size && dstOffset < srcOffset + size)
        {
            throw SubmissionScheduler.Fatal("The resolved Vulkan copy ranges overlap.");
        }

        dst.CopyFrom(command, src, srcOffset, dstOffset, size);
    }

    // Buffers are ordered and disjoint: the last one starting before the query end is the only candidate.
    public bool IsRegionRegistered(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The registered-region query is invalid.");
        }

        return _registry.HasOverlap(guestAddress, size);
    }

    public bool HasGpuDirtyPages(ulong guestAddress, ulong size) => _tracker.HasGpuDirtyPages(guestAddress, size);

    public bool HasGpuDirtyBytes(ulong guestAddress, ulong size) => _gpuModifiedRanges.Overlaps(guestAddress, size);

    public long GpuModifiedVersion => Volatile.Read(ref _gpuModifiedVersion);

    public bool HasCpuDirtyPages(ulong guestAddress, ulong size) => _tracker.HasCpuDirtyPages(guestAddress, size);

    public void ProcessFaultBuffer() => _faults.ProcessFaultBuffer();

    private static long _reportedDeviceWriteConflicts;

    // Pages that shaders wrote through device addresses (runtime V#s, global and FLAT stores,
    // atomics) are reported by the GPU after the fact; no binding announced them. They become
    // GPU-owned like any written binding, so CPU reads download them and a CPU write to the page
    // flushes them first instead of a later upload overwriting them with stale guest bytes.
    internal void NoteDeviceAddressWrites(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            return;
        }

        var end = guestAddress + size;
        var marked = false;
        for (var page = guestAddress & ~(TrackerLayout.PageBytes - 1); page < end; page += TrackerLayout.PageBytes)
        {
            if (FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            var conflicts = _tracker.MarkGpuWrittenPages(page, TrackerLayout.PageBytes);
            if (conflicts != 0)
            {
                // The CPU dirtied the page before the write was reported: the page stays
                // CPU-owned and the shader's bytes are merged into guest memory instead: now when the
                // owner's copy is mapped, otherwise once the render thread has downloaded the page.
                if (_sharedPages.Contains(page))
                {
                    marked |= PullSharedPage(page, owner);
                }
                else
                {
                    AdoptLateSharedPage(page, owner);
                }

                continue;
            }

            // The first CPU access downloads the page; the shadow starts from the downloaded bytes.
            if (!_sharedPages.Contains(page))
            {
                lock (_shaderWrittenPages)
                {
                    if (_shaderWrittenPages.Count < MaxShaderWrittenPages)
                    {
                        _shaderWrittenPages[page] = owner;
                        Volatile.Write(ref _shaderWrittenPageCount, _shaderWrittenPages.Count);
                    }
                }
            }

            if (!_gpuModifiedRanges.Contains(page, TrackerLayout.PageBytes))
            {
                _gpuModifiedRanges.Add(page, TrackerLayout.PageBytes);
                Interlocked.Increment(ref _gpuModifiedVersion);
            }

            owner.NoteGpuWrite();
            marked = true;
            if (GuestGpuMemoryHook.Traces(page, TrackerLayout.PageBytes))
                GuestGpuMemoryHook.Trace(page, TrackerLayout.PageBytes, $"device-address-write-reported submission_tick={_scheduler.CurrentTick}");
        }

        if (marked)
        {
            RequireImageCache().InvalidateMemoryCopiesFromGpu(guestAddress, size);
        }
    }

    // Pages shaders write through device addresses; see SharedPageShadows. 64 MiB of shadows at most.
    private const int SharedPageCapacity = 16384;
    private readonly SharedPageShadows _sharedPages = new(TrackerLayout.PageBytes, SharedPageCapacity);
    private static long _reportedLateSharedPages;
    private static long _reportedSharedPageMerges;
    private static int _reportedSharedPageCapacity;

    // Pages shaders wrote that have no shadow yet, with their owner at the time of the write.
    private const int MaxShaderWrittenPages = 1 << 20;
    private readonly Dictionary<ulong, GpuBuffer> _shaderWrittenPages = new();
    private int _shaderWrittenPageCount;

    // A page the CPU dirtied before its first shader write was reported: shadowed from the GPU copy.
    private void AdoptLateSharedPage(ulong page, GpuBuffer owner)
    {
        var adopted = owner.MappedPointer != null && owner.IsCoherent &&
            _sharedPages.Adopt(page, owner, new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), (int)TrackerLayout.PageBytes),
                _scheduler.CurrentTick);
        if (!adopted && _sharedPages.Count >= SharedPageCapacity && Interlocked.Exchange(ref _reportedSharedPageCapacity, 1) == 0)
        {
            Console.Error.WriteLine("[GPU][WARN] The shared page shadows are full; CPU writes to further shader-written pages may lose shader bytes.");
        }

        var reported = Interlocked.Increment(ref adopted ? ref _reportedLateSharedPages : ref _reportedDeviceWriteConflicts);
        if (reported <= 16 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(adopted
                ? FormattableString.Invariant(
                    $"[GPU][SHARED_PAGE] The CPU dirtied page=0x{page:X16} before its first shader write was reported; shader bytes an upload already overwrote are lost once shared={_sharedPages.Count} n={reported}")
                : FormattableString.Invariant(
                    $"[GPU][WARN] A device-address write reached a page the CPU dirtied and the page has no shadow: page=0x{page:X16} count={reported}"));
        }
    }

    internal GuestPageTracker Tracker => _tracker;

    // Writes downloaded GPU bytes into guest memory; shadows of shared pages take them too.
    internal bool WriteDownloaded(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (!_backing.TryWriteBacking(address, bytes))
        {
            return false;
        }

        _sharedPages.NoteDownloaded(address, bytes);
        AdoptDownloadedPages(address, (ulong)bytes.Length);
        return true;
    }

    // Downloaded shader-written pages: the GPU-written bytes are in guest memory now, but guest
    // memory says nothing about the bytes the owner's GPU copy holds elsewhere on the page. Those may
    // never have received the guest's CPU writes (they can reach the GPU through the stream ring, or
    // the owner can be newer than its last full upload), so a shadow taken from guest memory would
    // filter out every later CPU write that repeats the guest's value and leave the stale GPU bytes.
    // A readable owner is shadowed from its own GPU copy: the download made the GPU-written bytes
    // equal in guest memory, and any other difference is a CPU change the next upload must carry.
    // Otherwise the page has no baseline: its next upload copies all of it into the owner. The
    // CPU's write to a page marks it dirty, so that upload happens before the GPU reads the CPU's
    // value; the whole page is guest memory at that point, with the downloaded GPU bytes in it.
    private void AdoptDownloadedPages(ulong address, ulong size)
    {
        if (Volatile.Read(ref _shaderWrittenPageCount) == 0)
        {
            return;
        }

        var end = address + size;
        for (var page = address & ~(TrackerLayout.PageBytes - 1); page < end; page += TrackerLayout.PageBytes)
        {
            GpuBuffer? owner;
            lock (_shaderWrittenPages)
            {
                if (!_shaderWrittenPages.Remove(page, out owner))
                {
                    continue;
                }

                Volatile.Write(ref _shaderWrittenPageCount, _shaderWrittenPages.Count);
            }

            // A retired owner may be unmapped: only the buffer that holds the page now is read.
            var adopted = owner!.MappedPointer != null && owner.IsCoherent && ReferenceEquals(FindOwner(page, TrackerLayout.PageBytes), owner)
                ? _sharedPages.Adopt(page, owner,
                    new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), (int)TrackerLayout.PageBytes), _scheduler.CurrentTick)
                : _sharedPages.AdoptUnsynchronized(page);
            if (!adopted && Interlocked.Exchange(ref _reportedSharedPageCapacity, 1) == 0)
            {
                Console.Error.WriteLine("[GPU][WARN] The shared page shadows are full; CPU writes to further shader-written pages may lose shader bytes.");
            }
        }
    }

    // Merges what shaders wrote on a shared page into guest memory; true when bytes were merged. An
    // owner the host cannot read through a coherent mapping is merged later, from a download.
    private bool PullSharedPage(ulong page, GpuBuffer owner)
    {
        if (_sharedPages.Count == 0 || !_sharedPages.Contains(page))
        {
            return false;
        }

        if (owner.MappedPointer == null || !owner.IsCoherent)
        {
            DeferSharedPageMerge(page);
            return false;
        }

        // The GPU may still write the page: merge one snapshot of it.
        Span<byte> gpu = stackalloc byte[(int)TrackerLayout.PageBytes];
        new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(page), gpu.Length).CopyTo(gpu);
        return MergeSharedPage(page, owner, gpu, _scheduler.Timeline.CompletedTick);
    }

    // Merges a snapshot of the owner's copy of a shadowed page into guest memory and the shadow: the
    // bytes shaders changed reach guest memory where the CPU left them alone, and the shadow takes the
    // GPU state, so a later CPU write is compared with what the GPU holds. completedTick is the newest
    // tick whose effects the snapshot is known to include. True when bytes were merged.
    private bool MergeSharedPage(ulong page, GpuBuffer owner, ReadOnlySpan<byte> gpu, ulong completedTick)
    {
        var guest = new byte[TrackerLayout.PageBytes];
        if (!_backing.TryReadBacking(page, guest))
        {
            return false;
        }

        var count = _sharedPages.PullGpuWrites(page, owner, gpu, guest, completedTick, (offset, length) =>
        {
            GpuWritebackEpoch.Advance();
            if (!_backing.TryWriteBacking(page + (ulong)offset, guest.AsSpan(offset, length)))
            {
                throw SubmissionScheduler.Fatal($"Could not merge shader writes into guest memory: addr=0x{page + (ulong)offset:X16} size=0x{length:X}");
            }
        });
        if (count == 0)
        {
            return false;
        }

        var reported = Interlocked.Increment(ref _reportedSharedPageMerges);
        if (reported <= 16 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[GPU][SHARED_PAGE] merged shader bytes into a CPU-dirty page=0x{page:X16} bytes={count} shared={_sharedPages.Count} merged_bytes={_sharedPages.MergedBytes} skipped_upload_bytes={_sharedPages.SkippedUploadBytes} n={reported}"));
        }

        if (GuestGpuMemoryHook.Traces(page, TrackerLayout.PageBytes))
            GuestGpuMemoryHook.Trace(page, TrackerLayout.PageBytes, $"shared-page-merged bytes={count} submission_tick={_scheduler.CurrentTick}");
        return true;
    }

    // Shadowed pages whose owner the host cannot read through a coherent mapping (device-local memory
    // behind a non-coherent or unmapped heap) and that a shader wrote after the CPU dirtied them.
    // The completion action that reports the write may not wait for the GPU, so it cannot download
    // the page. Until the render thread does, the shader's bytes exist only in the GPU copy: the
    // shadow does not know them, so an upload would filter out any CPU write that repeats the
    // shadow's value and leave the shader's byte in the GPU copy for good.
    // Each page waits here with the completed tick at the report, which the shader's writes precede.
    // The render thread reads such a page back on the async readback queue as soon as the tick it
    // depends on (that report and the last upload staged for the page) is submitted, without waiting:
    // the page is then in flight until the copy completes and its snapshot is merged. Merging happens
    // before an upload reads the page (SynchronizeBuffer), before a synchronized CPU read of it, and
    // opportunistically after each batch submission and fault scan. Only a caller that needs a page
    // whose copy is still in flight waits, for that copy alone. The owner is looked up again at the
    // merge: a replaced owner holds the same bytes, and a retired one needs no merge.
    // A CPU write that repeats the shadow's value between the report and the merge cannot be told
    // from no write: the shader's byte wins there, as it would on a coherent owner for a write that
    // came before the report.
    private readonly Dictionary<ulong, ulong> _pendingSharedMerges = new();
    private readonly Dictionary<ulong, SharedMergeFlight> _inflightSharedMerges = new();
    private readonly List<SharedMergeBatch> _sharedMergeBatches = new();
    private int _pendingSharedMergeCount;
    private bool _resolvingSharedMerges;
    private const int SharedMergeChunkPages = 256;

    // One readback ticket covering several pages. SnapshotTick is the newest main-queue tick the copy
    // waits for: every upload staged up to it and every shader write reported by it is in the bytes.
    private sealed class SharedMergeBatch(VulkanAsyncReadback.Ticket ticket, ulong[] pages, ulong[] reported, GpuBuffer[] owners, ulong snapshotTick)
    {
        public VulkanAsyncReadback.Ticket Ticket { get; } = ticket;
        public ulong[] Pages { get; } = pages;
        public ulong[] Reported { get; } = reported;
        public GpuBuffer[] Owners { get; } = owners;
        public ulong SnapshotTick { get; } = snapshotTick;
    }

    private readonly record struct SharedMergeFlight(SharedMergeBatch Batch, int Index);

    private readonly record struct SharedMergeCandidate(ulong Page, ulong Reported, GpuBuffer Owner, ulong WaitTick);

    private enum SharedMergeCounter
    {
        Issued,
        MergedWithoutWait,
        WaitedInFlight,
        IssuedAndWaited,
        ForcedFlush,
        Reissued,
        Count,
    }

    // How a batch is consumed: whether its ticket had already completed, was in flight when a caller
    // needed it, or was issued by that very call.
    private enum SharedMergeArrival
    {
        Completed,
        WaitedInFlight,
        IssuedAndWaited,
    }

    private readonly long[] _sharedMergeCounts = new long[(int)SharedMergeCounter.Count];
    private static readonly long[] ReportedSharedMergeCounts = new long[(int)SharedMergeCounter.Count];

    private void CountSharedMerge(SharedMergeCounter counter, int pages)
    {
        if (pages == 0)
        {
            return;
        }

        Interlocked.Add(ref _sharedMergeCounts[(int)counter], pages);
        Interlocked.Add(ref ReportedSharedMergeCounts[(int)counter], pages);
    }

    // Pages handed to the async readback queue (a page issued again counts again).
    internal long SharedMergesIssued => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.Issued]);

    // Pages merged from a copy that had already completed when it was looked at.
    internal long SharedMergesMergedWithoutWait => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.MergedWithoutWait]);

    // Pages merged after blocking on a copy that was issued earlier, which waits for that copy only.
    internal long SharedMergesWaitedInFlight => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.WaitedInFlight]);

    // Pages issued by the call that needed them at once (their tick was already submitted) and waited for.
    internal long SharedMergesIssuedAndWaited => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.IssuedAndWaited]);

    // Pages whose tick was still in the open command buffer or that found no free readback slot, so
    // the call submitted the buffer and waited: the blocking path.
    internal long SharedMergesForcedFlush => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.ForcedFlush]);

    // Pages whose snapshot was dropped because an upload was staged after it was issued.
    internal long SharedMergesReissued => Volatile.Read(ref _sharedMergeCounts[(int)SharedMergeCounter.Reissued]);

    // True when every shared page readback in flight has completed, so merging them would not wait.
    internal bool SharedMergeReadsComplete()
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            batches = _sharedMergeBatches.ToArray();
        }

        if (AsyncReadback is not { } readback)
        {
            return batches.Length == 0;
        }

        foreach (var batch in batches)
        {
            if (!readback.IsComplete(batch.Ticket))
            {
                return false;
            }
        }

        return true;
    }

    public static string TakeSharedMergeReport() => FormattableString.Invariant(
        $"[PERF][SHARED_MERGE] issued={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.Issued], 0)} merged_nowait={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.MergedWithoutWait], 0)} waited_inflight={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.WaitedInFlight], 0)} issued_waited={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.IssuedAndWaited], 0)} forced_flush={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.ForcedFlush], 0)} reissued={Interlocked.Exchange(ref ReportedSharedMergeCounts[(int)SharedMergeCounter.Reissued], 0)}");

    private void DeferSharedPageMerge(ulong page) =>
        DeferSharedPageMerge(page, _scheduler.Timeline.CompletedTick);

    private void DeferSharedPageMerge(ulong page, ulong reportedTick)
    {
        lock (_pendingSharedMerges)
        {
            _pendingSharedMerges[page] = _pendingSharedMerges.TryGetValue(page, out var earlier)
                ? Math.Max(earlier, reportedTick)
                : reportedTick;
            PublishSharedMergeCount();
        }
    }

    // Everything the resolve entry points still have to look at: pending pages, pages in flight and
    // the batches that hold a readback slot (a batch whose pages were all superseded still does).
    // Callers hold the _pendingSharedMerges lock.
    private void PublishSharedMergeCount() =>
        Volatile.Write(ref _pendingSharedMergeCount,
            _pendingSharedMerges.Count + _inflightSharedMerges.Count + _sharedMergeBatches.Count);

    private bool HasPendingSharedMerges(ulong guestAddress, ulong size)
    {
        if (Volatile.Read(ref _pendingSharedMergeCount) == 0)
        {
            return false;
        }

        lock (_pendingSharedMerges)
        {
            return TakePendingSharedMerges(guestAddress, size, null) || OverlapsSharedPages(_inflightSharedMerges, guestAddress, size);
        }
    }

    private static bool OverlapsSharedPages<T>(Dictionary<ulong, T> pages, ulong guestAddress, ulong size)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        if (pages.Count == 0)
        {
            return false;
        }

        var first = guestAddress & ~(pageBytes - 1);
        var end = guestAddress + size;
        if ((end - first) / pageBytes <= (ulong)pages.Count)
        {
            for (var page = first; page < end; page += pageBytes)
            {
                if (pages.ContainsKey(page))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var page in pages.Keys)
        {
            if (page < end && page + pageBytes > guestAddress)
            {
                return true;
            }
        }

        return false;
    }

    // The pending pages overlapping the range, removed from the table when a list takes them.
    private bool TakePendingSharedMerges(ulong guestAddress, ulong size, List<KeyValuePair<ulong, ulong>>? taken)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        var first = guestAddress & ~(pageBytes - 1);
        var end = guestAddress + size;
        var any = false;
        if ((end - first) / pageBytes <= (ulong)_pendingSharedMerges.Count)
        {
            for (var page = first; page < end; page += pageBytes)
            {
                if (!_pendingSharedMerges.TryGetValue(page, out var tick))
                {
                    continue;
                }

                any = true;
                if (taken is null)
                {
                    return true;
                }

                taken.Add(new KeyValuePair<ulong, ulong>(page, tick));
            }
        }
        else
        {
            foreach (var pair in _pendingSharedMerges)
            {
                if (pair.Key < end && pair.Key + pageBytes > guestAddress)
                {
                    any = true;
                    if (taken is null)
                    {
                        return true;
                    }

                    taken.Add(pair);
                }
            }
        }

        if (taken is not null)
        {
            foreach (var pair in taken)
            {
                _pendingSharedMerges.Remove(pair.Key);
            }

            PublishSharedMergeCount();
        }

        return any;
    }

    // Brings the pending and in-flight pages in the range to guest memory and the shadows. With wait,
    // every one is: a copy already in flight is waited for on its own, and a page not yet read is
    // read at once (submitting its tick first when that is still being recorded). Without it, the
    // call merges the copies that have completed, starts a copy for every page whose tick is
    // submitted, and leaves the rest pending; it never waits for the GPU.
    // Render thread only, never inside a completion action.
    internal void ResolvePendingSharedMerges(ulong guestAddress, ulong size, bool wait)
    {
        if (Volatile.Read(ref _pendingSharedMergeCount) == 0 || _resolvingSharedMerges ||
            !_scheduler.Active || _scheduler.InsideTickCallback)
        {
            return;
        }

        _resolvingSharedMerges = true;
        try
        {
            ConsumeSharedMergeBatches(guestAddress, size, wait);
            List<KeyValuePair<ulong, ulong>> pages;
            lock (_pendingSharedMerges)
            {
                if (_pendingSharedMerges.Count == 0)
                {
                    return;
                }

                pages = new List<KeyValuePair<ulong, ulong>>();
                if (!TakePendingSharedMerges(guestAddress, size, pages))
                {
                    return;
                }
            }

            ResolveSharedMerges(pages, wait);
        }
        finally
        {
            _resolvingSharedMerges = false;
        }
    }

    private void ResolveSharedMerges(List<KeyValuePair<ulong, ulong>> pages, bool wait)
    {
        var ready = new List<SharedMergeCandidate>(pages.Count);
        foreach (var (page, reported) in pages)
        {
            // A page without a shadow or an owner has nothing to merge into or from.
            if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            ready.Add(new SharedMergeCandidate(page, reported, owner, Math.Max(reported, staged)));
        }

        if (AsyncReadback is not { } readback)
        {
            // Without the readback queue the copy goes through the download ring and drains the scheduler.
            if (wait)
            {
                CountSharedMerge(SharedMergeCounter.ForcedFlush, ready.Count);
                MergeSharedPagesSynchronously(ready);
            }
            else
            {
                foreach (var candidate in ready)
                {
                    DeferSharedPageMerge(candidate.Page, candidate.Reported);
                }
            }

            return;
        }

        var unissued = new List<SharedMergeCandidate>();
        var issued = IssueSharedMergeReads(ready, unissued);
        if (!wait)
        {
            // The pages whose tick is still being recorded are issued after the next submission.
            foreach (var candidate in unissued)
            {
                DeferSharedPageMerge(candidate.Page, candidate.Reported);
            }

            return;
        }

        if (unissued.Count != 0)
        {
            // The newest tick the pages wait for is still in the open command buffer: submit it, then read.
            CountSharedMerge(SharedMergeCounter.ForcedFlush, unissued.Count);
            _scheduler.Flush();
            var stuck = new List<SharedMergeCandidate>();
            issued.AddRange(IssueSharedMergeReads(unissued, stuck));
            // No free readback slot: the synchronous slot or the download ring reads them.
            MergeSharedPagesSynchronously(stuck);
        }

        foreach (var batch in issued)
        {
            readback.Wait(batch.Ticket);
            ConsumeSharedMergeBatch(batch, SharedMergeArrival.IssuedAndWaited);
        }
    }

    // Starts a copy on the async readback queue for every candidate whose wait tick is already
    // submitted, in batches that share one ticket; the others go to unissued. Pages leave the pending
    // table and become in flight. Never waits for the GPU and never submits.
    private List<SharedMergeBatch> IssueSharedMergeReads(List<SharedMergeCandidate> candidates, List<SharedMergeCandidate> unissued)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        var issued = new List<SharedMergeBatch>();
        var readback = AsyncReadback;
        var submittedBelow = _scheduler.CurrentTick;
        var eligible = new List<SharedMergeCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (readback is null || candidate.WaitTick >= submittedBelow)
            {
                unissued.Add(candidate);
            }
            else
            {
                eligible.Add(candidate);
            }
        }

        for (var first = 0; first < eligible.Count; first += SharedMergeChunkPages)
        {
            var count = Math.Min(SharedMergeChunkPages, eligible.Count - first);
            var pieces = new ReadbackPiece[count];
            var waitTick = 0UL;
            for (var index = 0; index < count; index++)
            {
                var candidate = eligible[first + index];
                pieces[index] = new ReadbackPiece(candidate.Owner, candidate.Owner.Offset(candidate.Page), pageBytes);
                waitTick = Math.Max(waitTick, candidate.WaitTick);
            }

            VulkanAsyncReadback.Ticket? ticket;
            using (_device.Slabs.BeginForeignRead())
            {
                if (!readback!.TryBegin(pieces, waitTick, out ticket) || ticket is null)
                {
                    unissued.AddRange(eligible.GetRange(first, eligible.Count - first));
                    break;
                }

                // The owner may be retired while the copy is in flight; its memory stays until the merge.
                foreach (var piece in pieces)
                {
                    piece.Source.RetainForeignRead();
                }
            }

            var batchPages = new ulong[count];
            var batchReported = new ulong[count];
            var batchOwners = new GpuBuffer[count];
            for (var index = 0; index < count; index++)
            {
                var candidate = eligible[first + index];
                batchPages[index] = candidate.Page;
                batchReported[index] = candidate.Reported;
                batchOwners[index] = candidate.Owner;
            }

            var batch = new SharedMergeBatch(ticket, batchPages, batchReported, batchOwners, waitTick);
            lock (_pendingSharedMerges)
            {
                for (var index = 0; index < count; index++)
                {
                    // A page read again supersedes its earlier copy, which is then only reaped.
                    _inflightSharedMerges[batchPages[index]] = new SharedMergeFlight(batch, index);
                }

                _sharedMergeBatches.Add(batch);
                PublishSharedMergeCount();
            }

            CountSharedMerge(SharedMergeCounter.Issued, count);
            issued.Add(batch);
        }

        return issued;
    }

    // Merges the batches of in-flight copies that overlap the range and have completed, and, with
    // wait, waits for the ones a page of the range still depends on. A copy outside the range is
    // left alone unless the range covers everything.
    private void ConsumeSharedMergeBatches(ulong guestAddress, ulong size, bool wait)
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            if (_sharedMergeBatches.Count == 0)
            {
                return;
            }

            batches = _sharedMergeBatches.ToArray();
        }

        if (AsyncReadback is not { } readback)
        {
            return;
        }

        const ulong pageBytes = TrackerLayout.PageBytes;
        var end = guestAddress + size;
        foreach (var batch in batches)
        {
            var overlaps = false;
            var needed = false;
            lock (_pendingSharedMerges)
            {
                for (var index = 0; index < batch.Pages.Length; index++)
                {
                    var page = batch.Pages[index];
                    if (page >= end || page + pageBytes <= guestAddress)
                    {
                        continue;
                    }

                    overlaps = true;
                    if (_inflightSharedMerges.TryGetValue(page, out var flight) && ReferenceEquals(flight.Batch, batch))
                    {
                        needed = true;
                        break;
                    }
                }
            }

            if (!overlaps)
            {
                continue;
            }

            if (readback.IsComplete(batch.Ticket))
            {
                ConsumeSharedMergeBatch(batch, SharedMergeArrival.Completed);
            }
            else if (wait && needed)
            {
                readback.Wait(batch.Ticket);
                ConsumeSharedMergeBatch(batch, SharedMergeArrival.WaitedInFlight);
            }
        }
    }

    // Merges the snapshot of a completed copy page by page and frees its readback slot. A page that
    // was read again since is left to its newer copy; one whose shadow got an upload staged after the
    // copy's tick is not merged from this snapshot and goes back to the pending table.
    private void ConsumeSharedMergeBatch(SharedMergeBatch batch, SharedMergeArrival arrival)
    {
        const int pageBytes = (int)TrackerLayout.PageBytes;
        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The shared page readback lost its queue.");
        lock (_pendingSharedMerges)
        {
            if (!_sharedMergeBatches.Remove(batch))
            {
                return;
            }
        }

        var snapshot = new byte[batch.Pages.Length * pageBytes];
        var copied = 0;
        try
        {
            readback.Complete(batch.Ticket, (index, bytes) =>
            {
                bytes.CopyTo(snapshot.AsSpan(index * pageBytes, pageBytes));
                copied++;
            });
        }
        finally
        {
            foreach (var owner in batch.Owners)
            {
                owner.ReleaseForeignRead();
            }
        }

        var current = new bool[batch.Pages.Length];
        lock (_pendingSharedMerges)
        {
            for (var index = 0; index < batch.Pages.Length; index++)
            {
                if (_inflightSharedMerges.TryGetValue(batch.Pages[index], out var flight) && ReferenceEquals(flight.Batch, batch))
                {
                    current[index] = true;
                    _inflightSharedMerges.Remove(batch.Pages[index]);
                }
            }

            PublishSharedMergeCount();
        }

        var merged = 0;
        var stale = 0;
        for (var index = 0; index < batch.Pages.Length; index++)
        {
            if (!current[index])
            {
                continue;
            }

            var page = batch.Pages[index];
            if (copied != batch.Pages.Length)
            {
                // The queue closed before the copy was read: nothing was snapshotted.
                DeferSharedPageMerge(page, batch.Reported[index]);
                continue;
            }

            // The owner may have been replaced meanwhile; its copy of the page holds the same bytes.
            if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
            {
                continue;
            }

            if (staged > batch.SnapshotTick)
            {
                // An upload or buffer copy was staged after the copy's tick; the snapshot lacks those bytes.
                DeferSharedPageMerge(page, batch.Reported[index]);
                stale++;
                continue;
            }

            merged++;
            var slice = snapshot.AsSpan(index * pageBytes, pageBytes);
            if (MergeSharedPage(page, owner, slice, batch.SnapshotTick))
            {
                RequireImageCache().InvalidateMemoryCopiesFromGpu(page, TrackerLayout.PageBytes);
            }

            // A later report the snapshot already covers needs no copy of its own.
            lock (_pendingSharedMerges)
            {
                if (_pendingSharedMerges.TryGetValue(page, out var pendingTick) && pendingTick <= batch.SnapshotTick)
                {
                    _pendingSharedMerges.Remove(page);
                    PublishSharedMergeCount();
                }
            }
        }

        CountSharedMerge(arrival switch
        {
            SharedMergeArrival.Completed => SharedMergeCounter.MergedWithoutWait,
            SharedMergeArrival.WaitedInFlight => SharedMergeCounter.WaitedInFlight,
            _ => SharedMergeCounter.IssuedAndWaited,
        }, merged);
        CountSharedMerge(SharedMergeCounter.Reissued, stale);
    }

    // Reads the candidates through the synchronous readback slot, or the download ring without a
    // readback queue, and merges them. Submits and waits for the GPU as the read needs.
    private void MergeSharedPagesSynchronously(List<SharedMergeCandidate> ready)
    {
        for (var first = 0; first < ready.Count; first += SharedMergeChunkPages)
        {
            var count = Math.Min(SharedMergeChunkPages, ready.Count - first);
            var snapshot = new byte[count * (int)TrackerLayout.PageBytes];
            var snapshotTick = ReadOwnerPages(ready, first, count, snapshot);
            for (var index = 0; index < count; index++)
            {
                var (page, reported, _, _) = ready[first + index];
                // The owner may have been replaced meanwhile; its copy of the page holds the same bytes.
                if (!_sharedPages.TryGetStagedTick(page, out var staged) || FindOwner(page, TrackerLayout.PageBytes) is not { } owner)
                {
                    continue;
                }

                if (staged > snapshotTick)
                {
                    // An upload or buffer copy was staged after the snapshot; it lacks those bytes.
                    DeferSharedPageMerge(page, reported);
                    continue;
                }

                var slice = snapshot.AsSpan(index * (int)TrackerLayout.PageBytes, (int)TrackerLayout.PageBytes);
                if (MergeSharedPage(page, owner, slice, snapshotTick))
                {
                    RequireImageCache().InvalidateMemoryCopiesFromGpu(page, TrackerLayout.PageBytes);
                }
            }
        }
    }

    // Teardown: waits for the copies in flight and drops them; their pages need no merge any more.
    private void AbandonSharedMergeBatches()
    {
        SharedMergeBatch[] batches;
        lock (_pendingSharedMerges)
        {
            batches = _sharedMergeBatches.ToArray();
            _sharedMergeBatches.Clear();
            _inflightSharedMerges.Clear();
            PublishSharedMergeCount();
        }

        var readback = AsyncReadback;
        foreach (var batch in batches)
        {
            if (readback is not null)
            {
                readback.Wait(batch.Ticket);
                readback.Complete(batch.Ticket, null);
            }

            foreach (var owner in batch.Owners)
            {
                owner.ReleaseForeignRead();
            }
        }
    }

    // Copies each page out of its owner after the ticks it waits for, and returns the newest tick the
    // snapshot is known to include. The async readback queue waits for only those ticks; without it the
    // copy goes through the download ring and the call drains the scheduler.
    private ulong ReadOwnerPages(List<SharedMergeCandidate> pages, int first, int count, byte[] snapshot)
    {
        const ulong pageBytes = TrackerLayout.PageBytes;
        var waitTick = 0UL;
        for (var index = 0; index < count; index++)
        {
            waitTick = Math.Max(waitTick, pages[first + index].WaitTick);
        }

        if (AsyncReadback is { } readback)
        {
            using var foreignRead = _device.Slabs.BeginForeignRead();
            // The newest tick may still be recording: the readback queue needs it submitted.
            if (waitTick >= _scheduler.CurrentTick)
            {
                _scheduler.Flush();
            }

            var pieces = new ReadbackPiece[count];
            for (var index = 0; index < count; index++)
            {
                var (page, _, owner, _) = pages[first + index];
                pieces[index] = new ReadbackPiece(owner, owner.Offset(page), pageBytes);
            }

            readback.Read(pieces, waitTick, (index, bytes) => bytes.CopyTo(snapshot.AsSpan((int)((uint)index * pageBytes), (int)pageBytes)));
            return waitTick;
        }

        var total = (ulong)count * pageBytes;
        if (!_download.TryMap(total, out var baseOffset, BufferDownloadBatchPlanner.Alignment))
        {
            throw SubmissionScheduler.Fatal("The download ring could not map the shared page snapshot.");
        }

        for (var index = 0; index < count; index++)
        {
            var (page, _, owner, _) = pages[first + index];
            _download.CopyFrom(
                _scheduler.Current, owner, owner.Offset(page), baseOffset + (ulong)index * pageBytes, pageBytes,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        }

        _download.Commit();
        var copyTick = _scheduler.CurrentTick;
        _scheduler.Finish();
        _scheduler.WaitForPriorityOperations(copyTick);
        _download.Invalidate(baseOffset, total);
        _download.Mapped.Slice((int)baseOffset, (int)total).CopyTo(snapshot);
        return copyTick;
    }

    // Uploads every mapped range before a BDA draw; the fault pass runs at the next collection.
    // Every device-address program prepares all GPU-mapped memory; visiting each registered
    // buffer per dispatch cost ~17 % of the Demon's Souls render thread. The same work is done
    // in two cheaper parts: the recency touch, a no-op after the first one in a retirement tick,
    // runs once per tick (and when the mapping changes; new buffers register with the current
    // tick), and uploads only visit buffers in blocks the tracker does not know to be clean.
    // Hot pages stay dirty and writable, so every preparation still re-uploads them.
    private ulong _bdaTouchTick = ulong.MaxValue;
    private ulong _bdaTouchMapping;

    // Ranges shaders reached through device addresses outside the GPU mappings: pointers
    // into ordinary guest memory, which the GPU reads too. Only a fault reveals them, so
    // they are kept and touched like the mappings. Otherwise their buffers age out while
    // still in use, fault again, and the cache collects and re-creates them every frame.
    private readonly SpanSet _deviceAddressFaultSpans = new();

    // Fault ranges inside a known guest mapping; they are forgotten once it is unmapped.
    private readonly SpanSet _mappedDeviceAddressFaultSpans = new();

    internal void NoteDeviceAddressFault(ulong guestAddress, ulong size, bool insideGuestMapping = false)
    {
        _deviceAddressFaultSpans.Add(guestAddress, size);
        if (insideGuestMapping)
            _mappedDeviceAddressFaultSpans.Add(guestAddress, size);
    }

    // A guest buffer the GPU reads through pointers (a per-frame ring, for one) is reached a
    // page at a time, and every first touch reads zeros for that frame. A fault therefore
    // brings in the aligned window around it, clipped to the guest mapping that holds it.
    internal const ulong DeviceAddressFaultWindow = 2UL << 20;

    internal static GuestSpan DeviceAddressFaultSpan(ulong pageAddress, ulong pageSize, ulong mappingStart, ulong mappingLength)
    {
        if (mappingLength == 0 || pageAddress < mappingStart || pageAddress - mappingStart >= mappingLength)
        {
            return new GuestSpan(pageAddress, pageSize);
        }

        var mappingEnd = mappingStart + mappingLength;
        var windowStart = Math.Max(pageAddress & ~(DeviceAddressFaultWindow - 1), mappingStart);
        var windowEnd = Math.Min((pageAddress & ~(DeviceAddressFaultWindow - 1)) + DeviceAddressFaultWindow, mappingEnd);
        return new GuestSpan(windowStart, windowEnd - windowStart);
    }

    // Forgets fault ranges whose guest mapping is gone, so their buffers can age out again.
    private void PruneUnmappedDeviceAddressFaults()
    {
        List<GuestSpan>? unmapped = null;
        _mappedDeviceAddressFaultSpans.ForEach((start, size) =>
        {
            if (!KernelMemoryCompatExports.TryGetMappedRange(start, out var mappingStart, out var mappingLength) ||
                start + size > mappingStart + mappingLength)
                (unmapped ??= []).Add(new GuestSpan(start, size));
        });
        if (unmapped is null) return;
        foreach (var span in unmapped)
        {
            _mappedDeviceAddressFaultSpans.Remove(span.Address, span.Size);
            _deviceAddressFaultSpans.Remove(span.Address, span.Size);
        }
    }

    public static ulong MappingKey(IReadOnlyCollection<GuestSpan> spans)
    {
        var mapping = (ulong)spans.Count;
        foreach (var span in spans)
            mapping = (mapping ^ span.Address ^ (span.Size << 17)) * 0x100000001B3UL;
        return mapping;
    }

    public void PrepareBda(IEnumerable<GuestSpan> mapped)
    {
        var spans = mapped as IReadOnlyCollection<GuestSpan> ?? mapped.ToList();
        PrepareBda(spans, MappingKey(spans));
    }

    public void PrepareBda(IReadOnlyCollection<GuestSpan> mapped, ulong mapping)
    {
        var traceAddress = GuestGpuMemoryHook.TraceAddress;
        var traceCovered = false;
        if (traceAddress != 0)
        {
            foreach (var span in mapped)
            {
                if (traceAddress != 0 && GuestGpuMemoryHook.Traces(span.Address, span.Size))
                    traceCovered = true;
                SynchronizeBuffersInRange(span.Address, span.Size);
            }
        }
        else
        {
            var spans = mapped;
            if (_retirementPolicy.CurrentTick != _bdaTouchTick || mapping != _bdaTouchMapping)
            {
                foreach (var span in spans)
                    TouchBuffersInRange(span.Address, span.Size);
                PruneUnmappedDeviceAddressFaults();
                _deviceAddressFaultSpans.ForEach(_touchBuffersInRange ??= TouchBuffersInRange);
                _bdaTouchTick = _retirementPolicy.CurrentTick;
                _bdaTouchMapping = mapping;
            }

            var epoch = _tracker.CpuDirtyEpoch;
            if (epoch != _bdaSweepEpoch || mapping != _bdaSweepMapping)
            {
                foreach (var span in spans)
                    _tracker.ForEachPossiblyCpuDirtyRange(span.Address, span.Size, _uploadDirtyBuffersInRange ??= UploadDirtyBuffersInRange);
                _bdaSweepEpoch = epoch;
                _bdaSweepMapping = mapping;
            }
        }

        if (traceAddress != 0)
            GuestGpuMemoryHook.Trace(traceAddress, 1,
                $"device-address-preparation covered={traceCovered} registered={IsRegionRegistered(traceAddress, 1)} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        _faultProcessPending = true;
    }

    private Action<ulong, ulong>? _uploadDirtyBuffersInRange;
    private Action<ulong, ulong>? _touchBuffersInRange;
    private long _bdaSweepEpoch = -1;
    private ulong _bdaSweepMapping;

    private void TouchBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
            TouchBuffer(_registry.GetRegisteredIdentifier(index));
    }

    // The upload half of SynchronizeBuffersInRange, for a range that may hold CPU-dirty pages.
    private void UploadDirtyBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(index));
            var start = Math.Max(buffer.CpuAddress, guestAddress);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, end);
            if (start < finish && _tracker.MayHaveCpuDirtyPages(start, finish - start))
                _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: false);
        }
    }

    public void SynchronizeBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
        {
            var identifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(identifier);
            var start = Math.Max(buffer.CpuAddress, guestAddress);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, end);
            if (start < finish)
            {
                if (GuestGpuMemoryHook.Traces(start, finish - start))
                    GuestGpuMemoryHook.Trace(start, finish - start,
                        $"device-address-touch buffer={identifier} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
                // Clean buffers remain in use through their device addresses.
                TouchBuffer(identifier);
                // Device-address reads reuse persistent buffers; track writes after each upload.
                // A range without CPU-dirty pages has nothing to upload (the same early exit
                // SynchronizeBuffer takes for this read-only call), and the block summary
                // answers that without a lock for the common all-clean case.
                if (_tracker.HasCpuDirtyPages(start, finish - start))
                    _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: false);
            }
        }
    }

    // Tests use lower thresholds to check collection without large allocations.
    internal void SetCollectionThresholds(ulong collectionThreshold, ulong criticalThreshold)
    {
        _retirementPolicy.SetThresholds(collectionThreshold, criticalThreshold);
    }

    // Runs before the image readback flush and both collectors; the order matches the render loop.
    public void ProcessPendingFaultBuffer()
    {
        if (_faultProcessPending)
        {
            _faultProcessPending = false;
            ProcessFaultBuffer();
        }

        // The scan's report may have run just now: start the readbacks of the pages it left pending
        // and merge the copies that finished, without waiting.
        ResolvePendingSharedMerges(0, ulong.MaxValue, wait: false);
    }

    // Called once per guest frame; see GuestPageTracker.DecayCpuWriteHeat.
    public void DecayCpuWriteHeat() => _tracker.DecayCpuWriteHeat();

    public void RunGarbageCollector()
    {
        using var foreignRead = _device.Slabs.BeginForeignRead();
        ProcessPendingFaultBuffer();
        if (!_retirementPolicy.TryBeginCollection(_registry.RegisteredBytes, out var retirement))
        {
            return;
        }

        var dirtyBuffers = new List<ResourceSlotIdentifier>();
        var copies = new List<DownloadPiece>();
        var retireCount = 0;
        _registry.VisitRetirementCandidates(retirement.LatestEligibleTick, bufferIdentifier =>
        {
            var buffer = _registry.TryGetRegisteredBuffer(bufferIdentifier);
            if (buffer == null)
            {
                throw SubmissionScheduler.Fatal("The recency queue contains a deleted buffer.");
            }

            _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, buffer.CpuAddress, buffer.Size, "garbage collection");
            var dirty = _tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size);
            if (dirty && !retirement.DownloadDirtyBuffers)
            {
                return false;
            }

            if (GuestGpuMemoryHook.Traces(buffer.CpuAddress, buffer.Size))
                GuestGpuMemoryHook.Trace(buffer.CpuAddress, buffer.Size,
                    $"device-address-collection buffer={bufferIdentifier} dirty={dirty} aggressive={retirement.DownloadDirtyBuffers} cutoff_tick={retirement.LatestEligibleTick} collection_tick={_retirementPolicy.CurrentTick - 1} used_bytes={_registry.RegisteredBytes}");
            if (dirty)
            {
                CollectDirtyPieces(buffer, copies, "garbage collection");
                dirtyBuffers.Add(bufferIdentifier);
            }
            else
            {
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                DeleteBuffer(bufferIdentifier);
            }

            return ++retireCount == retirement.MaximumBufferCount;
        });
        if (dirtyBuffers.Count == 0)
        {
            return;
        }

        if (copies.Count == 0)
        {
            throw SubmissionScheduler.Fatal("Dirty buffers have no download ranges.");
        }

        DownloadBufferMemory(copies);
        foreach (var bufferIdentifier in dirtyBuffers)
        {
            ReleaseDownloaded(bufferIdentifier);
        }
    }

    // Teardown: every GPU result reaches guest memory and every page returns to its guest protection.
    public void Shutdown()
    {
        var drained = false;
        try
        {
            AbandonEagerReadbacks();
            AbandonSharedMergeBatches();
            if (_scheduler.Active)
            {
                _scheduler.Finish();
            }

            using var foreignRead = _device.Slabs.BeginForeignRead();
            var copies = new List<DownloadPiece>();
            var dirtyBuffers = new List<ResourceSlotIdentifier>();
            foreach (var bufferIdentifier in _registry.SnapshotRegisteredIdentifiers())
            {
                var buffer = _registry.GetBuffer(bufferIdentifier);
                if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size))
                {
                    CollectDirtyPieces(buffer, copies, "shutdown");
                    dirtyBuffers.Add(bufferIdentifier);
                }
            }

            if (copies.Count != 0)
            {
                DownloadBufferMemory(copies);
            }

            foreach (var bufferIdentifier in dirtyBuffers)
            {
                ReleaseDownloaded(bufferIdentifier);
            }

            foreach (var bufferIdentifier in _registry.SnapshotRegisteredIdentifiers())
            {
                var buffer = _registry.GetBuffer(bufferIdentifier);
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                Unregister(bufferIdentifier);
                _registry.CompleteRetirement(bufferIdentifier);
            }

            if (_scheduler.Active)
            {
                _scheduler.Finish();
            }

            Dispose();
            drained = true;
        }
        finally
        {
            lock (_shutdownGate)
            {
                _outcome = drained ? ShutdownOutcome.Drained : ShutdownOutcome.Failed;
                Monitor.PulseAll(_shutdownGate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Dispose();
        _deviceRing.Dispose();
        _download.Dispose();
        _stream.Dispose();
        _staging.Dispose();
        _bdaPageTable.Dispose();
        _gds.Dispose();
        _faults.Dispose();
    }

    // False only when the store closed and its drain failed; the caller then declines the fault.
    private bool ReadMemoryOrAwaitShutdown(ulong guestAddress, ulong size, bool isWrite,
        GuestMemoryProfile.ReadbackSource source = GuestMemoryProfile.ReadbackSource.ExplicitReadback)
    {
        if (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation)
        {
            throw SubmissionScheduler.Fatal(
                $"unsupported buffer readback from an asynchronous GPU completion, addr=0x{guestAddress:X16} size=0x{size:X16}");
        }

        if (!_relay.IsGpuQueueThread && AsyncReadback is { } readback && GuestReadsAwaitOffQueue)
        {
            for (var attempt = 0; ; attempt++)
            {
                var allowPending = attempt < MaxAsyncReadbackAttempts;
                var markHot = attempt == 0 && !isWrite;
                PendingDownload? pending = null;
                if (!_relay.TryRunOnGpuQueue(() =>
                        pending = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending, markHot)))
                {
                    return AwaitShutdown();
                }

                if (pending is null)
                {
                    return true;
                }

                readback.Wait(pending.Ticket);
                var complete = false;
                if (!_relay.TryRunOnGpuQueue(() => complete = CompleteReadMemoryOnGpu(pending, consumed: true)))
                {
                    return AwaitShutdown();
                }

                if (complete)
                {
                    return true;
                }
            }
        }

        var onQueue = _relay.IsGpuQueueThread;
        if (_relay.TryRunOnGpuQueue(() =>
            {
                if (!onQueue || isWrite || !TryUseEagerReadback(guestAddress, size))
                {
                    _ = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: false, markHot: onQueue);
                }
            }))
        {
            return true;
        }

        return AwaitShutdown();
    }

    private const ulong ReadbackWindowBytes = 512 * 1024;
    private const long HotWindowLifetime = 512;
    private const int MaxEagerReadbacks = 4;
    private const int MaxAsyncReadbackAttempts = 4;
    private const int EagerStrikeLimit = 3;
    private const long EagerMuteBatches = 4096;
    private readonly Dictionary<ulong, HotWindow> _hotWindows = new();
    private readonly HashSet<ulong> _eagerCandidates = new();
    private readonly List<PendingDownload> _eagerDownloads = new();
    private long _batchSerial;
    private static long _reportedEagerStarted;
    private static long _reportedEagerUsed;
    private static long _reportedEagerStale;
    private static long _reportedEagerMuted;

    private readonly record struct HotWindow(ulong Address, ulong Size, long LastHit, int Strikes = 0, long MutedUntil = 0);

    internal long EagerReadbacksStarted { get; private set; }

    internal long EagerReadbacksUsed { get; private set; }

    private void NoteHotReadback(ulong guestAddress, ulong size)
    {
        var key = guestAddress & ~(ReadbackWindowBytes - 1);
        _hotWindows[key] = _hotWindows.TryGetValue(key, out var hot)
            ? hot with { Address = guestAddress, Size = size, LastHit = _batchSerial }
            : new HotWindow(guestAddress, size, _batchSerial);
    }

    internal long EagerReadbacksWasted { get; private set; }

    private void NoteEagerOutcome(ulong key, bool wasted)
    {
        if (wasted)
        {
            EagerReadbacksWasted++;
        }

        if (!_hotWindows.TryGetValue(key, out var hot))
        {
            return;
        }

        if (!wasted)
        {
            if (hot.Strikes != 0)
            {
                _hotWindows[key] = hot with { Strikes = 0 };
            }

            return;
        }

        var strikes = hot.Strikes + 1;
        _hotWindows[key] = strikes >= EagerStrikeLimit
            ? hot with { Strikes = 0, MutedUntil = _batchSerial + EagerMuteBatches }
            : hot with { Strikes = strikes };
        if (strikes >= EagerStrikeLimit)
        {
            Interlocked.Increment(ref _reportedEagerMuted);
        }
    }

    private void NoteHotWindowWrite(ulong guestAddress, ulong size)
    {
        if (_hotWindows.Count == 0 || size == 0)
        {
            return;
        }

        var last = (guestAddress + size - 1) & ~(ReadbackWindowBytes - 1);
        for (var key = guestAddress & ~(ReadbackWindowBytes - 1); key <= last; key += ReadbackWindowBytes)
        {
            if (_hotWindows.TryGetValue(key, out var hot) && _batchSerial - hot.LastHit <= HotWindowLifetime &&
                _batchSerial >= hot.MutedUntil)
            {
                _eagerCandidates.Add(key);
            }
        }
    }

    public void OnBatchSubmitted()
    {
        _batchSerial++;
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        // The batch just submitted may hold the tick a pending shared page waits for: start its
        // readback now, and merge the ones that finished, so a later upload finds them done.
        ResolvePendingSharedMerges(0, ulong.MaxValue, wait: false);

        for (var index = _eagerDownloads.Count - 1; index >= 0; index--)
        {
            var pending = _eagerDownloads[index];
            if (readback.IsComplete(pending.Ticket))
            {
                _eagerDownloads.RemoveAt(index);
                _ = CompleteReadMemoryOnGpu(pending, consumed: false);
            }
        }

        if ((_batchSerial & 1023) == 0)
        {
            foreach (var key in _hotWindows.Where(pair => _batchSerial - pair.Value.LastHit > HotWindowLifetime && _batchSerial >= pair.Value.MutedUntil)
                         .Select(pair => pair.Key).ToList())
            {
                _hotWindows.Remove(key);
            }
        }

        if (_eagerCandidates.Count == 0)
        {
            return;
        }

        List<ulong>? settled = null;
        foreach (var key in _eagerCandidates)
        {
            if (_eagerDownloads.Count >= MaxEagerReadbacks)
            {
                break;
            }

            if (!_hotWindows.TryGetValue(key, out var hot) || _batchSerial - hot.LastHit > HotWindowLifetime ||
                _batchSerial < hot.MutedUntil || StartEagerReadback(hot) != EagerStart.Deferred)
            {
                (settled ??= []).Add(key);
            }
        }

        if (settled is null)
        {
            return;
        }

        foreach (var key in settled)
        {
            _eagerCandidates.Remove(key);
        }
    }

    private enum EagerStart
    {
        Started,
        Deferred,
        Nothing,
    }

    private EagerStart StartEagerReadback(HotWindow hot)
    {
        if (FindOwner(hot.Address, hot.Size) is not { } buffer)
        {
            return EagerStart.Nothing;
        }

        if (OverlapsEagerReadback(hot.Address, hot.Size))
        {
            return EagerStart.Deferred;
        }

        var copies = CollectReadbackWindow(buffer, hot.Address, hot.Size, out var windowBegin, out var windowEnd);
        if (copies.Count == 0)
        {
            return EagerStart.Nothing;
        }

        if (!TryBeginDownloadAsync(copies, out var ticket, out var writeTicks))
        {
            return EagerStart.Deferred;
        }

        foreach (var copy in copies)
        {
            copy.Buffer.RetainForeignRead();
        }

        _eagerDownloads.Add(new PendingDownload
        {
            HotKey = hot.Address & ~(ReadbackWindowBytes - 1),
            Ticket = ticket,
            Copies = copies,
            WriteTicks = writeTicks,
            WindowBegin = windowBegin,
            WindowEnd = windowEnd,
            GuestAddress = hot.Address,
            Size = hot.Size,
            IsWrite = false,
            Eager = true,
            Source = GuestMemoryProfile.ReadbackSource.ShaderResourceRead,
            Started = System.Diagnostics.Stopwatch.GetTimestamp(),
        });
        EagerReadbacksStarted++;
        Interlocked.Increment(ref _reportedEagerStarted);
        return EagerStart.Started;
    }

    private bool OverlapsEagerReadback(ulong guestAddress, ulong size)
    {
        foreach (var pending in _eagerDownloads)
        {
            if (guestAddress < pending.WindowEnd && pending.WindowBegin < guestAddress + size)
            {
                return true;
            }
        }

        return false;
    }

    private PendingDownload? TakeEagerReadback(ulong guestAddress, ulong size)
    {
        for (var index = _eagerDownloads.Count - 1; index >= 0; index--)
        {
            var pending = _eagerDownloads[index];
            if (guestAddress >= pending.WindowBegin && size <= pending.WindowEnd - pending.WindowBegin &&
                guestAddress - pending.WindowBegin <= pending.WindowEnd - pending.WindowBegin - size)
            {
                _eagerDownloads.RemoveAt(index);
                NoteHotReadback(guestAddress, size);
                return pending;
            }
        }

        return null;
    }

    private bool TryUseEagerReadback(ulong guestAddress, ulong size)
    {
        if (AsyncReadback is not { } readback || TakeEagerReadback(guestAddress, size) is not { } pending)
        {
            return false;
        }

        readback.Wait(pending.Ticket);
        return CompleteReadMemoryOnGpu(pending with { GuestAddress = guestAddress, Size = size }, consumed: true);
    }

    private void AbandonEagerReadbacks()
    {
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        foreach (var pending in _eagerDownloads)
        {
            readback.Wait(pending.Ticket);
            readback.Complete(pending.Ticket, null);
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }

        _eagerDownloads.Clear();
        _eagerCandidates.Clear();
    }

    private static readonly bool GuestReadsAwaitOffQueue =
        Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_GUEST_READBACK") != "0";

    private bool AwaitShutdown()
    {
        lock (_shutdownGate)
        {
            while (_outcome == ShutdownOutcome.Pending)
            {
                Monitor.Wait(_shutdownGate);
            }

            return _outcome == ShutdownOutcome.Drained;
        }
    }

    internal long PendingReadbacksApplied { get; private set; }

    internal long PendingReadbacksRetried { get; private set; }

    private static long _reportedApplied;
    private static long _reportedRetried;

    private static long _reportedStalePieces;
    private static long _reportedPublishedPieces;

    internal long StalePiecesKept { get; private set; }

    public static string TakeAsyncReadbackReport() => FormattableString.Invariant(
        $"[PERF][ASYNC_READBACK] applied={Interlocked.Exchange(ref _reportedApplied, 0)} retried={Interlocked.Exchange(ref _reportedRetried, 0)} published_pieces={Interlocked.Exchange(ref _reportedPublishedPieces, 0)} stale_pieces={Interlocked.Exchange(ref _reportedStalePieces, 0)} eager_started={Interlocked.Exchange(ref _reportedEagerStarted, 0)} eager_used={Interlocked.Exchange(ref _reportedEagerUsed, 0)} eager_stale={Interlocked.Exchange(ref _reportedEagerStale, 0)} eager_muted={Interlocked.Exchange(ref _reportedEagerMuted, 0)}");

    private sealed record PendingDownload
    {
        public required VulkanAsyncReadback.Ticket Ticket { get; init; }
        public required List<DownloadPiece> Copies { get; init; }
        public required ulong[] WriteTicks { get; init; }
        public required ulong WindowBegin { get; init; }
        public required ulong WindowEnd { get; init; }
        public required ulong GuestAddress { get; init; }
        public required ulong Size { get; init; }
        public required bool IsWrite { get; init; }
        public bool Eager { get; init; }
        public ulong HotKey { get; init; }
        public required GuestMemoryProfile.ReadbackSource Source { get; init; }
        public required long Started { get; init; }
    }

    private PendingDownload? BeginReadMemoryOnGpu(ulong guestAddress, ulong size, bool isWrite, GuestMemoryProfile.ReadbackSource source,
        bool allowPending, bool markHot = false)
    {
        if (MeshDrawTrace.Enabled) MeshDrawTrace.Range("cpu-synchronize", guestAddress, size, $"write={isWrite} source={source}");
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        using var foreignRead = _device.Slabs.BeginForeignRead();
        var readbackStarted = GuestMemoryProfile.ReadbackDetailsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (isWrite && !IsRegionRegistered(guestAddress, size))
        {
            return null;
        }

        if (!isWrite && allowPending && TakeEagerReadback(guestAddress, size) is { } eager)
        {
            return eager with { GuestAddress = guestAddress, Size = size, IsWrite = isWrite, Source = source };
        }

        var copies = CollectReadbackWindow(guestAddress, size, out var windowBegin, out var windowEnd);
        if (markHot && copies.Count != 0)
        {
            NoteHotReadback(guestAddress, size);
        }
        if (copies.Count != 0 && allowPending && TryBeginDownloadAsync(copies, out var ticket, out var writeTicks))
        {
            foreach (var copy in copies)
            {
                copy.Buffer.RetainForeignRead();
            }

            return new PendingDownload
            {
                Ticket = ticket,
                Copies = copies,
                WriteTicks = writeTicks,
                WindowBegin = windowBegin,
                WindowEnd = windowEnd,
                GuestAddress = guestAddress,
                Size = size,
                IsWrite = isWrite,
                Source = source,
                Started = readbackStarted,
            };
        }

        if (copies.Count != 0)
        {
            DownloadBufferMemory(copies);
            _tracker.ClearGpuDirtyPages(windowBegin, windowEnd - windowBegin);
        }

        if (isWrite)
        {
            _tracker.MarkCpuDirtyPages(guestAddress, size);
        }

        RecordReadback(windowBegin, windowEnd, isWrite, copies, readbackStarted, source);
        return null;
    }

    private bool CompleteReadMemoryOnGpu(PendingDownload pending, bool consumed)
    {
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The pending readback lost its queue.");
        try
        {
            var copies = pending.Copies;
            var current = new bool[copies.Count];
            var published = 0;
            for (var index = 0; index < copies.Count; index++)
            {
                current[index] = IsPieceCurrent(pending, index);
                if (current[index])
                {
                    published++;
                }
            }

            readback.Complete(pending.Ticket, published == 0 ? null : (index, bytes) =>
            {
                if (!current[index])
                {
                    return;
                }

                GpuWritebackEpoch.Advance();
                if (!WriteDownloaded(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
                }
            });
            for (var index = 0; index < copies.Count; index++)
            {
                if (current[index])
                {
                    _gpuModifiedRanges.Remove(copies[index].Address, copies[index].Size);
                    Interlocked.Increment(ref _gpuModifiedVersion);
                }
            }

            for (var index = 0; index < copies.Count; index++)
            {
                if (current[index])
                {
                    ClearGpuDirtyPagesOfRemovedRange(copies[index].Address, copies[index].Size);
                }
            }

            var stale = copies.Count - published;
            if (pending.Eager)
            {
                NoteEagerOutcome(pending.HotKey, stale != 0);
            }

            if (stale != 0)
            {
                StalePiecesKept += stale;
                Interlocked.Add(ref _reportedStalePieces, stale);
            }

            Interlocked.Add(ref _reportedPublishedPieces, published);
            var clean = pending.IsWrite
                ? !_tracker.HasGpuDirtyPages(pending.GuestAddress, pending.Size)
                : !HasGpuDirtyBytes(pending.GuestAddress, pending.Size);
            if (!consumed)
            {
                if (pending.Eager && stale != 0)
                {
                    Interlocked.Increment(ref _reportedEagerStale);
                }

                return clean;
            }

            if (!clean)
            {
                PendingReadbacksRetried++;
                Interlocked.Increment(ref _reportedRetried);
                if (pending.Eager)
                {
                    Interlocked.Increment(ref _reportedEagerStale);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);
            if (pending.Eager)
            {
                EagerReadbacksUsed++;
                Interlocked.Increment(ref _reportedEagerUsed);
            }

            if (pending.IsWrite)
            {
                _tracker.MarkCpuDirtyPages(pending.GuestAddress, pending.Size);
            }

            RecordReadback(pending.WindowBegin, pending.WindowEnd, pending.IsWrite, copies, pending.Started, pending.Source);
            return true;
        }
        finally
        {
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }
    }

    private bool IsPieceCurrent(PendingDownload pending, int index)
    {
        var copy = pending.Copies[index];
        return copy.Buffer.LastGpuWriteTickIn(copy.SourceOffset, copy.Size) == pending.WriteTicks[index] &&
               _gpuModifiedRanges.Contains(copy.Address, copy.Size) &&
               ReferenceEquals(FindOwner(copy.Address, copy.Size), copy.Buffer);
    }

    private static void RecordReadback(ulong windowBegin, ulong windowEnd, bool isWrite, List<DownloadPiece> copies, long started,
        GuestMemoryProfile.ReadbackSource source)
    {
        if (!GuestMemoryProfile.ReadbackDetailsEnabled)
        {
            return;
        }

        var downloadedBytes = 0UL;
        foreach (var copy in copies)
            downloadedBytes += copy.Size;
        GuestMemoryProfile.RecordBufferReadback(windowBegin, windowEnd - windowBegin, isWrite, downloadedBytes,
            System.Diagnostics.Stopwatch.GetTimestamp() - started, source);
    }

    // The GPU-modified ranges to download for a read of the range, widened to a window so
    // nearby CPU reads share one GPU drain.
    private List<DownloadPiece> CollectReadbackWindow(ulong guestAddress, ulong size, out ulong windowBegin, out ulong windowEnd) =>
        CollectReadbackWindow(_registry.GetBuffer(FindBuffer(guestAddress, size)), guestAddress, size, out windowBegin, out windowEnd);

    private List<DownloadPiece> CollectReadbackWindow(GpuBuffer buffer, ulong guestAddress, ulong size, out ulong windowBegin, out ulong windowEnd)
    {
        const ulong windowSize = ReadbackWindowBytes;
        var bufferEnd = buffer.CpuAddress + buffer.Size;
        windowBegin = Math.Max(guestAddress & ~(windowSize - 1), buffer.CpuAddress);
        windowEnd = Math.Min(Math.Max(windowBegin + windowSize, guestAddress + size), bufferEnd);

        var copies = new List<DownloadPiece>();
        _tracker.ForEachDownloadRange(
            windowBegin,
            windowEnd - windowBegin,
            clear: false,
            (address, bytes) => _tracker.ValidateGpuDirtyPages(_gpuModifiedRanges, address, bytes, "memory invalidation"),
            (address, bytes) =>
            {
                foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(address, bytes))
                {
                    copies.Add(new DownloadPiece(buffer, buffer.Offset(range.Address), range.Address, range.Size));
                }
            });
        return copies;
    }

    private void CollectDirtyPieces(GpuBuffer buffer, List<DownloadPiece> copies, string operation)
    {
        _tracker.ForEachDownloadRange(
            buffer.CpuAddress,
            buffer.Size,
            clear: false,
            (address, bytes) => _tracker.ValidateGpuDirtyPages(_gpuModifiedRanges, address, bytes, operation),
            (address, bytes) =>
            {
                foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(address, bytes))
                {
                    copies.Add(new DownloadPiece(buffer, range.Address - buffer.CpuAddress, range.Address, range.Size));
                }
            });
    }

    private void ReleaseDownloaded(ResourceSlotIdentifier bufferIdentifier)
    {
        var buffer = _registry.GetBuffer(bufferIdentifier);
        _tracker.ClearGpuDirtyPages(buffer.CpuAddress, buffer.Size);
        if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size) || _gpuModifiedRanges.Overlaps(buffer.CpuAddress, buffer.Size))
        {
            throw SubmissionScheduler.Fatal("Buffer collection left GPU-owned memory.");
        }

        _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
        Unregister(bufferIdentifier);
        CompleteRetirementAfterSubmittedWork(bufferIdentifier);
    }

    private void WriteDataBuffer(GpuBuffer buffer, ulong address, ReadOnlySpan<byte> source)
    {
        while (!source.IsEmpty)
        {
            var chunk = (int)Math.Min((ulong)source.Length, _staging.Size);
            var offset = _staging.Copy(source[..chunk], 4);
            buffer.CopyFrom(_scheduler.Current, _staging, offset, buffer.Offset(address), (ulong)chunk, AccessFlags.HostWriteBit);
            source = source[chunk..];
            address += (ulong)chunk;
        }
    }

    private void Register(ResourceSlotIdentifier bufferIdentifier) => UpdateRegistration(bufferIdentifier, insert: true);

    private void Unregister(ResourceSlotIdentifier bufferIdentifier) => UpdateRegistration(bufferIdentifier, insert: false);

    private void UpdateRegistration(ResourceSlotIdentifier bufferIdentifier, bool insert)
    {
        var buffer = _registry.GetBuffer(bufferIdentifier);
        if (!PageOwnerTable.TryGetPageRange(buffer.CpuAddress, buffer.Size, out var first, out var lastExclusive))
        {
            throw SubmissionScheduler.Fatal("The buffer is outside the page table.");
        }

        var sizePages = lastExclusive - first;
        if (GuestGpuMemoryHook.Traces(buffer.CpuAddress, buffer.Size))
            GuestGpuMemoryHook.Trace(buffer.CpuAddress, buffer.Size,
                $"device-address-registration insert={insert} buffer={bufferIdentifier} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        if (insert)
        {
            _registry.RegisterBuffer(bufferIdentifier, _retirementPolicy.CurrentTick);
            var addresses = new ulong[sizePages];
            for (ulong page = 0; page < sizePages; page++)
            {
                addresses[page] = buffer.DeviceAddress + (page << CachingPageBits);
            }

            WriteDataBuffer(_bdaPageTable, first * sizeof(ulong), MemoryMarshal.AsBytes<ulong>(addresses));
        }
        else
        {
            _registry.BeginRetirement(bufferIdentifier);
            _bdaPageTable.Fill(first * sizeof(ulong), sizePages * sizeof(ulong), 0);
        }
    }

    private void TouchBuffer(GpuBuffer buffer)
    {
        var identifier = _registry.FindContainingBuffer(buffer.CpuAddress, buffer.Size);
        if (identifier.IsValid && ReferenceEquals(_registry.GetBuffer(identifier), buffer))
        {
            TouchBuffer(identifier);
        }
    }

    private void TouchBuffer(ResourceSlotIdentifier identifier) =>
        _registry.MarkBufferUsed(identifier, _retirementPolicy.CurrentTick);

    private void DeleteBuffer(ResourceSlotIdentifier bufferIdentifier)
    {
        if (_registry.TryGetRegisteredBuffer(bufferIdentifier) == null)
        {
            return;
        }

        Unregister(bufferIdentifier);
        CompleteRetirementAfterSubmittedWork(bufferIdentifier);
    }

    // Unregistering clears the buffer's page-table entries only for work recorded from now
    // on. Work already recorded or in flight can still reach it through its device address
    // (an async readback waits only for its last recorded writer), so it is destroyed once
    // that work completes.
    private void CompleteRetirementAfterSubmittedWork(ResourceSlotIdentifier bufferIdentifier)
    {
        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(() => _registry.CompleteRetirement(bufferIdentifier));
        }
        else
        {
            _registry.CompleteRetirement(bufferIdentifier);
        }
    }

    // Set when a second queue can copy readbacks; null keeps every readback on the main queue.
    internal VulkanAsyncReadback? AsyncReadback { get; set; }

    private const ulong AsyncReadbackLimit = 64UL << 20;

    // Packs pieces into the download ring, waits for the copy, then writes each through the backing alias.
    private void DownloadBufferMemory(List<DownloadPiece> copies)
    {
        if (TryDownloadAsync(copies))
        {
            foreach (var copy in copies)
            {
                _gpuModifiedRanges.Remove(copy.Address, copy.Size);
                Interlocked.Increment(ref _gpuModifiedVersion);
            }

            return;
        }

        var batch = new List<PlannedDownload>();
        var planner = new BufferDownloadBatchPlanner(_download.Size);
        foreach (var piece in copies)
        {
            if (MeshDrawTrace.Enabled) MeshDrawTrace.Range("buffer-download-planned", piece.Address, piece.Size, $"sourceOffset=0x{piece.SourceOffset:X}");
            var copy = piece;
            while (copy.Size != 0)
            {
                var placement = planner.Append(copy.SourceOffset, copy.Size, copy.Buffer.Size);
                batch.Add(new PlannedDownload(copy.Buffer, copy.Address, placement));
                copy = copy with
                {
                    SourceOffset = copy.SourceOffset + placement.DataSize,
                    Address = copy.Address + placement.DataSize,
                    Size = copy.Size - placement.DataSize,
                };
                if (planner.IsFull)
                {
                    FlushDownloads(batch, planner.PackedSize);
                    planner.Reset();
                }
            }
        }

        if (batch.Count != 0)
        {
            FlushDownloads(batch, planner.PackedSize);
        }

        foreach (var copy in copies)
        {
            _gpuModifiedRanges.Remove(copy.Address, copy.Size);
            Interlocked.Increment(ref _gpuModifiedVersion);
        }
    }

    private bool TryBeginDownloadAsync(List<DownloadPiece> copies, out VulkanAsyncReadback.Ticket ticket, out ulong[] writeTicks)
    {
        ticket = null!;
        writeTicks = [];
        if (AsyncReadback is not { } readback || copies.Count == 0)
        {
            return false;
        }

        var waitTick = 0UL;
        var total = 0UL;
        var ticks = new ulong[copies.Count];
        for (var index = 0; index < copies.Count; index++)
        {
            var written = copies[index].Buffer.LastGpuWriteTickIn(copies[index].SourceOffset, copies[index].Size);
            if (written == 0)
            {
                return false;
            }

            ticks[index] = written;
            waitTick = Math.Max(waitTick, written);
            total += copies[index].Size;
        }

        if (total > AsyncReadbackLimit)
        {
            return false;
        }

        if (waitTick >= _scheduler.CurrentTick)
        {
            _scheduler.Flush();
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        if (!readback.TryBegin(pieces, waitTick, out var started) || started is null)
        {
            return false;
        }

        ticket = started;
        writeTicks = ticks;
        return true;
    }

    // Copies the pieces on the readback queue after only the tick that last wrote their
    // buffers, instead of appending the copy to the main queue and draining all of it.
    private bool TryDownloadAsync(List<DownloadPiece> copies)
    {
        if (AsyncReadback is not { } readback || copies.Count == 0)
        {
            return false;
        }

        var waitTick = 0UL;
        var total = 0UL;
        foreach (var copy in copies)
        {
            var written = copy.Buffer.LastGpuWriteTickIn(copy.SourceOffset, copy.Size);
            // A GPU-modified range with no recorded writer: keep the conservative path.
            if (written == 0)
            {
                return false;
            }

            waitTick = Math.Max(waitTick, written);
            total += copy.Size;
        }

        if (total > AsyncReadbackLimit)
        {
            return false;
        }

        // The last writer is still in the buffer being recorded; submit it (without
        // waiting) so the readback queue has a signal to wait for.
        if (waitTick >= _scheduler.CurrentTick)
        {
            _scheduler.Flush();
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        readback.Read(pieces, waitTick, (index, bytes) =>
        {
            GpuWritebackEpoch.Advance();
            if (!WriteDownloaded(copies[index].Address, bytes))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
            }
        });
        return true;
    }

    private void FlushDownloads(List<PlannedDownload> batch, ulong packedSize)
    {
        if (!_download.TryMap(packedSize, out var baseOffset, BufferDownloadBatchPlanner.Alignment))
        {
            throw SubmissionScheduler.Fatal("The download ring could not map the batch.");
        }

        foreach (var copy in batch)
        {
            var placement = copy.Placement;
            _download.CopyFrom(
                _scheduler.Current, copy.Buffer, placement.SourceOffset, baseOffset + placement.DestinationOffset, placement.TransferSize,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        }

        _download.Commit();
        var completionTick = _scheduler.CurrentTick;
        _scheduler.Finish();
        _scheduler.WaitForPriorityOperations(completionTick);
        foreach (var copy in batch)
        {
            var placement = copy.Placement;
            var offset = baseOffset + placement.DataOffset;
            _download.Invalidate(offset, placement.DataSize);
            GpuWritebackEpoch.Advance();
            if (GpuCommands.VisibilityResultTrace.Enabled)
                GpuCommands.VisibilityResultTrace.Download(copy.Address,
                    _download.Mapped.Slice((int)offset, (int)placement.DataSize), _backing.TryReadBacking);
            if (!WriteDownloaded(copy.Address, _download.Mapped.Slice((int)offset, (int)placement.DataSize)))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copy.Address:X16} size=0x{placement.DataSize:X16}");
            }
            if (MeshDrawTrace.Enabled) MeshDrawTrace.Range("buffer-published", copy.Address, placement.DataSize, $"tick={completionTick}");
        }

        batch.Clear();
    }

    private OverlapSpan ResolveOverlaps(ulong guestAddress, ulong size)
    {
        var range = new BufferMergeRange(guestAddress, guestAddress + size);
        var first = _registry.FindFirstOverlappingIndex(range.Begin);
        var last = first;
        for (; last < _registry.RegisteredCount && _registry.GetRegisteredAddress(last) < range.End; last++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(last));
            if (range.IncludeBuffer(buffer.CpuAddress, buffer.CpuAddress + buffer.Size, buffer.StreamScore))
            {
                first = _registry.FindFirstOverlappingIndex(range.Begin);
                if (first < _registry.RegisteredCount)
                {
                    range.IncludeEarlierBuffer(_registry.GetRegisteredAddress(first));
                }
            }
        }

        return new OverlapSpan(first, last, range.Begin, range.End, range.HasStreamExpansion);
    }

    private void MergeOverlappingBuffer(ResourceSlotIdentifier newBufferIdentifier, ResourceSlotIdentifier overlappingBufferIdentifier, bool accumulateStreamScore)
    {
        var newBuffer = _registry.GetBuffer(newBufferIdentifier);
        var overlap = _registry.GetBuffer(overlappingBufferIdentifier);
        if (GuestGpuMemoryHook.Traces(overlap.CpuAddress, overlap.Size))
            GuestGpuMemoryHook.Trace(overlap.CpuAddress, overlap.Size,
                $"device-address-merge old={overlappingBufferIdentifier} replacement={newBufferIdentifier} submission_tick={_scheduler.CurrentTick}");
        if (accumulateStreamScore)
        {
            newBuffer.AddStreamScore(overlap.StreamScore + 1);
        }

        newBuffer.CopyFrom(_scheduler.Current, overlap, 0, overlap.CpuAddress - newBuffer.CpuAddress, overlap.Size);
        _sharedPages.NoteOwnerCopy(overlap, newBuffer, _scheduler.CurrentTick);
        if (Volatile.Read(ref _shaderWrittenPageCount) != 0)
        {
            lock (_shaderWrittenPages)
            {
                for (var page = overlap.CpuAddress; page < overlap.CpuAddress + overlap.Size; page += TrackerLayout.PageBytes)
                {
                    if (_shaderWrittenPages.TryGetValue(page, out var owner) && ReferenceEquals(owner, overlap))
                        _shaderWrittenPages[page] = newBuffer;
                }
            }
        }

        DeleteBuffer(overlappingBufferIdentifier);
    }

    private ResourceSlotIdentifier CreateBuffer(ulong guestAddress, ulong size)
    {
        if (_scheduler.Current.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Buffer creation requires a command buffer that is recording.");
        }

        var end = (guestAddress + size + CachingPageSize - 1) & ~(CachingPageSize - 1);
        guestAddress &= ~(CachingPageSize - 1);
        size = end - guestAddress;
        var overlap = ResolveOverlaps(guestAddress, size);
        var overlapping = new List<ResourceSlotIdentifier>();
        for (var index = overlap.First; index < overlap.Last; index++)
        {
            overlapping.Add(_registry.GetRegisteredIdentifier(index));
        }

        GpuBuffer CreateGpuBuffer() => new(
            _device, _scheduler, GpuBufferUsage.DeviceLocal, overlap.Begin,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, overlap.End - overlap.Begin, allowSlab: true);
        GpuBuffer created;
        try
        {
            created = CreateGpuBuffer();
        }
        catch (GpuBuffer.OutOfMemoryException)
        {
            // Images hold most of the device memory: free the ones that can go and try once more.
            RequireImageCache().ReclaimForAllocation();
            try
            {
                created = CreateGpuBuffer();
            }
            catch (GpuBuffer.OutOfMemoryException again)
            {
                throw SubmissionScheduler.Fatal(again.Message);
            }
        }

        var bufferIdentifier = _registry.AllocateBuffer(created, overlap.Begin, overlap.End - overlap.Begin);
        foreach (var oldId in overlapping)
        {
            MergeOverlappingBuffer(bufferIdentifier, oldId, !overlap.HasStreamLeap);
        }

        Register(bufferIdentifier);
        return bufferIdentifier;
    }

    private bool SynchronizeBuffer(GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer,
        bool preserveCpuWriteHotPages = true, bool readImageBacking = false)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySynchronization);
        // Shader bytes a shared page still owes guest memory and its shadow must land before an upload
        // is filtered against the shadow, whether or not this call finds more to upload.
        ResolvePendingSharedMerges(guestAddress, size, wait: true);
        var startedAt = BufferUploadProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var nothingToUpload = isWritten
            ? _gpuModifiedRanges.Contains(guestAddress, size)
            : !_tracker.MayHaveCpuDirtyPages(guestAddress, size);
        if (nothingToUpload)
        {
            if (BufferUploadProfile.Enabled)
                BufferUploadProfile.Record(guestAddress, size, 0, 0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            return isTexelBuffer && !readImageBacking && SynchronizeTexelFromImage(buffer, guestAddress, size);
        }

        profileScope.SwitchPhase(isWritten
            ? RenderPhaseProfile.Phase.BufferDirtySyncWritten
            : isTexelBuffer ? RenderPhaseProfile.Phase.BufferDirtySyncTexel : RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        var collector = _uploadCollector;
        if (collector is null || collector.InUse)
        {
            // The first call on a thread keeps its collector; a nested one gets its own.
            collector = new UploadCollector();
            _uploadCollector ??= collector;
        }

        collector.Begin(this, buffer, guestAddress, size, readImageBacking ? _tryReadImageSource ??= TryReadImageSource : null);
        try
        {
            return SynchronizeBufferCore(collector, buffer, guestAddress, size, isWritten, isTexelBuffer, preserveCpuWriteHotPages, readImageBacking, startedAt);
        }
        finally
        {
            collector.End();
        }
    }

    // The ranges of one synchronization. The delegates are bound once, so a call that has dirty
    // pages to upload allocates no closure and no list.
    private sealed class UploadCollector
    {
        private GuestBufferCache _cache = null!;
        private GpuBuffer _buffer = null!;
        private ulong _guestAddress;
        private ulong _size;
        private GuestBufferSourceReader? _readSource;
        public readonly List<BufferCopy> Copies = new();
        public ulong TotalSize;
        public GpuBuffer? Source;
        public bool InUse;
        public readonly Action<ulong, ulong> Collect;
        public readonly Action Upload;

        public UploadCollector()
        {
            Collect = (address, bytes) =>
            {
                Copies.Add(new BufferCopy(TotalSize, _buffer.Offset(address), bytes));
                TotalSize += bytes;
            };
            Upload = () => Source = _cache._uploader.PrepareSource(_buffer.CpuAddress, CollectionsMarshal.AsSpan(Copies), TotalSize, _guestAddress, _size, _readSource);
        }

        public void Begin(GuestBufferCache cache, GpuBuffer buffer, ulong guestAddress, ulong size, GuestBufferSourceReader? readSource)
        {
            InUse = true;
            _cache = cache;
            _buffer = buffer;
            _guestAddress = guestAddress;
            _size = size;
            _readSource = readSource;
        }

        public void End()
        {
            InUse = false;
            _cache = null!;
            _buffer = null!;
            _readSource = null;
            Copies.Clear();
            TotalSize = 0;
            Source = null;
        }
    }

    [ThreadStatic] private static UploadCollector? _uploadCollector;

    private bool SynchronizeBufferCore(UploadCollector collector, GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten,
        bool isTexelBuffer, bool preserveCpuWriteHotPages, bool readImageBacking, long startedAt)
    {
        var copies = collector.Copies;
        _tracker.ForEachUploadRange(guestAddress, size, isWritten, collector.Collect, collector.Upload, preserveCpuWriteHotPages);
        var source = collector.Source;
        var totalSize = collector.TotalSize;
        if (source != null && _sharedPages.Count != 0)
        {
            // Shared pages copy only the bytes the CPU changed, so shader bytes in the buffer stay.
            _sharedPages.FilterUpload(copies, buffer, buffer.CpuAddress, source.Mapped, _scheduler.CurrentTick);
            if (copies.Count == 0)
            {
                source = null;
            }
        }

        if (source != null)
        {
            foreach (var copy in copies)
            {
                buffer.NoteGpuWrite(copy.DstOffset, copy.Size);
            }

            if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-upload", guestAddress, size, $"bytes={totalSize} copies={copies.Count}");
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            var vk = _device.Vk;
            var before = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer.Handle,
                Offset = 0,
                Size = buffer.Size,
            };
            VulkanSynchronization.PipelineBarrier(vk,
                native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
                0, null, 1, &before, 0, null);
            var regions = CollectionsMarshal.AsSpan(copies);
            fixed (BufferCopy* pointer = regions)
            {
                vk.CmdCopyBuffer(native, source.Handle, buffer.Handle, (uint)regions.Length, pointer);
            }

            var after = before;
            after.SrcAccessMask = AccessFlags2.TransferWriteBit;
            after.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
            VulkanSynchronization.PipelineBarrier(vk,
                native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
                0, null, 1, &after, 0, null);
        }

        if (BufferUploadProfile.Enabled)
        {
            var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
            ulong hotBytes = 0;
            foreach (var copy in copies)
                hotBytes += _tracker.CountCpuWriteHotBytes(buffer.CpuAddress + copy.DstOffset, copy.Size);
            BufferUploadProfile.Record(guestAddress, size, copies.Count, totalSize, hotBytes, elapsedTicks);
        }

        return isTexelBuffer && !readImageBacking && SynchronizeTexelFromImage(buffer, guestAddress, size);
    }

    private bool SynchronizeTexelFromImage(GpuBuffer buffer, ulong guestAddress, ulong size)
    {
        var copiedFromImage = RequireImageCache().TrySynchronizeBufferFromImage(buffer, guestAddress, size);
        if (copiedFromImage)
        {
            buffer.NoteGpuWrite(buffer.Offset(guestAddress), size);
        }

        return copiedFromImage;
    }

    private GpuBuffer? FindOwner(ulong guestAddress, ulong size)
    {
        return _registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(guestAddress, size));
    }

    private IGuestImageCache RequireImageCache() =>
        ImageCache ?? throw SubmissionScheduler.Fatal("The image cache is not connected.");

    private static bool IsValidRange(ulong guestAddress, ulong size) => guestAddress != 0 && size != 0 && new GuestSpan(guestAddress, size).IsValid;

}
