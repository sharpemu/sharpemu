// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;
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
    private readonly bool _unifiedBuffers;
    private readonly IGuestBackedSpace _backing;
    private readonly BdaFaultProcessor _faults;
    private readonly GpuBuffer _gds;
    private readonly GpuBuffer _bdaPageTable;
    private readonly GuestBufferRegistry<GpuBuffer> _registry = new(CachingPageSize, PageOwnerTable.AddressSpaceSize);
    private readonly SpanSet _gpuModifiedRanges = new();
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
        _faults = new BdaFaultProcessor(device, scheduler, this, CachingPageBits, CachingPageCount);
        _unifiedBuffers = UnifiedBuffersEnabled && HasUnifiedMemoryType(device);
        _gds = new GpuBuffer(device, scheduler, GpuBufferUsage.Stream, 0, GpuBuffer.AllFlags, GdsBufferSize);
        _bdaPageTable = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, BdaPageTableSize);
        _tracker = new GuestPageTracker(pages);
        _staging = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Upload, 512 * MiB);
        _uploader = new GuestBufferUploader(device, scheduler, guest, _staging);
        _stream = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Stream, 64 * MiB);
        _download = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Download, 32 * MiB);
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
        !_tracker.MayHaveGpuDirtyPages(address, size) ||
        !_tracker.HasGpuDirtyPages(address, size) ||
        ReadMemoryOrAwaitShutdown(address, size, isWrite: false, source);

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

    public (GpuBuffer Buffer, ulong Offset) ObtainBuffer(ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer = false, ResourceSlotIdentifier bufferIdentifier = default)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferAcquisitionChecks);
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("A buffer request requires a command buffer that is recording.");
        }

        if (!isWritten &&
            !_tracker.HasGpuDirtyPages(guestAddress, size) &&
            _tracker.HasCpuDirtyPages(guestAddress, size) &&
            (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size))))
        {
            using var streamProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStreamUpload);
            if (_stream.TryMap(size, out var streamOffset, StreamOffsetAlignment, allowWait: false) &&
                _backing.TryReadBacking(guestAddress, _stream.Mapped.Slice((int)streamOffset, (int)size)))
            {
                _stream.Commit();
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
            buffer.NoteGpuWrite();
            if (Diagnostics.DccWriterTrace.Enabled)
            {
                Diagnostics.DccWriterTrace.Record(RequireImageCache(), guestAddress, size);
            }

            if (!_gpuModifiedRanges.Contains(guestAddress, size))
            {
                _gpuModifiedRanges.Add(guestAddress, size);
                Interlocked.Increment(ref _gpuModifiedVersion);
            }

            NoteHotWindowWrite(guestAddress, size);
        }

        return (buffer, buffer.Offset(guestAddress));
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBufferForImage(ulong guestAddress, ulong size)
    {
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

        if (owner != null && !cpuModified && (!gpuModified || hasDirtyBufferSource))
        {
            TouchBuffer(owner);
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
        if (!_backing.TryReadBacking(guestAddress, _staging.Mapped.Slice((int)stageOffset, (int)size)) &&
            !KernelMemoryCompatExports.TryReadPrtBacking(_backing, guestAddress,
                _staging.Mapped.Slice((int)stageOffset, (int)size)))
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
        _ = SynchronizeBuffer(owner, guestAddress, size, false, false);
        return (owner, owner.Offset(guestAddress));
    }

    // The command worker can read the stable backing alias without changing CPU page
    // permissions. A GPU counter elsewhere on the same page is not a dependency of
    // the packet bytes being decoded. Actual GPU-written bytes retain the slow path.
    private static readonly bool CommandBackingReadsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_COMMAND_BACKING_READS") != "0";

    public bool TryReadCommandBacking(ulong address, Span<byte> destination) =>
        CommandBackingReadsEnabled && destination.Length != 0 &&
        !_gpuModifiedRanges.Overlaps(address, (ulong)destination.Length) &&
        ImageCache is { } images && !images.HasGpuModifiedImageBytes(address, (ulong)destination.Length) &&
        _backing.TryReadBacking(address, destination);

    public void WriteHostMemory(ulong guestAddress, ReadOnlySpan<byte> data)
    {
        if (guestAddress == 0 || data.IsEmpty || (ulong)data.Length > ulong.MaxValue - guestAddress)
        {
            throw SubmissionScheduler.Fatal("The host DMA write range is invalid.");
        }

        if (!_backing.TryWriteBacking(guestAddress, data))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{data.Length:X16}");
        }

        // Registered buffers are ordered and disjoint: walk only the ones the write overlaps.
        var end = guestAddress + (ulong)data.Length;
        if (DeferHostWrites)
        {
            // When no GPU-written byte shares the written pages, the pages can simply turn
            // CPU-dirty: every later GPU reader synchronizes CPU-dirty pages first (bound buffers
            // on obtain, device-address programs at the next visibility point), so the bytes
            // arrive in command order without a staging copy that ends the rendering scope.
            var firstPage = guestAddress & ~(TrackerLayout.PageBytes - 1);
            var pagesEnd = (end + TrackerLayout.PageBytes - 1) & ~(TrackerLayout.PageBytes - 1);
            if (!_gpuModifiedRanges.Overlaps(firstPage, pagesEnd - firstPage) &&
                !_tracker.HasGpuDirtyPages(firstPage, pagesEnd - firstPage))
            {
                if (IsRegionRegistered(guestAddress, (ulong)data.Length))
                {
                    _tracker.MarkCpuDirtyPages(guestAddress, (ulong)data.Length);
                    NoteMemoryVisibilityPoint();
                }

                return;
            }
        }

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

    private static readonly bool DeferHostWrites =
        Environment.GetEnvironmentVariable("SHARPEMU_DEFER_HOST_WRITES") != "0";

    public void FillBuffer(ulong guestAddress, ulong size, uint value, bool isGds)
    {
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
        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, true, true, bufferIdentifier);
        destination.Fill(destinationOffset, size, value);
        if (images.OverlapsDccMetadata(guestAddress, size) && TryWriteFillToBacking(guestAddress, size, value))
        {
            _gpuModifiedRanges.Remove(guestAddress, size);
            var firstPage = guestAddress & ~(TrackerLayout.PageBytes - 1);
            for (var page = firstPage; page < guestAddress + size; page += TrackerLayout.PageBytes)
            {
                if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
                {
                    _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
                }
            }
        }
    }

    public void FillDccMetadata(ulong guestAddress, ulong size, uint value)
    {
        if (guestAddress == 0 || (guestAddress & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - guestAddress ||
            HasGpuDirtyBytes(guestAddress, size) || RequireImageCache().QueryRegion(guestAddress, size).ImageBytes)
        {
            FillBuffer(guestAddress, size, value, false);
            return;
        }

        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, false, false, FindBuffer(guestAddress, size));
        destination.Fill(destinationOffset, size, value);
        if (!TryWriteFillToBacking(guestAddress, size, value))
        {
            FillBuffer(guestAddress, size, value, false);
        }
    }

    // The fill pattern staged per thread; DCC fills arrive with every compressed target clear.
    [ThreadStatic]
    private static uint[]? _fillPattern;

    private bool TryWriteFillToBacking(ulong guestAddress, ulong size, uint value)
    {
        var values = (_fillPattern ??= new uint[4096]).AsSpan(0, (int)Math.Min(size / sizeof(uint), 4096));
        values.Fill(value);
        var bytes = MemoryMarshal.AsBytes(values);
        for (ulong offset = 0; offset < size;)
        {
            var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
            if (!_backing.TryWriteBacking(guestAddress + offset, bytes[..chunk]))
            {
                return false;
            }

            offset += (ulong)chunk;
        }

        return true;
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

    // Uploads every mapped range before a BDA draw; the fault pass runs at the next collection.
    // Every device-address program prepares all GPU-mapped memory; visiting each registered
    // buffer per dispatch cost ~17 % of the Demon's Souls render thread. The same work is done
    // in two cheaper parts: the recency touch, a no-op after the first one in a retirement tick,
    // runs once per tick (and when the mapping changes; new buffers register with the current
    // tick), and uploads only visit buffers in blocks the tracker does not know to be clean.
    // Hot pages stay dirty and writable, so every preparation still re-uploads them.
    private ulong _bdaTouchTick = ulong.MaxValue;
    private ulong _bdaTouchMapping;

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
                _bdaTouchTick = _retirementPolicy.CurrentTick;
                _bdaTouchMapping = mapping;
            }

            // Device-address programs may read any mapped page, so CPU writes are swept into the
            // buffers - but only at memory visibility points (a submission slice starting, a wait
            // packet satisfied). A CPU write after the submission began carries no ordering
            // guarantee for its commands, and sweeping on every draw would end the render pass
            // for every page the guest streams while the stream is being translated.
            // Hot pages remain writable, so later CPU writes need not advance CpuDirtyEpoch.
            // Every visibility point must revisit their dirty ranges even with an unchanged epoch.
            if (mapping != _bdaSweepMapping || _bdaVisibilityPending || SweepBdaOnEveryDraw)
            {
                BeginUploadBatch();
                try
                {
                    foreach (var span in spans)
                        _tracker.ForEachPossiblyCpuDirtyRange(span.Address, span.Size, _uploadDirtyBuffersInRange ??= UploadDirtyBuffersInRange);
                }
                finally
                {
                    EndUploadBatch();
                }

                _bdaSweepMapping = mapping;
                _bdaVisibilityPending = false;
            }
        }

        if (traceAddress != 0)
            GuestGpuMemoryHook.Trace(traceAddress, 1,
                $"device-address-preparation covered={traceCovered} registered={IsRegionRegistered(traceAddress, 1)} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        _faultProcessPending = true;
    }

    private Action<ulong, ulong>? _uploadDirtyBuffersInRange;

    private static readonly bool PreserveHotPagesInSweeps =
        Environment.GetEnvironmentVariable("SHARPEMU_BDA_SWEEP_REPROTECT_HOT") != "1";
    private bool _bdaVisibilityPending = true;

    private static readonly bool SweepBdaOnEveryDraw =
        Environment.GetEnvironmentVariable("SHARPEMU_BDA_SWEEP_EVERY_DRAW") == "1";

    public void NoteMemoryVisibilityPoint() => _bdaVisibilityPending = true;
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
            // Hot pages stay writable and are copied again on the next sweep: a few hundred KiB of
            // ring memory per sweep costs far less than a protection fault per page per frame.
            if (start < finish && _tracker.MayHaveCpuDirtyPages(start, finish - start))
                _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: PreserveHotPagesInSweeps);
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
                    _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: PreserveHotPagesInSweeps);
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
    }

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

        if (!_relay.IsGpuQueueThread && GuestReadsAwaitOffQueue && (AsyncReadback is not null || MainQueuePendingReads))
        {
            // A GPU write that lands after the copy was recorded makes the read stale. Retrying
            // synchronously stalls the command worker until the GPU drains, so re-issue the read and
            // wait here again; only the last attempt falls back to the synchronous read.
            for (var attempt = 1; ; attempt++)
            {
                PendingDownload? pending = null;
                if (!_relay.TryRunOnGpuQueue(() => pending = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: true)))
                {
                    return AwaitShutdown();
                }

                if (pending is null)
                {
                    return true;
                }

                if (pending.Ticket is { } ticket)
                {
                    (AsyncReadback ?? throw SubmissionScheduler.Fatal("The pending readback lost its queue.")).Wait(ticket);
                }
                else
                {
                    _scheduler.WaitForSubmittedTick(pending.MainQueueTick);
                }

                var finalAttempt = attempt >= PendingReadAttempts;
                var applied = false;
                if (!_relay.TryRunOnGpuQueue(() => applied = CompleteReadMemoryOnGpu(pending, retrySynchronously: finalAttempt)))
                {
                    return AwaitShutdown();
                }

                if (applied || finalAttempt)
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
    private readonly Dictionary<ulong, HotWindow> _hotWindows = new();
    private readonly HashSet<ulong> _eagerCandidates = new();
    private readonly List<PendingDownload> _eagerDownloads = new();
    private long _batchSerial;
    private static long _reportedEagerStarted;
    private static long _reportedEagerUsed;
    private static long _reportedEagerStale;

    private readonly record struct HotWindow(ulong Address, ulong Size, long LastHit);

    internal long EagerReadbacksStarted { get; private set; }

    internal long EagerReadbacksUsed { get; private set; }

    private void NoteHotReadback(ulong guestAddress, ulong size)
    {
        _hotWindows[guestAddress & ~(ReadbackWindowBytes - 1)] = new HotWindow(guestAddress, size, _batchSerial);
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
            if (_hotWindows.TryGetValue(key, out var hot) && _batchSerial - hot.LastHit <= HotWindowLifetime)
            {
                _eagerCandidates.Add(key);
                HotWritePending = true;
            }
        }
    }

    public bool HotWritePending { get; private set; }

    public void OnBatchSubmitted()
    {
        _batchSerial++;
        HotWritePending = false;
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        for (var index = _eagerDownloads.Count - 1; index >= 0; index--)
        {
            var pending = _eagerDownloads[index];
            if (readback.IsComplete(pending.Ticket))
            {
                _eagerDownloads.RemoveAt(index);
                if (!CompleteReadMemoryOnGpu(pending, retrySynchronously: false))
                {
                    Interlocked.Increment(ref _reportedEagerStale);
                }
            }
        }

        if ((_batchSerial & 1023) == 0)
        {
            foreach (var key in _hotWindows.Where(pair => _batchSerial - pair.Value.LastHit > HotWindowLifetime).Select(pair => pair.Key).ToList())
            {
                _hotWindows.Remove(key);
            }
        }

        if (_eagerCandidates.Count == 0)
        {
            return;
        }

        foreach (var key in _eagerCandidates)
        {
            if (_eagerDownloads.Count >= MaxEagerReadbacks)
            {
                break;
            }

            if (_hotWindows.TryGetValue(key, out var hot))
            {
                StartEagerReadback(hot);
            }
        }

        _eagerCandidates.Clear();
    }

    private void StartEagerReadback(HotWindow hot)
    {
        if (FindOwner(hot.Address, hot.Size) is not { } buffer)
        {
            return;
        }

        var copies = CollectReadbackWindow(buffer, hot.Address, hot.Size, out var windowBegin, out var windowEnd);
        if (copies.Count == 0 || !TryBeginDownloadAsync(copies, out var ticket, out var writeTicks))
        {
            return;
        }

        foreach (var copy in copies)
        {
            copy.Buffer.RetainForeignRead();
        }

        _eagerDownloads.Add(new PendingDownload
        {
            Ticket = ticket,
            Copies = copies,
            WriteTicks = writeTicks,
            WindowBegin = windowBegin,
            WindowEnd = windowEnd,
            GuestAddress = hot.Address,
            Size = hot.Size,
            IsWrite = false,
            Source = GuestMemoryProfile.ReadbackSource.ShaderResourceRead,
            Started = System.Diagnostics.Stopwatch.GetTimestamp(),
        });
        EagerReadbacksStarted++;
        Interlocked.Increment(ref _reportedEagerStarted);
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
        if (!CompleteReadMemoryOnGpu(pending, retrySynchronously: false))
        {
            Interlocked.Increment(ref _reportedEagerStale);
            return false;
        }

        EagerReadbacksUsed++;
        Interlocked.Increment(ref _reportedEagerUsed);
        return !HasGpuDirtyBytes(guestAddress, size);
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

    private void ReadMemoryOnGpu(ulong guestAddress, ulong size, bool isWrite, GuestMemoryProfile.ReadbackSource source) =>
        _ = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: false);

    internal long PendingReadbacksApplied { get; private set; }

    internal long PendingReadbacksRetried { get; private set; }

    private static long _reportedApplied;
    private static long _reportedRetried;

    public static string TakeAsyncReadbackReport() => FormattableString.Invariant(
        $"[PERF][ASYNC_READBACK] applied={Interlocked.Exchange(ref _reportedApplied, 0)} retried={Interlocked.Exchange(ref _reportedRetried, 0)} eager_started={Interlocked.Exchange(ref _reportedEagerStarted, 0)} eager_used={Interlocked.Exchange(ref _reportedEagerUsed, 0)} eager_stale={Interlocked.Exchange(ref _reportedEagerStale, 0)}");

    // Ticket is set when the readback queue carries the download; otherwise the main queue
    // does, into MainQueueBuffer, and MainQueueTick is the submission the guest thread waits for.
    private sealed record PendingDownload
    {
        public VulkanAsyncReadback.Ticket? Ticket { get; init; }
        public ulong MainQueueTick { get; init; }
        public GpuBuffer? MainQueueBuffer { get; init; }
        public ulong[] MainQueueOffsets { get; init; } = [];
        public required List<DownloadPiece> Copies { get; init; }
        public required ulong[] WriteTicks { get; init; }
        public required ulong WindowBegin { get; init; }
        public required ulong WindowEnd { get; init; }
        public required ulong GuestAddress { get; init; }
        public required ulong Size { get; init; }
        public required bool IsWrite { get; init; }
        public required GuestMemoryProfile.ReadbackSource Source { get; init; }
        public required long Started { get; init; }

        // Read from the buffers' own host mappings once the writer tick retires; no copy was recorded.
        public bool Mapped { get; init; }
    }

    private PendingDownload? BeginReadMemoryOnGpu(ulong guestAddress, ulong size, bool isWrite, GuestMemoryProfile.ReadbackSource source,
        bool allowPending, bool markHot = false)
    {
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        using var foreignRead = _device.Slabs.BeginForeignRead();
        var readbackStarted = GuestMemoryProfile.ReadbackDetailsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (isWrite && !IsRegionRegistered(guestAddress, size))
        {
            return null;
        }

        if (allowPending && TakeEagerReadback(guestAddress, size) is { } eager)
        {
            return eager with { GuestAddress = guestAddress, Size = size, IsWrite = isWrite, Source = source };
        }

        var copies = CollectReadbackWindow(guestAddress, size, out var windowBegin, out var windowEnd);
        if (markHot && copies.Count != 0)
        {
            NoteHotReadback(guestAddress, size);
        }
        if (copies.Count != 0 && _unifiedBuffers && AreMapped(copies, out var writer))
        {
            if (writer == 0 || _scheduler.IsTickComplete(writer))
            {
                ApplyMappedRead(copies, windowBegin, windowEnd, guestAddress, size, isWrite, readbackStarted, source);
                return null;
            }

            if (allowPending && writer < _scheduler.CurrentTick)
            {
                // A guest thread had to wait for this writer: submit later writes to the window right
                // after their command, so the next wait ends with them instead of with their whole batch.
                NoteHotReadback(guestAddress, size);

                // The writer is already submitted: wait for it alone, not for the work queued after it.
                var mappedTicks = new ulong[copies.Count];
                for (var index = 0; index < copies.Count; index++)
                {
                    mappedTicks[index] = copies[index].Buffer.LastGpuWriteTick;
                    copies[index].Buffer.RetainForeignRead();
                }

                return new PendingDownload
                {
                    Mapped = true,
                    MainQueueTick = writer,
                    Copies = copies,
                    WriteTicks = mappedTicks,
                    WindowBegin = windowBegin,
                    WindowEnd = windowEnd,
                    GuestAddress = guestAddress,
                    Size = size,
                    IsWrite = isWrite,
                    Source = source,
                    Started = readbackStarted,
                };
            }

            if (!allowPending)
            {
                // On the command worker: submit the writer if it is still recording, then read the mapping.
                _scheduler.Wait(writer);
                ApplyMappedRead(copies, windowBegin, windowEnd, guestAddress, size, isWrite, readbackStarted, source);
                return null;
            }
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

        if (copies.Count != 0 && allowPending &&
            TryBeginDownloadOnMainQueue(copies, out var mainTick, out var mainBuffer, out var mainOffsets, out var mainWriteTicks))
        {
            foreach (var copy in copies)
            {
                copy.Buffer.RetainForeignRead();
            }

            return new PendingDownload
            {
                MainQueueTick = mainTick,
                MainQueueBuffer = mainBuffer,
                MainQueueOffsets = mainOffsets,
                Copies = copies,
                WriteTicks = mainWriteTicks,
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

    // Without a readback queue the main queue copies the pieces into a buffer of their own and
    // submits at once; the guest thread then waits for that submission instead of the worker
    // waiting for the whole GPU (a guest read of GPU-written memory otherwise idled the worker
    // for tens of milliseconds, several times per frame in Silent Hill: The Short Message).
    private bool TryBeginDownloadOnMainQueue(List<DownloadPiece> copies, out ulong tick, out GpuBuffer buffer, out ulong[] offsets, out ulong[] writeTicks)
    {
        tick = 0;
        buffer = null!;
        offsets = [];
        writeTicks = [];
        if (!MainQueuePendingReads || copies.Count == 0)
        {
            return false;
        }

        var ticks = new ulong[copies.Count];
        var placements = new ulong[copies.Count];
        var total = 0UL;
        for (var index = 0; index < copies.Count; index++)
        {
            var written = copies[index].Buffer.LastGpuWriteTick;
            if (written == 0)
            {
                return false;
            }

            ticks[index] = written;
            placements[index] = total;
            total += (copies[index].Size + BufferDownloadBatchPlanner.Alignment - 1) & ~(BufferDownloadBatchPlanner.Alignment - 1);
        }

        if (total > AsyncReadbackLimit)
        {
            return false;
        }

        buffer = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, total);
        for (var index = 0; index < copies.Count; index++)
        {
            buffer.CopyFrom(
                _scheduler.Current, copies[index].Buffer, copies[index].SourceOffset, placements[index], copies[index].Size,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        }

        tick = _scheduler.Flush();
        offsets = placements;
        writeTicks = ticks;
        return true;
    }

    private static readonly bool MainQueuePendingReads =
        Environment.GetEnvironmentVariable("SHARPEMU_MAIN_QUEUE_PENDING_READS") != "0";

    // Pending reads a guest thread re-issues before it lets the worker read synchronously.
    private const int PendingReadAttempts = 4;

    private bool CompleteReadMemoryOnGpu(PendingDownload pending, bool retrySynchronously)
    {
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        if (pending.Mapped)
        {
            return CompleteMappedRead(pending, retrySynchronously);
        }

        if (pending.Ticket is null)
        {
            return CompleteMainQueueRead(pending, retrySynchronously);
        }

        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The pending readback lost its queue.");
        try
        {
            if (!IsPendingDownloadCurrent(pending))
            {
                readback.Complete(pending.Ticket, null);
                if (retrySynchronously)
                {
                    PendingReadbacksRetried++;
                    Interlocked.Increment(ref _reportedRetried);
                    ReadMemoryOnGpu(pending.GuestAddress, pending.Size, pending.IsWrite, pending.Source);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);

            var copies = pending.Copies;
            readback.Complete(pending.Ticket, (index, bytes) =>
            {
                if (!_backing.TryWriteBacking(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
                }
            });
            foreach (var copy in copies)
            {
                _gpuModifiedRanges.Remove(copy.Address, copy.Size);
            }

            _tracker.ClearGpuDirtyPages(pending.WindowBegin, pending.WindowEnd - pending.WindowBegin);
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

    // Guest buffers live in host-visible device memory on unified-memory devices (Apple GPUs), so a
    // readback can copy straight out of the buffer once the last GPU write to it retired. That skips
    // the copy command at the tail of the queue and the wait for every draw recorded after the writer.
    // SHARPEMU_UNIFIED_BUFFERS=0 keeps device-local buffers and copy readbacks.
    private static readonly bool UnifiedBuffersEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_UNIFIED_BUFFERS") != "0";

    private static bool HasUnifiedMemoryType(GpuDeviceInfo device)
    {
        const MemoryPropertyFlags unified = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (uint index = 0; index < device.MemoryTypeCount; index++)
        {
            if ((device.GetMemoryTypeFlags(index) & unified) == unified)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreMapped(List<DownloadPiece> copies, out ulong writer)
    {
        writer = 0;
        foreach (var copy in copies)
        {
            if (copy.Buffer.MappedPointer == null || !copy.Buffer.IsCoherent)
            {
                return false;
            }

            writer = Math.Max(writer, copy.Buffer.LastGpuWriteTick);
        }

        return true;
    }

    private void ApplyMappedRead(List<DownloadPiece> copies, ulong windowBegin, ulong windowEnd, ulong guestAddress, ulong size,
        bool isWrite, long started, GuestMemoryProfile.ReadbackSource source)
    {
        foreach (var copy in copies)
        {
            var bytes = new ReadOnlySpan<byte>(copy.Buffer.MappedPointer + copy.SourceOffset, checked((int)copy.Size));
            if (!_backing.TryWriteBacking(copy.Address, bytes))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copy.Address:X16} size=0x{copy.Size:X16}");
            }

            _gpuModifiedRanges.Remove(copy.Address, copy.Size);
        }

        _tracker.ClearGpuDirtyPages(windowBegin, windowEnd - windowBegin);
        if (isWrite)
        {
            _tracker.MarkCpuDirtyPages(guestAddress, size);
        }

        MappedReadbacks++;
        RecordReadback(windowBegin, windowEnd, isWrite, copies, started, source);
    }

    // Readbacks served from a buffer's own mapping, without a copy command.
    internal long MappedReadbacks { get; private set; }

    internal bool UnifiedBuffers => _unifiedBuffers;

    private bool CompleteMappedRead(PendingDownload pending, bool retrySynchronously)
    {
        try
        {
            if (!IsPendingDownloadCurrent(pending))
            {
                if (retrySynchronously)
                {
                    PendingReadbacksRetried++;
                    Interlocked.Increment(ref _reportedRetried);
                    ReadMemoryOnGpu(pending.GuestAddress, pending.Size, pending.IsWrite, pending.Source);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);
            ApplyMappedRead(pending.Copies, pending.WindowBegin, pending.WindowEnd, pending.GuestAddress, pending.Size,
                pending.IsWrite, pending.Started, pending.Source);
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

    private bool CompleteMainQueueRead(PendingDownload pending, bool retrySynchronously)
    {
        var buffer = pending.MainQueueBuffer ?? throw SubmissionScheduler.Fatal("The pending readback lost its buffer.");
        try
        {
            if (!IsPendingDownloadCurrent(pending))
            {
                if (retrySynchronously)
                {
                    PendingReadbacksRetried++;
                    Interlocked.Increment(ref _reportedRetried);
                    ReadMemoryOnGpu(pending.GuestAddress, pending.Size, pending.IsWrite, pending.Source);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);
            var copies = pending.Copies;
            buffer.Invalidate(0, buffer.Size);
            for (var index = 0; index < copies.Count; index++)
            {
                var bytes = buffer.Mapped.Slice((int)pending.MainQueueOffsets[index], (int)copies[index].Size);
                if (!_backing.TryWriteBacking(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{copies[index].Size:X16}");
                }

                _gpuModifiedRanges.Remove(copies[index].Address, copies[index].Size);
            }

            _tracker.ClearGpuDirtyPages(pending.WindowBegin, pending.WindowEnd - pending.WindowBegin);
            if (pending.IsWrite)
            {
                _tracker.MarkCpuDirtyPages(pending.GuestAddress, pending.Size);
            }

            RecordReadback(pending.WindowBegin, pending.WindowEnd, pending.IsWrite, copies, pending.Started, pending.Source);
            return true;
        }
        finally
        {
            // The guest thread waited for the submission, so the GPU is done with the buffer.
            buffer.Dispose();
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }
    }

    private bool IsPendingDownloadCurrent(PendingDownload pending)
    {
        var total = 0UL;
        for (var index = 0; index < pending.Copies.Count; index++)
        {
            var copy = pending.Copies[index];
            if (copy.Buffer.LastGpuWriteTick != pending.WriteTicks[index] ||
                !_gpuModifiedRanges.Contains(copy.Address, copy.Size) ||
                !ReferenceEquals(FindOwner(copy.Address, copy.Size), copy.Buffer))
            {
                return false;
            }

            total += copy.Size;
        }

        var current = 0UL;
        foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(pending.WindowBegin, pending.WindowEnd - pending.WindowBegin))
        {
            current += range.Size;
        }

        return current == total;
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
        _registry.CompleteRetirement(bufferIdentifier);
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
            }

            return;
        }

        var batch = new List<PlannedDownload>();
        var planner = new BufferDownloadBatchPlanner(_download.Size);
        foreach (var piece in copies)
        {
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
            var written = copies[index].Buffer.LastGpuWriteTick;
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
            var written = copy.Buffer.LastGpuWriteTick;
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
            if (!_backing.TryWriteBacking(copies[index].Address, bytes))
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
            if (!_backing.TryWriteBacking(copy.Address, _download.Mapped.Slice((int)offset, (int)placement.DataSize)))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copy.Address:X16} size=0x{placement.DataSize:X16}");
            }
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

        var bufferIdentifier = _registry.AllocateBuffer(new GpuBuffer(
            _device, _scheduler, _unifiedBuffers ? GpuBufferUsage.Unified : GpuBufferUsage.DeviceLocal, overlap.Begin,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, overlap.End - overlap.Begin, allowSlab: true), overlap.Begin, overlap.End - overlap.Begin);
        foreach (var oldId in overlapping)
        {
            MergeOverlappingBuffer(bufferIdentifier, oldId, !overlap.HasStreamLeap);
        }

        Register(bufferIdentifier);
        return bufferIdentifier;
    }

    private bool SynchronizeBuffer(GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer,
        bool preserveCpuWriteHotPages = true)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySynchronization);
        var startedAt = BufferUploadProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if ((!preserveCpuWriteHotPages && !isWritten && !isTexelBuffer && !_tracker.MayHaveCpuDirtyPages(guestAddress, size)) ||
            (isWritten && !isTexelBuffer && _gpuModifiedRanges.Contains(guestAddress, size)))
        {
            if (BufferUploadProfile.Enabled)
                BufferUploadProfile.Record(guestAddress, size, 0, 0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            return false;
        }

        profileScope.SwitchPhase(isWritten
            ? RenderPhaseProfile.Phase.BufferDirtySyncWritten
            : isTexelBuffer ? RenderPhaseProfile.Phase.BufferDirtySyncTexel : RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        var copies = _syncCopies ??= new List<BufferCopy>();
        copies.Clear();
        var sink = new UploadSink(_uploader, buffer, copies, guestAddress, size);
        _tracker.ForEachUploadRange(guestAddress, size, isWritten, ref sink, preserveCpuWriteHotPages);
        var source = sink.Source;
        if (source != null)
        {
            buffer.NoteGpuWrite();
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            var vk = _device.Vk;
            if (_uploadBatchActive)
            {
                OpenUploadBatch(native);
                var batched = CollectionsMarshal.AsSpan(copies);
                fixed (BufferCopy* pointer = batched)
                {
                    vk.CmdCopyBuffer(native, source.Handle, buffer.Handle, (uint)batched.Length, pointer);
                }
            }
            else
            {
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
        }

        if (BufferUploadProfile.Enabled)
        {
            var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
            ulong hotBytes = 0;
            foreach (var copy in copies)
                hotBytes += _tracker.CountCpuWriteHotBytes(buffer.CpuAddress + copy.DstOffset, copy.Size);
            BufferUploadProfile.Record(guestAddress, size, copies.Count, sink.TotalSize, hotBytes, elapsedTicks);
        }

        if (isTexelBuffer)
        {
            var copiedFromImage = RequireImageCache().TrySynchronizeBufferFromImage(buffer, guestAddress, size);
            if (copiedFromImage)
            {
                buffer.NoteGpuWrite();
            }

            return copiedFromImage;
        }

        return false;
    }

    // A sweep uploads many buffers back to back: one global barrier before the first copy and one
    // after the last replace a barrier pair per buffer, so the copies share one transfer pass.
    private bool _uploadBatchActive;
    private ulong _uploadBatchCommand;

    private void BeginUploadBatch()
    {
        _uploadBatchActive = true;
        _uploadBatchCommand = 0;
    }

    private void OpenUploadBatch(CommandBuffer native)
    {
        if (_uploadBatchCommand == (ulong)native.Handle)
        {
            return;
        }

        // A new command buffer (or the first copy) orders the copies after all earlier work.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk,
            native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
        _uploadBatchCommand = (ulong)native.Handle;
    }

    private void EndUploadBatch()
    {
        _uploadBatchActive = false;
        if (_uploadBatchCommand == 0)
        {
            return;
        }

        // Recorded on the current command buffer; submission order covers copies recorded on an
        // earlier one.
        var command = _scheduler.Current;
        command.EndRendering();
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk,
            new CommandBuffer(command.Handle), PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
        _uploadBatchCommand = 0;
    }

    // The copies of one synchronization; the tracker forbids nesting, so one list per thread is reused.
    [ThreadStatic]
    private static List<BufferCopy>? _syncCopies;

    // Collects a buffer's CPU-dirty runs as copies, then stages them while the tracker holds the regions.
    private struct UploadSink(GuestBufferUploader uploader, GpuBuffer buffer, List<BufferCopy> copies, ulong guestAddress, ulong size)
        : GuestPageTracker.IUploadRangeSink
    {
        public ulong TotalSize { get; private set; }

        public GpuBuffer? Source { get; private set; }

        public void Range(ulong address, ulong bytes)
        {
            copies.Add(new BufferCopy(TotalSize, buffer.Offset(address), bytes));
            TotalSize += bytes;
        }

        public void Upload() =>
            Source = uploader.PrepareSource(buffer.CpuAddress, CollectionsMarshal.AsSpan(copies), TotalSize, guestAddress, size);
    }

    private GpuBuffer? FindOwner(ulong guestAddress, ulong size)
    {
        return _registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(guestAddress, size));
    }

    private IGuestImageCache RequireImageCache() =>
        ImageCache ?? throw SubmissionScheduler.Fatal("The image cache is not connected.");

    private static bool IsValidRange(ulong guestAddress, ulong size) => guestAddress != 0 && size != 0 && new GuestSpan(guestAddress, size).IsValid;

}
