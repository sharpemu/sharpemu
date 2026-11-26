// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Overlay;
using SkiaSharp;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly Dictionary<OverlayLayer, LayerTarget> _layerTargets = new(ReferenceEqualityComparer.Instance);

        private sealed class LayerTarget
        {
            public Image Image;
            public DeviceMemory ImageMemory;
            public bool Initialized;
            public SKRectI Bounds;
            public int UsedHeight;
            public long ImageRevision = -1;
            public long PixelsRevision = -1;
            public byte[] Pixels = [];
            public VkBuffer[] Staging = [];
            public DeviceMemory[] StagingMemory = [];
            public nint[] StagingMapped = [];
        }

        private void RecordOverlayLayers(uint imageIndex, int frameSlot)
        {
            foreach (var layer in OverlayLayers.Snapshot())
            {
                var revision = layer.Revision;
                if (!layer.IsVisible)
                {
                    continue;
                }

                if (!_layerTargets.TryGetValue(layer, out var target))
                {
                    var bounds = layer.Bounds((int)_extent.Width, (int)_extent.Height);
                    if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < 0 || bounds.Top < 0 ||
                        bounds.Right > _extent.Width || bounds.Bottom > _extent.Height)
                    {
                        continue;
                    }

                    target = CreateLayerTarget(bounds);
                    _layerTargets[layer] = target;
                }

                if (target.StagingMapped.Length <= frameSlot)
                {
                    continue;
                }

                if (target.ImageRevision != revision)
                {
                    if (target.PixelsRevision != revision)
                    {
                        var unit = OverlayStyle.UnitFor(_extent.Height);
                        target.UsedHeight = OverlayPainter.RenderInto(
                            target.Pixels,
                            target.Bounds.Width,
                            target.Bounds.Height,
                            canvas => layer.Render(canvas, target.Bounds.Width, target.Bounds.Height, unit));
                        target.PixelsRevision = revision;
                    }

                    RecordLayerUpload(target, frameSlot);
                    target.ImageRevision = revision;
                }

                RecordLayerCopy(target, imageIndex);
            }
        }

        private LayerTarget CreateLayerTarget(SKRectI bounds)
        {
            var target = new LayerTarget { Bounds = bounds, Pixels = new byte[checked(bounds.Width * bounds.Height * 4)] };
            var bytes = (ulong)target.Pixels.Length;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D((uint)bounds.Width, (uint)bounds.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out target.Image), "vkCreateImage(overlay layer)");
            _vk.GetImageMemoryRequirements(_device, target.Image, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_deviceInfo.AllocateMemory(memoryInfo, out target.ImageMemory), "vkAllocateMemory(overlay layer)");
            Check(_vk.BindImageMemory(_device, target.Image, target.ImageMemory, 0), "vkBindImageMemory(overlay layer)");

            target.Staging = new VkBuffer[MaxFramesInFlight];
            target.StagingMemory = new DeviceMemory[MaxFramesInFlight];
            target.StagingMapped = new nint[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                target.Staging[slot] = CreateBuffer(
                    bytes,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out target.StagingMemory[slot]);
                void* mapped;
                Check(
                    _vk.MapMemory(_device, target.StagingMemory[slot], 0, bytes, 0, &mapped),
                    "vkMapMemory(overlay layer staging)");
                target.StagingMapped[slot] = (nint)mapped;
            }

            return target;
        }

        private void DestroyOverlayLayerTargets()
        {
            foreach (var target in _layerTargets.Values)
            {
                if (target.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, target.Image, null);
                }
                if (target.ImageMemory.Handle != 0)
                {
                    _deviceInfo.FreeMemory(target.ImageMemory);
                }
                for (var slot = 0; slot < target.Staging.Length; slot++)
                {
                    if (target.Staging[slot].Handle != 0)
                    {
                        _vk.DestroyBuffer(_device, target.Staging[slot], null);
                    }
                    if (target.StagingMemory[slot].Handle != 0)
                    {
                        _deviceInfo.FreeMemory(target.StagingMemory[slot]);
                    }
                }
            }

            _layerTargets.Clear();
        }

        private void RecordLayerUpload(LayerTarget target, int frameSlot)
        {
            fixed (byte* source = target.Pixels)
            {
                System.Buffer.MemoryCopy(
                    source,
                    (void*)target.StagingMapped[frameSlot],
                    target.Pixels.Length,
                    target.Pixels.Length);
            }

            var toTransferDst = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = target.Initialized ? AccessFlags2.TransferReadBit : 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = target.Initialized ? ImageLayout.TransferSrcOptimal : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)target.Bounds.Width, (uint)target.Bounds.Height, 1),
            };
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    target.Staging[frameSlot],
                    target.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);
            }

            var toTransferSrc = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferSrc);
            target.Initialized = true;
        }

        private void RecordLayerCopy(LayerTarget target, uint imageIndex)
        {
            var presentationTarget = PresentationTargetImage(imageIndex);
            var toTransferDst = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = PresentationTargetFinalLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D(0, 0, 0),
                DstOffset = new Offset3D(
                    target.Bounds.Left,
                    target.Bounds.Top + (target.Bounds.Height - target.UsedHeight) / 2,
                    0),
                Extent = new Extent3D((uint)target.Bounds.Width, (uint)target.UsedHeight, 1),
            };
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
            {
                _vk.CmdCopyImage(
                    _commandBuffer,
                    target.Image,
                    ImageLayout.TransferSrcOptimal,
                    presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);
            }

            var toFinal = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = _hdrOutputActive ? AccessFlags2.ShaderReadBit : 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                _hdrOutputActive ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.BottomOfPipeBit,
                0, 0, null, 0, null, 1, &toFinal);
        }
    }
}
