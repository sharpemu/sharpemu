// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// The image accesses one slice recorded so far; the barrier before the slice covers everything
// earlier, so only accesses inside the slice need barriers against each other.
internal sealed class SliceHazards
{
    private const AccessFlags WriteAccess = AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit |
                                            AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit;
    private const AccessFlags TransferAccess = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;

    private readonly Dictionary<CachedImage, ImageAccessState> _states = new(ReferenceEqualityComparer.Instance);
    private readonly List<ImageMemoryBarrier2> _barriers = new();

    public void Reset() => _states.Clear();

    public static PipelineStageFlags StagesOf(AccessFlags access)
    {
        PipelineStageFlags stage = 0;
        if ((access & TransferAccess) != 0)
        {
            stage |= PipelineStageFlags.TransferBit;
        }

        if (access == 0 || (access & ~TransferAccess) != 0)
        {
            stage |= PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
        }

        return stage;
    }

    // The barriers the access needs against this slice's earlier accesses of the image; empty on the
    // first access. The returned list is reused by the next call.
    public List<ImageMemoryBarrier2> Transition(CachedImage image, AccessFlags access, PipelineStageFlags stage, out PipelineStageFlags sourceStages, bool guest = false)
    {
        _barriers.Clear();
        sourceStages = 0;
        var next = new ImageAccessState(stage, access, ImageLayout.General, guest);
        if (_states.TryGetValue(image, out var state))
        {
            if (state.RepeatsGuestStorageAccess(access, guest))
            {
                ImageAccessState.BarriersElided.Increment();
                _states[image] = state with { Stage = state.Stage | stage };
                return _barriers;
            }

            var repeatedWrite = (state.Access & WriteAccess) != 0;
            if ((state.Access != access || repeatedWrite) && (state.RepeatsAttachmentAccess(access) || state.ReadsAfterRead(access)))
            {
                if (GpuSyncProfile.Enabled)
                {
                    BarrierTally.CountAvoidableImageBarrier(in state, access);
                }

                ImageAccessState.BarriersElided.Increment();
                _states[image] = state with { Stage = state.Stage | stage, Access = state.Access | access };
                return _barriers;
            }

            if (state.Access != access || repeatedWrite)
            {
                _barriers.Add(new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = VulkanSynchronization.Access(state.Access),
                    DstAccessMask = VulkanSynchronization.Access(access),
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Backing.Handle,
                    SubresourceRange = new ImageSubresourceRange(ViewFormatRules.FullAspects(image.Backing.Format), 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
                });
                ImageAccessState.Barriers.Increment();
                sourceStages = state.Stage;
            }
            else
            {
                next = state with { Stage = state.Stage | stage };
            }
        }

        _states[image] = next;
        return _barriers;
    }
}
