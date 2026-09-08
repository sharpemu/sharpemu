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
            if (bufferBarriers.Length == 0)
            {
                return false;
            }

            fixed (BufferMemoryBarrier* bufferBarrierPointer = bufferBarriers)
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
                    0,
                    null);
            }

            return true;
        }

        // Image hazards are the store's transitions; a ranged cache operation sees buffers only.
        private List<VulkanGuestCacheResourceRange> CollectGuestCacheResourceRanges()
        {
            var resources = new List<VulkanGuestCacheResourceRange>(_bufferCache.BufferCount);
            _bufferCache.ForEachBuffer(buffer => resources.Add(new VulkanGuestCacheResourceRange(
                buffer.CpuAddress,
                buffer.Size,
                VulkanGuestCacheResourceKind.Buffer)));
            return resources;
        }

        private BufferMemoryBarrier[] CreateGuestCacheBufferBarriers(
            GuestGpuCacheOperation operation,
            VulkanGuestCacheBarrier plan)
        {
            var barriers = new List<BufferMemoryBarrier>();
            var operationEnd = VulkanGuestCacheBarrierPlanner.SaturatingEnd(
                operation.BaseAddress,
                operation.SizeBytes);
            _bufferCache.ForEachBuffer(buffer =>
            {
                if (!VulkanGuestCacheBarrierPlanner.RangesOverlap(
                        operation.BaseAddress,
                        operation.SizeBytes,
                        buffer.CpuAddress,
                        buffer.Size))
                {
                    return;
                }

                var bufferEnd = VulkanGuestCacheBarrierPlanner.SaturatingEnd(
                    buffer.CpuAddress,
                    buffer.Size);
                var overlapStart = Math.Max(
                    operation.BaseAddress,
                    buffer.CpuAddress);
                var overlapEnd = Math.Min(operationEnd, bufferEnd);
                barriers.Add(new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = plan.DestinationAccess,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = buffer.Handle,
                    Offset = overlapStart - buffer.CpuAddress,
                    Size = overlapEnd - overlapStart,
                });
            });

            return barriers.ToArray();
        }
    }
}
