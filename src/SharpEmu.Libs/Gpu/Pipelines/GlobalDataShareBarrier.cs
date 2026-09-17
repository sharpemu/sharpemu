// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The dependency every host, transfer and shader write to the global data share has before a shader reads it.
public static class GlobalDataShareBarrier
{
    public const PipelineStageFlags SourceStages =
        PipelineStageFlags.HostBit | PipelineStageFlags.TransferBit | PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;

    public static BufferMemoryBarrier Make(VkBuffer buffer)
    {
        if (buffer.Handle == 0)
        {
            throw SubmissionScheduler.Fatal("The global data share buffer is missing.");
        }

        return new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.HostWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0,
            Size = Vk.WholeSize,
        };
    }
}
