// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

// Guest-to-image uploads, image-to-guest downloads and the buffer/image hand-overs.
public sealed unsafe partial class GuestImageCache
{
    private enum TransferDirection
    {
        Upload,
        Download,
    }

    private sealed class ColorTransferPlan
    {
        public TextureTransferLayout Layout = null!;
        public List<BufferImageCopy> Regions = new();
        public List<TileTransfer> Tiles = new();
        public ulong LinearSize;
        public bool Tiled;
        public bool SwapBgra16;
        public bool Valid;
    }

    private sealed class ImageDownloadPlan
    {
        public ColorTransferPlan Color = new();
        public bool Depth;
        public bool Valid;
    }

    private static ulong LinearSizeOf(List<TileTransfer> tiles)
    {
        ulong size = 0;
        foreach (var tile in tiles)
        {
            size = Math.Max(size, tile.LinearOffset + tile.LinearSize);
        }

        return size;
    }

    private static ColorTransferPlan PlanColorTransfer(CachedImage image, ImageRole role, TransferDirection direction)
    {
        ref readonly var info = ref image.Description;
        var format = info.GuestFormat;
        var layers = info.TransferLayers;
        var volume = info.IsVolume;
        var allowDepthTile = direction == TransferDirection.Upload;
        var owner = direction == TransferDirection.Upload ? "texture upload" : "image readback";
        var plan = new ColorTransferPlan();
        if (direction == TransferDirection.Upload)
        {
            switch (role)
            {
                case ImageRole.Texture:
                    break;
                case ImageRole.StorageImage:
                    owner = "storage image upload";
                    break;
                case ImageRole.ColorTarget:
                    if (info.Resources.Layers == 0 || info.Data.Size % info.Resources.Layers != 0 || info.Samples != 1 || image.Backing.Samples != 1)
                    {
                        throw InvalidAttachmentUpload(info, image);
                    }

                    format = ImageDescription.RenderTargetTransferFormat(info.BytesPerBlock);
                    allowDepthTile = true;
                    plan.SwapBgra16 = info.Bgra16;
                    owner = "color target upload";
                    break;
                case ImageRole.DisplaySurface:
                    if (info.Resources.Layers == 0 || info.Data.Size % info.Resources.Layers != 0 || info.Samples != 1 || image.Backing.Samples != 1 ||
                        info.Metadata.Compression != DisplayCompression.Uncompressed)
                    {
                        throw InvalidAttachmentUpload(info, image);
                    }

                    format = info.GuestFormat;
                    layers = info.Resources.Layers;
                    volume = false;
                    allowDepthTile = false;
                    plan.SwapBgra16 = info.Bgra16;
                    owner = "display surface upload";
                    break;
                case ImageRole.DepthTarget:
                    return plan;
            }
        }
        else
        {
            if (role == ImageRole.DepthTarget)
            {
                return plan;
            }

            format = role == ImageRole.ColorTarget ? ImageDescription.RenderTargetTransferFormat(info.BytesPerBlock) : info.GuestFormat;
            allowDepthTile = role is ImageRole.StorageImage or ImageRole.ColorTarget;
            plan.SwapBgra16 = info.Bgra16;
        }

        plan.Layout = TextureTransferLayout.Compute(format, info.Extent.Width, info.Extent.Height, info.Resources.Levels, layers, info.TileMode, info.Data.Size, allowDepthTile, volume, owner);
        plan.Regions = plan.Layout.BuildCopies();
        plan.Tiled = plan.Layout.Surface.Description.TileMode != GuestTileMode.Linear;
        if (plan.Tiled)
        {
            if (!plan.Layout.TryBuildTileTransfers(info.Data.Size, plan.Regions, info.Resources.Levels, out var tiles))
            {
                return plan;
            }

            plan.Tiles = tiles;
            plan.LinearSize = LinearSizeOf(plan.Tiles);
        }

        plan.Valid = true;
        return plan;
    }

    private static Exception InvalidAttachmentUpload(in ImageDescription info, CachedImage image) =>
        SubmissionScheduler.Fatal(
            $"The color-attachment upload is invalid: address=0x{info.Data.Address:X16} size=0x{info.Data.Size:X} layers={info.Resources.Layers} samples={info.Samples} backingSamples={image.Backing.Samples} compression={info.Metadata.Compression}.");

    private static ImageDownloadPlan PlanDownload(CachedImage image)
    {
        ref readonly var info = ref image.Description;
        var plan = new ImageDownloadPlan { Depth = info.IsDepth };
        if (info.Samples != 1 || image.Backing.Samples != 1)
        {
            return plan;
        }

        if (plan.Depth)
        {
            plan.Valid = info.IsSupportedDepthPlaneReadback && info.Resources.Layers != 0 && info.Data.Size % info.Resources.Layers == 0 &&
                         GuestPixelFormats.BytesPerElement(info.GuestFormat) == info.BytesPerBlock;
            return plan;
        }

        if (info.Metadata.Compression != DisplayCompression.Uncompressed)
        {
            return plan;
        }

        plan.Color = PlanColorTransfer(image, UploadRole(image), TransferDirection.Download);
        plan.Valid = plan.Color.Valid;
        return plan;
    }

    private static List<TileTransfer> DepthTileTransfers(in ImageDescription info, in TileBlockLayout block, ulong fullSliceSize)
    {
        var tiles = new List<TileTransfer>((int)info.Resources.Layers);
        for (uint layer = 0; layer < info.Resources.Layers; layer++)
        {
            var offset = fullSliceSize * layer;
            tiles.Add(new TileTransfer
            {
                Kind = block.Kind,
                BytesPerElement = block.BytesPerElement,
                LinearOffset = offset,
                LinearSize = fullSliceSize,
                TiledOffset = offset,
                TiledSize = fullSliceSize,
                LinearSliceStride = 0,
                Width = info.Extent.Width,
                Height = info.Extent.Height,
                Depth = 1,
                Pitch = info.Pitch,
                SurfaceZ = layer,
            });
        }

        return tiles;
    }

    private static BufferImageCopy[] DepthCopies(in ImageDescription info, ulong sliceSize)
    {
        var copies = new BufferImageCopy[info.Resources.Layers];
        for (uint layer = 0; layer < info.Resources.Layers; layer++)
        {
            copies[layer] = new BufferImageCopy
            {
                BufferOffset = sliceSize * layer,
                BufferRowLength = info.Pitch,
                BufferImageHeight = info.Extent.Height,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, layer, 1),
                ImageExtent = new Extent3D(info.Extent.Width, info.Extent.Height, 1),
            };
        }

        return copies;
    }

    private static TileBlockLayout DepthBlock(in ImageDescription info)
    {
        if (!TileGeometry.TryGetBlockLayout(TileBlockKind.Depth64KB, info.BytesPerBlock, out var block))
        {
            throw SubmissionScheduler.Fatal($"The depth tile layout is not supported: bytesPerBlock={info.BytesPerBlock}.");
        }

        return block;
    }

    private static void UploadRegions(CachedImage image, List<BufferImageCopy> copies, TilerBufferSpan linear)
    {
        for (var index = 0; index < copies.Count; index++)
        {
            var copy = copies[index];
            copy.BufferOffset += linear.Offset;
            copies[index] = copy;
        }

        image.UploadFromBuffer(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(copies), linear.Buffer, linear.Offset, linear.Size);
    }

    private void UploadFromBuffer(CachedImage image, in ImageRequest request, GpuBuffer source, ulong sourceOffset)
    {
        ref readonly var info = ref image.Description;
        if (request.Role != ImageRole.DepthTarget)
        {
            var plan = PlanColorTransfer(image, request.Role, TransferDirection.Upload);
            if (!plan.Valid)
            {
                throw SubmissionScheduler.Fatal(
                    $"The color upload is invalid: role={request.Role} address=0x{info.Data.Address:X16} size=0x{info.Data.Size:X16} format={(uint)info.GuestFormat} tile={(uint)info.TileMode} " +
                    $"kind={(uint)plan.Layout.Surface.Texture.Block.Kind} extent={info.Extent.Width}x{info.Extent.Height}x{info.Extent.Depth} pitch={info.Pitch} levels={info.Resources.Levels} layers={info.Resources.Layers} samples={info.Samples}.");
            }

            var linear = new TilerBufferSpan(source.Handle, sourceOffset, info.Data.Size);
            if (plan.Tiled)
            {
                linear = _tiler.Detile(source.Handle, sourceOffset, info.Data.Size, plan.LinearSize, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(plan.Tiles));
            }

            if (plan.SwapBgra16)
            {
                linear = _tiler.SwapBgra16(linear);
            }

            UploadRegions(image, plan.Regions, linear);
            return;
        }

        if (info.Samples != 1 || image.Backing.Samples != 1 || info.Resources.Layers == 0 || info.Data.Size % info.Resources.Layers != 0 ||
            GuestPixelFormats.BytesPerElement(info.GuestFormat) != info.BytesPerBlock)
        {
            throw SubmissionScheduler.Fatal(
                $"The depth upload is invalid: address=0x{info.Data.Address:X16} size=0x{info.Data.Size:X} layers={info.Resources.Layers} samples={info.Samples} backingSamples={image.Backing.Samples} guestFormat={(uint)info.GuestFormat} bytesPerBlock={info.BytesPerBlock}.");
        }

        var block = DepthBlock(info);
        var layers = info.Resources.Layers;
        var fullSliceSize = info.Data.Size / layers;
        var copies = DepthCopies(info, fullSliceSize);
        var depthLinear = new TilerBufferSpan(source.Handle, sourceOffset, source.Size - sourceOffset);
        if (info.TileMode != GuestTileMode.Linear)
        {
            var tiles = DepthTileTransfers(info, block, fullSliceSize);
            depthLinear = _tiler.Detile(source.Handle, sourceOffset, info.Data.Size, info.Data.Size, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tiles));
        }

        var transferBytes = DepthFormatRule.AspectTransferBytes(info.PixelFormat);
        if (transferBytes != info.BytesPerBlock)
        {
            var texelsPerSlice = (ulong)info.Pitch * info.Extent.Height;
            if (info.BytesPerBlock != sizeof(ushort) || transferBytes != sizeof(uint) || texelsPerSlice > uint.MaxValue || texelsPerSlice > ulong.MaxValue / transferBytes)
            {
                throw SubmissionScheduler.Fatal($"The depth upload conversion is not supported: bytesPerBlock={info.BytesPerBlock} transferBytes={transferBytes} texelsPerSlice={texelsPerSlice}.");
            }

            var transferSlice = texelsPerSlice * transferBytes;
            if (transferSlice > ulong.MaxValue / layers)
            {
                throw SubmissionScheduler.Fatal($"The depth upload conversion size overflows: transferSlice={transferSlice} layers={layers}.");
            }

            var widened = _tiler.GetScratchBuffer(transferSlice * layers);
            _tiler.ConvertDepth16(depthLinear, widened, DepthConversionDirection.Widen, info.PixelFormat == Format.D32SfloatS8Uint, new DepthConversionLayout
            {
                Width = info.Extent.Width,
                Height = info.Extent.Height,
                Layers = layers,
                SourceRowStride = (ulong)info.Pitch * sizeof(ushort),
                TargetRowStride = (ulong)info.Pitch * sizeof(uint),
                SourceSliceStride = fullSliceSize,
                TargetSliceStride = transferSlice,
            });
            depthLinear = widened;
            for (uint layer = 0; layer < layers; layer++)
            {
                copies[layer].BufferOffset = transferSlice * layer;
            }
        }

        UploadRegions(image, copies.ToList(), depthLinear);
    }

    // Uploads the guest bytes when the guest or a buffer owns them; watches the image first.
    private void PopulateFromGuest(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, string uploadPath)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageUpload);
        var image = _slots[imageIdentifier];
        if (ImageDescription.IsEmptyRange(image.Description.Data))
        {
            return;
        }

        var measureUpload = RenderPhaseProfile.ImageUploadDetailsEnabled;
        var watchStarted = measureUpload ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        WatchImage(imageIdentifier);
        var watchFinished = measureUpload ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (image.Description.Metadata.Compression != DisplayCompression.Uncompressed)
        {
            if (image.IsCpuDirty)
            {
                image.RefreshComplete();
            }

            return;
        }

        if (image.Description.Samples > 1)
        {
            return;
        }

        var dataImported = false;
        var upload = image.IsBufferModified || image.IsCpuDirty;
        if (upload)
        {
            var reason = image.IsBufferModified
                ? (image.IsCpuDirty ? "buffer-and-cpu-dirty" : "buffer-dirty")
                : (image.IsMaybeCpuDirty ? "maybe-cpu-dirty" : "cpu-dirty");
            var sourceStarted = measureUpload ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var (source, sourceOffset) = _bufferCache.ObtainBufferForImage(image.Description.Data.Address, image.Description.Data.Size);
            var sourceFinished = measureUpload ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            dataImported = true;
            UploadFromBuffer(image, request, source, sourceOffset);
            if (measureUpload)
            {
                RenderPhaseProfile.RecordImageUpload(image.Description, reason,
                    watchFinished - watchStarted, sourceFinished - sourceStarted,
                    System.Diagnostics.Stopwatch.GetTimestamp() - sourceFinished,
                    uploadPath, image.LastCpuWriteAddress, image.LastCpuWriteSize);
            }
        }

        if (dataImported)
        {
            image.ClearBufferModified();
        }

        if (image.IsCpuDirty)
        {
            image.RefreshComplete();
        }
    }

    // A maybe-dirty image resolves through its edge hash first; a dirty one is populated again.
    private void RefreshFromGuest(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageRefresh);
        WatchImage(imageIdentifier);
        var image = _slots[imageIdentifier];
        if (image.IsMaybeCpuDirty)
        {
            var hash = image.HashGuestEdges();
            if (image.NeedsMaybeCpuHash)
            {
                image.SetMaybeCpuHash(hash);
                return;
            }

            _ = image.ResolveMaybeCpuHash(hash);
        }

        var cpuDirty = image.IsBufferModified || image.IsDefinitelyCpuDirty;
        if (image.Description.Metadata.Compression != DisplayCompression.Uncompressed)
        {
            if (cpuDirty)
            {
                throw SubmissionScheduler.Fatal($"A compressed guest image cannot be refreshed from guest memory: address=0x{image.Description.Data.Address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsDefinitelyCpuDirty}.");
            }

            return;
        }

        if (!cpuDirty)
        {
            return;
        }

        PopulateFromGuest(imageIdentifier, request, "refresh");
    }

    private void DownloadDepthToBuffer(CachedImage image, GpuBuffer destination, ulong destinationOffset)
    {
        ref readonly var info = ref image.Description;
        var layers = info.Resources.Layers;
        var fullSliceSize = info.Data.Size / layers;
        var transferBytes = DepthFormatRule.AspectTransferBytes(info.PixelFormat);
        var texelsPerSlice = (ulong)info.Pitch * info.Extent.Height;
        if (transferBytes == 0 || texelsPerSlice > uint.MaxValue || texelsPerSlice > ulong.MaxValue / transferBytes || texelsPerSlice > ulong.MaxValue / info.BytesPerBlock)
        {
            throw SubmissionScheduler.Fatal($"The depth readback layout is not supported: transferBytes={transferBytes} texelsPerSlice={texelsPerSlice} bytesPerBlock={info.BytesPerBlock}.");
        }

        var transferSlice = texelsPerSlice * transferBytes;
        var guestSlice = texelsPerSlice * info.BytesPerBlock;
        if (transferSlice > ulong.MaxValue / layers)
        {
            throw SubmissionScheduler.Fatal($"The depth readback size overflows: transferSlice={transferSlice} layers={layers}.");
        }

        var transferSize = transferSlice * layers;
        if (guestSlice > fullSliceSize)
        {
            throw SubmissionScheduler.Fatal($"The depth readback slice exceeds the guest slice: guestSlice={guestSlice} fullSlice={fullSliceSize}.");
        }

        var copies = DepthCopies(info, fullSliceSize);
        if (transferBytes == info.BytesPerBlock)
        {
            if (!info.IsTiled)
            {
                for (var index = 0; index < copies.Length; index++)
                {
                    copies[index].BufferOffset += destinationOffset;
                }

                image.DownloadToBuffer(copies, destination.Handle, destinationOffset, info.Data.Size);
                return;
            }

            var tiledTiles = DepthTileTransfers(info, DepthBlock(info), fullSliceSize);
            _tiler.TileImage(image, copies, destination.Handle, destinationOffset, info.Data.Size, info.Data.Size, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tiledTiles));
            return;
        }

        if (info.BytesPerBlock != sizeof(ushort) || transferBytes != sizeof(uint))
        {
            throw SubmissionScheduler.Fatal($"The depth readback conversion is not supported: bytesPerBlock={info.BytesPerBlock} transferBytes={transferBytes}.");
        }

        for (uint layer = 0; layer < layers; layer++)
        {
            copies[layer].BufferOffset = transferSlice * layer;
        }

        var hostLinear = _tiler.GetScratchBuffer(transferSize);
        image.DownloadToBuffer(copies, hostLinear.Buffer, 0, hostLinear.Size);
        var tiled = info.IsTiled;
        var guestLinear = tiled ? _tiler.GetScratchBuffer(info.Data.Size) : new TilerBufferSpan(destination.Handle, destinationOffset, destination.Size - destinationOffset);
        _tiler.ConvertDepth16(hostLinear, guestLinear, DepthConversionDirection.Narrow, DepthFormatRule.AspectTransferFormat(info.PixelFormat) == Format.D32Sfloat, new DepthConversionLayout
        {
            Width = info.Extent.Width,
            Height = info.Extent.Height,
            Layers = layers,
            SourceRowStride = (ulong)info.Pitch * sizeof(uint),
            TargetRowStride = (ulong)info.Pitch * sizeof(ushort),
            SourceSliceStride = transferSlice,
            TargetSliceStride = fullSliceSize,
        });
        if (!tiled)
        {
            return;
        }

        var tiles = DepthTileTransfers(info, DepthBlock(info), fullSliceSize);
        _tiler.Tile(guestLinear.Buffer, guestLinear.Offset, info.Data.Size, destination.Handle, destinationOffset, info.Data.Size, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tiles));
    }

    private void DownloadToBuffer(CachedImage image, GpuBuffer destination, ulong destinationOffset, ulong destinationSize, ImageDownloadPlan plan)
    {
        if (!plan.Valid)
        {
            throw SubmissionScheduler.Fatal($"The image download plan is invalid: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X}.");
        }

        if (plan.Depth)
        {
            if (destinationSize != image.Description.Data.Size)
            {
                throw SubmissionScheduler.Fatal($"A partial depth image download is not supported: requested=0x{destinationSize:X} image=0x{image.Description.Data.Size:X}.");
            }

            DownloadDepthToBuffer(image, destination, destinationOffset);
            return;
        }

        var color = plan.Color;
        var swap = color.SwapBgra16 ? ColorChannelSwap.SwapBgra16 : ColorChannelSwap.None;
        if (!color.Tiled)
        {
            if (swap == ColorChannelSwap.SwapBgra16)
            {
                var linear = _tiler.GetScratchBuffer(destinationSize);
                image.DownloadToBuffer(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(color.Regions), linear.Buffer, 0, linear.Size);
                _tiler.SwapBgra16(linear, new TilerBufferSpan(destination.Handle, destinationOffset, destinationSize));
                return;
            }

            for (var index = 0; index < color.Regions.Count; index++)
            {
                var copy = color.Regions[index];
                copy.BufferOffset += destinationOffset;
                color.Regions[index] = copy;
            }

            image.DownloadToBuffer(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(color.Regions), destination.Handle, destinationOffset, destinationSize);
            return;
        }

        _tiler.TileImage(image, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(color.Regions), destination.Handle, destinationOffset, destinationSize, color.LinearSize, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(color.Tiles), swap);
    }

    // Copies image data into the buffer without transferring GPU ownership to the buffer.
    public bool TrySynchronizeBufferFromImage(GpuBuffer buffer, ulong address, ulong size)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageDownload);
        using var held = _lock.Hold();
        var matches = new List<ResourceSlotIdentifier>();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null || owner.Description.Data.Address != address)
            {
                continue;
            }

            if (owner.DepthOwner.IsValid)
            {
                owner = _slots.TryGet(owner.DepthOwner);
            }

            if (owner != null && CanReadBack(owner))
            {
                matches.Add(imageIdentifier);
            }
        }

        var selected = ResourceSlotIdentifier.Invalid;
        if (matches.Count == 1)
        {
            selected = matches[0];
        }
        else
        {
            foreach (var imageIdentifier in matches)
            {
                if (_slots[imageIdentifier].Description.Data.Size == size)
                {
                    selected = imageIdentifier;
                    break;
                }
            }
        }

        if (!selected.IsValid)
        {
            return false;
        }

        if (_slots.TryGet(selected) is { DepthOwner.IsValid: true } proxy)
        {
            selected = proxy.DepthOwner;
        }

        var image = _slots[selected];
        ref readonly var info = ref image.Description;
        if (!buffer.IsInBounds(info.Data.Address, 1))
        {
            return false;
        }

        var bufferOffset = buffer.Offset(info.Data.Address);
        var available = buffer.Size - bufferOffset;
        uint levels = 0;
        ulong copySize = 0;
        if (info.IsVolume)
        {
            // Volume mips hold strided block slices; only a whole image proves every slice fits.
            if (!buffer.IsInBounds(info.Data.Address, info.Data.Size))
            {
                return false;
            }

            levels = info.Resources.Levels;
            copySize = info.Data.Size;
        }
        else
        {
            for (; levels < info.Resources.Levels; levels++)
            {
                var mip = info.MipLayout[(int)levels];
                if (mip.Size == 0 || mip.Offset > available || mip.Size > available - mip.Offset)
                {
                    break;
                }

                copySize = Math.Max(copySize, mip.Offset + mip.Size);
            }
        }

        if (copySize == 0)
        {
            return false;
        }

        var plan = PlanDownload(image);
        if (!plan.Valid || (plan.Depth && copySize != info.Data.Size))
        {
            return false;
        }

        if (!plan.Depth && levels < info.Resources.Levels)
        {
            var color = plan.Color;
            var limit = levels;
            color.Regions.RemoveAll(region => region.ImageSubresource.MipLevel >= limit);
            if (color.Regions.Count == 0)
            {
                return false;
            }

            if (color.Tiled)
            {
                if (!color.Layout.TryBuildTileTransfers(copySize, color.Regions, levels, out var tiles))
                {
                    return false;
                }

                color.Tiles = tiles;
                color.LinearSize = LinearSizeOf(color.Tiles);
            }
        }

        DownloadToBuffer(image, buffer, bufferOffset, copySize, plan);
        return true;
    }

    // Publishes a GPU-owned image to guest memory after its tick completes; false when it cannot.
    private bool TryDownloadToGuest(ResourceSlotIdentifier imageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageDownload);
        var image = _slots[imageIdentifier];
        if (image.DepthOwner.IsValid)
        {
            return false;
        }

        var plan = PlanDownload(image);
        if (!plan.Valid || !CanReadBack(image))
        {
            return false;
        }

        var range = image.Description.Data;
        var download = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Download);
        if (!download.TryMap(range.Size, out var offset, Math.Max(image.Description.BytesPerBlock, 4u)))
        {
            throw SubmissionScheduler.Fatal($"The reusable download ring cannot map the image: address=0x{range.Address:X16} size=0x{range.Size:X}.");
        }

        download.Commit();
        if (!_backing.TryReadBacking(range.Address, download.Mapped.Slice((int)offset, (int)range.Size)))
        {
            return false;
        }

        download.Flush(offset, range.Size);
        DownloadToBuffer(image, download, offset, range.Size, plan);
        var barrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = download.Handle,
            Offset = offset,
            Size = range.Size,
        };
        _scheduler.EndRendering();
        _device.Vk.CmdPipelineBarrier(new CommandBuffer(_scheduler.Current.Handle), PipelineStageFlags.AllCommandsBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &barrier, 0, null);
        var backing = _backing;
        _scheduler.QueuePriorityCompletionAction(() =>
        {
            download.Invalidate(offset, range.Size);
            if (!backing.TryWriteBacking(range.Address, download.Mapped.Slice((int)offset, (int)range.Size)))
            {
                throw SubmissionScheduler.Fatal($"The image readback could not be written to guest memory: address=0x{range.Address:X16} size=0x{range.Size:X}.");
            }
        });
        return true;
    }

    // Clears the one image that owns exactly this range; false when no single owner or clear value fits.
    public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The image clear is invalid: address=0x{address:X16} size=0x{size:X16} recording={!command.IsInvalid}.");
        }

        using var held = _lock.Hold();
        var selected = ResourceSlotIdentifier.Invalid;
        ImageAspectFlags aspect = 0;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null)
            {
                continue;
            }

            ImageAspectFlags candidate = 0;
            var candidateId = imageIdentifier;
            if (owner.DepthOwner.IsValid && owner.Description.Data.Address == address && owner.Description.Data.Size == size)
            {
                candidate = ImageAspectFlags.StencilBit;
                candidateId = owner.DepthOwner;
                owner = _slots.TryGet(candidateId);
                if (owner == null || !owner.Backing.Exists || !owner.Description.HasStencil)
                {
                    continue;
                }
            }
            else if (!owner.DepthOwner.IsValid && owner.Description.Data.Address == address && owner.Description.Data.Size == size)
            {
                candidate = owner.Description.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
            }

            if (candidate == 0)
            {
                continue;
            }

            if (selected.IsValid && selected != candidateId)
            {
                return false;
            }

            selected = candidateId;
            aspect = candidate;
        }

        if (!selected.IsValid)
        {
            return false;
        }

        var image = _slots[selected];
        var colorClear = default(ClearColorValue);
        var depthClear = 0.0f;
        byte stencilClear = 0;
        if (aspect == ImageAspectFlags.ColorBit)
        {
            if (!PackedClearValue.TryDecodeColor(image.Description.PixelFormat, packedClear, out colorClear))
            {
                return false;
            }
        }
        else if ((aspect == ImageAspectFlags.DepthBit && !PackedClearValue.TryDecodeDepth(image.Description.PixelFormat, packedClear, out depthClear)) ||
                 (aspect == ImageAspectFlags.StencilBit && !PackedClearValue.TryDecodeStencil(packedClear, out stencilClear)))
        {
            return false;
        }

        if (aspect == ImageAspectFlags.ColorBit)
        {
            // The full color clear replaces all texels. Keep the watch without copying old data.
            WatchImage(selected);
        }
        else if (image.IsBufferModified || image.IsCpuDirty)
        {
            PopulateFromGuest(selected, RefreshRequest(image), "before-clear");
            if (image.Description.Samples == 1 && (image.IsBufferModified || image.IsCpuDirty))
            {
                throw SubmissionScheduler.Fatal($"The image clear left guest ownership in place: address=0x{address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty}.");
            }
        }

        command.EndRendering();
        var native = new CommandBuffer(command.Handle);
        image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, native);
        var range = new ImageSubresourceRange(aspect, 0, Vk.RemainingMipLevels, 0, image.Backing.Layers);
        if (aspect == ImageAspectFlags.ColorBit)
        {
            _device.Vk.CmdClearColorImage(native, image.Backing.Handle, ImageLayout.TransferDstOptimal, &colorClear, 1, &range);
        }
        else
        {
            var clear = new ClearDepthStencilValue(depthClear, stencilClear);
            _device.Vk.CmdClearDepthStencilImage(native, image.Backing.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
        }

        TakeGpuOwnership(image);
        return true;
    }
}
