// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed unsafe partial class GuestImageCache
{
    internal void SaveDiagnosticImage(CachedImage image, string path, bool pssrCapture = false)
    {
        var backing = image.Backing;
        var depthFormat = pssrCapture ? DepthFormatRule.AspectTransferFormat(backing.Format) : Format.Undefined;
        var depth = depthFormat != Format.Undefined;
        var format = depth ? depthFormat : backing.Format;
        var bytesPerPixel = depth ? DepthFormatRule.AspectTransferBytes(backing.Format) : format switch
        {
            Format.R16G16B16A16Sfloat => 8u,
            Format.R32Sfloat => 4u,
            Format.R16G16Sfloat or Format.B10G11R11UfloatPack32 or Format.E5B9G9R9UfloatPack32 or
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb when pssrCapture => 4u,
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
        if (pssrCapture && (backing.State.Layout != ImageLayout.General || (backing.Usage & ImageUsageFlags.TransferSrcBit) == 0))
        {
            Console.Error.WriteLine($"[GPU][WARN] ImageSnapshot unsupported path={path} layout={backing.State.Layout} usage={backing.Usage}.");
            return;
        }

        var state = backing.State;
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            Address = $"0x{image.Description.Data.Address:X16}", Format = format.ToString(),
            backing.Extent.Width, backing.Extent.Height, backing.Extent.Depth,
            BytesPerPixel = bytesPerPixel, Size = size, Tick = _scheduler.CurrentTick,
        });
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, size);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = backing.Extent,
        };
        _scheduler.EndRendering();
        CommandBuffer command;
        var destinationStages = PipelineStageFlags.HostBit;
        if (pssrCapture)
        {
            command = new CommandBuffer(_scheduler.Current.Handle);
            // PSSR inputs are already in shader-read state. Make prior writes visible
            // to the copy while preserving the state expected by the upscaler.
            var toTransfer = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
            };
            VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit, 0, 1, &toTransfer, 0, null, 0, null);
            _device.Vk.CmdCopyImageToBuffer(command, backing.Handle, ImageLayout.General, download.Handle, 1, &copy);
            destinationStages |= PipelineStageFlags.AllCommandsBit;
        }
        else
        {
            image.DownloadToBuffer([copy], download.Handle, 0, size);
            command = new CommandBuffer(_scheduler.Current.Handle);
            image.Transition(state.Access, null, command);
        }

        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.TransferBit,
            destinationStages, 0, 1, &barrier, 0, null, 0, null);
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
