// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
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
    public static readonly BufferSlot NullBufferId = new(0, 1);

    private const ulong MiB = 1024 * 1024;
    private const ulong GdsBufferSize = 64 * 1024;
    private const ulong DownloadAlignment = 64;

    private enum ShutdownOutcome
    {
        Pending,
        Drained,
        Failed,
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly IGpuQueueRelay _relay;
    private readonly ICpuMemory _guest;
    private readonly IGuestBackedSpace _backing;
    private readonly BdaFaultProcessor _faults;
    private readonly GpuBuffer _gds;
    private readonly GpuBuffer _bdaPageTable;
    private readonly BufferSlots _slots = new();
    private readonly RecencyQueue _lru = new();
    private readonly SortedList<ulong, BufferSlot> _buffers = new();
    private readonly PageOwnerTable _pageTable = new();
    private readonly SpanSet _gpuModifiedRanges = new();
    private readonly GuestPageTracker _tracker;
    private readonly GpuRingBuffer _staging;
    private readonly GpuRingBuffer _stream;
    private readonly GpuRingBuffer _download;
    private readonly GpuRingBuffer _deviceRing;
    private readonly object _shutdownGate = new();
    private ShutdownOutcome _outcome;
    private ulong _totalUsedMemory;
    private ulong _triggerGcMemory = 1024 * MiB;
    private ulong _criticalGcMemory = 2048 * MiB;
    private ulong _gcTick;
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
        _guest = guest;
        _backing = backing;
        _faults = new BdaFaultProcessor(device, scheduler, this, CachingPageBits, CachingPageCount);
        _gds = new GpuBuffer(device, scheduler, GpuBufferUsage.Stream, 0, GpuBuffer.AllFlags, GdsBufferSize);
        _bdaPageTable = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, BdaPageTableSize);
        _tracker = new GuestPageTracker(pages);
        _staging = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Upload, 512 * MiB);
        _stream = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Stream, 64 * MiB);
        _download = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Download, 32 * MiB);
        _deviceRing = new GpuRingBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 128 * MiB);
        StreamOffsetAlignment = device.MinUniformBufferOffsetAlignment;
        _gds.Mapped.Clear();
        _gds.Flush(0, _gds.Size);
        var nullId = _slots.Insert(new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 16));
        if (nullId != NullBufferId)
        {
            throw SubmissionScheduler.Fatal("The null buffer occupies the wrong slot.");
        }
    }

    public IGuestImageCache? ImageCache { get; set; }

    public GpuBuffer GdsBuffer => _gds;

    public GpuBuffer BdaPageTableBuffer => _bdaPageTable;

    public GpuBuffer FaultBuffer => _faults.FaultBuffer;

    public ulong TotalUsedMemory => _totalUsedMemory;

    public int BufferCount => _buffers.Count;

    // The stream fast path aligns to this; the presenter raises it to its descriptor alignment.
    public ulong StreamOffsetAlignment { get; set; }

    public void ForEachBuffer(Action<GpuBuffer> visit)
    {
        foreach (var id in _buffers.Values)
        {
            visit(_slots[id]);
        }
    }

    public GpuBuffer GetBuffer(BufferSlot id) => _slots[id];

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
        if (!_tracker.HasRegion(address, size))
        {
            GuestGpuMemoryHook.Trace(address, size, "buffer-write result=no-region");
            return false;
        }

        var completed = true;
        _tracker.InvalidateRegion(address, size, () => completed &= ReadMemoryOrAwaitShutdown(address, size, isWrite: true));
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-write completed={completed}");
        return completed;
    }

    public bool TrySynchronizeCpuRead(ulong address, ulong size) =>
        !_tracker.HasGpuDirtyPages(address, size) || ReadMemoryOrAwaitShutdown(address, size, isWrite: false);

    // A CPU read fault: GPU-dirty pages download through the worker first.
    public bool DownloadToCpu(ulong address, ulong size)
    {
        var tracked = _tracker.HasRegion(address, size);
        var dirty = tracked && _tracker.HasGpuDirtyPages(address, size);
        var completed = tracked && (!dirty || ReadMemoryOrAwaitShutdown(address, size, isWrite: false));
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-read tracked={tracked} gpu_dirty={dirty} completed={completed}");
        return completed;
    }

    public void InvalidateMemory(ulong vaddr, ulong size)
    {
        if (!IsValidRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal("The memory invalidation range is invalid.");
        }

        _tracker.InvalidateRegion(vaddr, size, () => ReadMemory(vaddr, size, isWrite: true));
    }

    public void ReadMemory(ulong vaddr, ulong size, bool isWrite = false)
    {
        if (!ReadMemoryOrAwaitShutdown(vaddr, size, isWrite))
        {
            throw SubmissionScheduler.Fatal($"Cannot download buffer data after a failed shutdown: addr=0x{vaddr:X16} size=0x{size:X16}");
        }
    }

    public BufferSlot FindBuffer(ulong vaddr, ulong size)
    {
        if (vaddr == 0)
        {
            return NullBufferId;
        }

        if (!IsValidRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal("The buffer lookup range is invalid.");
        }

        var owner = _pageTable.Find(vaddr >> PageOwnerTable.PageBits);
        if (owner.IsValid && _slots[owner].IsInBounds(vaddr, size))
        {
            return owner;
        }

        return CreateBuffer(vaddr, size);
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBuffer(ulong vaddr, ulong size, bool isWritten, bool isTexelBuffer = false, BufferSlot id = default)
    {
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal("A buffer request requires a command buffer that is recording.");
        }

        if (!isWritten && size <= CachingPageSize && !_tracker.HasGpuDirtyPages(vaddr, size) && _tracker.HasCpuDirtyPages(vaddr, size))
        {
            if (_stream.TryMap(size, out var streamOffset, StreamOffsetAlignment, allowWait: false) &&
                _backing.TryReadBacking(vaddr, _stream.Mapped.Slice((int)streamOffset, (int)size)))
            {
                _stream.Commit();
                return (_stream, streamOffset);
            }
        }

        // A GPU write into memory without backing could never download later; refuse it now.
        if (isWritten && !_backing.IsBackedRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{vaddr:X16} size=0x{size:X16}");
        }

        var buffer = _slots.TryGet(id);
        if (buffer == null || buffer.IsDeleted || !buffer.IsInBounds(vaddr, size))
        {
            id = FindBuffer(vaddr, size);
            buffer = _slots[id];
        }

        TouchBuffer(buffer);
        _ = SynchronizeBuffer(buffer, vaddr, size, isWritten, isTexelBuffer);
        if (isWritten)
        {
            _gpuModifiedRanges.Add(vaddr, size);
        }

        return (buffer, buffer.Offset(vaddr));
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBufferForImage(ulong vaddr, ulong size)
    {
        if (!IsValidRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal("The image source range is invalid.");
        }

        var cpuModified = _tracker.HasCpuDirtyPages(vaddr, size);
        var gpuModified = _tracker.HasGpuDirtyPages(vaddr, size);
        var hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(vaddr, size);
        _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, vaddr, size, "image source");

        var owner = FindOwner(vaddr, size);
        if (hasDirtyBufferSource && owner == null)
        {
            if (!IsRegionRegistered(vaddr, size))
            {
                throw SubmissionScheduler.Fatal("The GPU-dirty image source has no device buffer.");
            }

            owner = _slots[FindBuffer(vaddr, size)];
        }

        if (owner != null && !cpuModified && (!gpuModified || hasDirtyBufferSource))
        {
            TouchBuffer(owner);
            return (owner, owner.Offset(vaddr));
        }

        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("Cannot find the device buffer that owns the GPU-dirty image source.");
        }

        if (!_staging.TryMap(size, out var stageOffset, 16) || !_backing.TryReadBacking(vaddr, _staging.Mapped.Slice((int)stageOffset, (int)size)))
        {
            throw SubmissionScheduler.Fatal("Could not read the mapped guest image backing.");
        }

        _staging.Commit();
        hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(vaddr, size);
        owner = FindOwner(vaddr, size);
        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("The GPU-dirty image source lost its device buffer owner.");
        }

        if (owner == null || (_tracker.HasGpuDirtyPages(vaddr, size) && !hasDirtyBufferSource))
        {
            return (_staging, stageOffset);
        }

        TouchBuffer(owner);
        var uploads = new List<(ulong Address, ulong Size)>();
        _tracker.ForEachUploadRange(vaddr, size, false, (address, uploadSize) => uploads.Add((address, uploadSize)), () =>
        {
            foreach (var (address, uploadSize) in uploads)
            {
                owner.CopyFrom(_scheduler.Current, _staging, stageOffset + address - vaddr, owner.Offset(address), uploadSize, AccessFlags.HostWriteBit);
            }
        });
        return (owner, owner.Offset(vaddr));
    }

    public void WriteHostMemory(ulong vaddr, ReadOnlySpan<byte> data)
    {
        if (vaddr == 0 || data.IsEmpty || (ulong)data.Length > ulong.MaxValue - vaddr)
        {
            throw SubmissionScheduler.Fatal("The host DMA write range is invalid.");
        }

        if (!_backing.TryWriteBacking(vaddr, data))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{vaddr:X16} size=0x{data.Length:X16}");
        }

        var end = vaddr + (ulong)data.Length;
        foreach (var (address, id) in _buffers)
        {
            var buffer = _slots[id];
            var begin = Math.Max(vaddr, address);
            var rangeEnd = Math.Min(end, address + buffer.Size);
            if (begin >= rangeEnd)
            {
                continue;
            }

            WriteDataBuffer(buffer, begin, data.Slice((int)(begin - vaddr), (int)(rangeEnd - begin)));
            TouchBuffer(buffer);
        }
    }

    public void FillBuffer(ulong vaddr, ulong size, uint value, bool isGds)
    {
        if ((vaddr & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - vaddr)
        {
            throw SubmissionScheduler.Fatal("The fill range must be aligned to four bytes.");
        }

        if (isGds)
        {
            if (vaddr > _gds.Size || size > _gds.Size - vaddr)
            {
                throw SubmissionScheduler.Fatal("The GDS fill range is outside the buffer.");
            }

            _gds.Fill(vaddr, size, value);
            return;
        }

        if (vaddr == 0)
        {
            throw SubmissionScheduler.Fatal("The fill memory address is invalid.");
        }

        var images = RequireImageCache();
        _ = images.ClearMeta(vaddr);
        var region = images.QueryRegion(vaddr, size);
        if (!HasGpuDirtyBytes(vaddr, size) && !region.GpuImageBytes)
        {
            if (region.ImageBytes)
            {
                images.InvalidateMemory(vaddr, size);
            }

            var values = new uint[4096];
            Array.Fill(values, value);
            var bytes = MemoryMarshal.AsBytes<uint>(values);
            for (ulong offset = 0; offset < size;)
            {
                var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                WriteHostMemory(vaddr + offset, bytes[..chunk]);
                offset += (ulong)chunk;
            }

            return;
        }

        images.InvalidateMemoryFromGpu(vaddr, size);
        var id = FindBuffer(vaddr, size);
        var (destination, destinationOffset) = ObtainBuffer(vaddr, size, true, true, id);
        destination.Fill(destinationOffset, size, value);
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

            var bytes = new byte[64 * 1024];
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
    public bool IsRegionRegistered(ulong vaddr, ulong size)
    {
        if (!IsValidRange(vaddr, size))
        {
            throw SubmissionScheduler.Fatal("The registered-region query is invalid.");
        }

        var candidate = LowerBound(vaddr + size);
        if (candidate == 0)
        {
            return false;
        }

        var address = _buffers.Keys[candidate - 1];
        return address + _slots[_buffers.Values[candidate - 1]].Size > vaddr;
    }

    public bool HasGpuDirtyPages(ulong vaddr, ulong size) => _tracker.HasGpuDirtyPages(vaddr, size);

    public bool HasGpuDirtyBytes(ulong vaddr, ulong size) => _gpuModifiedRanges.Overlaps(vaddr, size);

    public bool HasCpuDirtyPages(ulong vaddr, ulong size) => _tracker.HasCpuDirtyPages(vaddr, size);

    public void ProcessFaultBuffer() => _faults.ProcessFaultBuffer();

    // Uploads every mapped range before a BDA draw; the fault pass runs at the next collection.
    public void PrepareBda(IEnumerable<GuestSpan> mapped)
    {
        foreach (var span in mapped)
        {
            SynchronizeBuffersInRange(span.Address, span.Size);
        }

        _faultProcessPending = true;
    }

    public void SynchronizeBuffersInRange(ulong vaddr, ulong size)
    {
        var end = vaddr + size;
        var index = UpperBound(vaddr);
        if (index != 0)
        {
            index--;
        }

        for (; index < _buffers.Count && _buffers.Keys[index] < end; index++)
        {
            var buffer = _slots[_buffers.Values[index]];
            var start = Math.Max(buffer.CpuAddress, vaddr);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, end);
            if (start < finish)
            {
                _ = SynchronizeBuffer(buffer, start, finish - start, false, false);
            }
        }
    }

    // Tests use lower thresholds to check collection without large allocations.
    internal void SetCollectionThresholds(ulong trigger, ulong critical)
    {
        _triggerGcMemory = trigger;
        _criticalGcMemory = critical;
    }

    public void RunGarbageCollector()
    {
        if (_faultProcessPending)
        {
            _faultProcessPending = false;
            ProcessFaultBuffer();
        }

        var tick = _gcTick++;
        if (_totalUsedMemory < _triggerGcMemory)
        {
            return;
        }

        var aggressive = _totalUsedMemory >= _criticalGcMemory;
        var age = Math.Min(aggressive ? 80UL : 160UL, tick);
        var limit = aggressive ? 64 : 32;

        var dirtyBuffers = new List<BufferSlot>();
        var copies = new List<DownloadPiece>();
        var retireCount = 0;
        _lru.ForEachItemAtOrBeforeTick(tick - age, id =>
        {
            var buffer = _slots[id];
            if (buffer.IsDeleted)
            {
                throw SubmissionScheduler.Fatal("The recency queue contains a deleted buffer.");
            }

            _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, buffer.CpuAddress, buffer.Size, "garbage collection");
            var dirty = _tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size);
            if (dirty && !aggressive)
            {
                return false;
            }

            if (dirty)
            {
                CollectDirtyPieces(buffer, copies, "garbage collection");
                dirtyBuffers.Add(id);
            }
            else
            {
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                DeleteBuffer(id);
            }

            return ++retireCount == limit;
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
        foreach (var id in dirtyBuffers)
        {
            ReleaseDownloaded(id);
        }
    }

    // Teardown: every GPU result reaches guest memory and every page returns to its guest protection.
    public void Shutdown()
    {
        var drained = false;
        try
        {
            if (_scheduler.Active)
            {
                _scheduler.Finish();
            }

            var copies = new List<DownloadPiece>();
            var dirtyBuffers = new List<BufferSlot>();
            foreach (var id in _buffers.Values.ToArray())
            {
                var buffer = _slots[id];
                if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size))
                {
                    CollectDirtyPieces(buffer, copies, "shutdown");
                    dirtyBuffers.Add(id);
                }
            }

            if (copies.Count != 0)
            {
                DownloadBufferMemory(copies);
            }

            foreach (var id in dirtyBuffers)
            {
                ReleaseDownloaded(id);
            }

            foreach (var id in _buffers.Values.ToArray())
            {
                var buffer = _slots[id];
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                Unregister(id);
                _slots.Erase(id);
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
        _slots.ForEach((_, buffer) => buffer.Dispose());
        _deviceRing.Dispose();
        _download.Dispose();
        _stream.Dispose();
        _staging.Dispose();
        _bdaPageTable.Dispose();
        _gds.Dispose();
        _faults.Dispose();
    }

    // False only when the store closed and its drain failed; the caller then declines the fault.
    private bool ReadMemoryOrAwaitShutdown(ulong vaddr, ulong size, bool isWrite)
    {
        if (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation)
        {
            throw SubmissionScheduler.Fatal(
                $"unsupported buffer readback from an asynchronous GPU completion, addr=0x{vaddr:X16} size=0x{size:X16}");
        }

        if (_relay.TryRunOnGpuQueue(() => ReadMemoryOnGpu(vaddr, size, isWrite)))
        {
            return true;
        }

        lock (_shutdownGate)
        {
            while (_outcome == ShutdownOutcome.Pending)
            {
                Monitor.Wait(_shutdownGate);
            }

            return _outcome == ShutdownOutcome.Drained;
        }
    }

    private void ReadMemoryOnGpu(ulong vaddr, ulong size, bool isWrite)
    {
        if (isWrite && !IsRegionRegistered(vaddr, size))
        {
            return;
        }

        var buffer = _slots[FindBuffer(vaddr, size)];

        // Widen nearby CPU reads so they share one GPU drain.
        const ulong windowSize = 512 * 1024;
        var bufferEnd = buffer.CpuAddress + buffer.Size;
        var windowBegin = Math.Max(vaddr & ~(windowSize - 1), buffer.CpuAddress);
        var windowEnd = Math.Min(Math.Max(windowBegin + windowSize, vaddr + size), bufferEnd);

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
        if (copies.Count != 0)
        {
            DownloadBufferMemory(copies);
            _tracker.ClearGpuDirtyPages(windowBegin, windowEnd - windowBegin);
        }

        if (isWrite)
        {
            _tracker.MarkCpuDirtyPages(vaddr, size);
        }
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

    private void ReleaseDownloaded(BufferSlot id)
    {
        var buffer = _slots[id];
        _tracker.ClearGpuDirtyPages(buffer.CpuAddress, buffer.Size);
        if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size) || _gpuModifiedRanges.Overlaps(buffer.CpuAddress, buffer.Size))
        {
            throw SubmissionScheduler.Fatal("Buffer collection left GPU-owned memory.");
        }

        _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
        Unregister(id);
        _slots.Erase(id);
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

    private void Register(BufferSlot id) => UpdateRegistration(id, insert: true);

    private void Unregister(BufferSlot id) => UpdateRegistration(id, insert: false);

    private void UpdateRegistration(BufferSlot id, bool insert)
    {
        var buffer = _slots[id];
        if (!PageOwnerTable.TryGetPageRange(buffer.CpuAddress, buffer.Size, out var first, out var lastExclusive))
        {
            throw SubmissionScheduler.Fatal("The buffer is outside the page table.");
        }

        for (var page = first; page < lastExclusive; page++)
        {
            _pageTable.Set(page, insert ? id : BufferSlot.Invalid);
        }

        var sizePages = lastExclusive - first;
        if (insert)
        {
            if (!_buffers.TryAdd(buffer.CpuAddress, id))
            {
                throw SubmissionScheduler.Fatal("The buffer is already registered.");
            }

            _totalUsedMemory += buffer.Size;
            buffer.LruId = _lru.Insert(id, _gcTick);
            var addresses = new ulong[sizePages];
            for (ulong page = 0; page < sizePages; page++)
            {
                addresses[page] = buffer.DeviceAddress + (page << CachingPageBits);
            }

            WriteDataBuffer(_bdaPageTable, first * sizeof(ulong), MemoryMarshal.AsBytes<ulong>(addresses));
        }
        else
        {
            if (!_buffers.TryGetValue(buffer.CpuAddress, out var found) || found != id)
            {
                throw SubmissionScheduler.Fatal("Cannot unregister an unknown buffer.");
            }

            _buffers.Remove(buffer.CpuAddress);
            if (buffer.Size > _totalUsedMemory)
            {
                throw SubmissionScheduler.Fatal("The released memory exceeds the tracked allocation total.");
            }

            _totalUsedMemory -= buffer.Size;
            _lru.Free(buffer.LruId);
            _bdaPageTable.Fill(first * sizeof(ulong), sizePages * sizeof(ulong), 0);
            buffer.IsDeleted = true;
        }
    }

    private void TouchBuffer(GpuBuffer buffer)
    {
        if (!buffer.IsDeleted)
        {
            _lru.Touch(buffer.LruId, _gcTick);
        }
    }

    private void DeleteBuffer(BufferSlot id)
    {
        var buffer = _slots.TryGet(id);
        if (buffer == null || buffer.IsDeleted)
        {
            return;
        }

        Unregister(id);
        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(() => _slots.Erase(id));
        }
        else
        {
            _slots.Erase(id);
        }
    }

    private static (ulong Begin, ulong Size) GetAlignedDownloadRange(in DownloadPiece copy)
    {
        if (copy.Size == 0 || copy.SourceOffset > copy.Buffer.Size || copy.Size > copy.Buffer.Size - copy.SourceOffset)
        {
            throw SubmissionScheduler.Fatal("The download copy range is invalid.");
        }

        var begin = copy.SourceOffset & ~3UL;
        if (copy.SourceOffset > ulong.MaxValue - copy.Size || copy.SourceOffset + copy.Size > ulong.MaxValue - 3)
        {
            throw SubmissionScheduler.Fatal("The aligned download range exceeds the address limit.");
        }

        var end = (copy.SourceOffset + copy.Size + 3) & ~3UL;
        if (end > copy.Buffer.Size)
        {
            throw SubmissionScheduler.Fatal("The aligned download range exceeds its buffer.");
        }

        return (begin, end - begin);
    }

    private static ulong AlignDownloadSize(ulong size) => (size + DownloadAlignment - 1) & ~(DownloadAlignment - 1);

    // Packs pieces into the download ring, waits for the copy, then writes each through the backing alias.
    private void DownloadBufferMemory(List<DownloadPiece> copies)
    {
        var batch = new List<DownloadPiece>();
        var packedSize = 0UL;
        foreach (var piece in copies)
        {
            var copy = piece;
            while (copy.Size != 0)
            {
                var available = _download.Size - packedSize;
                var prefix = copy.SourceOffset & 3;
                var bytes = Math.Min(copy.Size, available - prefix);
                var part = copy with { Size = bytes };
                var (_, envelopeSize) = GetAlignedDownloadRange(part);
                packedSize += AlignDownloadSize(envelopeSize);
                batch.Add(part);
                copy = copy with { SourceOffset = copy.SourceOffset + bytes, Address = copy.Address + bytes, Size = copy.Size - bytes };
                if (packedSize == _download.Size)
                {
                    FlushDownloads(batch, packedSize);
                    packedSize = 0;
                }
            }
        }

        if (batch.Count != 0)
        {
            FlushDownloads(batch, packedSize);
        }

        foreach (var copy in copies)
        {
            _gpuModifiedRanges.Remove(copy.Address, copy.Size);
        }
    }

    private void FlushDownloads(List<DownloadPiece> batch, ulong packedSize)
    {
        if (!_download.TryMap(packedSize, out var baseOffset, DownloadAlignment))
        {
            throw SubmissionScheduler.Fatal("The download ring could not map the batch.");
        }

        var cursor = 0UL;
        foreach (var copy in batch)
        {
            var (sourceBegin, envelopeSize) = GetAlignedDownloadRange(copy);
            _download.CopyFrom(
                _scheduler.Current, copy.Buffer, sourceBegin, baseOffset + cursor, envelopeSize,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
            cursor += AlignDownloadSize(envelopeSize);
        }

        _download.Commit();
        var completionTick = _scheduler.CurrentTick;
        _scheduler.Finish();
        _scheduler.WaitForPriorityOperations(completionTick);
        cursor = 0;
        foreach (var copy in batch)
        {
            var (sourceBegin, envelopeSize) = GetAlignedDownloadRange(copy);
            var offset = cursor + copy.SourceOffset - sourceBegin;
            _download.Invalidate(baseOffset + offset, copy.Size);
            if (!_backing.TryWriteBacking(copy.Address, _download.Mapped.Slice((int)(baseOffset + offset), (int)copy.Size)))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copy.Address:X16} size=0x{copy.Size:X16}");
            }

            cursor += AlignDownloadSize(envelopeSize);
        }

        batch.Clear();
    }

    private OverlapSpan ResolveOverlaps(ulong vaddr, ulong size)
    {
        const int streamLeapThreshold = 16;
        const ulong streamLeapSize = CachingPageSize * 128;

        var begin = vaddr;
        var end = vaddr + size;
        var first = FindFirstOverlappingBuffer(begin);
        var last = first;
        var streamScore = 0;
        var hasStreamLeap = false;
        for (; last < _buffers.Count && _buffers.Keys[last] < end; last++)
        {
            var buffer = _slots[_buffers.Values[last]];
            var bufferBegin = buffer.CpuAddress;
            var bufferEnd = bufferBegin + buffer.Size;
            var expandsLeft = bufferBegin < begin;
            var expandsRight = bufferEnd > end;
            begin = Math.Min(begin, bufferBegin);
            end = Math.Max(end, bufferEnd);
            if (!hasStreamLeap && (streamScore += buffer.StreamScore) > streamLeapThreshold)
            {
                hasStreamLeap = true;
                if (expandsRight)
                {
                    end += Math.Min(streamLeapSize, PageOwnerTable.AddressSpaceSize - end);
                }

                if (expandsLeft)
                {
                    const ulong minimum = CachingPageSize * 2;
                    if (begin > minimum)
                    {
                        begin -= Math.Min(streamLeapSize, begin - minimum);
                    }

                    first = FindFirstOverlappingBuffer(begin);
                    if (first < _buffers.Count)
                    {
                        begin = Math.Min(begin, _buffers.Keys[first]);
                    }
                }
            }
        }

        return new OverlapSpan(first, last, begin, end, hasStreamLeap);
    }

    private int FindFirstOverlappingBuffer(ulong address)
    {
        var first = LowerBound(address);
        if (first != 0)
        {
            var previous = _slots[_buffers.Values[first - 1]];
            if (previous.CpuAddress + previous.Size > address)
            {
                first--;
            }
        }

        return first;
    }

    private void MergeOverlappingBuffer(BufferSlot newId, BufferSlot overlapId, bool accumulateStreamScore)
    {
        var newBuffer = _slots[newId];
        var overlap = _slots[overlapId];
        if (accumulateStreamScore)
        {
            newBuffer.AddStreamScore(overlap.StreamScore + 1);
        }

        newBuffer.CopyFrom(_scheduler.Current, overlap, 0, overlap.CpuAddress - newBuffer.CpuAddress, overlap.Size);
        DeleteBuffer(overlapId);
    }

    private BufferSlot CreateBuffer(ulong vaddr, ulong size)
    {
        if (_scheduler.Current.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Buffer creation requires a command buffer that is recording.");
        }

        var end = (vaddr + size + CachingPageSize - 1) & ~(CachingPageSize - 1);
        vaddr &= ~(CachingPageSize - 1);
        size = end - vaddr;
        var overlap = ResolveOverlaps(vaddr, size);
        var overlapping = new List<BufferSlot>();
        for (var index = overlap.First; index < overlap.Last; index++)
        {
            overlapping.Add(_buffers.Values[index]);
        }

        var id = _slots.Insert(new GpuBuffer(
            _device, _scheduler, GpuBufferUsage.DeviceLocal, overlap.Begin,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, overlap.End - overlap.Begin));
        foreach (var oldId in overlapping)
        {
            MergeOverlappingBuffer(id, oldId, !overlap.HasStreamLeap);
        }

        Register(id);
        return id;
    }

    private bool SynchronizeBuffer(GpuBuffer buffer, ulong vaddr, ulong size, bool isWritten, bool isTexelBuffer)
    {
        var copies = new List<BufferCopy>();
        var totalSize = 0UL;
        GpuBuffer? source = null;
        _tracker.ForEachUploadRange(
            vaddr,
            size,
            isWritten,
            (address, bytes) =>
            {
                copies.Add(new BufferCopy(totalSize, buffer.Offset(address), bytes));
                totalSize += bytes;
            },
            () => source = UploadCopies(buffer, copies, totalSize, vaddr, size));
        if (source != null)
        {
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            var vk = _device.Vk;
            var before = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer.Handle,
                Offset = 0,
                Size = buffer.Size,
            };
            vk.CmdPipelineBarrier(
                native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
                0, null, 1, &before, 0, null);
            var regions = CollectionsMarshal.AsSpan(copies);
            fixed (BufferCopy* pointer = regions)
            {
                vk.CmdCopyBuffer(native, source.Handle, buffer.Handle, (uint)regions.Length, pointer);
            }

            var after = before;
            after.SrcAccessMask = AccessFlags.TransferWriteBit;
            after.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
            vk.CmdPipelineBarrier(
                native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
                0, null, 1, &after, 0, null);
        }

        if (isTexelBuffer && !isWritten)
        {
            return RequireImageCache().TrySynchronizeBufferFromImage(buffer, vaddr, size);
        }

        return false;
    }

    // Reads the CPU-dirty runs from guest memory into the staging ring, or a one-shot upload buffer.
    private GpuBuffer? UploadCopies(GpuBuffer buffer, List<BufferCopy> copies, ulong totalSize, ulong requestedAddress, ulong requestedSize)
    {
        if (copies.Count == 0)
        {
            return null;
        }

        var regions = CollectionsMarshal.AsSpan(copies);
        if (_staging.TryMap(totalSize, out var baseOffset, 4))
        {
            foreach (ref var copy in regions)
            {
                ReadGuestBytes(buffer.CpuAddress + copy.DstOffset, _staging.Mapped.Slice((int)(baseOffset + copy.SrcOffset), (int)copy.Size), requestedAddress, requestedSize);
                copy.SrcOffset += baseOffset;
            }

            _staging.Commit();
            return _staging;
        }

        var temporary = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.TransferSrcBit, totalSize);
        foreach (ref readonly var copy in regions)
        {
            ReadGuestBytes(buffer.CpuAddress + copy.DstOffset, temporary.Mapped.Slice((int)copy.SrcOffset, (int)copy.Size), requestedAddress, requestedSize);
        }

        temporary.Flush(0, totalSize);
        _scheduler.QueueCompletionAction(temporary.Dispose);
        return temporary;
    }

    private void ReadGuestBytes(ulong address, Span<byte> destination, ulong requestedAddress, ulong requestedSize)
    {
        if (!_guest.TryRead(address, destination))
        {
            throw SubmissionScheduler.Fatal($"Could not read guest memory: addr=0x{address:X16} size=0x{destination.Length:X}");
        }
    }

    private GpuBuffer? FindOwner(ulong vaddr, ulong size)
    {
        var owner = _pageTable.Find(vaddr >> PageOwnerTable.PageBits);
        if (!owner.IsValid)
        {
            return null;
        }

        var buffer = _slots[owner];
        return buffer.IsInBounds(vaddr, size) ? buffer : null;
    }

    private IGuestImageCache RequireImageCache() =>
        ImageCache ?? throw SubmissionScheduler.Fatal("The image cache is not connected.");

    private static bool IsValidRange(ulong vaddr, ulong size) => vaddr != 0 && size != 0 && new GuestSpan(vaddr, size).IsValid;

    private int LowerBound(ulong key)
    {
        var low = 0;
        var high = _buffers.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_buffers.Keys[mid] < key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int UpperBound(ulong key)
    {
        var low = 0;
        var high = _buffers.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_buffers.Keys[mid] <= key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
