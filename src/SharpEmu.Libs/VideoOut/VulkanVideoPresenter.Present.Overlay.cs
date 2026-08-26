// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial manages presentation of the performance overlay.

        // Perf overlay: CPU-rasterized panel copied through per-slot staging
        // buffers into one image, then blitted onto the swapchain.
        private Image _overlayImage;
        private DeviceMemory _overlayImageMemory;
        private bool _overlayImageInitialized;
        private VkBuffer[] _overlayStagingBuffers = [];
        private DeviceMemory[] _overlayStagingMemory = [];
        private nint[] _overlayStagingMapped = [];

        private void CreateOverlayResources()
        {
            const ulong overlayBytes = PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out _overlayImage), "vkCreateImage(overlay)");
            _vk.GetImageMemoryRequirements(_device, _overlayImage, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &memoryInfo, null, out _overlayImageMemory),
                "vkAllocateMemory(overlay)");
            Check(
                _vk.BindImageMemory(_device, _overlayImage, _overlayImageMemory, 0),
                "vkBindImageMemory(overlay)");
            _overlayImageInitialized = false;

            _overlayStagingBuffers = new VkBuffer[MaxFramesInFlight];
            _overlayStagingMemory = new DeviceMemory[MaxFramesInFlight];
            _overlayStagingMapped = new nint[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                _overlayStagingBuffers[slot] = CreateBuffer(
                    overlayBytes,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _overlayStagingMemory[slot]);
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _overlayStagingMemory[slot], 0, overlayBytes, 0, &mapped),
                    "vkMapMemory(overlay staging)");
                _overlayStagingMapped[slot] = (nint)mapped;
            }
        }

        private void RecordOverlayBlit(uint imageIndex, int frameSlot)
        {
            if (_overlayImage.Handle == 0 || _overlayStagingMapped.Length <= frameSlot)
            {
                return;
            }

            int pendingWork;
            lock (_gate)
            {
                pendingWork = _pendingGuestWorkCount;
            }

            var pixels = new Span<byte>(
                (void*)_overlayStagingMapped[frameSlot],
                PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4);
            PerfOverlay.Fill(pixels, pendingWork, _pendingGuestSubmissions.Count);
            var presentationTarget = PresentationTargetImage(imageIndex);

            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _overlayImageInitialized ? AccessFlags.TransferReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _overlayImageInitialized
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _overlayStagingBuffers[frameSlot],
                _overlayImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toTransferSrc = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var swapchainToDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = PresentationTargetFinalLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            var preBlitBarriers = stackalloc ImageMemoryBarrier[2] { toTransferSrc, swapchainToDst };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 2, preBlitBarriers);

            const int margin = 12;
            var panelWidth = (int)Math.Min(PerfOverlay.PanelWidth, _extent.Width - margin);
            var panelHeight = (int)Math.Min(PerfOverlay.PanelHeight, _extent.Height - margin);
            // Source and destination are both B8G8R8A8 and the panel is not
            // scaled. MoltenVK has corrupted pixels outside the blit region
            // for this transfer-on-swapchain path (horizontal red/yellow
            // scanlines across the entire window). An exact image copy has
            // the required semantics and avoids the driver's blit conversion
            // path altogether.
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D(0, 0, 0),
                DstOffset = new Offset3D(margin, margin, 0),
                Extent = new Extent3D((uint)panelWidth, (uint)panelHeight, 1),
            };
            _vk.CmdCopyImage(
                _commandBuffer,
                _overlayImage,
                ImageLayout.TransferSrcOptimal,
                presentationTarget,
                ImageLayout.TransferDstOptimal,
                1,
                &copy);

            var presentationTargetToFinal = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = _hdrOutputActive ? AccessFlags.ShaderReadBit : 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                _hdrOutputActive
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.BottomOfPipeBit,
                0, 0, null, 0, null, 1, &presentationTargetToFinal);
            _overlayImageInitialized = true;
        }
    }
}
