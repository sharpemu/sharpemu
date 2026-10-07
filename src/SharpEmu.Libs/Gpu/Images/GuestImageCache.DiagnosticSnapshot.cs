// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed unsafe partial class GuestImageCache
{
    internal void SaveDiagnosticImage(CachedImage image, string path)
    {
        var backing = image.Backing;
        var bytesPerPixel = backing.Format switch
        {
            Format.R16G16B16A16Sfloat => 8u,
            Format.R32Sfloat => 4u,
            Format.R16Sfloat => 2u,
            Format.R32G32B32A32Sfloat => 16u,
            _ => 0u,
        };
        var size = (ulong)backing.Extent.Width * backing.Extent.Height * backing.Extent.Depth * bytesPerPixel;
        if (bytesPerPixel == 0 || size == 0 || size > 128 * 1024 * 1024 ||
            backing.Samples != 1 || backing.Layers != 1 || backing.MipLevels != 1 || backing.SubresourceStates != null)
        {
            Console.Error.WriteLine($"[GPU][WARN] ImageSnapshot unsupported path={path} format={backing.Format} size={size} layers={backing.Layers} levels={backing.MipLevels} samples={backing.Samples}.");
            return;
        }
        var state = backing.State;
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            Address = $"0x{image.Description.Data.Address:X16}", Format = backing.Format.ToString(),
            backing.Extent.Width, backing.Extent.Height, backing.Extent.Depth,
            BytesPerPixel = bytesPerPixel, Size = size, Tick = _scheduler.CurrentTick,
        });
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, size);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = backing.Extent,
        };
        _scheduler.EndRendering();
        image.DownloadToBuffer([copy], download.Handle, 0, size);
        var command = new CommandBuffer(_scheduler.Current.Handle);
        image.Transition(state.Layout, state.Access, null, command);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.TransferBit,
            PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        // Keep the private copy until GPU completion. Do not publish it to guest memory.
        _scheduler.QueueCompletionAction(() =>
        {
            try
            {
                download.Invalidate(0, size);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path + ".bin", download.Mapped[..(int)size]);
                File.WriteAllText(path + ".json", manifest);
                Console.Error.WriteLine($"[GPU][TRACE] ImageSnapshot saved={path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[GPU][WARN] ImageSnapshot write failed: {exception.Message}");
            }
            finally { download.Dispose(); }
        });
    }
}
