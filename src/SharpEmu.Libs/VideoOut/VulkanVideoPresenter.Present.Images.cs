// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns offscreen images used for presentation.

        private Format PresentationTargetFormat =>
            _hdrOutputActive ? Format.B8G8R8A8Unorm : _swapchainFormat;

        private ImageLayout PresentationTargetFinalLayout =>
            _hdrOutputActive ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.PresentSrcKhr;

        private Image PresentationTargetImage(uint imageIndex) =>
            _hdrOutputActive ? _presentationImages[imageIndex] : _swapchainImages[imageIndex];

        private void CreatePresentationImage(int index)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateMutableFormatBit,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit |
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out _presentationImages[index]),
                "vkCreateImage(HDR presentation source)");
            _vk.GetImageMemoryRequirements(
                _device,
                _presentationImages[index],
                out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(
                    _device,
                    &allocationInfo,
                    null,
                    out _presentationImageMemory[index]),
                "vkAllocateMemory(HDR presentation source)");
            Check(
                _vk.BindImageMemory(
                    _device,
                    _presentationImages[index],
                    _presentationImageMemory[index],
                    0),
                "vkBindImageMemory(HDR presentation source)");

            _presentationImageViews[index] = CreatePresentationImageView(
                _presentationImages[index],
                Format.B8G8R8A8Unorm);
            _presentationSampleViews[index] = CreatePresentationImageView(
                _presentationImages[index],
                Format.B8G8R8A8Srgb);
        }

        private ImageView CreatePresentationImageView(Image image, Format format)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(HDR presentation source)");
            return view;
        }
    }
}
