// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct ImageAccessState(PipelineStageFlags Stage, AccessFlags Access, ImageLayout Layout)
{
    public static ImageAccessState Initial => new(PipelineStageFlags.AllCommandsBit, AccessFlags.None, ImageLayout.Undefined);
}

public struct ImageUses
{
    public bool Texture;
    public bool Storage;
    public bool RenderTarget;
    public bool DepthTarget;
    public bool VideoOut;
}

public struct ImageBindingState
{
    public bool IsBound;
    public bool IsTarget;
    public bool NeedsRebind;
    public bool ForceGeneral;
    public bool ShaderWrite;
}

public readonly record struct CachedImageView(ImageViewDescription Description, ImageView View);

// The host image behind a cached guest image and its per-subresource access state.
public sealed class ImageBacking
{
    public Format Format = Format.Undefined;
    public ImageType ImageType = ImageType.Type2D;
    public Extent3D Extent = new(1, 1, 1);
    public uint GuestPitch;
    public uint Layers = 1;
    public uint MipLevels = 1;
    public uint Samples = 1;
    public ImageUsageFlags Usage;
    public ImageCreateFlags Flags;
    public Image Handle;
    public ImageAccessState State = ImageAccessState.Initial;
    public List<ImageAccessState>? SubresourceStates;
    public DeviceMemory Memory;
    public ulong AllocationSize;

    public bool Exists => Handle.Handle != 0;
}

// One guest image resident on the host: description, backing, views and ownership flags.
public sealed unsafe partial class CachedImage : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly IGuestBackedSpace _guestBacking;
    private ulong _maybeCpuHash;
    private bool _cpuDirty;
    private bool _maybeCpuDirty;
    private bool _maybeHashValid;
    private bool _gpuModified;
    private bool _bufferModified;

    public ImageDescription Description;
    public readonly ImageBacking Backing = new();
    public readonly List<CachedImageView> Views = new();
    public ImageUses Uses;
    public ImageBindingState Binding;
    public bool Registered;
    public uint QueryEpoch;
    public ulong WatchBegin;
    public ulong WatchEnd;
    public ResourceSlotIdentifier DepthOwner;
    public ulong LastAccessTick;
    public int RecencyEntryIndex;

    public CachedImage(GpuDeviceInfo device, SubmissionScheduler scheduler, IGuestBackedSpace guestBacking, in ImageDescription description)
    {
        _device = device;
        _scheduler = scheduler;
        _guestBacking = guestBacking;
        Description = description;
        Description.Validate();
        _cpuDirty = !ImageDescription.IsEmptyRange(Description.Data) && Description.Metadata.Compression == DisplayCompression.Uncompressed;
        if (Description.PixelFormat == Format.Undefined)
        {
            return;
        }

        Backing.Format = Description.PixelFormat;
        Backing.ImageType = HostImageType(Description.Type);
        Backing.Extent = Description.Extent;
        Backing.GuestPitch = Description.Pitch;
        Backing.Layers = Description.IsVolume ? 1 : Description.Resources.Layers;
        Backing.MipLevels = Description.Resources.Levels;
        Backing.Samples = Description.Samples;
        Backing.Flags = CreateFlags(Description);
        Backing.Usage = UsageFlags(device, Description);

        var create = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = Backing.Flags,
            ImageType = Backing.ImageType,
            Extent = Backing.Extent,
            MipLevels = Backing.MipLevels,
            ArrayLayers = Backing.Layers,
            Format = Backing.Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = Backing.State.Layout,
            Usage = Backing.Usage,
            SharingMode = SharingMode.Exclusive,
            Samples = ImageDescription.VulkanSampleCount(Backing.Samples),
        };
        if (!TrySelectSupportedImageConfiguration(device, ref create, allowCompressedImageFallback: OperatingSystem.IsMacOS()))
        {
            throw SubmissionScheduler.Fatal(
                $"The image format does not support the required usage: format={(int)create.Format} type={(int)create.ImageType} usage=0x{(uint)create.Usage:x} flags=0x{(uint)create.Flags:x} samples={Backing.Samples}.");
        }

        Backing.Flags = create.Flags;
        Backing.Usage = create.Usage;

        var vk = device.Vk;
        if (vk.CreateImage(device.Device, &create, null, out Backing.Handle) != Result.Success)
        {
            throw CreateFailure(create);
        }

        vk.GetImageMemoryRequirements(device.Device, Backing.Handle, out var requirements);
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
        };
        var allocated = Result.ErrorOutOfDeviceMemory;
        for (uint index = 0; index < device.MemoryTypeCount; index++)
        {
            if ((requirements.MemoryTypeBits & (1u << (int)index)) == 0 ||
                (device.GetMemoryTypeFlags(index) & MemoryPropertyFlags.DeviceLocalBit) == 0)
            {
                continue;
            }

            allocateInfo.MemoryTypeIndex = index;
            allocated = device.AllocateMemory(allocateInfo, out Backing.Memory);
            if (allocated == Result.Success)
            {
                break;
            }
        }

        if (allocated != Result.Success || vk.BindImageMemory(device.Device, Backing.Handle, Backing.Memory, 0) != Result.Success)
        {
            vk.DestroyImage(device.Device, Backing.Handle, null);
            device.FreeMemory(Backing.Memory);
            Backing.Handle = default;
            Backing.Memory = default;
            throw CreateFailure(create);
        }

        Backing.AllocationSize = requirements.Size;
    }

    private static Exception CreateFailure(in ImageCreateInfo create) =>
        SubmissionScheduler.Fatal(
            $"The image could not be created: extent={create.Extent.Width}x{create.Extent.Height}x{create.Extent.Depth} format={(int)create.Format} layers={create.ArrayLayers} levels={create.MipLevels}.");

    internal static bool TrySelectSupportedImageConfiguration(IImageFormatSupport device, ref ImageCreateInfo configuration, bool allowCompressedImageFallback)
    {
        static bool SupportsImageConfiguration(IImageFormatSupport device, in ImageCreateInfo configuration) =>
            device.TryGetImageFormatProperties(configuration.Format, configuration.ImageType, configuration.Tiling, configuration.Usage, configuration.Flags, out var properties) &&
            (properties.SampleCounts & configuration.Samples) != 0;

        if (SupportsImageConfiguration(device, configuration))
        {
            return true;
        }

        if (!allowCompressedImageFallback || (configuration.Flags & ImageCreateFlags.CreateBlockTexelViewCompatibleBit) == 0)
        {
            return false;
        }

        // Remove block views and their storage usage when MoltenVK rejects them.
        // Use the new configuration only if the device supports it.
        var fallbackConfiguration = configuration;
        fallbackConfiguration.Flags &= ~ImageCreateFlags.CreateBlockTexelViewCompatibleBit;
        fallbackConfiguration.Usage &= ~ImageUsageFlags.StorageBit;
        if (!SupportsImageConfiguration(device, fallbackConfiguration))
        {
            return false;
        }

        configuration = fallbackConfiguration;
        return true;
    }

    private static ImageType HostImageType(GuestImageType type) => type switch
    {
        GuestImageType.Color1D => ImageType.Type1D,
        GuestImageType.Color3D => ImageType.Type3D,
        GuestImageType.Color2D => ImageType.Type2D,
        _ => throw SubmissionScheduler.Fatal($"The image type is not a base type: type={(uint)type}."),
    };

    private static ImageCreateFlags CreateFlags(in ImageDescription description)
    {
        ImageCreateFlags flags = 0;
        if (DepthFormatRule.AspectTransferFormat(description.PixelFormat) == Format.Undefined)
        {
            flags |= ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit;
            if (GuestPixelFormats.BlockCompressedBytes(description.GuestFormat) != 0)
            {
                flags |= ImageCreateFlags.CreateBlockTexelViewCompatibleBit;
            }
        }

        if (description.IsVolume)
        {
            flags |= ImageCreateFlags.Create2DArrayCompatibleBit;
        }
        else if (description.Type == GuestImageType.Color2D &&
                 description.Samples == 1 &&
                 description.Extent.Depth == 1 &&
                 description.Extent.Width == description.Extent.Height &&
                 description.Resources.Layers >= 6 &&
                 description.Resources.Layers % 6 == 0)
        {
            flags |= ImageCreateFlags.CreateCubeCompatibleBit;
        }

        return flags;
    }

    private static ImageUsageFlags UsageFlags(GpuDeviceInfo device, in ImageDescription description)
    {
        var features = device.GetFormatProperties(description.PixelFormat).OptimalTilingFeatures;
        var usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        if ((features & FormatFeatureFlags.SampledImageBit) != 0)
        {
            usage |= ImageUsageFlags.SampledBit;
        }

        if (DepthFormatRule.AspectTransferFormat(description.PixelFormat) != Format.Undefined)
        {
            return usage | ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if ((features & FormatFeatureFlags.ColorAttachmentBit) != 0)
        {
            usage |= ImageUsageFlags.ColorAttachmentBit;
        }

        var storageFormat = ViewFormatRules.SrgbStorageFormat(description.PixelFormat);
        var storageFeatures = storageFormat == Format.Undefined ? features : device.GetFormatProperties(storageFormat).OptimalTilingFeatures;
        if (description.Samples == 1 && (storageFeatures & FormatFeatureFlags.StorageImageBit) != 0)
        {
            usage |= ImageUsageFlags.StorageBit;
        }

        return usage;
    }

    public void AssociateDepth(ResourceSlotIdentifier depthImage) => DepthOwner = depthImage;

    internal ulong LastCpuWriteAddress { get; private set; }
    internal ulong LastCpuWriteSize { get; private set; }

    // A byte overlap makes the image definitely dirty; a page-only overlap makes it maybe dirty.
    public void InvalidateCpuWrite(ulong address, ulong size)
    {
        if (GuestRangeOverlap.Bytes(Description.Data.Address, Description.Data.Size, address, size))
        {
            if (SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled)
            {
                LastCpuWriteAddress = address;
                LastCpuWriteSize = size;
            }
            _cpuDirty = true;
            _maybeCpuDirty = false;
            _maybeHashValid = false;
        }
        else if (GuestRangeOverlap.Pages(Description.Data.Address, Description.Data.Size, address, size))
        {
            _maybeCpuDirty = true;
        }
    }

    public bool IsCpuDirty => _cpuDirty || _maybeCpuDirty;

    public bool IsDefinitelyCpuDirty => _cpuDirty;

    public bool IsMaybeCpuDirty => _maybeCpuDirty;

    public void MarkMaybeCpuDirty()
    {
        if (!_cpuDirty)
        {
            _maybeCpuDirty = true;
        }
    }

    public bool NeedsMaybeCpuHash => _maybeCpuDirty && !_maybeHashValid;

    public void SetMaybeCpuHash(ulong hash)
    {
        if (!NeedsMaybeCpuHash)
        {
            throw SubmissionScheduler.Fatal("The image cannot initialize a maybe-dirty hash in its current state.");
        }

        _maybeCpuHash = hash;
        _maybeHashValid = true;
    }

    public bool ResolveMaybeCpuHash(ulong hash)
    {
        if (!_maybeCpuDirty || !_maybeHashValid || _cpuDirty)
        {
            throw SubmissionScheduler.Fatal("The image cannot resolve a maybe-dirty hash in its current state.");
        }

        _maybeCpuDirty = false;
        _maybeHashValid = false;
        _cpuDirty |= hash != _maybeCpuHash;
        return _cpuDirty;
    }

    public void RefreshComplete()
    {
        if (!IsCpuDirty)
        {
            throw SubmissionScheduler.Fatal("A clean image cannot complete a refresh.");
        }

        _cpuDirty = false;
        _maybeCpuDirty = false;
        _maybeHashValid = false;
        LastCpuWriteAddress = 0;
        LastCpuWriteSize = 0;
    }

    public bool IsGpuModified => _gpuModified;

    public void MarkGpuModified() => _gpuModified = true;

    public void ClearGpuModified() => _gpuModified = false;

    public bool IsBufferModified => _bufferModified;

    public void MarkBufferModified() => _bufferModified = true;

    public void ClearBufferModified() => _bufferModified = false;

    public bool Overlaps(ulong address, ulong size, bool pages = false) => pages
        ? GuestRangeOverlap.Pages(Description.Data.Address, Description.Data.Size, address, size)
        : GuestRangeOverlap.Bytes(Description.Data.Address, Description.Data.Size, address, size);

    public bool GpuOverlaps(ulong address, ulong size) => IsGpuModified && Overlaps(address, size);

    public bool SafeToDownload => IsGpuModified && !IsBufferModified && !IsCpuDirty;

    public bool IsWatched => WatchBegin != 0 && WatchEnd != 0;

    public ulong AccountedSize => Backing.Exists ? (Description.Data.Size + 1023) & ~1023UL : 0;

    // Hashes the first and last partial tracker page of the guest data through the backing alias.
    public ulong HashGuestEdges()
    {
        const ulong pageMask = TrackerLayout.PageBytes - 1;
        var range = Description.Data;
        var end = range.End;
        var headEnd = Math.Min(end, (range.Address + pageMask) & ~pageMask);
        var tailBegin = Math.Max(range.Address, end & ~pageMask);
        var headSize = headEnd - range.Address;
        var tailAddress = tailBegin < headEnd ? headEnd : tailBegin;
        var tailSize = end - tailAddress;
        var bytes = new byte[headSize + tailSize];
        if ((headSize != 0 && !_guestBacking.TryReadBacking(range.Address, bytes.AsSpan(0, (int)headSize))) ||
            (tailSize != 0 && !_guestBacking.TryReadBacking(tailAddress, bytes.AsSpan((int)headSize, (int)tailSize))))
        {
            throw SubmissionScheduler.Fatal($"The guest backing of the image could not be read for hashing: address=0x{range.Address:X16} size=0x{range.Size:X}.");
        }

        return XxHash3.HashToUInt64(bytes);
    }

    internal bool SupportsViewType(in ImageViewDescription view) => IsValidViewType(Backing, view);

    private static bool IsValidViewType(ImageBacking image, in ImageViewDescription view)
    {
        switch (image.ImageType)
        {
            case ImageType.Type1D:
                if (view.Type is not (ImageViewType.Type1D or ImageViewType.Type1DArray))
                {
                    return false;
                }

                return view.Type != ImageViewType.Type1D || view.LayerCount == 1;
            case ImageType.Type2D:
                return view.Type switch
                {
                    ImageViewType.Type2D => view.LayerCount == 1,
                    ImageViewType.Type2DArray => true,
                    ImageViewType.TypeCube => (image.Flags & ImageCreateFlags.CreateCubeCompatibleBit) != 0 && view.BaseLayer % 6 == 0 && view.LayerCount == 6,
                    ImageViewType.TypeCubeArray => (image.Flags & ImageCreateFlags.CreateCubeCompatibleBit) != 0 && view.BaseLayer % 6 == 0 && view.LayerCount % 6 == 0,
                    _ => false,
                };
            case ImageType.Type3D:
                return view.Type switch
                {
                    ImageViewType.Type3D => view.BaseLayer == 0 && view.LayerCount == 1,
                    ImageViewType.Type2D => (image.Flags & ImageCreateFlags.Create2DArrayCompatibleBit) != 0 && view.LevelCount == 1 && view.LayerCount == 1,
                    ImageViewType.Type2DArray => (image.Flags & ImageCreateFlags.Create2DArrayCompatibleBit) != 0 && view.LevelCount == 1,
                    _ => false,
                };
            default:
                return false;
        }
    }

    private static bool IsValidAspect(ImageBacking image, ImageAspectFlags aspect)
    {
        if (DepthFormatRule.AspectTransferFormat(image.Format) == Format.Undefined)
        {
            return aspect == ImageAspectFlags.ColorBit;
        }

        var supported = ViewFormatRules.DepthAspects(image.Format);
        return aspect != 0 && (aspect & ~supported) == 0;
    }

    // Returns the cached view for the normalized description, creating it on first use.
    public ImageView GetOrCreateView(in ImageViewDescription requested)
    {
        var image = Backing;
        var normalized = requested;
        var isStorage = (normalized.Usage & ImageUsageFlags.StorageBit) != 0;
        var imageAspect = ViewFormatRules.FullAspects(image.Format);
        if ((imageAspect & ImageAspectFlags.DepthBit) != 0 && ViewFormatRules.IsDepthCompatible(normalized.Format))
        {
            normalized = normalized with { Format = image.Format, Aspect = ImageAspectFlags.DepthBit };
        }

        if ((imageAspect & ImageAspectFlags.StencilBit) != 0 && ViewFormatRules.IsStencilViewFormat(normalized.Format))
        {
            normalized = normalized with { Format = image.Format, Aspect = ImageAspectFlags.StencilBit };
        }

        normalized = normalized with { Usage = isStorage ? ImageUsageFlags.StorageBit : 0 };
        var formatCompatible = normalized.Format != Format.Undefined && ViewFormatRules.AreImageViewFormatsCompatible(image.Format, normalized.Format, image.Flags);
        var usageValid = !isStorage || (image.Usage & ImageUsageFlags.StorageBit) != 0;
        var sliceView = image.ImageType == ImageType.Type3D && normalized.Type is ImageViewType.Type2D or ImageViewType.Type2DArray;
        var levelsValid = normalized.LevelCount != 0 && normalized.BaseLevel < image.MipLevels && normalized.LevelCount <= image.MipLevels - normalized.BaseLevel;
        var viewLayers = sliceView && levelsValid ? Math.Max(image.Extent.Depth >> (int)normalized.BaseLevel, 1) : image.Layers;
        var rangesValid = levelsValid && normalized.LayerCount != 0 && normalized.BaseLayer < viewLayers && normalized.LayerCount <= viewLayers - normalized.BaseLayer;
        var mappingValid = ViewFormatRules.IsComponentSwizzle(normalized.Mapping.R) && ViewFormatRules.IsComponentSwizzle(normalized.Mapping.G) &&
                           ViewFormatRules.IsComponentSwizzle(normalized.Mapping.B) && ViewFormatRules.IsComponentSwizzle(normalized.Mapping.A);
        var typeValid = IsValidViewType(image, normalized);
        var aspectValid = IsValidAspect(image, normalized.Aspect);
        if (!image.Exists || !formatCompatible || !usageValid || !rangesValid || !mappingValid || !typeValid || !aspectValid)
        {
            throw SubmissionScheduler.Fatal(
                $"The image view is invalid: imageFormat={(int)image.Format} viewFormat={(int)normalized.Format} type={(int)normalized.Type} aspect=0x{(uint)normalized.Aspect:x} " +
                $"mip={normalized.BaseLevel}+{normalized.LevelCount} layer={normalized.BaseLayer}+{normalized.LayerCount} usage=0x{(uint)normalized.Usage:x} imageLevels={image.MipLevels} imageLayers={image.Layers} " +
                $"address=0x{Description.Data.Address:X16} image=0x{image.Handle.Handle:X16} imageType={(int)image.ImageType} imageFlags=0x{(uint)image.Flags:x} imageUsage=0x{(uint)image.Usage:x} " +
                $"mapping={(int)normalized.Mapping.R},{(int)normalized.Mapping.G},{(int)normalized.Mapping.B},{(int)normalized.Mapping.A} " +
                $"exists={image.Exists} formatValid={formatCompatible} usageValid={usageValid} rangesValid={rangesValid} mappingValid={mappingValid} typeValid={typeValid} aspectValid={aspectValid}.");
        }

        foreach (var cached in Views)
        {
            if (cached.Description.Equals(normalized))
            {
                return cached.View;
            }
        }

        var usage = new ImageViewUsageCreateInfo
        {
            SType = StructureType.ImageViewUsageCreateInfo,
            Usage = isStorage ? image.Usage : image.Usage & ~ImageUsageFlags.StorageBit,
        };
        var create = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            PNext = &usage,
            Image = image.Handle,
            ViewType = normalized.Type,
            Format = normalized.Format,
            Components = normalized.Mapping,
            SubresourceRange = new ImageSubresourceRange(normalized.Aspect, normalized.BaseLevel, normalized.LevelCount, normalized.BaseLayer, normalized.LayerCount),
        };
        var result = _device.Vk.CreateImageView(_device.Device, &create, null, out var view);
        if (result != Result.Success || view.Handle == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"vkCreateImageView failed: result={(int)result} imageFormat={(int)image.Format} viewFormat={(int)requested.Format} type={(int)requested.Type} aspect=0x{(uint)requested.Aspect:x} " +
                $"mip={requested.BaseLevel}+{requested.LevelCount} layer={requested.BaseLayer}+{requested.LayerCount} usage=0x{(uint)requested.Usage:x}.");
        }

        Views.Add(new CachedImageView(normalized, view));
        return view;
    }

    // Immediate destruction; the owner waits for GPU work before disposing.
    public void Dispose()
    {
        foreach (var cached in Views)
        {
            _device.Vk.DestroyImageView(_device.Device, cached.View, null);
        }

        Views.Clear();
        if (Backing.Exists)
        {
            _device.Vk.DestroyImage(_device.Device, Backing.Handle, null);
            _device.FreeMemory(Backing.Memory);
            Backing.Handle = default;
            Backing.Memory = default;
        }
    }
}
