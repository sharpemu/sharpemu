// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class OcclusionSumPipeline : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RecycledDescriptorSets? _sets;

    public OcclusionSumPipeline(GpuDeviceInfo device)
    {
        _device = device;
        ShaderModule module = default;
        try
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[2];
            for (uint index = 0; index < 2; index++)
                bindings[index] = new(index, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &setInfo, null, out _setLayout));
            _sets = new RecycledDescriptorSets(device, _setLayout, 2);
            var setLayout = _setLayout;
            var push = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, SpirvOcclusionSum.PushWords * 4);
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &push,
            };
            Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out _layout));
            var spirv = SpirvOcclusionSum.Create();
            fixed (byte* code = spirv)
            {
                var info = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
                };
                Check(device.Vk.CreateShaderModule(device.Device, &info, null, out module));
            }
            fixed (byte* entry = "main\0"u8)
            {
                var info = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo, Layout = _layout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                        Module = module, PName = entry,
                    },
                };
                Check(device.Vk.CreateComputePipelines(device.Device, default, 1, &info, null, out _pipeline));
            }
        }
        catch { Dispose(); throw; }
        finally { if (module.Handle != 0) device.Vk.DestroyShaderModule(device.Device, module, null); }
    }

    public void Record(SubmissionScheduler scheduler, GpuBuffer input, uint firstWord, uint count,
        GpuBuffer output, ulong outputOffset, bool accumulate, ulong resetBase = 0, bool ready = false)
    {
        if ((ulong)firstWord * 4 > input.Size || (ulong)count * 8 > input.Size - (ulong)firstWord * 4 ||
            (outputOffset & 7) != 0 || outputOffset > output.Size || 8 > output.Size - outputOffset)
            throw SubmissionScheduler.Fatal("The occlusion reduction range is invalid.");
        var alignment = _device.MinStorageBufferOffsetAlignment;
        var outputBase = outputOffset / alignment * alignment;
        var inputBase = (ulong)firstWord * 4 / alignment * alignment;
        var inputRange = (ulong)firstWord * 4 - inputBase + (ulong)count * 8;
        if (inputRange == 0 || inputRange > _device.MaxStorageBufferRange)
            throw SubmissionScheduler.Fatal("The occlusion input descriptor range is invalid.");
        var sets = _sets!;
        var set = sets.Rent();
        var retained = false;
        var committed = false;
        try
        {
            var infos = stackalloc DescriptorBufferInfo[2];
            infos[0] = new(input.Handle, inputBase, inputRange);
            infos[1] = new(output.Handle, outputBase, outputOffset - outputBase + 8);
            var writes = stackalloc WriteDescriptorSet[2];
            for (uint index = 0; index < 2; index++)
                writes[index] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = index,
                    DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + index,
                };
            _device.Vk.UpdateDescriptorSets(_device.Device, 2, writes, 0, null);
            input.RetainForeignRead();
            output.RetainForeignRead();
            retained = true;
            var rented = set;
            scheduler.QueueCompletionAction(() =>
            {
                sets.Return(rented);
                input.ReleaseForeignRead();
                output.ReleaseForeignRead();
            });
            committed = true;
            var command = new CommandBuffer(scheduler.Current.Handle);
            Barrier(command);
            _device.Vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
            _device.Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
            uint* push = stackalloc uint[7]
            {
                checked((uint)(((ulong)firstWord * 4 - inputBase) / 4)), count,
                checked((uint)((outputOffset - outputBase) / 4)), accumulate ? 1u : 0u,
                (uint)resetBase, (uint)(resetBase >> 32), ready ? 0x80000000u : 0u,
            };
            _device.Vk.CmdPushConstants(command, _layout, ShaderStageFlags.ComputeBit, 0, 28, push);
            NativeBindingEpoch.Advance();
            _device.Vk.CmdDispatch(command, 1, 1, 1);
            output.NoteGpuWrite(outputOffset, 8);
            Barrier(command);
        }
        finally
        {
            if (!committed)
            {
                if (retained) { input.ReleaseForeignRead(); output.ReleaseForeignRead(); }
                sets.Return(set);
            }
        }
    }

    private void Barrier(CommandBuffer command)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"Cannot create occlusion reduction resources: {result}.");
    }

    public void Dispose()
    {
        _sets?.Dispose();
        _sets = null;
        if (_pipeline.Handle != 0) _device.Vk.DestroyPipeline(_device.Device, _pipeline, null);
        if (_layout.Handle != 0) _device.Vk.DestroyPipelineLayout(_device.Device, _layout, null);
        if (_setLayout.Handle != 0) _device.Vk.DestroyDescriptorSetLayout(_device.Device, _setLayout, null);
        _pipeline = default;
        _layout = default;
        _setLayout = default;
    }
}
