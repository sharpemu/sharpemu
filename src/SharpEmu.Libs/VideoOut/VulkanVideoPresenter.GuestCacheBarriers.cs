// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial maps guest cache operations to host resource dependencies.

    private sealed partial class Presenter
    {
        private void RecordGlobalGuestCacheBarrier(
            CommandBuffer commandBuffer,
            GuestGpuCacheOperation operation)
        {
            var plan = VulkanGuestCacheBarrierPlanner.Resolve(operation);
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = plan.DestinationAccess,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                plan.DestinationStages,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }

        private bool TryRecordResourceGuestCacheBarrier(
            CommandBuffer commandBuffer,
            GuestGpuCacheOperation operation,
            IReadOnlyList<VulkanGuestCacheResourceRange> resources)
        {
            var resourcePlan = VulkanGuestCacheBarrierPlanner.ResolveResources(
                operation,
                resources);
            if (resourcePlan.UseGlobalBarrier)
            {
                return false;
            }

            var plan = VulkanGuestCacheBarrierPlanner.Resolve(operation);
            var bufferBarriers = CreateGuestCacheBufferBarriers(operation, plan);
            var imageBarriers = CreateGuestCacheDepthBarriers(operation, plan);
            if (bufferBarriers.Length + imageBarriers.Length == 0)
            {
                return false;
            }

            fixed (BufferMemoryBarrier* bufferBarrierPointer = bufferBarriers)
            fixed (ImageMemoryBarrier* imageBarrierPointer = imageBarriers)
            {
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    plan.DestinationStages,
                    0,
                    0,
                    null,
                    (uint)bufferBarriers.Length,
                    bufferBarrierPointer,
                    (uint)imageBarriers.Length,
                    imageBarrierPointer);
            }

            return true;
        }

        private List<VulkanGuestCacheResourceRange> CollectGuestCacheResourceRanges()
        {
            var resources = new List<VulkanGuestCacheResourceRange>(
                _guestBufferAllocations.Count +
                _guestDepthImages.Count +
                _guestImages.Count +
                _textureCache.Count);
            foreach (var allocation in _guestBufferAllocations)
            {
                resources.Add(new VulkanGuestCacheResourceRange(
                    allocation.BaseAddress,
                    allocation.Size,
                    VulkanGuestCacheResourceKind.Buffer));
            }

            foreach (var depth in _guestDepthImages.Values)
            {
                var kind = depth.Layout == ImageLayout.Undefined
                    ? VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout
                    : VulkanGuestCacheResourceKind.DepthImage;
                var byteCount = GetGuestDepthByteCount(depth);
                AddGuestDepthRange(resources, depth.Address, byteCount, kind);
                AddGuestDepthRange(resources, depth.ReadAddress, byteCount, kind);
                AddGuestDepthRange(resources, depth.WriteAddress, byteCount, kind);
            }

            foreach (var image in _guestImages.Values)
            {
                if (image.Address == 0)
                {
                    continue;
                }

                if (TryGetGuestImageExtent(
                        image.Address,
                        out _,
                        out _,
                        out var byteCount))
                {
                    resources.Add(new VulkanGuestCacheResourceRange(
                        image.Address,
                        byteCount,
                        VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout));
                    continue;
                }

                // The image exists, but its exact guest span is unknown. Make
                // every ranged operation conservative instead of allowing a
                // coincident buffer match to omit this image from the barrier.
                resources.Add(new VulkanGuestCacheResourceRange(
                    0,
                    ulong.MaxValue,
                    VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout));
            }

            foreach (var texture in _textureCache.Values)
            {
                if (texture.Address == 0)
                {
                    continue;
                }

                // Cached sampled images do not retain a tracked Vulkan
                // layout. Include their exact guest span so an overlapping
                // ranged operation uses the conservative global barrier.
                // If the transport did not provide a span, make every ranged
                // operation conservative instead of silently omitting the
                // image.
                resources.Add(texture.SourceByteCount == 0
                    ? new VulkanGuestCacheResourceRange(
                        0,
                        ulong.MaxValue,
                        VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout)
                    : new VulkanGuestCacheResourceRange(
                        texture.Address,
                        texture.SourceByteCount,
                        VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout));
            }

            return resources;
        }

        private static void AddGuestDepthRange(
            List<VulkanGuestCacheResourceRange> resources,
            ulong address,
            ulong byteCount,
            VulkanGuestCacheResourceKind kind)
        {
            if (address == 0 ||
                resources.Any(resource =>
                    resource.BaseAddress == address &&
                    resource.SizeBytes == byteCount &&
                    resource.Kind == kind))
            {
                return;
            }

            resources.Add(new VulkanGuestCacheResourceRange(
                address,
                byteCount,
                kind));
        }

        private BufferMemoryBarrier[] CreateGuestCacheBufferBarriers(
            GuestGpuCacheOperation operation,
            VulkanGuestCacheBarrier plan)
        {
            var barriers = new List<BufferMemoryBarrier>();
            var operationEnd = VulkanGuestCacheBarrierPlanner.SaturatingEnd(
                operation.BaseAddress,
                operation.SizeBytes);
            foreach (var allocation in _guestBufferAllocations)
            {
                if (!VulkanGuestCacheBarrierPlanner.RangesOverlap(
                        operation.BaseAddress,
                        operation.SizeBytes,
                        allocation.BaseAddress,
                        allocation.Size))
                {
                    continue;
                }

                var allocationEnd = VulkanGuestCacheBarrierPlanner.SaturatingEnd(
                    allocation.BaseAddress,
                    allocation.Size);
                var overlapStart = Math.Max(
                    operation.BaseAddress,
                    allocation.BaseAddress);
                var overlapEnd = Math.Min(operationEnd, allocationEnd);
                barriers.Add(new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = plan.DestinationAccess,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = allocation.Buffer,
                    Offset = overlapStart - allocation.BaseAddress,
                    Size = overlapEnd - overlapStart,
                });
            }

            return barriers.ToArray();
        }

        private ImageMemoryBarrier[] CreateGuestCacheDepthBarriers(
            GuestGpuCacheOperation operation,
            VulkanGuestCacheBarrier plan)
        {
            var barriers = new List<ImageMemoryBarrier>();
            var seenImages = new HashSet<ulong>();
            foreach (var depth in _guestDepthImages.Values)
            {
                if (depth.Layout == ImageLayout.Undefined ||
                    !GuestCacheOperationOverlapsDepth(operation, depth) ||
                    !seenImages.Add(depth.Image.Handle))
                {
                    continue;
                }

                barriers.Add(new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.DepthStencilAttachmentWriteBit |
                        AccessFlags.ShaderWriteBit |
                        AccessFlags.TransferWriteBit,
                    DstAccessMask = plan.DestinationAccess,
                    OldLayout = depth.Layout,
                    NewLayout = depth.Layout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                });
            }

            return barriers.ToArray();
        }

        private static bool GuestCacheOperationOverlapsDepth(
            GuestGpuCacheOperation operation,
            GuestDepthResource depth)
        {
            var byteCount = GetGuestDepthByteCount(depth);
            return VulkanGuestCacheBarrierPlanner.RangesOverlap(
                    operation.BaseAddress,
                    operation.SizeBytes,
                    depth.Address,
                    byteCount) ||
                VulkanGuestCacheBarrierPlanner.RangesOverlap(
                    operation.BaseAddress,
                    operation.SizeBytes,
                    depth.ReadAddress,
                    byteCount) ||
                VulkanGuestCacheBarrierPlanner.RangesOverlap(
                    operation.BaseAddress,
                    operation.SizeBytes,
                    depth.WriteAddress,
                    byteCount);
        }

        private static ulong GetGuestDepthByteCount(GuestDepthResource depth)
        {
            // This is the logical depth surface only. Tiled padding and HTILE
            // can occupy additional guest memory. An operation that cannot be
            // matched to this tracked range must retain the global fallback.
            var pixels = (ulong)depth.LogicalWidth * depth.LogicalHeight;
            return pixels > ulong.MaxValue / sizeof(float)
                ? ulong.MaxValue
                : pixels * sizeof(float);
        }
    }
}
