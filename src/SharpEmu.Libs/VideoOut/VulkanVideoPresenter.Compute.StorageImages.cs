// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

// This partial manages Vulkan storage-image bindings for compute dispatches.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private TextureResource ResolveStorageImageResource(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            if (!SupportsStorageImage(vkFormat))
            {
                throw new InvalidOperationException(
                    $"Storage image format {vkFormat} is unsupported for guest " +
                    $"format={texture.Format}/num={texture.NumberType}.");
            }
            var selectedMipLevel = GetStorageMipLevel(texture);
            var mipWidth = GetMipDimension(guestImage.Width, selectedMipLevel);
            var mipHeight = GetMipDimension(guestImage.Height, selectedMipLevel);
            var mipDepth = GetMipDimension(guestImage.Depth, selectedMipLevel);
            var view = GetOrCreateGuestImageIdentityView(
                guestImage,
                vkFormat,
                selectedMipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = mipWidth,
                Height = mipHeight,
                Depth = mipDepth,
                Type = guestImage.Type,
                RowLength = mipWidth,
                DstSelect = texture.DstSelect,
                MipLevel = selectedMipLevel,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };

            if (!guestImage.Initialized &&
                !guestImage.InitialUploadPending &&
                texture.MipLevel == 0)
            {
                var expectedSize = GetTextureByteCount(
                    texture.Format,
                    texture.Width,
                    texture.Height,
                    GetGuestTextureDepth(texture.Type, texture.Depth));
                if ((ulong)texture.RgbaPixels.Length == expectedSize &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    var uploadPixels = texture.Format == 13
                        ? ExpandRgb32Pixels(texture.RgbaPixels)
                        : texture.RgbaPixels;
                    var uploadSize = (ulong)uploadPixels.Length;
                    (resource.StagingBuffer, resource.StagingMemory) =
                        CreateTextureStagingBuffer(
                            uploadPixels,
                            $"{TextureDebugName(texture, guestImage.Format)} storage staging");
                    resource.NeedsUpload = true;
                    guestImage.InitialUploadPending = true;
                    TraceVulkanShader(
                        $"vk.storage_upload addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"logical_bytes={expectedSize} upload_bytes={uploadSize}");
                }
            }

            return resource;
        }

        private TextureResource CreateStorageScratchResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var vkFormat = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            if (!SupportsStorageImage(vkFormat))
            {
                throw new InvalidOperationException(
                    $"Storage scratch format {vkFormat} is unsupported for guest " +
                    $"format={texture.Format}/num={texture.NumberType}.");
            }
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = GetGuestTextureImageType(texture.Type),
                Format = vkFormat,
                Extent = new Extent3D(width, height, depth),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(storage scratch)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(storage scratch)");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                "vkBindImageMemory(storage scratch)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = GetGuestTextureViewType(texture.Type),
                Format = vkFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(storage scratch)");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat}");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat} view");

            var guestImage = new GuestImageResource
            {
                Address = 0,
                Width = width,
                Height = height,
                Depth = depth,
                Type = texture.Type,
                LogicalWidth = width,
                LogicalHeight = height,
                LogicalDepth = depth,
                MipLevels = 1,
                GuestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType),
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
                SupportsStorageUsage = true,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                Depth = depth,
                Type = texture.Type,
                RowLength = width,
                DstSelect = texture.DstSelect,
                OwnsStorage = true,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };
        }

        private GuestImageResource ResolveStorageGuestImage(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                throw new InvalidOperationException("Storage image has no guest address.");
            }

            var format = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            var guestImage = GetOrCreateGuestImage(
                new GuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.ResourceMipLevels,
                    TileMode: texture.TileMode),
                format,
                requiresStorage: true,
                texture.Type,
                GetGuestTextureDepth(texture.Type, texture.Depth));
            var selectedMipLevel = GetStorageMipLevel(texture);
            if (selectedMipLevel >= guestImage.MipLevels)
            {
                throw new InvalidOperationException(
                    $"Storage mip {selectedMipLevel} (base {texture.BaseMipLevel} + relative " +
                    $"{texture.MipLevel}) exceeds image mip count {guestImage.MipLevels}.");
            }

            return guestImage;
        }

        private static uint GetStorageMipLevel(GuestDrawTexture texture)
        {
            // IMAGE_STORE targets BASE_LEVEL and IMAGE_STORE_MIP's operand is
            // expressed in resource-view space, so Vulkan's absolute image
            // subresource is descriptor base plus the instruction-relative
            // mip. Sampled views achieve the same mapping through their view
            // base in ResolveTextureResource.
            var selectedMipLevel = (ulong)texture.BaseMipLevel + texture.MipLevel;
            if (selectedMipLevel > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Storage mip overflow (base {texture.BaseMipLevel} + relative {texture.MipLevel}).");
            }

            return (uint)selectedMipLevel;
        }




        private void RecordStorageImagesForWrite(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    OldLayout =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    guestImage.Initialized || guestImage.InitialUploadPending
                        ? shaderStage
                        : PipelineStageFlags.TopOfPipeBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void RecordStorageImagesForRead(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    shaderStage,
                    PipelineStageFlags.AllCommandsBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void MarkStorageImagesInitialized(
            TranslatedDrawResources resources,
            bool traceContents = true)
        {
            List<GuestImageResource>? traceImages = null;
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (guestImage.GuestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestImage.GuestFormat;
                    }

                    if (traceContents &&
                        ShouldTraceGuestImageContents(guestImage))
                    {
                        traceImages ??= [];
                        traceImages.Add(guestImage);
                    }
                }
            }

            if (traceImages is null)
            {
                return;
            }

            foreach (var image in traceImages)
            {
                TraceGuestImageContents(image);
            }
        }
    }
}
