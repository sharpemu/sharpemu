// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed unsafe partial class GuestImageCache
{
    internal void SynchronizeColorMetadata(in ImageRequest request)
    {
        var metadata = request.Description.Metadata;
        if (request.Role != ImageRole.ColorTarget || !metadata.NativeColorClear) return;
        var range = metadata.Range;
        // Native metadata writes must finish before image discovery and final draw uploads.
        if (_bufferCache.HasGpuDirtyBytes(range.Address, range.Size))
            _bufferCache.ReadMemory(range.Address, range.Size);
    }

    internal void ApplyNativeColorClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        var description = request.Description;
        var metadata = description.Metadata;
        if (request.Role != ImageRole.ColorTarget || !metadata.NativeColorClear) return;
        var view = request.View;
        var sliceSize = metadata.Range.Size / description.Resources.Layers;
        if (sliceSize == 0 || metadata.ColorMetadataBaseLayer >= description.Resources.Layers ||
            view.LayerCount > description.Resources.Layers - metadata.ColorMetadataBaseLayer)
            throw SubmissionScheduler.Fatal("The native color metadata view is outside its slices.");

        using (var held = _lock.Hold())
        {
            IncludeMetadataWriteRange(metadata.Range.Address, metadata.Range.Size);
            if (!_surfaceMetadata.TryGetValue(metadata.Range.Address, out var registration) || registration.Invalidated)
            {
                registration = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc };
                _surfaceMetadata[metadata.Range.Address] = registration;
            }
            else if (registration.Kind == SurfaceMetadataKind.PendingDcc)
            {
                registration.Kind = SurfaceMetadataKind.Dcc;
            }
            else if (registration.Kind != SurfaceMetadataKind.Dcc)
            {
                throw SubmissionScheduler.Fatal("The native color target metadata is not DCC.");
            }
            registration.NativeColorClear = true;
            registration.RangeSize = Math.Max(registration.RangeSize, metadata.Range.Size);
            _slots[imageIdentifier].Description.Metadata = metadata;
            _slots[imageIdentifier].MetadataRegistration = registration;
        }

        Span<byte> bytes = stackalloc byte[4096];
        for (uint slice = 0; slice < view.LayerCount; slice++)
        {
            var layer = view.BaseLayer + slice;
            var address = metadata.Range.Address + sliceSize * (metadata.ColorMetadataBaseLayer + slice);
            if (!TryInspectColorClear(request, layer, address, sliceSize, bytes, out var operation)) continue;
            RecordColorClear(imageIdentifier, request, operation);
        }
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
        if (code == 0x20 && metadata.PackedColorClearSupported)
            color = metadata.PackedColorClear;
        else if (!NativeColorClear.TryDecode(code, view.Format, metadata.ColorAlphaOnLeastSignificantBits, out color))
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
        using (var held = _lock.Hold())
        {
            RefreshFromGuest(imageIdentifier, request);
            var image = _slots[imageIdentifier];
            var clearView = view with { BaseLayer = layer, LayerCount = 1, Type = ImageViewType.Type2D };
            var attachmentView = image.GetOrCreateView(clearView);
            var command = _scheduler.Current;
            command.EndRendering();
            var native = new CommandBuffer(command.Handle);
            image.Transition(ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit,
                new SubresourceRange(view.BaseLevel, 1, layer, 1), native);
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = attachmentView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
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

        // Publish expanded keys only after the clear is recorded. The buffer path preserves ordering.
        _bufferCache.FillBuffer(address, sliceSize, uint.MaxValue, false);
        if (ImageClearTrace.Enabled)
            ImageClearTrace.Write($"applied image=0x{description.Data.Address:X16} metadata=0x{address:X16} size=0x{sliceSize:X} layer={layer} code=0x{code:X2}");
    }
}
