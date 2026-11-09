// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed unsafe partial class GuestImageCache
{
    internal void SynchronizeColorMetadata(in ImageRequest request)
    {
        var metadata = request.Description.Metadata;
        if (!metadata.NativeColorClear) return;
        var range = metadata.Range;
        // Native metadata writes must finish before image discovery and final draw uploads.
        if (_bufferCache.HasGpuDirtyBytes(range.Address, range.Size))
        {
            if (Diagnostics.DccWriterTrace.Enabled)
                Diagnostics.DccWriterTrace.ReportSync(range.Address, range.Size);
            _bufferCache.ReadMemory(range.Address, range.Size);
        }
    }

    internal void ApplyNativeColorClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        var description = request.Description;
        var metadata = description.Metadata;
        if (!metadata.NativeColorClear) return;
        var metadataKind = metadata.Kind == MetadataKind.Cmask ? SurfaceMetadataKind.CMask : SurfaceMetadataKind.Dcc;
        var view = request.View;
        var layers = description.TransferLayers;
        var volumeTexture = description.IsVolume && view.Type == ImageViewType.Type3D;
        var layerCount = volumeTexture ? description.Extent.Depth : view.LayerCount;
        var sliceSize = metadata.Range.Size / layers;
        if (sliceSize == 0 || metadata.ColorMetadataBaseLayer >= layers ||
            layerCount > layers - metadata.ColorMetadataBaseLayer)
            throw SubmissionScheduler.Fatal("The native color metadata view is outside its slices.");

        using (var held = _lock.Hold())
        {
            IncludeMetadataWriteRange(metadata.Range.Address, metadata.Range.Size);
            // A new metadata use must not retain another surface's clear state or ownership.
            var changed = false;
            if (!_surfaceMetadata.TryGetValue(metadata.Range.Address, out var registration) ||
                registration.Invalidated || registration.Kind != metadataKind || !registration.NativeColorClear)
            {
                registration = new SurfaceMetadata { Kind = metadataKind };
                _surfaceMetadata[metadata.Range.Address] = registration;
                changed = true;
            }
            changed |= !ReferenceEquals(_slots[imageIdentifier].MetadataRegistration, registration) || registration.DccSliceCount != layers ||
                registration.ClearMask != 0 || registration.FillSize != 0 || registration.FillValue != uint.MaxValue ||
                registration.RangeSize < metadata.Range.Size || registration.Size < metadata.Range.Size;
            registration.NativeColorClear = true;
            registration.DccSliceCount = layers;
            if (changed)
            {
                NoteMetadataChange(metadata.Range.Address);
            }
            // Native bytes own the clear state. Do not reuse deferred fill flags.
            registration.ClearMask = 0;
            registration.FillSize = 0;
            registration.FillValue = uint.MaxValue;
            registration.RangeSize = Math.Max(registration.RangeSize, metadata.Range.Size);
            registration.Size = Math.Max(registration.Size, metadata.Range.Size);
            _slots[imageIdentifier].Description.Metadata = metadata;
            _slots[imageIdentifier].MetadataRegistration = registration;
        }

        Span<byte> bytes = stackalloc byte[4096];
        for (uint slice = 0; slice < layerCount; slice++)
        {
            var layer = view.BaseLayer + slice;
            var address = metadata.Range.Address + sliceSize * (metadata.ColorMetadataBaseLayer + slice);
            if (!TryInspectColorClear(request, layer, address, sliceSize, bytes, out var operation)) continue;
            RecordColorClear(imageIdentifier, request, operation);
        }
    }

    // True when ApplyNativeColorClear would change nothing: the registration is current and no slice holds a clear code.
    internal bool NativeColorClearSettled(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        var description = request.Description;
        var metadata = description.Metadata;
        if (!metadata.NativeColorClear) return true;
        var metadataKind = metadata.Kind == MetadataKind.Cmask ? SurfaceMetadataKind.CMask : SurfaceMetadataKind.Dcc;
        var view = request.View;
        var layers = description.TransferLayers;
        var volumeTexture = description.IsVolume && view.Type == ImageViewType.Type3D;
        var layerCount = volumeTexture ? description.Extent.Depth : view.LayerCount;
        var sliceSize = metadata.Range.Size / layers;
        if (sliceSize == 0 || metadata.ColorMetadataBaseLayer >= layers ||
            layerCount > layers - metadata.ColorMetadataBaseLayer)
            return false;

        using (_lock.Read())
        {
            if (_slots.TryGet(imageIdentifier) is not { } image ||
                !_surfaceMetadata.TryGetValue(metadata.Range.Address, out var registration) ||
                registration.Invalidated || registration.Kind != metadataKind || !registration.NativeColorClear ||
                !ReferenceEquals(image.MetadataRegistration, registration) || registration.DccSliceCount != layers ||
                registration.ClearMask != 0 || registration.FillSize != 0 || registration.FillValue != uint.MaxValue ||
                registration.RangeSize < metadata.Range.Size || registration.Size < metadata.Range.Size ||
                _maximumMetadataSize < metadata.Range.Size || !_metadataWriteRanges.Contains(metadata.Range.Address, metadata.Range.Size) ||
                !MemoryMarshal.AsBytes(new ReadOnlySpan<MetadataDescription>(in image.Description.Metadata))
                    .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<MetadataDescription>(in metadata))))
                return false;
        }

        Span<byte> bytes = stackalloc byte[4096];
        for (uint slice = 0; slice < layerCount; slice++)
        {
            var address = metadata.Range.Address + sliceSize * (metadata.ColorMetadataBaseLayer + slice);
            if (TryInspectColorClear(request, view.BaseLayer + slice, address, sliceSize, bytes, out _)) return false;
        }

        return true;
    }

    private bool TryInspectColorClear(in ImageRequest request, uint layer, ulong address,
        ulong sliceSize, Span<byte> bytes, out ColorClearOperation operation)
    {
        operation = default;
        var metadata = request.Description.Metadata;
        var view = request.View;
        if (!_backing.TryReadBacking(address, bytes[..1]))
            throw SubmissionScheduler.Fatal($"The color metadata is unreadable: address=0x{address:X16}.");
        var code = bytes[0];
        ClearColorValue color;
        var cmask = metadata.Kind == MetadataKind.Cmask;
        if (code == (cmask ? 0 : 0x20) && request.Role == ImageRole.ColorTarget && metadata.PackedColorClearSupported)
            color = metadata.PackedColorClear;
        else if (cmask || !NativeColorClear.TryDecode(code, view.Format, metadata.ColorAlphaOnLeastSignificantBits, out color))
            return false;

        var scan = new UniformMetadataScan(code, sliceSize);
        for (ulong offset = 0; offset < sliceSize; offset += (ulong)bytes.Length)
        {
            var chunk = bytes[..(int)Math.Min((ulong)bytes.Length, sliceSize - offset)];
            if (!_backing.TryReadBacking(address + offset, chunk))
                throw SubmissionScheduler.Fatal($"The color metadata slice is unreadable: address=0x{address + offset:X16}.");
            if (!scan.Accept(chunk))
            {
                return false;
            }
        }
        if (!scan.Complete) return false;

        operation = new ColorClearOperation(layer, address, sliceSize, code, color);
        return true;
    }

    private static bool ClearCoversImage(CachedImage image, in ImageRequest request)
    {
        ref readonly var description = ref image.Description;
        return description.Resources.Levels == 1 && description.TransferLayers == 1 && request.View.BaseLevel == 0 &&
            request.Description.Extent.Width == description.Extent.Width && request.Description.Extent.Height == description.Extent.Height;
    }

    private void RecordColorClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request,
        in ColorClearOperation operation)
    {
        var description = request.Description;
        var view = request.View;
        var layer = operation.ImageLayer;
        var address = operation.MetadataAddress;
        var sliceSize = operation.MetadataSize;
        var code = operation.MetadataCode;
        var color = operation.Color;
        // Consume the code first: an image whose range covers the slice takes the fill as a write to its memory,
        // and the clear's ownership must have the last word.
        _bufferCache.FillBuffer(address, sliceSize, uint.MaxValue, false);
        using (var held = _lock.Hold())
        {
            var image = _slots[imageIdentifier];
            if (!ClearCoversImage(image, request))
            {
                RefreshFromGuest(imageIdentifier, request);
            }

            var clearView = view with
            {
                BaseLayer = layer, LayerCount = 1, Type = ImageViewType.Type2D,
                Mapping = default, Usage = ImageUsageFlags.ColorAttachmentBit, MinLod = 0,
            };
            var attachmentView = image.GetOrCreateView(clearView);
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            image.Transition(AccessFlags.ColorAttachmentWriteBit,
                new SubresourceRange(view.BaseLevel, 1, layer, 1), native);
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = attachmentView,
                ImageLayout = ImageLayout.General,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { Color = color },
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(default, new Extent2D(description.Extent.Width, description.Extent.Height)),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &attachment,
            };
            _device.Vk.CmdBeginRendering(native, &rendering);
            _device.Vk.CmdEndRendering(native);
            WatchImage(imageIdentifier);
            TakeGpuOwnership(image);
        }

        if (ImageClearTrace.Enabled)
            ImageClearTrace.Write($"applied image=0x{description.Data.Address:X16} metadata=0x{address:X16} size=0x{sliceSize:X} layer={layer} code=0x{code:X2}");
    }
}
