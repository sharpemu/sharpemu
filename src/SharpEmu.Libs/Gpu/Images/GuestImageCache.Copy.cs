// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    // Used only after a frontend proves the dispatch copies one entire color surface.
    public bool TryCopyImage(in ImageRequest sourceRequest, in ImageRequest destinationRequest)
    {
        if (sourceRequest.Role == ImageRole.DepthTarget && destinationRequest.Role == ImageRole.DepthTarget)
            return TryCopyDepthImage(sourceRequest.Description.Data, destinationRequest.Description.Data);
        ref readonly var from = ref sourceRequest.Description;
        ref readonly var to = ref destinationRequest.Description;
        if (sourceRequest.Role != ImageRole.ColorTarget || destinationRequest.Role != ImageRole.ColorTarget ||
            from.IsDepth || to.IsDepth || !SameExtent(from.Extent, to.Extent) || from.Resources != to.Resources ||
            from.PixelFormat != to.PixelFormat || from.Samples != 1 || to.Samples != 1 ||
            from.Data.Size != to.Data.Size || from.Pitch != to.Pitch || from.TileMode != to.TileMode ||
            (from.Data.Address < to.Data.End && to.Data.Address < from.Data.End))
            return false;

        var selected = ResourceSlotIdentifier.Invalid;
        using (var held = _lock.Hold())
        {
            foreach (var id in FindImagesInRange(from.Data.Address, from.Data.Size, pageOverlap: false))
            {
                var image = _slots[id];
                if (HasSameBacking(image.Description, from, exactFormat: false) && image.SafeToDownload &&
                    !image.RawWriteAfterGpuWrite && !HasNewerHandedOverImage(id, image) &&
                    (!selected.IsValid || image.GpuWriteSequence > _slots[selected].GpuWriteSequence))
                    selected = id;
            }
        }
        if (!selected.IsValid)
            return false;

        var request = destinationRequest;
        var destinationId = FindImage(ref request, exactFormat: true);
        using var copyLock = _lock.Hold();
        if (_slots.TryGet(selected) is not { SafeToDownload: true, RawWriteAfterGpuWrite: false } source ||
            !HasSameBacking(source.Description, from, exactFormat: false) || HasNewerHandedOverImage(selected, source))
            return false;
        var destination = _slots[destinationId];
        destination.CopyFrom(source);
        destination.Uses.RenderTarget = true;
        WatchImage(destinationId);
        TakeGpuOwnership(destination);
        return true;
    }

    private bool TryCopyDepthImage(GuestSpan from, GuestSpan to)
    {
        if (!ImageDescription.IsValidRange(from) || !ImageDescription.IsValidRange(to) || from.Size != to.Size ||
            (from.Address < to.End && to.Address < from.End))
            return DepthCopyFailed("ranges", from, to);

        var selected = ResourceSlotIdentifier.Invalid;
        using (var held = _lock.Hold())
        {
            foreach (var id in FindImagesInRange(from.Address, from.Size, pageOverlap: false))
            {
                var image = _slots[id];
                if (image.Description.IsDepth && image.Description.Data.Address == from.Address &&
                    image.Description.Data.End <= from.End && image.Backing.Exists && image.SafeToDownload &&
                    !image.RawWriteAfterGpuWrite && !HasNewerHandedOverImage(id, image) &&
                    (!selected.IsValid || image.GpuWriteSequence > _slots[selected].GpuWriteSequence))
                    selected = id;
            }
        }
        if (!selected.IsValid)
            return DepthCopyFailed("no-source-image", from, to);

        var description = _slots[selected].Description;
        description.Data = new GuestSpan(to.Address, description.Data.Size);
        description.Stencil = default;
        description.Metadata = default;
        description.HtileClearMask = 0;
        description.PixelFormat = DepthFormatRule.AspectTransferFormat(description.PixelFormat);
        var layers = description.Resources.Layers;
        var view = new ImageViewDescription(description.PixelFormat, layers == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray,
            ViewFormatRules.DepthAspects(description.PixelFormat), 0, 1, 0, layers, default, ImageUsageFlags.DepthStencilAttachmentBit);
        var request = new ImageRequest(description, view, ImageRole.DepthTarget);
        var destinationId = FindImage(ref request, exactFormat: false);
        using var copyLock = _lock.Hold();
        if (_slots.TryGet(selected) is not { SafeToDownload: true, RawWriteAfterGpuWrite: false } source ||
            !source.Description.IsDepth || source.Description.Data.Address != from.Address || HasNewerHandedOverImage(selected, source))
            return DepthCopyFailed("source-changed", from, to);
        var destination = _slots[destinationId];
        if (!destination.Description.IsDepth || destination.Backing.Samples != source.Backing.Samples ||
            destination.Backing.Samples != 1)
            return DepthCopyFailed("destination-not-depth", from, to);
        CopyWholeImage(destinationId, selected);
        destination.Uses.DepthTarget = true;
        TakeGpuOwnership(destination);
        return true;
    }

    private static int _depthCopyFailures;

    private static bool DepthCopyFailed(string reason, GuestSpan from, GuestSpan to)
    {
        if (Interlocked.Increment(ref _depthCopyFailures) <= 16)
            Console.Error.WriteLine($"[GPU][WARN] A depth surface copy falls back to a memory copy: reason={reason} " +
                $"source=0x{from.Address:X16} destination=0x{to.Address:X16} size=0x{from.Size:X}.");
        return false;
    }
}
