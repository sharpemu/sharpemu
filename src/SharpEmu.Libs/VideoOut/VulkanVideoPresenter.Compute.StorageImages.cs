// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

// This partial manages Vulkan storage-image bindings for compute dispatches.
internal static unsafe partial class VulkanVideoPresenter
{
    internal enum StorageImageComponentKind
    {
        Float,
        Sint,
        Uint,
    }

    internal readonly record struct SpirvStorageImageContract(
        SpirvImageFormat Format,
        StorageImageComponentKind ComponentKind,
        SpirvImageDim Dimension);

    internal static bool TryReadSpirvStorageImageContracts(
        ReadOnlySpan<byte> spirv,
        out SpirvStorageImageContract[] contracts,
        out string error)
    {
        contracts = [];
        error = string.Empty;
        if (spirv.Length < 5 * sizeof(uint) ||
            BinaryPrimitives.ReadUInt32LittleEndian(spirv) != 0x07230203u)
        {
            error = "invalid-spirv-header";
            return false;
        }

        var componentTypes = new Dictionary<uint, StorageImageComponentKind>();
        var storageImageTypes = new Dictionary<uint, SpirvStorageImageContract>();
        var uniformConstantPointers = new Dictionary<uint, uint>();
        var uniformConstantVariables = new List<(uint Id, uint PointerType)>();
        var bindings = new Dictionary<uint, uint>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.Slice(offset, sizeof(uint)));
            var wordCount = checked((int)(instruction >> 16));
            var byteCount = checked(wordCount * sizeof(uint));
            if (wordCount == 0 || offset + byteCount > spirv.Length)
            {
                error = "invalid-spirv-instruction-size";
                return false;
            }

            switch ((SpirvOp)(instruction & 0xFFFFu))
            {
                case SpirvOp.TypeInt when wordCount >= 4:
                    componentTypes[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3) != 0
                            ? StorageImageComponentKind.Sint
                            : StorageImageComponentKind.Uint;
                    break;
                case SpirvOp.TypeFloat when wordCount >= 3:
                    componentTypes[ReadSpirvWord(spirv, offset, 1)] =
                        StorageImageComponentKind.Float;
                    break;
                case SpirvOp.TypeImage when wordCount >= 9 &&
                    ReadSpirvWord(spirv, offset, 7) == 2:
                    var imageType = ReadSpirvWord(spirv, offset, 1);
                    var componentType = ReadSpirvWord(spirv, offset, 2);
                    if (!componentTypes.TryGetValue(componentType, out var componentKind))
                    {
                        error = $"unknown-storage-component-type({componentType})";
                        return false;
                    }

                    storageImageTypes[imageType] = new SpirvStorageImageContract(
                        (SpirvImageFormat)ReadSpirvWord(spirv, offset, 8),
                        componentKind,
                        (SpirvImageDim)ReadSpirvWord(spirv, offset, 3));
                    break;
                case SpirvOp.TypePointer when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 2) ==
                    (uint)SpirvStorageClass.UniformConstant:
                    uniformConstantPointers[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3);
                    break;
                case SpirvOp.Variable when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 3) ==
                    (uint)SpirvStorageClass.UniformConstant:
                    uniformConstantVariables.Add((
                        ReadSpirvWord(spirv, offset, 2),
                        ReadSpirvWord(spirv, offset, 1)));
                    break;
                case SpirvOp.Decorate when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 2) ==
                    (uint)SpirvDecoration.Binding:
                    bindings[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3);
                    break;
            }

            offset += byteCount;
        }

        var result = new List<(uint Binding, SpirvStorageImageContract Contract)>();
        foreach (var variable in uniformConstantVariables)
        {
            if (!uniformConstantPointers.TryGetValue(variable.PointerType, out var imageType) ||
                !storageImageTypes.TryGetValue(imageType, out var contract))
            {
                continue;
            }

            if (!bindings.TryGetValue(variable.Id, out var binding) ||
                result.Any(entry => entry.Binding == binding))
            {
                error = $"invalid-storage-image-binding({variable.Id})";
                return false;
            }

            result.Add((binding, contract));
        }

        contracts = result
            .OrderBy(static entry => entry.Binding)
            .Select(static entry => entry.Contract)
            .ToArray();
        return true;
    }

    private static uint ReadSpirvWord(
        ReadOnlySpan<byte> spirv,
        int instructionOffset,
        int wordIndex) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            spirv.Slice(
                instructionOffset + wordIndex * sizeof(uint),
                sizeof(uint)));

    internal static bool TryValidateStorageImageContract(
        SpirvStorageImageContract shaderContract,
        uint guestFormat,
        uint guestNumberType,
        uint guestType,
        bool supportsStorage,
        out Format vulkanFormat,
        out string error)
    {
        vulkanFormat = Presenter.GetStorageImageFormat(
            Presenter.GetTextureFormat(guestFormat, guestNumberType));
        var guestComponentKind = guestNumberType switch
        {
            4 => StorageImageComponentKind.Uint,
            5 => StorageImageComponentKind.Sint,
            _ => StorageImageComponentKind.Float,
        };
        var guestDimension = IsGuestTexture3D(guestType)
            ? SpirvImageDim.Dim3D
            : SpirvImageDim.Dim2D;
        if (shaderContract.Dimension != guestDimension)
        {
            error = $"dimension-mismatch(spirv={shaderContract.Dimension}," +
                $"guest-type={guestType}/{guestDimension})";
            return false;
        }

        if (shaderContract.ComponentKind != guestComponentKind)
        {
            error = $"component-kind-mismatch(spirv={shaderContract.ComponentKind}," +
                $"guest={guestComponentKind})";
            return false;
        }

        if (shaderContract.Format != SpirvImageFormat.Unknown &&
            TryGetVulkanStorageImageFormat(shaderContract.Format, out var typedFormat) &&
            typedFormat != vulkanFormat)
        {
            error = $"typed-format-mismatch(spirv={shaderContract.Format}/{typedFormat}," +
                $"guest={guestFormat}/num={guestNumberType},vk={vulkanFormat})";
            return false;
        }

        if (shaderContract.Format != SpirvImageFormat.Unknown &&
            !TryGetVulkanStorageImageFormat(shaderContract.Format, out _))
        {
            error = $"unsupported-spirv-storage-format({shaderContract.Format})";
            return false;
        }

        if (!supportsStorage)
        {
            error = $"vulkan-storage-feature-missing(vk={vulkanFormat})";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetVulkanStorageImageFormat(
        SpirvImageFormat format,
        out Format vulkanFormat)
    {
        vulkanFormat = format switch
        {
            SpirvImageFormat.Rgba32f => Format.R32G32B32A32Sfloat,
            SpirvImageFormat.Rgba16f => Format.R16G16B16A16Sfloat,
            SpirvImageFormat.R32f => Format.R32Sfloat,
            SpirvImageFormat.Rgba8 => Format.R8G8B8A8Unorm,
            SpirvImageFormat.Rgba8Snorm => Format.R8G8B8A8SNorm,
            SpirvImageFormat.Rg32f => Format.R32G32Sfloat,
            SpirvImageFormat.Rg16f => Format.R16G16Sfloat,
            SpirvImageFormat.R11fG11fB10f => Format.B10G11R11UfloatPack32,
            SpirvImageFormat.R16f => Format.R16Sfloat,
            SpirvImageFormat.Rgba16 => Format.R16G16B16A16Unorm,
            SpirvImageFormat.Rgb10A2 => Format.A2B10G10R10UnormPack32,
            SpirvImageFormat.Rg16 => Format.R16G16Unorm,
            SpirvImageFormat.Rg8 => Format.R8G8Unorm,
            SpirvImageFormat.R16 => Format.R16Unorm,
            SpirvImageFormat.R8 => Format.R8Unorm,
            SpirvImageFormat.Rgba16Snorm => Format.R16G16B16A16SNorm,
            SpirvImageFormat.Rg16Snorm => Format.R16G16SNorm,
            SpirvImageFormat.Rg8Snorm => Format.R8G8SNorm,
            SpirvImageFormat.R16Snorm => Format.R16SNorm,
            SpirvImageFormat.R8Snorm => Format.R8SNorm,
            SpirvImageFormat.Rgba32i => Format.R32G32B32A32Sint,
            SpirvImageFormat.Rgba16i => Format.R16G16B16A16Sint,
            SpirvImageFormat.Rgba8i => Format.R8G8B8A8Sint,
            SpirvImageFormat.R32i => Format.R32Sint,
            SpirvImageFormat.Rg32i => Format.R32G32Sint,
            SpirvImageFormat.Rg16i => Format.R16G16Sint,
            SpirvImageFormat.Rg8i => Format.R8G8Sint,
            SpirvImageFormat.R16i => Format.R16Sint,
            SpirvImageFormat.R8i => Format.R8Sint,
            SpirvImageFormat.Rgba32ui => Format.R32G32B32A32Uint,
            SpirvImageFormat.Rgba16ui => Format.R16G16B16A16Uint,
            SpirvImageFormat.Rgba8ui => Format.R8G8B8A8Uint,
            SpirvImageFormat.R32ui => Format.R32Uint,
            SpirvImageFormat.Rgb10A2ui => Format.A2B10G10R10UintPack32,
            SpirvImageFormat.Rg32ui => Format.R32G32Uint,
            SpirvImageFormat.Rg16ui => Format.R16G16Uint,
            SpirvImageFormat.Rg8ui => Format.R8G8Uint,
            SpirvImageFormat.R16ui => Format.R16Uint,
            SpirvImageFormat.R8ui => Format.R8Uint,
            _ => Format.Undefined,
        };
        return vulkanFormat != Format.Undefined;
    }

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
