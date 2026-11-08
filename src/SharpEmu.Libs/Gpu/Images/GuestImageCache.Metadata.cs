// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Surface metadata (HTile, DCC, CMask, FMask) keyed by its guest address.
public sealed partial class GuestImageCache
{
    private static long _dccSyncs;
    private static long _dccSlicesUndecodable;
    private static long _dccSlicesMixed;
    private static long _dccSlicesCleared;

    public static string TakeDccReport() => FormattableString.Invariant(
        $"[PERF][DCC_SYNC] gpu_syncs={Interlocked.Exchange(ref _dccSyncs, 0)} cleared={Interlocked.Exchange(ref _dccSlicesCleared, 0)} mixed={Interlocked.Exchange(ref _dccSlicesMixed, 0)} undecodable={Interlocked.Exchange(ref _dccSlicesUndecodable, 0)}");

    private unsafe void MaterializeDccClear(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        uint metadataBaseLayer)
    {
        // Native clears use the view-aware DCC and CMASK path after texture owner validation.
        if (request.Description.Metadata.Kind != MetadataKind.Dcc || request.Description.Metadata.NativeColorClear)
        {
            return;
        }

        var description = request.Description;
        var range = description.Metadata.Range;
        using (var held = _lock.Hold())
        {
            var image = _slots.TryGet(imageIdentifier);
            if (image == null)
            {
                return;
            }

            image.Description.Metadata = description.Metadata;
            // The request decides: a one-level view of an image that a texture grew into a mip chain still takes its fast clear on that level.
            if (range.Size == 0 || description.Resources.Levels != 1)
            {
                return;
            }

            _surfaceMetadata.Remove(range.Address);
            NoteMetadataChange(range.Address);
        }

        var layers = description.TransferLayers;
        const ulong metadataBlockSize = 0x1000;
        if (!ImageDescription.IsValidRange(range) ||
            range.Address % metadataBlockSize != 0 ||
            layers == 0 ||
            range.Size % layers != 0 ||
            (range.Size / layers) % metadataBlockSize != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"DCC slices must contain aligned 4 KiB blocks: address=0x{range.Address:X16} size=0x{range.Size:X} layers={layers}.");
        }

        var view = request.View;
        var volume = description.IsVolume && view.Type == ImageViewType.Type3D;
        var first = volume ? 0u : metadataBaseLayer;
        var imageFirst = volume ? 0u : view.BaseLayer;
        var count = volume ? description.Extent.Depth : view.LayerCount;
        if (first >= layers || count == 0 || count > layers - first)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC view exceeds its metadata slices: first={first} count={count} layers={layers}.");
        }

        var sliceSize = range.Size / layers;
        var synchronizedAddress = range.Address + sliceSize * first;
        var synchronizedSize = sliceSize * count;

        if (_bufferCache.HasGpuDirtyBytes(synchronizedAddress, synchronizedSize))
        {
            _bufferCache.ReadMemory(synchronizedAddress, synchronizedSize, isWrite: false);
        }

        const int metadataScanChunkSize = 64 * 1024;
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(
            checked((int)Math.Min(sliceSize, (ulong)metadataScanChunkSize)));
        try
        {
            Span<byte> firstByte = stackalloc byte[1];
            for (uint slice = 0; slice < count; slice++)
            {
                var address = range.Address + sliceSize * (first + slice);
                if (!_backing.TryReadBacking(address, firstByte))
                {
                    throw SubmissionScheduler.Fatal(
                        $"Could not read DCC metadata key: address=0x{address:X16}.");
                }

                var code = firstByte[0];
                if (!PackedClearValue.TryDecodeDccColor(
                        request.View.Format,
                        code * 0x01010101u,
                        description.Metadata,
                        allowClearToRegister: request.Role == ImageRole.ColorTarget,
                        out var clear))
                {
                    Interlocked.Increment(ref _dccSlicesUndecodable);
                    continue;
                }

                var uniform = true;
                for (ulong position = 0; position < sliceSize && uniform;)
                {
                    var length = checked((int)Math.Min(
                        sliceSize - position,
                        (ulong)Math.Min(bytes.Length, metadataScanChunkSize)));
                    var chunk = bytes.AsSpan(0, length);
                    if (!_backing.TryReadBacking(address + position, chunk))
                    {
                        throw SubmissionScheduler.Fatal(
                            $"Could not read DCC metadata backing: address=0x{address + position:X16} size=0x{length:X}.");
                    }

                    uniform = chunk.IndexOfAnyExcept(code) < 0;
                    position += (ulong)length;
                }

                if (!uniform)
                {
                    Interlocked.Increment(ref _dccSlicesMixed);
                    continue;
                }

                Interlocked.Increment(ref _dccSlicesCleared);

                // The fill that consumes the code is a GPU write into the surface's allocation. When the image's range covers the metadata it marks the image stale,
                // so it comes first: the clear, which takes the ownership, has the last word.
                if (request.Role != ImageRole.DisplaySurface)
                {
                    _bufferCache.FillBuffer(address, sliceSize, uint.MaxValue, isGds: false);
                }

                using (var held = _lock.Hold())
                {
                    if (_slots.TryGet(imageIdentifier) == null)
                    {
                        return;
                    }

                    ClearDccImage(
                        imageIdentifier,
                        request,
                        new SubresourceRange(view.BaseLevel, view.LevelCount, imageFirst + slice, 1),
                        clear);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private unsafe void ClearDccImage(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        in SubresourceRange range,
        in ClearColorValue clear)
    {
        var image = _slots[imageIdentifier];
        ref readonly var description = ref image.Description;
        if (range.BaseLevel >= description.Resources.Levels || range.LevelCount == 0 ||
            range.LevelCount > description.Resources.Levels - range.BaseLevel)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC clear mip range is invalid: base={range.BaseLevel} count={range.LevelCount} levels={description.Resources.Levels}.");
        }

        var layers = description.IsVolume
            ? Math.Max(description.Extent.Depth >> (int)range.BaseLevel, 1)
            : image.Backing.Layers;
        if (range.LayerCount == 0 || range.BaseLayer >= layers || range.LayerCount > layers - range.BaseLayer)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC clear layer range is invalid: base={range.BaseLayer} count={range.LayerCount} layers={layers}.");
        }

        var fullImage = range.BaseLevel == 0 && range.LevelCount == description.Resources.Levels &&
                        range.BaseLayer == 0 && range.LayerCount == layers;
        WatchImage(imageIdentifier);
        if (!fullImage && (image.IsBufferModified || image.IsCpuDirty))
        {
            PopulateFromGuest(imageIdentifier, RefreshRequest(image), "before-dcc-clear");
            if (image.Description.Samples == 1 && (image.IsBufferModified || image.IsCpuDirty))
            {
                throw SubmissionScheduler.Fatal(
                    $"The DCC clear left guest ownership in place: address=0x{description.Data.Address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty}.");
            }
        }

        var scheduled = _scheduler.Current;
        if (scheduled.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("A DCC clear needs a command buffer that is recording.");
        }

        scheduled.EndRendering();
        var command = new CommandBuffer(scheduled.Handle);
        var needsAttachmentClear = request.View.Format != image.Backing.Format ||
                                   (description.IsVolume && !fullImage);
        if (needsAttachmentClear)
        {
            var viewDescription = new ImageViewDescription(
                request.View.Format,
                range.LayerCount == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray,
                ImageAspectFlags.ColorBit,
                range.BaseLevel,
                range.LevelCount,
                range.BaseLayer,
                range.LayerCount,
                default,
                ImageUsageFlags.ColorAttachmentBit);
            var view = image.GetOrCreateView(viewDescription);
            image.Transition(
                AccessFlags.ColorAttachmentWriteBit,
                range,
                command);
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = ImageLayout.General,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { Color = clear },
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(
                    new Offset2D(0, 0),
                    new Extent2D(
                        Math.Max(description.Extent.Width >> (int)range.BaseLevel, 1),
                        Math.Max(description.Extent.Height >> (int)range.BaseLevel, 1))),
                LayerCount = range.LayerCount,
                ColorAttachmentCount = 1,
                PColorAttachments = &attachment,
            };
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _device.Vk.CmdBeginRendering(command, &rendering);
            }
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _device.Vk.CmdEndRendering(command);
            }
        }
        else
        {
            image.Transition(AccessFlags.TransferWriteBit, range, command);
            var nativeRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                range.BaseLevel,
                range.LevelCount,
                description.IsVolume ? 0u : range.BaseLayer,
                description.IsVolume ? 1u : range.LayerCount);
            var nativeClear = clear;
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _device.Vk.CmdClearColorImage(
                    command,
                    image.Backing.Handle,
                    ImageLayout.General,
                    &nativeClear,
                    1,
                    &nativeRange);
            }
        }

        TakeGpuOwnership(image);
    }

    private readonly MetadataWriteRanges _metadataWriteRanges = new();
    private ulong _maximumMetadataSize = 1;

    // Publish coverage under the image lock before a registration grows.
    private void IncludeMetadataWriteRange(ulong address, ulong size)
    {
        _maximumMetadataSize = Math.Max(_maximumMetadataSize, size);
        _metadataWriteRanges.Include(address, size);
    }

    // Keep the bound after removals and shrinking fills. All growth passes through IncludeMetadataWriteRange.
    private int FirstPossibleMetadataOverlap(ulong address)
    {
        if (address < _maximumMetadataSize) return 0;
        var earliestExcluded = address - _maximumMetadataSize;
        var low = 0;
        var high = _surfaceMetadata.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_surfaceMetadata.GetKeyAtIndex(middle) <= earliestExcluded) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal bool MayOverlapMetadata(ulong address, ulong size)
    {
        return _metadataWriteRanges.Overlaps(address, size);
    }

    // A target that binds metadata of another kind where a registration lies ends the use of the surface that
    // registered it: the guest reused that memory. The new kind starts as at an address nothing registered.
    private SurfaceMetadata ReplaceSurfaceMetadata(ulong address, SurfaceMetadata previous, SurfaceMetadata replacement)
    {
        RecordMetadataEvent("replace-other-kind", address);
        previous.ClearMask = 0;
        previous.FillSize = 0;
        previous.Invalidated = true;
        _surfaceMetadata[address] = replacement;
        NoteMetadataChange(address);
        return replacement;
    }

    private void InvalidateMetadataForCpuWrite(ulong address, ulong size)
    {
        if (_surfaceMetadata.Count == 0 || !MayOverlapMetadata(address, size)) return;
        var end = address + size;
        for (var index = FirstPossibleMetadataOverlap(address); index < _surfaceMetadata.Count; index++)
        {
            var metadataAddress = _surfaceMetadata.GetKeyAtIndex(index);
            if (metadataAddress >= end) break;
            var metadata = _surfaceMetadata.GetValueAtIndex(index);
            var metadataSize = Math.Max(1UL, Math.Max(metadata.Size, Math.Max(metadata.RangeSize, metadata.FillSize)));
            if (metadataAddress >= address || address - metadataAddress < metadataSize)
            {
                RecordMetadataEvent("invalidate-cpu-write", metadataAddress);
                metadata.ClearMask = 0;
                metadata.FillSize = 0;
                metadata.Invalidated = true;
                NoteMetadataChange(metadataAddress);
            }
        }
    }

    private void TraceMetadataBinding(in ImageRequest request)
    {
        if (!ImageClearTrace.Enabled) return;
        if (!request.Description.HasMetadata)
        {
            return;
        }

        var description = request.Description;
        var address = description.Metadata.Range.Address;
        _metadataHistory.Record(address, description.Metadata.Range.Size,
            CreateMetadataEvent(request.Role == ImageRole.DepthTarget ? "bind-depth" : "bind-color",
                address, description.Metadata.Range.Size, description.Data.Address,
                description.Metadata.Kind == MetadataKind.Htile ? "HTile" : "Dcc"));
        _surfaceMetadata.TryGetValue(address, out var previous);
        var conflict = previous is not null && !previous.Invalidated && previous.Kind != SurfaceMetadataKind.PendingDcc &&
            ((description.Metadata.Kind == MetadataKind.Dcc && previous.Kind != SurfaceMetadataKind.Dcc) ||
             (request.Role == ImageRole.DepthTarget && previous.Kind != SurfaceMetadataKind.HTile));
        if (!conflict && !Rendering.RenderTrace.Enabled)
        {
            return;
        }

        var previousKind = previous?.Kind.ToString() ?? "unregistered";
        var message = $"MetadataBinding role={request.Role} " +
            $"image=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} " +
            $"metadata=0x{address:X16} metadataSize=0x{description.Metadata.Range.Size:X} " +
            $"kind={description.Metadata.Kind} previousKind={previousKind} " +
            $"clearMask=0x{previous?.ClearMask ?? 0:X8} fill=0x{previous?.FillValue ?? 0:X8}";
        if (!conflict)
        {
            Rendering.RenderTrace.Write(message);
            return;
        }

        Console.Error.WriteLine($"[GPU][ERROR] {message}");
        PrintMetadataHistory(address);
        _slots.ForEach((identifier, owner) =>
        {
            if (owner.Description.HasMetadata && owner.Description.Metadata.Range.Address == address)
            {
                Console.Error.WriteLine($"[GPU][ERROR] MetadataOwner image=0x{owner.Description.Data.Address:X16} " +
                    $"size=0x{owner.Description.Data.Size:X} kind={owner.Description.Metadata.Kind} " +
                    $"registered={owner.Registered} gpuModified={owner.IsGpuModified} " +
                    $"needsRebind={owner.Binding.NeedsRebind} depthTarget={owner.Uses.DepthTarget} " +
                    $"colorTarget={owner.Uses.RenderTarget}");
            }
        });
    }

    private const int MetadataEventSlots = 1024;
    private readonly (long Generation, ulong Address)[] _metadataEvents = new (long, ulong)[MetadataEventSlots];
    private long _metadataGeneration = 1;

    public long MetadataGeneration => Volatile.Read(ref _metadataGeneration);

    private void NoteMetadataChange(ulong address)
    {
        var generation = _metadataGeneration + 1;
        _metadataEvents[generation & (MetadataEventSlots - 1)] = (generation, address);
        Volatile.Write(ref _metadataGeneration, generation);
    }

    internal bool MetadataUnchangedSinceForTest(long generation, ulong address)
    {
        using var held = _lock.Read();
        return MetadataUnchangedSince(generation, address);
    }

    private bool MetadataUnchangedSince(long generation, ulong address)
    {
        var current = _metadataGeneration;
        if (generation == current)
        {
            return true;
        }

        if (current - generation >= MetadataEventSlots)
        {
            return false;
        }

        for (var at = current; at > generation; at--)
        {
            if (_metadataEvents[at & (MetadataEventSlots - 1)].Address == address)
            {
                return false;
            }
        }

        return true;
    }

    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Read();
        return _surfaceMetadata.TryGetValue(address, out var found) && !found.Invalidated && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        using var held = _lock.Read();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Invalidated || found.Kind == SurfaceMetadataKind.PendingDcc ||
            slice >= (found.Kind == SurfaceMetadataKind.Dcc ? found.DccSliceCount : 32))
        {
            return false;
        }

        fillValue = found.FillValue;
        return found.IsSliceClear(slice);
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    public SurfaceMetadataKind? PeekSurfaceMetadataKind(ulong address)
    {
        using var held = _lock.Read();
        return _surfaceMetadata.TryGetValue(address, out var found) ? found.Kind : null;
    }

    // DCC metadata of a bound target, or the range of a DCC fill seen before its target: both are
    // filled on the host, which keeps them CPU-readable instead of GPU-written.
    public bool OverlapsDccMetadata(ulong address, ulong size)
    {
        using var held = _lock.Read();
        var end = address + size;
        for (var index = FirstPossibleMetadataOverlap(address); index < _surfaceMetadata.Count; index++)
        {
            var start = _surfaceMetadata.GetKeyAtIndex(index);
            if (start >= end) break;
            var metadata = _surfaceMetadata.GetValueAtIndex(index);
            var extent = metadata.Kind switch
            {
                SurfaceMetadataKind.Dcc => metadata.Size,
                SurfaceMetadataKind.PendingDcc => metadata.FillSize,
                _ => metadata.NativeColorClear ? metadata.Size : 0UL,
            };
            if (extent != 0 && address < start + extent && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("metadata clear");
        }

        using var held = _lock.Hold();
        if (_surfaceMetadata.ContainsKey(address)) RecordMetadataEvent("clear-request", address);
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Invalidated || found.NativeColorClear ||
            found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        NoteMetadataChange(address);
        return true;
    }

    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("dcc fill");
        }

        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The DCC fill range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        // A DCC fill repeats one byte code; only the known deferred-clear codes count as clear.
        var code = (byte)fillValue;
        var dccClearMask = fillValue != code * 0x01010101u ? 0u : code switch
        {
            0x00 or 0x20 or 0x40 or 0x80 or 0xc0 => uint.MaxValue,
            _ => 0u,
        };
        using var held = _lock.Hold();
        RecordMetadataEvent("dcc-fill-request", address, size, fillValue);
        NoteMetadataChange(address);
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Invalidated)
        {
            IncludeMetadataWriteRange(address, size);
            // The fill may precede color-target discovery; a pending entry stays invisible until then.
            _surfaceMetadata[address] = new SurfaceMetadata { Kind = SurfaceMetadataKind.PendingDcc, ClearMask = dccClearMask, FillValue = fillValue, FillSize = size };
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.PendingDcc)
        {
            IncludeMetadataWriteRange(address, size);
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.Dcc)
        {
            if (found.NativeColorClear) return false;
            IncludeMetadataWriteRange(address, size);
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return true;
        }

        return false;
    }

    public static bool IsDccClearCode(byte code) => code is 0x00 or 0x20 or 0x40 or 0x80 or 0xc0;

    public bool TryReadGuestDccClear(ulong metadataAddress, ulong sliceSize, uint slice, out ulong sliceAddress, out byte code)
    {
        sliceAddress = 0;
        code = 0;
        if (metadataAddress == 0 || sliceSize == 0 || sliceSize > int.MaxValue ||
            (ulong)slice > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return false;
        }

        var address = metadataAddress + (ulong)slice * sliceSize;
        if (!IsValidRange(address, sliceSize) || _bufferCache.HasGpuDirtyBytes(address, sliceSize))
        {
            return false;
        }

        Span<byte> first = stackalloc byte[1];
        if (!_backing.TryReadBacking(address, first) || !IsDccClearCode(first[0]))
        {
            return false;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(sliceSize, 64 * 1024));
        try
        {
            for (ulong position = 0; position < sliceSize;)
            {
                var span = buffer.AsSpan(0, (int)Math.Min(sliceSize - position, (ulong)buffer.Length));
                if (!_backing.TryReadBacking(address + position, span) || span.IndexOfAnyExcept(first[0]) >= 0)
                {
                    return false;
                }

                position += (ulong)span.Length;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        sliceAddress = address;
        code = first[0];
        return true;
    }

    public void SynchronizeGuestDccMetadata(ulong metadataAddress, ulong sliceSize, uint baseLayer, uint layerCount)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("dcc sync");
        }

        if (metadataAddress == 0 || sliceSize == 0 || layerCount == 0 ||
            (ulong)baseLayer + layerCount > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return;
        }

        var address = metadataAddress + (ulong)baseLayer * sliceSize;
        var size = (ulong)layerCount * sliceSize;
        if (IsValidRange(address, size) && _bufferCache.HasGpuDirtyBytes(address, size))
        {
            Interlocked.Increment(ref _dccSyncs);
            if (Diagnostics.DccWriterTrace.Enabled)
            {
                Diagnostics.DccWriterTrace.ReportSync(address, size);
            }

            _ = _bufferCache.TrySynchronizeCpuRead(address, size);
        }
    }

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("metadata slice");
        }

        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Invalidated || found.Kind == SurfaceMetadataKind.PendingDcc ||
            slice >= (found.Kind == SurfaceMetadataKind.Dcc ? found.DccSliceCount : 32))
        {
            return false;
        }

        NoteMetadataChange(address);
        found.SetSliceClear(slice, isClear);

        return true;
    }
}
