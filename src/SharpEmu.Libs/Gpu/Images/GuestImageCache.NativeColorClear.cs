// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// What ApplyNativeColorClear would do for a request.
internal enum NativeColorClearPrediction : byte
{
    // Nothing: the registration is current and no slice holds a clear code.
    Settled,
    // A registration update only: no slice holds a clear code, so it records nothing.
    RegistrationOnly,
    // A clear to record.
    ClearCode,
    // A view outside its slices, or metadata that cannot be read.
    BadView,
}

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

    [SkipLocalsInit]
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

        bool registered;
        using (_lock.Read())
        {
            registered = NativeColorClearRegistered(imageIdentifier, in metadata, metadataKind, layers);
        }

        if (!registered)
        {
            using var held = _lock.Hold();
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
    [SkipLocalsInit]
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
            if (!NativeColorClearRegistered(imageIdentifier, in metadata, metadataKind, layers))
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

    // What ApplyNativeColorClear would do for the request, read-only. Unlike the settled check it reads the slices of an
    // image whose registration is not current; metadata it cannot read is BadView, so the caller stops and the serial
    // apply decides.
    [SkipLocalsInit]
    internal NativeColorClearPrediction PredictNativeColorClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        var description = request.Description;
        var metadata = description.Metadata;
        if (!metadata.NativeColorClear) return NativeColorClearPrediction.Settled;
        var metadataKind = metadata.Kind == MetadataKind.Cmask ? SurfaceMetadataKind.CMask : SurfaceMetadataKind.Dcc;
        var view = request.View;
        var layers = description.TransferLayers;
        var volumeTexture = description.IsVolume && view.Type == ImageViewType.Type3D;
        var layerCount = volumeTexture ? description.Extent.Depth : view.LayerCount;
        var sliceSize = metadata.Range.Size / layers;
        if (sliceSize == 0 || metadata.ColorMetadataBaseLayer >= layers ||
            layerCount > layers - metadata.ColorMetadataBaseLayer)
            return NativeColorClearPrediction.BadView;

        bool registered;
        using (_lock.Read())
        {
            registered = NativeColorClearRegistered(imageIdentifier, in metadata, metadataKind, layers);
        }

        Span<byte> bytes = stackalloc byte[4096];
        for (uint slice = 0; slice < layerCount; slice++)
        {
            var address = metadata.Range.Address + sliceSize * (metadata.ColorMetadataBaseLayer + slice);
            if (!_backing.TryReadBacking(address, bytes[..1]))
                return NativeColorClearPrediction.BadView;
            if (TryInspectColorClear(request, view.BaseLayer + slice, address, sliceSize, bytes, out _))
                return NativeColorClearPrediction.ClearCode;
        }

        return registered ? NativeColorClearPrediction.Settled : NativeColorClearPrediction.RegistrationOnly;
    }

    private bool NativeColorClearRegistered(ResourceSlotIdentifier imageIdentifier, in MetadataDescription metadata,
        SurfaceMetadataKind metadataKind, uint layers) =>
        _slots.TryGet(imageIdentifier) is { } image &&
        _surfaceMetadata.TryGetValue(metadata.Range.Address, out var registration) &&
        !registration.Invalidated && registration.Kind == metadataKind && registration.NativeColorClear &&
        ReferenceEquals(image.MetadataRegistration, registration) && registration.DccSliceCount == layers &&
        registration.ClearMask == 0 && registration.FillSize == 0 && registration.FillValue == uint.MaxValue &&
        registration.RangeSize >= metadata.Range.Size && registration.Size >= metadata.Range.Size &&
        _maximumMetadataSize >= metadata.Range.Size && _metadataWriteRanges.Contains(metadata.Range.Address, metadata.Range.Size) &&
        MemoryMarshal.AsBytes(new ReadOnlySpan<MetadataDescription>(in image.Description.Metadata))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<MetadataDescription>(in metadata)));

    private bool TryInspectColorClear(in ImageRequest request, uint layer, ulong address,
        ulong sliceSize, Span<byte> bytes, out ColorClearOperation operation)
    {
        operation = default;
        var metadata = request.Description.Metadata;
        var view = request.View;
        if (!_backing.TryReadBacking(address, bytes[..1]))
            throw SubmissionScheduler.Fatal($"The color metadata is unreadable: address=0x{address:X16}.");
        var code = bytes[0];
        ClearColorValue color = default;
        var cmask = metadata.Kind == MetadataKind.Cmask;
        var kind = ColorClearKind.Fill;
        if (NativeColorClear.IsBlockFirstElement(code, cmask))
            kind = ColorClearKind.BlockFirstElement;
        else if (code == (cmask ? 0 : 0x20) && request.Role == ImageRole.ColorTarget && metadata.PackedColorClearSupported)
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
        if (kind == ColorClearKind.BlockFirstElement &&
            NativeColorClear.BlockFirstElementRefusal(request) is { } refusal)
        {
            ReportBlockExpansionRefused(request, address, refusal);
            return false;
        }

        operation = new ColorClearOperation(layer, address, sliceSize, code, color, kind);
        return true;
    }

    private static readonly HashSet<string> ReportedBlockExpansionRefusals = [];

    // A uniform 0x10 slice the expansion cannot prove right keeps its keys and its image; say why once.
    private static void ReportBlockExpansionRefused(in ImageRequest request, ulong metadataAddress, string reason)
    {
        lock (ReportedBlockExpansionRefusals)
        {
            if (!ReportedBlockExpansionRefusals.Add(reason)) return;
        }

        Console.Error.WriteLine(
            $"[ImageClear] DCC key 0x10 (block reads as its first element) not applied: {reason}; " +
            $"image=0x{request.Description.Data.Address:X16} metadata=0x{metadataAddress:X16}.");
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
        if (operation.Kind == ColorClearKind.BlockFirstElement)
        {
            RecordBlockFirstElementFill(imageIdentifier, request, operation);
            return;
        }

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

    // The tile layout of one layer for a block-expanding detile, or null with the reason it cannot be proven right.
    private static ColorTransferPlan? PlanBlockExpansion(CachedImage image, uint layer, out string reason)
    {
        ref readonly var info = ref image.Description;
        reason = "";
        if (image.DepthOwner.IsValid || info.Samples != 1 || image.Backing.Samples != 1 ||
            info.Resources.Levels != 1 || info.FirstLevel != 0 || info.Resources.Layers == 0 ||
            info.Data.Size % info.Resources.Layers != 0 || layer >= info.Resources.Layers)
        {
            reason = "the image layout is not a single-level color surface";
            return null;
        }

        var plan = PlanColorTransfer(image, ImageRole.ColorTarget, TransferDirection.Upload);
        if (!plan.Valid || !plan.Tiled || plan.Regions.Count != info.Resources.Layers || plan.Tiles.Count != plan.Regions.Count)
        {
            reason = "the tile layout does not map one transfer to each layer";
            return null;
        }

        var tile = plan.Tiles[(int)layer];
        if (!GpuTiler.CanExpandBlocks(tile))
        {
            reason = "the tile transfer does not start on a 256-byte block";
            return null;
        }

        plan.Regions = [plan.Regions[(int)layer]];
        plan.Tiles = [tile];
        return plan;
    }

    // DCC key 0x10: every element of a 256-byte block reads as the block's first element. The keys are consumed
    // as for any clear code, and the image is filled from the guest's block-first elements on the GPU, so the
    // image owns the expanded contents afterwards.
    private void RecordBlockFirstElementFill(ResourceSlotIdentifier imageIdentifier, in ImageRequest request,
        in ColorClearOperation operation)
    {
        var layer = operation.ImageLayer;
        ColorTransferPlan? plan;
        string reason;
        using (var held = _lock.Hold())
        {
            plan = PlanBlockExpansion(_slots[imageIdentifier], layer, out reason);
        }

        if (plan == null)
        {
            ReportBlockExpansionRefused(request, operation.MetadataAddress, reason);
            return;
        }

        _bufferCache.FillBuffer(operation.MetadataAddress, operation.MetadataSize, uint.MaxValue, false);
        using (var held = _lock.Hold())
        {
            var image = _slots[imageIdentifier];
            if (!ClearCoversImage(image, request))
            {
                RefreshFromGuest(imageIdentifier, request);
            }

            var info = image.Description;
            var (source, sourceOffset) = _bufferCache.ObtainBufferForImage(info.Data.Address, info.Data.Size);
            var linear = _tiler.Detile(source.Handle, sourceOffset, info.Data.Size, plan.LinearSize,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(plan.Tiles), expandBlocks: true);
            if (plan.SwapBgra16)
            {
                linear = _tiler.SwapBgra16(linear);
            }

            UploadRegions(image, plan.Regions, linear);
            WatchImage(imageIdentifier);
            TakeGpuOwnership(image);
        }

        if (ImageClearTrace.Enabled)
            ImageClearTrace.Write($"applied image=0x{request.Description.Data.Address:X16} metadata=0x{operation.MetadataAddress:X16} size=0x{operation.MetadataSize:X} layer={layer} code=0x{operation.MetadataCode:X2} block-first-element");
    }
}
