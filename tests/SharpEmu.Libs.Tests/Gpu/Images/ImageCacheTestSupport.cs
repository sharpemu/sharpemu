// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Request builders and worker-thread wrappers shared by the image cache tests.
internal static class ImageCacheTestSupport
{
    public const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    // A linear single-level image request: the texture role with a sampled view.
    public static ImageRequest LinearRequest(ulong address, ulong size, Format format, GuestPixelFormat guestFormat, GuestImageType type, Extent3D extent, uint layers, uint bytesPerBlock, uint samples)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, size);
        description.PixelFormat = format;
        description.GuestFormat = guestFormat;
        description.Type = type;
        description.Extent = extent;
        description.Resources = new SubresourceCount(1, layers);
        description.Pitch = extent.Width;
        description.BytesPerBlock = bytesPerBlock;
        description.Samples = samples;
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = extent.Width, Height = extent.Height };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = type == GuestImageType.Color3D ? ImageViewType.Type3D : layers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Aspect = ImageAspectFlags.ColorBit,
            LayerCount = layers,
            Usage = ImageUsageFlags.SampledBit,
        };
        return new ImageRequest(description, view, ImageRole.Texture);
    }

    public static ImageRequest Color32(ulong address, uint width = 1) =>
        LinearRequest(address, width * 4, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(width, 1, 1), 1, 4, 1);

    public static ImageRequest AsDepthTarget(ImageRequest request, Format format)
    {
        request.Role = ImageRole.DepthTarget;
        request.Description.PixelFormat = format;
        request.View = request.View with { Format = format, Aspect = ImageAspectFlags.DepthBit, Usage = ImageUsageFlags.DepthStencilAttachmentBit };
        return request;
    }

    public static ImageRequest AsColorTarget(ImageRequest request)
    {
        request.Role = ImageRole.ColorTarget;
        request.View = request.View with { Usage = ImageUsageFlags.ColorAttachmentBit };
        return request;
    }

    public static ImageRequest AsStorage(ImageRequest request)
    {
        request.Role = ImageRole.StorageImage;
        request.View = request.View with { Usage = ImageUsageFlags.StorageBit };
        return request;
    }

    public static ResourceSlotIdentifier Find(this CacheHarness harness, ref ImageRequest request, bool exactFormat = false)
    {
        var local = request;
        var imageIdentifier = harness.Worker.Run(() => harness.Images.FindImage(ref local, exactFormat));
        request = local;
        return imageIdentifier;
    }

    // Finds the image and acquires the view its role needs, as the presenter does.
    public static ResourceSlotIdentifier Acquire(this CacheHarness harness, ref ImageRequest request, bool exactFormat = false)
    {
        var imageIdentifier = harness.Find(ref request, exactFormat);
        var local = request;
        harness.Worker.Run(() =>
        {
            _ = local.Role switch
            {
                ImageRole.ColorTarget => harness.Images.AcquireColorTargetView(imageIdentifier, local),
                ImageRole.DepthTarget => harness.Images.AcquireDepthTargetView(imageIdentifier, local),
                _ => harness.Images.AcquireTextureView(imageIdentifier, local),
            };
        });
        return imageIdentifier;
    }

    public static void MarkGpuWritten(this CacheHarness harness, ResourceSlotIdentifier imageIdentifier) => harness.Worker.Run(() => harness.Images.MarkGpuWritten(imageIdentifier));

    public static CachedImage Image(this CacheHarness harness, ResourceSlotIdentifier imageIdentifier) => harness.Images.GetImage(imageIdentifier);

    public static void Finish(this CacheHarness harness) => harness.Worker.Run(() =>
    {
        harness.Scheduler.Finish();
        harness.Scheduler.WaitForAllPriorityOperations();
    });

    public static byte[] Bytes(uint value) => BitConverter.GetBytes(value);

    public static byte[] Bytes(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 4), values[index]);
        }

        return bytes;
    }

    public static byte[] Bytes(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2), values[index]);
        }

        return bytes;
    }

    public static byte[] Bytes(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 4), values[index]);
        }

        return bytes;
    }

    public static uint ReadUInt32(this CacheHarness harness, ulong address) => BitConverter.ToUInt32(harness.Read(address, 4));

    // Reads the whole first level of every layer of an image through a download buffer.
    public static byte[] ReadImageBytes(this CacheHarness harness, CachedImage image, ImageAspectFlags aspect = ImageAspectFlags.ColorBit) => harness.Worker.Run(() =>
    {
        var extent = image.Backing.Extent;
        var layers = image.Backing.ImageType == ImageType.Type3D ? 1u : image.Backing.Layers;
        var bytesPerTexel = image.Description.BytesPerBlock;
        if (aspect == ImageAspectFlags.DepthBit)
        {
            bytesPerTexel = DepthFormatRule.AspectTransferBytes(image.Backing.Format);
        }

        var size = (ulong)extent.Width * extent.Height * extent.Depth * layers * bytesPerTexel;
        var aligned = (size + 3) & ~3UL;
        using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, aligned);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, layers),
            ImageExtent = extent,
        };
        image.DownloadToBuffer(new[] { copy }, download.Handle, 0, aligned);
        harness.Scheduler.Finish();
        download.Invalidate(0, aligned);
        return download.Mapped[..(int)size].ToArray();
    });

    // Reads a range of a device buffer obtained from the buffer cache.
    public static byte[] ReadBufferBytes(this CacheHarness harness, GpuBuffer buffer, ulong offset, ulong size) => harness.ReadBack(buffer, offset, size);

    // Copies a raw device range to the host; the caller is on the worker thread and the copy finishes the tick.
    public static unsafe byte[] CopyFromDevice(this CacheHarness harness, VkBuffer source, ulong offset, ulong size)
    {
        var vk = harness.Vulkan.Vk;
        var aligned = (size + 3) & ~3UL;
        using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, aligned);
        var command = new CommandBuffer(harness.Scheduler.Current.Handle);
        var before = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryWriteBit | AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.HostWriteBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = source,
            Offset = offset,
            Size = aligned,
        };
        VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, &before, 0, null);
        var region = new BufferCopy(offset, 0, aligned);
        vk.CmdCopyBuffer(command, source, download.Handle, 1, &region);
        var after = before with { Buffer = download.Handle, Offset = 0, SrcAccessMask = AccessFlags2.TransferWriteBit, DstAccessMask = AccessFlags2.HostReadBit };
        VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &after, 0, null);
        harness.Scheduler.Finish();
        download.Invalidate(0, aligned);
        return download.Mapped[..(int)size].ToArray();
    }

    public static List<ResourceSlotIdentifier> ImagesInRange(this CacheHarness harness, ulong address, ulong size, bool pageOverlap = false) =>
        harness.Images.FindImagesInRangeForTest(address, size, pageOverlap);

    // The stencil proxy that starts at the address, when one exists.
    public static ResourceSlotIdentifier ProxyAt(this CacheHarness harness, ulong address, ulong size)
    {
        foreach (var imageIdentifier in harness.ImagesInRange(address, size))
        {
            var owner = harness.Images.Owner(imageIdentifier);
            if (owner != null && owner.Description.Data.Address == address && owner.DepthOwner.IsValid)
            {
                return imageIdentifier;
            }
        }

        return ResourceSlotIdentifier.Invalid;
    }

    public static bool WriteFault(this CacheHarness harness, ulong address) => harness.Gpu.TryResolveFault(FaultKind.Write, address);

    public static bool ReadFault(this CacheHarness harness, ulong address) => harness.Gpu.TryResolveFault(FaultKind.Read, address);
}
