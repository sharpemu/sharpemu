// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Diagnostics;
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
public sealed unsafe partial class GuestBufferCache : IGuestBufferStore, IDisposable
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
    private readonly BdaFaultProcessor _faults;
    private readonly GpuBuffer _gds;
    private readonly GpuBuffer _bdaPageTable;
    private GpuBuffer? _bdaFallback;
    private GpuBuffer? _bdaFallbackAddress;
    private readonly GuestBufferRegistry<GpuBuffer> _registry = new(CachingPageSize, PageOwnerTable.AddressSpaceSize);
    private readonly AvlSpanSet _gpuModifiedRanges = new();
    private readonly ReaderWriterLockSlim _gpuModifiedGate = new(LockRecursionPolicy.NoRecursion);

    private SpanSnapshot _gpuModifiedSnapshot = SpanSnapshot.Empty;

    private GpuModifiedWrite WriteGpuModified()
    {
        _gpuModifiedGate.EnterWriteLock();
        return new GpuModifiedWrite(this);
    }

    private readonly ref struct GpuModifiedWrite(GuestBufferCache cache)
    {
        public void Dispose()
        {
            Volatile.Write(ref cache._gpuModifiedSnapshot, cache._gpuModifiedRanges.Snapshot());
            cache._gpuModifiedGate.ExitWriteLock();
        }
    }
    private const int ObtainMemoSlots = 4096;
    private readonly ObtainMemo[] _obtainMemos = new ObtainMemo[ObtainMemoSlots];

    private readonly record struct ObtainMemo(ulong Address, ulong Size, ResourceSlotIdentifier Identifier, ulong Offset, GpuBuffer? Buffer);
    private long _gpuModifiedVersion;
    private readonly GuestPageTracker _tracker;
    private readonly GpuRingBuffer _staging;
    private readonly ParallelGuestCopy _sweepCopy;
    private readonly GpuRingBuffer _stream;
    private readonly GpuRingBuffer _download;
    private readonly GpuRingBuffer _deviceRing;
    private readonly object _shutdownGate = new();
    private readonly BufferRetirementPolicy _retirementPolicy = new();
    private ShutdownOutcome _outcome;
    private bool _faultProcessPending;
    private bool _disposed;

    public static bool PublishCopySourceImages { get; set; }

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
        // Up to three helper threads copy a large sweep's guest bytes with the render thread.
        _sweepCopy = new ParallelGuestCopy(guest, Math.Clamp(Environment.ProcessorCount - 1, 0, 3));
        _stream = new GpuRingBuffer(device, scheduler, GpuMemoryPlacement.StreamRing, 64 * MiB, GpuBuffer.AddressedFlags);
        _download = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Download, 64 * MiB);
        _deviceRing = new GpuRingBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 128 * MiB);
        StreamOffsetAlignment = device.MinUniformBufferOffsetAlignment;
        MappedBudget = RenderDocCapture.IsAvailable ? 0 : device.MappedDeviceLocalBudget;
        AllocatesMappedBuffers = device.ResizableBar && MappedBudget != 0;
        _gds.Mapped.Clear();
        _gds.Flush(0, _gds.Size);
        var nullId = _registry.AllocateBuffer(new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AddressedFlags, 16), 0, 16);
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

    internal Func<long>? PublicationOrdinal { get; set; }

    public bool DefersGpuWriteProtection
    {
        get => _tracker.DefersGpuWriteProtection;
        set => _tracker.DefersGpuWriteProtection = value;
    }

    public void ApplyPendingGpuProtections() => _tracker.ApplyPendingGpuProtections();

    public GpuBuffer GdsBuffer => _gds;

    public GpuBuffer BdaPageTableBuffer => _bdaPageTable;

    // The address of a readable buffer, for programs with shader calls: their branchless device-address loads read there for
    // an access they do not make and drop the value (DescriptorBindingKind.DeviceAddressFallback). Made at their first use.
    public GpuBuffer DeviceAddressFallbackBuffer
    {
        get
        {
            if (_bdaFallbackAddress is null)
            {
                _bdaFallback = new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0,
                    GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, 256);
                _bdaFallbackAddress = new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 16);
                var address = _bdaFallback.DeviceAddress;
                _bdaFallbackAddress.Fill(0, sizeof(uint), (uint)address);
                _bdaFallbackAddress.Fill(sizeof(uint), sizeof(uint), (uint)(address >> 32));
            }

            return _bdaFallbackAddress;
        }
    }

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

    public GpuRingBuffer GetUtilityBuffer(GpuBufferUsage usage) => SubmissionScheduler.ReadOnlyThread ? throw SubmissionScheduler.ReadOnlyViolation("utility buffer") : usage switch
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

    // A managed write has landed: pages an upload cleaned between MarkCpuWrite and the write turn
    // CPU-dirty again. GPU-dirty pages stay as they are, since a download now would replace the new bytes.
    void IGuestBufferStore.MarkCpuWriteLanded(ulong address, ulong size)
    {
        var tracked = _tracker.MarkLandedCpuWrite(address, size);
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-write-landed tracked={tracked}");
    }

    bool IGuestBufferStore.MayHoldGpuData(ulong address, ulong size) => _tracker.MayHaveGpuDirtyPages(address, size);

    bool IGuestBufferStore.NoteEmulatorWrite(ulong address, ulong size)
    {
        var tracked = _tracker.MarkCpuWriteKeepingProtection(address, size, out var needsGpuFlush);
        return needsGpuFlush ? _tracker.MarkLandedCpuWrite(address, size) : tracked;
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

    // Resolve merges before tessellation captures physical pointers into these allocations.
    public void PrepareBufferAllocations(ReadOnlySpan<GuestSpan> ranges)
    {
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
            while (next < pages.Length && pages[next].Address <= range.End)
            {
                range = new GuestSpan(range.Address, Math.Max(range.End, pages[next].End) - range.Address);
                next++;
            }

            if (next > index + 1 || _registry.HasOverlap(range.Address, range.Size))
                _ = FindBuffer(range.Address, range.Size);
            index = next;
        }
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBuffer(ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer = false,
        ResourceSlotIdentifier bufferIdentifier = default, ulong traceShaderHash = 0, bool allowStream = true, bool requiresDeviceAddress = false)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("buffer obtain");
        }

        CompleteGuestPublications?.Invoke(guestAddress, size, isWritten ? PublicationAccess.Write : PublicationAccess.OverlaidRead);
        if (isWritten && ImageClearTrace.Enabled) ImageCache?.TraceGuestWrite("shader-buffer-write", guestAddress, size, shaderHash: traceShaderHash);
        if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-obtain", guestAddress, size, $"written={isWritten} texel={isTexelBuffer} hash=0x{traceShaderHash:X16}", remember: true);
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferAcquisitionChecks);
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("A buffer request requires a command buffer that is recording.");
        }

        var memoSlot = -1;
        if (!isWritten)
        {
            memoSlot = (int)(((guestAddress >> 4) ^ (guestAddress >> 17) ^ (size * 0x9E3779B1UL)) & (ObtainMemoSlots - 1));
            ref var memo = ref _obtainMemos[memoSlot];
            if (memo.Buffer is { } kept && memo.Address == guestAddress && memo.Size == size &&
                ReferenceEquals(_registry.TryGetRegisteredBuffer(memo.Identifier), kept) && kept.IsInBounds(guestAddress, size) &&
                !_tracker.MayHaveCpuDirtyPages(guestAddress, size) &&
                (!isTexelBuffer || !RequireImageCache().MayHaveImageBytes(guestAddress, size)))
            {
                TouchBuffer(memo.Identifier);
                OverlayGuestPublications?.Invoke(kept, memo.Offset, guestAddress, size);
                return (kept, memo.Offset);
            }
        }

        if (!isWritten && allowStream && !requiresDeviceAddress &&
            _tracker.HasCpuDirtyPages(guestAddress, size) &&
            !(_tracker.MayHaveGpuDirtyPages(guestAddress, size) && _tracker.HasGpuDirtyPages(guestAddress, size)) &&
            (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size))) &&
            ImageCache?.QueryRegion(guestAddress, size).GpuImageBytes != true)
        {
            if (StreamUseExceeded(guestAddress, size) && !_tracker.IsCpuWriteHotRange(guestAddress, size))
            {
                Interlocked.Increment(ref StreamCleaned);
                _streamCopyShares.Forget(guestAddress, size);
            }
            else
            {
                using var streamProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStreamUpload);
                var tick = _scheduler.CurrentTick;
                var publications = PublicationOrdinal?.Invoke() ?? 0;
                if (ObservationGated && _streamCopyShares.TryShare(guestAddress, size, _observation, tick, publications, out var sharedOffset))
                {
                    return (_stream, sharedOffset);
                }

                if (_stream.TryMap(size, out var streamOffset, StreamOffsetAlignment, allowWait: false) &&
                    _backing.TryReadBacking(guestAddress, _stream.Mapped.Slice((int)streamOffset, (int)size)))
                {
                    _stream.Commit();
                    if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-stream-upload", guestAddress, size, $"offset=0x{streamOffset:X}");
                    OverlayGuestPublications?.Invoke(_stream, streamOffset, guestAddress, size);
                    if (ObservationGated)
                    {
                        _streamCopyShares.Remember(guestAddress, size, _observation, tick, publications, streamOffset);
                    }

                    return (_stream, streamOffset);
                }
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
                using (WriteGpuModified())
                {
                    _gpuModifiedRanges.Add(guestAddress, size);
                }

                BumpGpuModifiedVersion();
            }

            NoteHotWindowWrite(guestAddress, size);
        }
        else OverlayGuestPublications?.Invoke(buffer, buffer.Offset(guestAddress), guestAddress, size);

        if (memoSlot >= 0)
        {
            _obtainMemos[memoSlot] = new ObtainMemo(guestAddress, size, bufferIdentifier, buffer.Offset(guestAddress), buffer);
        }

        return (buffer, buffer.Offset(guestAddress));
    }

    // The image cache publishes an image's GPU-only contents into the buffer over the same bytes before a
    // partial write drops them; the buffer then owns them, as after any GPU write. Null without backing.
    public GpuBuffer? ObtainBufferForImageWriteBack(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size) || !_backing.IsBackedRange(guestAddress, size))
        {
            return null;
        }

        return ObtainBuffer(guestAddress, size, isWritten: true).Buffer;
    }

    private const int UploadRunBudgetPages = 256;
    public static long WidenedUploads;
    public static long WidenedUploadBytes;
    private const int StreamUseSlots = 1024;
    internal const int StreamUsesBeforeClean = 3;
    private readonly (ulong Address, ulong Size, int Uses)[] _streamUses = new (ulong, ulong, int)[StreamUseSlots];
    private readonly StreamCopyShares _streamCopyShares = new();
    public static long StreamCleaned;
    public static long StreamUses;

    private bool StreamUseExceeded(ulong guestAddress, ulong size)
    {
        Interlocked.Increment(ref StreamUses);
        ref var slot = ref _streamUses[(int)(((guestAddress >> 4) ^ (guestAddress >> 17) ^ (size * 0x9E3779B1UL)) & (StreamUseSlots - 1))];
        if (slot.Address != guestAddress || slot.Size != size)
        {
            slot = (guestAddress, size, 1);
            return false;
        }

        if (++slot.Uses < StreamUsesBeforeClean)
        {
            return false;
        }

        slot = default;
        return true;
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

    public void WriteHostMemory(ulong guestAddress, ReadOnlySpan<byte> data)
    {
        CompleteGuestPublications?.Invoke(guestAddress, (ulong)data.Length, PublicationAccess.Write);
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
        if (size >= 0x10000 && images.TryClearImageFromBuffer(guestAddress, size, value))
        {
            return;
        }

        if ((publishFill || images.OverlapsDccMetadata(guestAddress, size)) && TryWriteFillToBacking(guestAddress, size, value))
        {
            using (WriteGpuModified())
            {
                _gpuModifiedRanges.Remove(guestAddress, size);
            }

            BumpGpuModifiedVersion();
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
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("dcc metadata fill");
        }

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
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("host copy");
        }

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

        if (srcMemory && PublishCopySourceImages && srcRegion.GpuImageBytes)
        {
            images.PublishImagesUnderRead(srcVaddr, size);
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

    public bool HasGpuDirtyBytes(ulong guestAddress, ulong size)
    {
        if (size == 0 || guestAddress + size < guestAddress)
        {
            Environment.FailFast($"The address range is invalid: 0x{guestAddress:X16}, 0x{size:X16}.");
        }

        return Volatile.Read(ref _gpuModifiedSnapshot).Overlaps(guestAddress, size);
    }

    public bool EverGpuWritten(ulong guestAddress, ulong size) => _tracker.MayHaveGpuWrittenPages(guestAddress, size);

    public long GpuModifiedVersion => Volatile.Read(ref _gpuModifiedVersion);

    private static long _gpuVersionBumps;

    private void BumpGpuModifiedVersion()
    {
        Interlocked.Increment(ref _gpuModifiedVersion);
        Interlocked.Increment(ref _gpuVersionBumps);
    }

    public static long TakeGpuVersionBumps() => Interlocked.Exchange(ref _gpuVersionBumps, 0);

    public long RegistrationSerial => _registry.RegistrationSerial;

    public bool HasCpuDirtyPages(ulong guestAddress, ulong size) => _tracker.HasCpuDirtyPages(guestAddress, size);

    public readonly record struct ContentMark(GpuBuffer? Buffer, ulong Tick, long Writes);

    public ContentMark MarkContents(ulong guestAddress, ulong size) =>
        FindOwner(guestAddress, size) is { } owner ? new(owner, _scheduler.CurrentTick, owner.GpuWrites) : default;

    public bool ContentsChangedSince(ulong guestAddress, ulong size, in ContentMark mark) =>
        mark.Buffer is not { } buffer || _tracker.HasCpuDirtyPages(guestAddress, size) ||
        !ReferenceEquals(FindOwner(guestAddress, size), buffer) ||
        buffer.GpuWrites != mark.Writes && buffer.LastGpuWriteTickIn(buffer.Offset(guestAddress), size) >= mark.Tick;

    public void ProcessFaultBuffer() => _faults.ProcessFaultBuffer();

    // The registered guest memory; a device-address fault inside it brings in the window around the page.
    internal GuestGpuMemory? GpuMemory { get; set; }

    // A shader reaches some memory only through pointers it reads on the GPU, such as the nodes of a
    // ray-tracing structure, and walks it a page at a time. A page the cache does not hold reads zeros and
    // drops stores until the fault is processed, so a fault brings in the aligned window around the page,
    // clipped to its registered span. A window the guest cannot read in one piece keeps the single page.
    internal const ulong DeviceAddressFaultWindow = 2 * MiB;

    internal GuestSpan DeviceAddressFaultRange(ulong pageAddress, ulong pageSize)
    {
        if (GpuMemory is { } memory && memory.TryGetSpan(pageAddress, out var spanStart, out var spanEnd))
        {
            var windowStart = pageAddress & ~(DeviceAddressFaultWindow - 1);
            var start = Math.Max(windowStart, spanStart);
            var end = Math.Min(windowStart + DeviceAddressFaultWindow, spanEnd);
            if (pageAddress + pageSize <= end && _guest.CanRead(start, end - start))
            {
                return new GuestSpan(start, end - start);
            }
        }

        return new GuestSpan(pageAddress, pageSize);
    }

    internal bool CanReadGuest(ulong address, ulong size) => _guest.CanRead(address, size);

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
        var spans = mapped as IReadOnlyList<GuestSpan> ?? mapped.ToList();
        PrepareBda(spans, MappingKey(spans));
    }

    public void PrepareBda(IReadOnlyList<GuestSpan> spans, ulong mapping)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("bda");
        }

        var traceAddress = GuestGpuMemoryHook.TraceAddress;
        var traceCovered = false;
        if (traceAddress != 0)
        {
            for (var index = 0; index < spans.Count; index++)
            {
                var span = spans[index];
                if (traceAddress != 0 && GuestGpuMemoryHook.Traces(span.Address, span.Size))
                    traceCovered = true;
                SynchronizeBuffersInRange(span.Address, span.Size);
            }
        }
        else
        {
            if (_retirementPolicy.CurrentTick != _bdaTouchTick || mapping != _bdaTouchMapping)
            {
                for (var index = 0; index < spans.Count; index++)
                    TouchBuffersInRange(spans[index].Address, spans[index].Size);
                _bdaTouchTick = _retirementPolicy.CurrentTick;
                _bdaTouchMapping = mapping;
            }

            var epoch = _tracker.CpuDirtyEpoch;
            if (mapping != _bdaSweepMapping || (epoch != _bdaSweepEpoch && (!ObservationGated || _observation != _bdaSweepObservation)))
            {
                if (!_bdaSweepPlanBuilt || mapping != _bdaSweepPlanMapping)
                {
                    _bdaSweepPlan = GuestPageTracker.PlanSweep(spans);
                    _bdaSweepPlanMapping = mapping;
                    _bdaSweepPlanBuilt = true;
                }

                // The buffers' copies are collected as the sweep visits them and staged when it ends.
                _sweepActive = true;
                _inDeviceAddressSweep = true;
                try
                {
                    if (_bdaSweepPlan is { } plan && _bufferPages.Complete)
                    {
                        _handleWindows.Sort();
                        // Only a registered buffer has anything to upload, so only the pages buffers cover are looked at.
                        _tracker.ForEachCoveredCpuDirtyPageRun(plan, _bufferPages, _uploadClearedRun ??= UploadClearedRun,
                            _flushClearedRuns ??= FlushClearedRuns);
                    }
                    else
                    {
                        var upload = _uploadDirtyBuffersInRange ??= UploadDirtyBuffersInRange;
                        for (var index = 0; index < spans.Count; index++)
                            _tracker.ForEachPossiblyCpuDirtyRange(spans[index].Address, spans[index].Size, upload);
                    }
                }
                finally
                {
                    DiscardClearedRuns();
                    _sweepActive = false;
                    try
                    {
                        FlushSweepUploads();
                    }
                    finally
                    {
                        _inDeviceAddressSweep = false;
                        if (_uploadBatchDepth == 0)
                        {
                            CloseSweepBarrier();
                        }
                    }
                }

                _bdaSweepEpoch = epoch;
                _bdaSweepMapping = mapping;
                _bdaSweepObservation = _observation;
                Interlocked.Increment(ref BdaSweeps);
            }
            else if (epoch != _bdaSweepEpoch)
            {
                Interlocked.Increment(ref BdaSweepsSkipped);
            }
        }

        _handleWindows.Clear();
        if (traceAddress != 0)
            GuestGpuMemoryHook.Trace(traceAddress, 1,
                $"device-address-preparation covered={traceCovered} registered={IsRegionRegistered(traceAddress, 1)} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        _faultProcessPending = true;
    }

    private Action<ulong, ulong>? _uploadDirtyBuffersInRange;
    private bool _inDeviceAddressSweep;
    private int _uploadBatchDepth;

    public void BeginUploadBatch() => _uploadBatchDepth++;

    public void EndUploadBatch()
    {
        if (--_uploadBatchDepth == 0 && !_inDeviceAddressSweep)
        {
            CloseSweepBarrier();
        }
    }
    private CommandBuffer _sweepBarrierBuffer;

    internal long UploadsRecorded { get; private set; }

    internal long UploadBarrierPairs { get; private set; }

    private sealed class SweepTarget
    {
        public ulong Begin;
        public ulong End;
        public readonly List<(ulong Offset, ulong End)> Ranges = [];
    }

    private readonly Dictionary<ulong, SweepTarget> _sweepTargets = [];
    private readonly Stack<SweepTarget> _spareSweepTargets = new();

    private void OpenSweepBarrier(CommandBuffer native, ulong buffer, ReadOnlySpan<BufferCopy> copies)
    {
        if (_sweepBarrierBuffer.Handle == native.Handle && !RepeatsSweepWrite(buffer, copies))
        {
            NoteSweepWrites(buffer, copies);
            return;
        }

        CloseSweepBarrier();
        NoteSweepWrites(buffer, copies);
        _sweepBarrierBuffer = native;
        UploadBarrierPairs++;
        RecordSweepBarrier(native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
            AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
            AccessFlags2.TransferWriteBit);
    }

    private bool RepeatsSweepWrite(ulong buffer, ReadOnlySpan<BufferCopy> copies)
    {
        if (!_sweepTargets.TryGetValue(buffer, out var target))
        {
            return false;
        }

        foreach (var copy in copies)
        {
            var end = copy.DstOffset + copy.Size;
            if (end <= target.Begin || copy.DstOffset >= target.End)
            {
                continue;
            }

            foreach (var (offset, rangeEnd) in target.Ranges)
            {
                if (copy.DstOffset < rangeEnd && offset < end)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void NoteSweepWrites(ulong buffer, ReadOnlySpan<BufferCopy> copies)
    {
        if (!_sweepTargets.TryGetValue(buffer, out var target))
        {
            target = _spareSweepTargets.Count != 0 ? _spareSweepTargets.Pop() : new SweepTarget();
            target.Begin = ulong.MaxValue;
            target.End = 0;
            target.Ranges.Clear();
            _sweepTargets.Add(buffer, target);
        }

        foreach (var copy in copies)
        {
            var end = copy.DstOffset + copy.Size;
            target.Ranges.Add((copy.DstOffset, end));
            target.Begin = Math.Min(target.Begin, copy.DstOffset);
            target.End = Math.Max(target.End, end);
        }
    }

    private void ForgetSweepWrites()
    {
        foreach (var target in _sweepTargets.Values)
        {
            _spareSweepTargets.Push(target);
        }

        _sweepTargets.Clear();
    }

    private void CloseSweepBarrier()
    {
        ForgetSweepWrites();
        if (_sweepBarrierBuffer.Handle == 0)
        {
            return;
        }

        _sweepBarrierBuffer = default;
        var current = _scheduler.Current;
        current.EndRendering();
        RecordSweepBarrier(new CommandBuffer(current.Handle), PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
            AccessFlags2.TransferWriteBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
    }

    private void RecordSweepBarrier(CommandBuffer native, PipelineStageFlags sourceStages, PipelineStageFlags destinationStages,
        AccessFlags2 sourceAccess, AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk, native, sourceStages, destinationStages, 0, 1, &barrier, 0, null, 0, null, BarrierSource.Sweep);
    }

    private long _bdaSweepEpoch = -1;
    private long _bdaSweepObservation = -1;
    private long _observation;
    public static long BdaSweeps;
    public static long BdaSweepsSkipped;

    // With the command stream on its own thread, guest CPU writes reach the GPU at the stream's
    // order points (a submission, a label, a write or copy the stream performs); a device-address
    // sweep between two of them finds nothing a later item may rely on.
    public bool ObservationGated { get; set; }

    public void NoteObservation() => _observation++;
    private ulong _bdaSweepMapping;
    private GuestPageTracker.SweepPlan? _bdaSweepPlan;
    private readonly PageCoverage _bufferPages = new();
    private ulong _bdaSweepPlanMapping;
    private bool _bdaSweepPlanBuilt;

    // The uploads of the running sweep: each one's copies are a run of _sweepCopies, with source
    // offsets inside its own bytes until they are staged.
    private readonly record struct SweepUpload(GpuBuffer Buffer, ulong GuestAddress, ulong Size, int FirstCopy, int CopyCount, ulong TotalSize);
    private readonly List<SweepUpload> _sweepUploads = [];
    private readonly List<BufferCopy> _sweepCopies = [];
    private ParallelGuestCopy.Piece[] _sweepPieces = [];
    private bool _sweepActive;

    // A sweep that stages fewer bytes copies them on the render thread alone.
    internal const ulong ParallelSweepCopyBytes = 256 * 1024;

    // Guest bytes per piece a copying thread takes.
    private const ulong SweepPieceBytes = 64 * 1024;

    // A sweep that stages more bytes stages each buffer on its own; tests lower it.
    internal ulong SweepStagingLimit { get; set; } = ulong.MaxValue;

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

    // A visited buffer's copies wait for the end of the sweep. The tracker has protected its pages
    // by then, so the bytes staged later are as new as the visit or newer, and a later CPU write
    // marks the pages dirty again.
    private void DeferSweepUpload(GpuBuffer buffer, ulong guestAddress, ulong size, List<BufferCopy> copies, ulong totalSize)
    {
        if (copies.Count != 0)
        {
            _sweepUploads.Add(new SweepUpload(buffer, guestAddress, size, _sweepCopies.Count, copies.Count, totalSize));
            _sweepCopies.AddRange(copies);
        }
    }

    // A sweep visits many buffers with a few dirty pages each: they share one staging reservation and
    // one copy of their guest bytes, and each buffer's copy is recorded as SynchronizeBuffer records
    // it, after all the bytes are staged.
    private void FlushSweepUploads()
    {
        if (_sweepUploads.Count == 0)
        {
            return;
        }

        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        try
        {
            var copies = CollectionsMarshal.AsSpan(_sweepCopies);
            ulong total = 0;
            foreach (var upload in _sweepUploads)
                total += upload.TotalSize;

            if (total <= SweepStagingLimit && _staging.TryMap(total, out var baseOffset, 4) && StageSweep(copies, baseOffset, total))
            {
                _staging.Commit();
                var uploadBase = baseOffset;
                foreach (var upload in _sweepUploads)
                {
                    var regions = copies.Slice(upload.FirstCopy, upload.CopyCount);
                    foreach (ref var copy in regions)
                        copy.SrcOffset += uploadBase;
                    RecordUpload(upload.Buffer, _staging, regions, upload.GuestAddress, upload.Size, upload.TotalSize);
                    uploadBase += upload.TotalSize;
                }

                return;
            }

            // More than the ring holds at once, or a read failed: each buffer stages on its own, as
            // outside a sweep, which also reports a failed read.
            foreach (var upload in _sweepUploads)
            {
                var regions = copies.Slice(upload.FirstCopy, upload.CopyCount);
                if (_uploader.PrepareSource(upload.Buffer.CpuAddress, regions, upload.TotalSize, upload.GuestAddress, upload.Size) is { } source)
                    RecordUpload(upload.Buffer, source, regions, upload.GuestAddress, upload.Size, upload.TotalSize);
            }
        }
        finally
        {
            _sweepUploads.Clear();
            _sweepCopies.Clear();
        }
    }

    // Copies the sweep's guest bytes into the reservation at baseOffset, each buffer's after the last;
    // helper threads take part once the sweep stages ParallelSweepCopyBytes. False when a read failed.
    private bool StageSweep(Span<BufferCopy> copies, ulong baseOffset, ulong total)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStagingUpload);
        var pieces = 0;
        foreach (var copy in copies)
            pieces += (int)((copy.Size + SweepPieceBytes - 1) / SweepPieceBytes);
        if (_sweepPieces.Length < pieces)
            _sweepPieces = new ParallelGuestCopy.Piece[Math.Max(pieces, _sweepPieces.Length * 2)];

        var count = 0;
        var uploadBase = baseOffset;
        fixed (byte* mapped = _staging.Mapped)
        {
            foreach (var upload in _sweepUploads)
            {
                foreach (var copy in copies.Slice(upload.FirstCopy, upload.CopyCount))
                {
                    var guestAddress = upload.Buffer.CpuAddress + copy.DstOffset;
                    var staged = mapped + uploadBase + copy.SrcOffset;
                    for (ulong done = 0; done < copy.Size; done += SweepPieceBytes)
                        _sweepPieces[count++] = new ParallelGuestCopy.Piece(guestAddress + done, (nint)(staged + done),
                            (int)Math.Min(SweepPieceBytes, copy.Size - done));
                }

                uploadBase += upload.TotalSize;
            }

            return _sweepCopy.Run(_sweepPieces, count, parallel: total >= ParallelSweepCopyBytes);
        }
    }

    private Action<ulong, ulong>? _uploadClearedRun;
    private Action? _flushClearedRuns;
    private readonly UploadCollector _clearedRuns = new();
    private GpuBuffer? _clearedRunsBuffer;
    private long _clearedRunsStarted;

    private void UploadClearedRun(ulong runAddress, ulong runSize)
    {
        CountSweptPages(runAddress, runSize);
        var runEnd = runAddress + runSize;
        var index = _registry.FindFirstOverlappingIndex(runAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < runEnd; index++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(index));
            var start = Math.Max(buffer.CpuAddress, runAddress);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, runEnd);
            if (start >= finish)
            {
                continue;
            }

            if (!ReferenceEquals(buffer, _clearedRunsBuffer))
            {
                FlushClearedRuns();
                _clearedRunsBuffer = buffer;
                _clearedRunsStarted = BufferUploadProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                _clearedRuns.Begin(this, buffer, start, finish - start, defer: _sweepActive);
            }

            _clearedRuns.Extend(finish);
            _clearedRuns.Collect(start, finish - start);
        }
    }

    private void FlushClearedRuns()
    {
        if (_clearedRunsBuffer is not { } buffer)
        {
            return;
        }

        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        try
        {
            var trackingStarted = RenderPhaseProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _clearedRuns.Upload();
            RecordCollectedUpload(_clearedRuns, buffer, _clearedRuns.GuestAddress, _clearedRuns.Size, trackingStarted, _clearedRunsStarted);
        }
        finally
        {
            DiscardClearedRuns();
        }
    }

    private void DiscardClearedRuns()
    {
        if (_clearedRunsBuffer is not null)
        {
            _clearedRunsBuffer = null;
            _clearedRuns.End();
        }
    }

    public void SynchronizeBuffersInRange(ulong guestAddress, ulong size)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("buffer sync");
        }

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
        _tracker.ApplyPendingGpuProtections();
        _registry.Dispose();
        _deviceRing.Dispose();
        _download.Dispose();
        _stream.Dispose();
        _staging.Dispose();
        _sweepCopy.Dispose();
        _bdaPageTable.Dispose();
        _bdaFallback?.Dispose();
        _bdaFallbackAddress?.Dispose();
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
            var slotWaits = 0;
            for (var attempt = 0; ; attempt++)
            {
                var allowPending = attempt < MaxAsyncReadbackAttempts;
                var markHot = attempt == 0 && slotWaits == 0 && !isWrite;
                var waitForSlot = slotWaits < MaxReadbackSlotWaits;
                PendingDownload? pending = null;
                var slotsBusy = false;
                var beginPosted = System.Diagnostics.Stopwatch.GetTimestamp();
                var beginStarted = 0L;
                var beginEnded = 0L;
                var completedAtBegin = 0UL;
                var frameAtBegin = 0L;
                if (!_relay.TryRunOnGpuQueue(() =>
                    {
                        beginStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        pending = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending, markHot, waitForSlot, out slotsBusy);
                        completedAtBegin = _scheduler.CompletedTick;
                        frameAtBegin = FrameTicks.LatestSubmittedFrame + 1;
                        beginEnded = System.Diagnostics.Stopwatch.GetTimestamp();
                    }))
                {
                    return AwaitShutdown();
                }

                var beginReturned = System.Diagnostics.Stopwatch.GetTimestamp();
                RelayHopBegin.Increment();
                RelayHopBeginTicks.Add(beginReturned - beginPosted);

                if (slotsBusy)
                {
                    Interlocked.Increment(ref ReadbacksSlotWaits);
                    slotWaits++;
                    attempt--;
                    readback.WaitForFreeSlot(1);
                    continue;
                }

                if (pending is null)
                {
                    return true;
                }

                var gpuWaitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                readback.Wait(pending.Ticket);
                NoteGuestReadback(pending, completedAtBegin, frameAtBegin, beginStarted - beginPosted, beginEnded - beginStarted,
                    System.Diagnostics.Stopwatch.GetTimestamp() - gpuWaitStarted, beginStarted);
                var complete = false;
                var completePosted = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!_relay.TryRunOnGpuQueue(() => complete = CompleteReadMemoryOnGpu(pending, consumed: true)))
                {
                    return AwaitShutdown();
                }

                RelayHopComplete.Increment();
                RelayHopCompleteTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - completePosted);

                if (complete)
                {
                    return true;
                }
            }
        }

        var onQueue = _relay.IsGpuQueueThread;
        if (_relay.TryRunOnGpuQueue(() => _ = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: false, markHot: onQueue, waitForSlot: false,
                out _, useEager: onQueue)))
        {
            return true;
        }

        return AwaitShutdown();
    }

    private const ulong ReadbackWindowBytes = 512 * 1024;
    private const long HotWindowLifetime = 512;
    private const int MaxEagerReadbacks = 4;
    private const int MaxAsyncReadbackAttempts = 4;
    private const int MaxReadbackSlotWaits = 64;
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
                HotWritePending |= hot.Strikes == 0;
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
        if (FindOwner(hot.Address, hot.Size) is not { IsMapped: false } buffer)
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

        if (!TryBeginDownloadAsync(copies, out var ticket, out var writeTicks, out _))
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
        bool allowPending, bool markHot, bool waitForSlot, out bool slotsBusy, bool useEager = false)
    {
        slotsBusy = false;
        if (MeshDrawTrace.Enabled) MeshDrawTrace.Range("cpu-synchronize", guestAddress, size, $"write={isWrite} source={source}");
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        using var foreignRead = _device.Slabs.BeginForeignRead();
        var readbackStarted = GuestMemoryProfile.ReadbackDetailsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (isWrite && !IsRegionRegistered(guestAddress, size))
        {
            return null;
        }

        var buffer = _registry.GetBuffer(FindBuffer(guestAddress, size));
        var copies = CollectReadbackWindow(buffer, guestAddress, size, out var windowBegin, out var windowEnd);

        if (!isWrite && allowPending && TakeEagerReadback(guestAddress, size) is { } eager)
        {
            return eager with { GuestAddress = guestAddress, Size = size, IsWrite = isWrite, Source = source };
        }

        if (useEager && !isWrite && _eagerDownloads.Count != 0)
        {
            if (TryUseEagerReadback(guestAddress, size))
            {
                return null;
            }

            buffer = _registry.GetBuffer(FindBuffer(guestAddress, size));
            copies = CollectReadbackWindow(buffer, guestAddress, size, out windowBegin, out windowEnd);
        }

        if (markHot && copies.Count != 0)
        {
            NoteHotReadback(guestAddress, size);
        }

        if (copies.Count != 0 && !buffer.IsMapped)
        {
            buffer.NoteReadback();
        }

        var busy = false;
        if (copies.Count != 0 && allowPending && TryBeginDownloadAsync(copies, out var ticket, out var writeTicks, out busy))
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

        if (busy && waitForSlot)
        {
            slotsBusy = true;
            return null;
        }

        if (copies.Count != 0)
        {
            Interlocked.Increment(ref ReadbacksSynchronous);
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

    private ulong _promotedBytes;
    private ulong _promotionCeiling = ulong.MaxValue;

    internal ulong MappedBudget { get; set; }

    internal ulong PromotedBytes => _promotedBytes;

    private long _mappedBuffers;

    public bool HasMappedBuffers => Volatile.Read(ref _mappedBuffers) != 0;

    private static bool IsPromoted(GpuBuffer buffer) => buffer.Usage == GpuBufferUsage.MappedDeviceLocal && buffer.IsMapped;

    internal bool AllocatesMappedBuffers { get; set; }

    private bool FitsMappedBudget(ulong size, ulong released = 0) =>
        AllocatesMappedBuffers && _promotedBytes - released + size <= Math.Min(MappedBudget, _promotionCeiling);

    private void NotePromotion(GpuBuffer buffer, bool atCreation = false)
    {
        if (IsPromoted(buffer))
        {
            if (atCreation)
            {
                MappedCreations.Increment();
                return;
            }

            Promotions.Increment();
            PromotedBufferBytes.Add((long)buffer.Size);
            return;
        }

        _promotionCeiling = _promotedBytes;
        PromotionsBudgetFull.Increment();
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

                if (!_backing.TryWriteBacking(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
                }
            });
            for (var index = 0; index < copies.Count; index++)
            {
                if (current[index])
                {
                    using (WriteGpuModified())
                    {
                        _gpuModifiedRanges.Remove(copies[index].Address, copies[index].Size);
                    }

                    BumpGpuModifiedVersion();
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
        if (IsPromoted(buffer))
        {
            _promotedBytes = insert ? _promotedBytes + buffer.Size : _promotedBytes - buffer.Size;
            Interlocked.Add(ref PromotedLiveBytes, insert ? (long)buffer.Size : -(long)buffer.Size);
        }

        if (buffer.IsMapped)
        {
            Interlocked.Add(ref _mappedBuffers, insert ? 1 : -1);
        }

        if (GuestGpuMemoryHook.Traces(buffer.CpuAddress, buffer.Size))
            GuestGpuMemoryHook.Trace(buffer.CpuAddress, buffer.Size,
                $"device-address-registration insert={insert} buffer={bufferIdentifier} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        if (insert)
        {
            _bufferPages.Set(buffer.CpuAddress, buffer.Size, covered: true);
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
            _bufferPages.Set(buffer.CpuAddress, buffer.Size, covered: false);
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
                using (WriteGpuModified())
                {
                    _gpuModifiedRanges.Remove(copy.Address, copy.Size);
                }

                BumpGpuModifiedVersion();
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
            using (WriteGpuModified())
            {
                _gpuModifiedRanges.Remove(copy.Address, copy.Size);
            }

            BumpGpuModifiedVersion();
        }
    }

    private static long _reportedKnownApplied;
    private static long _reportedKnownUnbacked;

    private static readonly ThreadCounter SweptPages = new();
    private static readonly ThreadCounter SweptPagesInHandleWindows = new();
    private const ulong HandleWindowBytes = 16 * MiB;
    private readonly List<ulong> _handleWindows = [];

    public static string TakeDirtyTrackingReport() => GuestPageTracker.TakeDirtyTrackingReport() + FormattableString.Invariant(
        $" swept_pages={SweptPages.Take()} pages_in_handle_windows={SweptPagesInHandleWindows.Take()} stream_copies={StreamCopyShares.Copies.Take()} stream_shared={StreamCopyShares.Shared.Take()}");

    public void NoteDeviceAddressWindow(ulong handleBase)
    {
        if (handleBase != 0 && _handleWindows.Count < 64)
        {
            _handleWindows.Add(handleBase);
        }
    }

    private void CountSweptPages(ulong runAddress, ulong runSize)
    {
        SweptPages.Add((long)(runSize / TrackerLayout.PageBytes));
        if (_handleWindows.Count == 0)
        {
            return;
        }

        var runEnd = runAddress + runSize;
        var covered = 0UL;
        var cursor = runAddress;
        foreach (var window in _handleWindows)
        {
            var start = Math.Max(window, cursor);
            var end = Math.Min(window + HandleWindowBytes, runEnd);
            if (start < end)
            {
                covered += end - start;
                cursor = end;
            }
        }

        SweptPagesInHandleWindows.Add((long)(covered / TrackerLayout.PageBytes));
    }

    public static string TakeKnownContentsReport() => FormattableString.Invariant(
        $"[PERF][KNOWN_GPU] applied={Interlocked.Exchange(ref _reportedKnownApplied, 0)} unbacked={Interlocked.Exchange(ref _reportedKnownUnbacked, 0)}");

    public void NoteKnownGpuContents(ulong address, ReadOnlySpan<uint> words)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("known contents");
        }

        var bytes = MemoryMarshal.AsBytes(words);
        var size = (ulong)bytes.Length;
        if (size == 0)
        {
            return;
        }

        if (!_backing.TryWriteBacking(address, bytes))
        {
            Interlocked.Increment(ref _reportedKnownUnbacked);
            return;
        }

        using (WriteGpuModified())
        {
            _gpuModifiedRanges.Remove(address, size);
        }

        var firstPage = address & ~(TrackerLayout.PageBytes - 1);
        for (var page = firstPage; page < address + size; page += TrackerLayout.PageBytes)
        {
            if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
            {
                _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
            }
        }
        Interlocked.Increment(ref _reportedKnownApplied);
    }

    // A store the command stream computed, over GPU-dirty pages: the GPU copy takes it behind the earlier GPU work
    // and the guest backing takes it as well, so its bytes leave GPU ownership while the bytes around them keep theirs.
    public bool TryWriteOverGpuBytes(ulong guestAddress, ReadOnlySpan<byte> data)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("kernel store");
        }

        var size = (ulong)data.Length;
        if (guestAddress == 0 || size == 0 || ((guestAddress | size) & 3) != 0 || size > ulong.MaxValue - guestAddress)
        {
            return false;
        }

        CompleteGuestPublications?.Invoke(guestAddress, size, PublicationAccess.Write);
        if (RequireImageCache().MayHaveImageBytes(guestAddress, size))
        {
            return false;
        }

        var (destination, _) = ObtainBuffer(guestAddress, size, isWritten: true);
        WriteDataBuffer(destination, guestAddress, data);
        if (!_backing.TryWriteBacking(guestAddress, data))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{size:X16}");
        }

        using (WriteGpuModified())
        {
            _gpuModifiedRanges.Remove(guestAddress, size);
        }

        BumpGpuModifiedVersion();
        ClearGpuDirtyPagesOfRemovedRange(guestAddress, size);
        return true;
    }

    public static long ReadbacksStarted;
    public static long ReadbacksNoWriter;
    public static long ReadbacksTooLarge;
    public static long ReadbacksBusy;
    public static long ReadbacksSlotWaits;
    public static long ReadbacksSynchronous;
    public static readonly ThreadCounter Promotions = new();
    public static readonly ThreadCounter PromotedBufferBytes = new();
    public static readonly ThreadCounter PromotionsBudgetFull = new();
    public static readonly ThreadCounter MappedCreations = new();
    public static long PromotedLiveBytes;
    public static readonly ThreadCounter RelayHopBegin = new();
    public static readonly ThreadCounter RelayHopBeginTicks = new();
    public static readonly ThreadCounter RelayHopComplete = new();
    public static readonly ThreadCounter RelayHopCompleteTicks = new();
    public static readonly ThreadCounter GuestReadbacks = new();
    public static readonly ThreadCounter GuestReadbackQueueTicks = new();
    public static readonly ThreadCounter GuestReadbackBeginTicks = new();
    public static readonly ThreadCounter GuestReadbackGpuWaitTicks = new();
    public static readonly ThreadCounter GuestReadbacksBehind = new();
    public static readonly ThreadCounter GuestReadbackTicksBehind = new();
    public static readonly ThreadCounter[] GuestReadbackFrameAge = [new(), new(), new(), new()];

    public FrameTickLedger FrameTicks { get; } = new();

    public static readonly ThreadCounter WriterHeldReads = new();
    public static readonly ThreadCounter WriterHeldTicks = new();
    public static readonly ThreadCounter WriterSubmittedByRead = new();
    public static readonly ThreadCounter WriterAgeTicks = new();
    public static readonly ThreadCounter WriterSinceSubmitTicks = new();
    public static readonly ThreadCounter WriterTimedReads = new();

    private void NoteGuestReadback(PendingDownload pending, ulong completedAtBegin, long frameAtBegin, long queueTicks, long beginTicks, long gpuWaitTicks, long beginStarted)
    {
        var waitTick = 0UL;
        foreach (var tick in pending.WriteTicks)
        {
            waitTick = Math.Max(waitTick, tick);
        }

        var reservedAt = _scheduler.ReservedAt(waitTick);
        var submittedAt = _scheduler.SubmittedAt(waitTick);
        if (reservedAt != 0 && submittedAt != 0)
        {
            var done = System.Diagnostics.Stopwatch.GetTimestamp();
            WriterTimedReads.Increment();
            WriterAgeTicks.Add(beginStarted - reservedAt);
            WriterSinceSubmitTicks.Add(done - Math.Max(submittedAt, beginStarted));
            if (submittedAt > reservedAt)
            {
                WriterHeldReads.Increment();
                WriterHeldTicks.Add(submittedAt - reservedAt);
            }

            if (submittedAt >= beginStarted)
            {
                WriterSubmittedByRead.Increment();
            }
        }

        GuestReadbacks.Increment();
        GuestReadbackQueueTicks.Add(queueTicks);
        GuestReadbackBeginTicks.Add(beginTicks);
        GuestReadbackGpuWaitTicks.Add(gpuWaitTicks);
        if (waitTick > completedAtBegin)
        {
            GuestReadbacksBehind.Increment();
            GuestReadbackTicksBehind.Add((long)(waitTick - completedAtBegin));
        }

        var age = frameAtBegin - FrameTicks.FrameOf(waitTick);
        GuestReadbackFrameAge[(int)Math.Clamp(age, 0, GuestReadbackFrameAge.Length - 1)].Increment();
    }

    private bool TryBeginDownloadAsync(List<DownloadPiece> copies, out VulkanAsyncReadback.Ticket ticket, out ulong[] writeTicks, out bool busy)
    {
        ticket = null!;
        writeTicks = [];
        busy = false;
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
                Interlocked.Increment(ref ReadbacksNoWriter);
                return false;
            }

            ticks[index] = written;
            waitTick = Math.Max(waitTick, written);
            total += copies[index].Size;
        }

        if (total > AsyncReadbackLimit)
        {
            Interlocked.Increment(ref ReadbacksTooLarge);
            return false;
        }

        if (waitTick >= _scheduler.CurrentTick)
        {
            _scheduler.Flush();
        }
        else
        {
            _scheduler.EnsureSubmitted(waitTick);
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        if (!readback.TryBegin(pieces, waitTick, out var started) || started is null)
        {
            Interlocked.Increment(ref ReadbacksBusy);
            busy = true;
            return false;
        }

        Interlocked.Increment(ref ReadbacksStarted);

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
        else
        {
            _scheduler.EnsureSubmitted(waitTick);
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
            if (GpuCommands.VisibilityResultTrace.Enabled)
                GpuCommands.VisibilityResultTrace.Download(copy.Address,
                    _download.Mapped.Slice((int)offset, (int)placement.DataSize), _backing.TryReadBacking);
            if (!_backing.TryWriteBacking(copy.Address, _download.Mapped.Slice((int)offset, (int)placement.DataSize)))
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
        var promotedOverlap = 0UL;
        for (var index = overlap.First; index < overlap.Last; index++)
        {
            var identifier = _registry.GetRegisteredIdentifier(index);
            overlapping.Add(identifier);
            if (IsPromoted(_registry.GetBuffer(identifier)))
            {
                promotedOverlap += _registry.GetBuffer(identifier).Size;
            }
        }

        var mergedSize = overlap.End - overlap.Begin;
        var keepsPromotion = promotedOverlap != 0 && FitsMappedBudget(mergedSize, promotedOverlap);
        if (promotedOverlap != 0 && !keepsPromotion)
        {
            PromotionsBudgetFull.Increment();
        }

        var mappedAtCreation = !keepsPromotion && AllocatesMappedBuffers && FitsMappedBudget(mergedSize, promotedOverlap);
        var mapped = keepsPromotion || mappedAtCreation;
        var bufferIdentifier = _registry.AllocateBuffer(new GpuBuffer(
            _device, _scheduler, mapped ? GpuBufferUsage.MappedDeviceLocal : GpuBufferUsage.DeviceLocal, overlap.Begin,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, mergedSize, allowSlab: !mapped || AllocatesMappedBuffers), overlap.Begin, mergedSize);
        foreach (var oldId in overlapping)
        {
            MergeOverlappingBuffer(bufferIdentifier, oldId, !overlap.HasStreamLeap);
        }

        Register(bufferIdentifier);
        if (mapped)
        {
            NotePromotion(_registry.GetBuffer(bufferIdentifier), mappedAtCreation);
        }

        return bufferIdentifier;
    }

    private bool SynchronizeBuffer(GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer,
        bool preserveCpuWriteHotPages = true)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySynchronization);
        var startedAt = BufferUploadProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var nothingToUpload = isWritten
            ? _gpuModifiedRanges.Contains(guestAddress, size)
            : !_tracker.MayHaveCpuDirtyPages(guestAddress, size);
        if (nothingToUpload)
        {
            if (BufferUploadProfile.Enabled)
                BufferUploadProfile.Record(guestAddress, size, 0, 0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            return isTexelBuffer && SynchronizeTexelFromImage(buffer, guestAddress, size);
        }

        if (isWritten && !isTexelBuffer && _tracker.IsGpuOwned(guestAddress, size))
        {
            return false;
        }

        profileScope.SwitchPhase(isWritten
            ? RenderPhaseProfile.Phase.BufferDirtySyncWritten
            : isTexelBuffer ? RenderPhaseProfile.Phase.BufferDirtySyncTexel : RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        if (isTexelBuffer && !isWritten && !_tracker.MayHaveCpuDirtyPages(guestAddress, size))
        {
            if (BufferUploadProfile.Enabled)
                BufferUploadProfile.Record(guestAddress, size, 0, 0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            return SynchronizeTexelFromImage(buffer, guestAddress, size);
        }

        _tracker.UploadBatch = (long)_scheduler.CurrentTick;
        if (!isWritten && !isTexelBuffer)
        {
            var (runAddress, runSize) = _tracker.ExtendCpuDirtyRun(
                guestAddress, size, buffer.CpuAddress, buffer.CpuAddress + buffer.Size, preserveCpuWriteHotPages, UploadRunBudgetPages);
            if (runSize != size && _backing.IsBackedRange(runAddress, runSize))
            {
                Interlocked.Increment(ref WidenedUploads);
                Interlocked.Add(ref WidenedUploadBytes, (long)(runSize - size));
                guestAddress = runAddress;
                size = runSize;
            }
        }

        var collector = _uploadCollector;
        if (collector is null || collector.InUse)
        {
            // The first call on a thread keeps its collector; a nested one gets its own.
            collector = new UploadCollector();
            _uploadCollector ??= collector;
        }

        collector.Begin(this, buffer, guestAddress, size, defer: _sweepActive && !isWritten && !isTexelBuffer);
        try
        {
            return SynchronizeBufferCore(collector, buffer, guestAddress, size, isWritten, isTexelBuffer, preserveCpuWriteHotPages, startedAt);
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
        private bool _defer;
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
            // A sweep takes the copies to stage with its other buffers; Source then stays null.
            Upload = () =>
            {
                if (_defer)
                    _cache.DeferSweepUpload(_buffer, _guestAddress, _size, Copies, TotalSize);
                else
                    Source = _cache._uploader.PrepareSource(_buffer.CpuAddress, CollectionsMarshal.AsSpan(Copies), TotalSize, _guestAddress, _size);
            };
        }

        public void Begin(GuestBufferCache cache, GpuBuffer buffer, ulong guestAddress, ulong size, bool defer)
        {
            InUse = true;
            _cache = cache;
            _buffer = buffer;
            _guestAddress = guestAddress;
            _size = size;
            _defer = defer;
        }

        public ulong GuestAddress => _guestAddress;

        public ulong Size => _size;

        public void Extend(ulong end) => _size = Math.Max(_size, end - _guestAddress);

        public void End()
        {
            InUse = false;
            _cache = null!;
            _buffer = null!;
            Copies.Clear();
            TotalSize = 0;
            Source = null;
        }
    }

    [ThreadStatic] private static UploadCollector? _uploadCollector;

    private bool SynchronizeBufferCore(UploadCollector collector, GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten,
        bool isTexelBuffer, bool preserveCpuWriteHotPages, long startedAt)
    {
        var trackingStarted = RenderPhaseProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _tracker.ForEachUploadRange(guestAddress, size, isWritten, collector.Collect, collector.Upload, preserveCpuWriteHotPages);
        RecordCollectedUpload(collector, buffer, guestAddress, size, trackingStarted, startedAt);
        return isTexelBuffer && SynchronizeTexelFromImage(buffer, guestAddress, size);
    }

    private void RecordCollectedUpload(UploadCollector collector, GpuBuffer buffer, ulong guestAddress, ulong size, long trackingStarted, long startedAt)
    {
        var copies = collector.Copies;
        var recordStarted = trackingStarted != 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var source = collector.Source;
        var totalSize = collector.TotalSize;
        if (source != null)
        {
            RecordUpload(buffer, source, CollectionsMarshal.AsSpan(copies), guestAddress, size, totalSize);
        }

        if (source != null)
        {
            BufferUploadProfile.NoteUpload(copies.Count, totalSize, size);
            if (recordStarted != 0)
            {
                BufferUploadProfile.NoteUploadShape(guestAddress, size, totalSize, recordStarted - trackingStarted, System.Diagnostics.Stopwatch.GetTimestamp() - recordStarted);
            }
        }

        if (BufferUploadProfile.Enabled)
        {
            var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
            ulong hotBytes = 0;
            foreach (var copy in copies)
                hotBytes += _tracker.CountCpuWriteHotBytes(buffer.CpuAddress + copy.DstOffset, copy.Size);
            BufferUploadProfile.Record(guestAddress, size, copies.Count, totalSize, hotBytes, elapsedTicks);
        }
    }

    // Records the copy of staged guest bytes into the buffer, ordered after earlier work on the buffer
    // and before later reads of it.
    private void RecordUpload(GpuBuffer buffer, GpuBuffer source, Span<BufferCopy> regions, ulong guestAddress, ulong size, ulong totalSize)
    {
        foreach (var copy in regions)
        {
            buffer.NoteGpuWrite(copy.DstOffset, copy.Size);
        }

        if (MeshDrawTrace.Active) MeshDrawTrace.Range("buffer-upload", guestAddress, size, $"bytes={totalSize} copies={regions.Length}");
        var command = _scheduler.Current;
        command.EndRendering();
        var native = new CommandBuffer(command.TransferHandle);
        var vk = _device.Vk;
        var sweep = _inDeviceAddressSweep || _uploadBatchDepth != 0;
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
        UploadsRecorded++;
        if (sweep)
        {
            OpenSweepBarrier(native, buffer.Handle.Handle, regions);
        }
        else
        {
            UploadBarrierPairs++;
            VulkanSynchronization.PipelineBarrier(vk,
                native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
                0, null, 1, &before, 0, null, BarrierSource.Upload);
        }

        fixed (BufferCopy* pointer = regions)
        {
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                vk.CmdCopyBuffer(native, source.Handle, buffer.Handle, (uint)regions.Length, pointer);
            }
        }

        if (!sweep)
        {
            var after = before;
            after.SrcAccessMask = AccessFlags2.TransferWriteBit;
            after.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
            VulkanSynchronization.PipelineBarrier(vk,
                native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
                0, null, 1, &after, 0, null, BarrierSource.Upload);
        }
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
