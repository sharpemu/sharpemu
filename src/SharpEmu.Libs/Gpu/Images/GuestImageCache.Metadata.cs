// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Surface metadata (HTile, DCC, CMask, FMask) keyed by its guest address.
public sealed partial class GuestImageCache
{
    // Expand native DCC clear keys into the host image before either the attachment or texture
    // path uses it. Guest backing is authoritative: each metadata slice is synchronized, decoded,
    // verified as uniform, then consumed independently. This also covers volumes deeper than the
    // 32 layers representable by the legacy side-state mask.
    private unsafe void MaterializeDccClear(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        uint metadataBaseLayer)
    {
        if (request.Description.Metadata.Kind != MetadataKind.Dcc)
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
            // Native DCC keys are not generic HTile/CMask/FMask side state. Dropping any stale
            // classification also lets FillBuffer publish consumed keys without recursion.
            _surfaceMetadata.Remove(range.Address);
            if (range.Size == 0 || description.Resources.Levels != 1 ||
                image.Description.Resources.Levels != 1)
            {
                return;
            }
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

        // Metadata can be written by a guest dispatch. Finish only the slices visible through
        // this view before examining their direct backing. ReadMemory may submit and wait, so no
        // image-cache lock is held here.
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

                    for (var index = 0; index < chunk.Length; index++)
                    {
                        if (chunk[index] != code)
                        {
                            uniform = false;
                            break;
                        }
                    }

                    position += (ulong)length;
                }

                if (!uniform)
                {
                    continue;
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

                // Expanded native keys no longer describe the guest allocation. Publish 0xff so a
                // future descriptor discovery cannot materialize the same clear a second time.
                if (request.Role != ImageRole.DisplaySurface)
                {
                    _bufferCache.FillBuffer(address, sliceSize, uint.MaxValue, isGds: false);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    // Vulkan transfer clears cannot address individual Z slices of a 3D image. A partial
    // volume clear therefore uses a compatible 2D-array attachment view and dynamic rendering.
    // The caller holds the image-cache lock.
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
                0,
                range.BaseLayer,
                range.LayerCount,
                default,
                ImageUsageFlags.ColorAttachmentBit);
            var view = image.GetOrCreateView(viewDescription);
            image.Transition(
                ImageLayout.ColorAttachmentOptimal,
                AccessFlags.ColorAttachmentWriteBit,
                range,
                command);
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
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
            _device.Vk.CmdBeginRendering(command, &rendering);
            _device.Vk.CmdEndRendering(command);
        }
        else
        {
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, command);
            var nativeRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                range.BaseLevel,
                range.LevelCount,
                description.IsVolume ? 0u : range.BaseLayer,
                description.IsVolume ? 1u : range.LayerCount);
            var nativeClear = clear;
            _device.Vk.CmdClearColorImage(
                command,
                image.Backing.Handle,
                ImageLayout.TransferDstOptimal,
                &nativeClear,
                1,
                &nativeRange);
        }

        TakeGpuOwnership(image);
    }

    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Hold();
        return _surfaceMetadata.TryGetValue(address, out var found) && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) ||
            found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        fillValue = found.FillValue;
        return (found.ClearMask & (1u << (int)slice)) != 0;
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    public bool OverlapsDccMetadata(ulong address, ulong size)
    {
        using var held = _lock.Hold();
        foreach (var (start, metadata) in _surfaceMetadata)
        {
            if (metadata.Kind == SurfaceMetadataKind.Dcc && metadata.Size != 0 &&
                address < start + metadata.Size && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        return true;
    }

    // DCC fills must remain real guest-memory producers. Descriptor discovery later reads the
    // authoritative metadata backing and materializes only complete, uniform native keys.
    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The DCC fill range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        _ = fillValue;
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
        if (metadataAddress == 0 || sliceSize == 0 || layerCount == 0 ||
            (ulong)baseLayer + layerCount > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return;
        }

        var address = metadataAddress + (ulong)baseLayer * sliceSize;
        var size = (ulong)layerCount * sliceSize;
        if (IsValidRange(address, size) && _bufferCache.HasGpuDirtyBytes(address, size))
        {
            _ = _bufferCache.TrySynchronizeCpuRead(address, size);
        }
    }

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) ||
            found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        if (isClear)
        {
            found.ClearMask |= 1u << (int)slice;
        }
        else
        {
            found.ClearMask &= ~(1u << (int)slice);
        }

        return true;
    }
}
