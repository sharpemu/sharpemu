// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// The groups and thread limits of a dispatch whose guest arguments count threads and were written by a shader
// (SpirvDispatchThreads). The counts stay on the GPU: the command stream records the dispatch without waiting for their
// writer, as the hardware's command processor converts them itself.
internal sealed unsafe class DispatchThreadsPipeline : IDisposable
{
    private const ulong GroupBytes = 4 * sizeof(uint);
    private const ulong CountBytes = 3 * sizeof(uint);
    private readonly GpuDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RecycledDescriptorSets? _sets;
    // Group buffers return here after their GPU work completes.
    private readonly object _resourceGate = new();
    private readonly Stack<GpuBuffer> _freeGroups = new();
    private bool _disposed;

    public DispatchThreadsPipeline(GpuDeviceInfo device)
    {
        _device = device;
        ShaderModule module = default;
        try
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[3];
            for (uint index = 0; index < 3; index++)
                bindings[index] = new DescriptorSetLayoutBinding(index, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &setInfo, null, out _setLayout));
            _sets = new RecycledDescriptorSets(device, _setLayout, 3);
            var setLayout = _setLayout;
            var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, SpirvDispatchThreads.PushWordCount * 4);
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
            };
            Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out _layout));
            var spirv = SpirvDispatchThreads.Create();
            fixed (byte* code = spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code,
                };
                Check(device.Vk.CreateShaderModule(device.Device, &moduleInfo, null, out module));
            }
            fixed (byte* entry = "main\0"u8)
            {
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo, Layout = _layout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit,
                        Module = module, PName = entry,
                    },
                };
                Check(device.Vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out _pipeline));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (module.Handle != 0) device.Vk.DestroyShaderModule(device.Device, module, null);
        }
    }

    // groupSize and physicalAxis are per logical axis; the groups land in the pipeline's physical axis order. Without
    // limits the shader writes none, and the arguments fill the unused binding.
    public PreparedDispatch Prepare(SubmissionScheduler scheduler, BufferBinding arguments, BufferBinding? limits,
        ReadOnlySpan<uint> groupSize, ReadOnlySpan<int> physicalAxis) =>
        new(this, scheduler, arguments, limits, groupSize, physicalAxis);

    private GpuBuffer RentGroups(SubmissionScheduler scheduler)
    {
        lock (_resourceGate)
            if (_freeGroups.TryPop(out var reused)) return reused;
        return new GpuBuffer(_device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, GroupBytes);
    }

    private void ReturnResources(GpuBuffer? groups, DescriptorSet set)
    {
        lock (_resourceGate)
        {
            if (_disposed)
            {
                groups?.Dispose();
                return;
            }
            if (groups is not null) _freeGroups.Push(groups);
            if (set.Handle != 0) _sets!.Return(set);
        }
    }

    public void Dispose()
    {
        lock (_resourceGate)
        {
            _disposed = true;
            foreach (var buffer in _freeGroups) buffer.Dispose();
            _freeGroups.Clear();
            _sets?.Dispose();
            _sets = null;
        }
        if (_pipeline.Handle != 0) _device.Vk.DestroyPipeline(_device.Device, _pipeline, null);
        if (_layout.Handle != 0) _device.Vk.DestroyPipelineLayout(_device.Device, _layout, null);
        if (_setLayout.Handle != 0) _device.Vk.DestroyDescriptorSetLayout(_device.Device, _setLayout, null);
        _pipeline = default;
        _layout = default;
        _setLayout = default;
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"The thread dispatch resource could not be created: result={result}.");
    }

    internal sealed class PreparedDispatch : IDisposable
    {
        private readonly DispatchThreadsPipeline _owner;
        private readonly uint[] _push = new uint[SpirvDispatchThreads.PushWordCount];
        private GpuBuffer? _groups;
        private DescriptorSet _set;

        internal PreparedDispatch(DispatchThreadsPipeline owner, SubmissionScheduler scheduler, BufferBinding arguments,
            BufferBinding? limits, ReadOnlySpan<uint> groupSize, ReadOnlySpan<int> physicalAxis)
        {
            _owner = owner;
            if (groupSize.Length != 3 || physicalAxis.Length != 3)
                throw SubmissionScheduler.Fatal("The thread dispatch needs three group sizes and axes.");
            var device = owner._device;
            try
            {
                _groups = owner.RentGroups(scheduler);
                _set = owner._sets!.Rent();
                var alignment = device.MinStorageBufferOffsetAlignment;
                var argumentsBase = arguments.Offset / alignment * alignment;
                var limitsBinding = limits ?? arguments;
                var limitsBase = limitsBinding.Offset / alignment * alignment;
                var infos = stackalloc DescriptorBufferInfo[3];
                infos[0] = new(new Silk.NET.Vulkan.Buffer(arguments.Handle), argumentsBase, arguments.Offset - argumentsBase + CountBytes);
                infos[1] = new(_groups.Handle, 0, GroupBytes);
                infos[2] = new(new Silk.NET.Vulkan.Buffer(limitsBinding.Handle), limitsBase, limitsBinding.Offset - limitsBase + CountBytes);
                var writes = stackalloc WriteDescriptorSet[3];
                for (uint index = 0; index < 3; index++)
                {
                    writes[index] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = index,
                        DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + index,
                    };
                }
                device.Vk.UpdateDescriptorSets(device.Device, 3, writes, 0, null);
                var maximum = device.MaxComputeWorkGroupCount;
                ReadOnlySpan<uint> physicalLimit = [maximum.X, maximum.Y, maximum.Z];
                _push[0] = checked((uint)((arguments.Offset - argumentsBase) / sizeof(uint)));
                for (var axis = 0; axis < 3; axis++)
                {
                    if (physicalAxis[axis] is < 0 or > 2)
                        throw SubmissionScheduler.Fatal("The thread dispatch axis is out of range.");
                    _push[1 + axis] = Math.Max(groupSize[axis], 1u);
                    _push[4 + axis] = (uint)physicalAxis[axis];
                    _push[8 + axis] = physicalLimit[physicalAxis[axis]];
                }
                _push[7] = limits is null
                    ? SpirvDispatchThreads.NoLimits
                    : checked((uint)((limitsBinding.Offset - limitsBase) / sizeof(uint)));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Silk.NET.Vulkan.Buffer GroupsBuffer => _groups!.Handle;

        public void Record(CommandBuffer command)
        {
            var vk = _owner._device.Vk;
            // The arguments' shader writer and the shader data upload come before the read.
            var before = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit | AccessFlags2.HostWriteBit,
                DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit,
                PipelineStageFlags.ComputeShaderBit, 0, 1, &before, 0, null, 0, null);
            vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _owner._pipeline);
            var set = _set;
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _owner._layout, 0, 1, &set, 0, null);
            fixed (uint* push = _push)
                vk.CmdPushConstants(command, _owner._layout, ShaderStageFlags.ComputeBit, 0, SpirvDispatchThreads.PushWordCount * 4, push);
            // The guest dispatch binds its own pipeline and sets again.
            NativeBindingEpoch.Advance();
            vk.CmdDispatch(command, 1, 1, 1);
            var after = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2, SrcAccessMask = AccessFlags2.ShaderWriteBit,
                DstAccessMask = AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderReadBit | AccessFlags2.UniformReadBit,
            };
            VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.ComputeShaderBit, 0, 1, &after, 0, null, 0, null);
        }

        public void Dispose()
        {
            // The owner calls this before recording or after the GPU work completes.
            _owner.ReturnResources(_groups, _set);
            _set = default;
            _groups = null;
        }
    }
}
