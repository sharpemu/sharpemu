// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed unsafe class MeshIndirectPipeline : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RecycledDescriptorSets? _sets;
    // Draw resources return here after their GPU work completes; outputs are kept by size class.
    private readonly object _resourceGate = new();
    private readonly Dictionary<ulong, Stack<GpuBuffer>> _freeOutputs = new();
    private readonly Stack<GpuBuffer> _freeStatuses = new();
    private bool _disposed;

    public MeshIndirectPipeline(GpuDeviceInfo device)
    {
        _device = device;
        ShaderModule module = default;
        try
        {
            var bindings = stackalloc DescriptorSetLayoutBinding[4];
            for (uint index = 0; index < 4; index++)
                bindings[index] = new DescriptorSetLayoutBinding(index, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings,
            };
            Check(device.Vk.CreateDescriptorSetLayout(device.Device, &setInfo, null, out _setLayout));
            _sets = new RecycledDescriptorSets(device, _setLayout, 4);
            var setLayout = _setLayout;
            var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, SpirvMeshIndirect.PushWordCount * 4);
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
                PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
            };
            Check(device.Vk.CreatePipelineLayout(device.Device, &layoutInfo, null, out _layout));
            var spirv = SpirvMeshIndirect.Create();
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

    public PreparedDraw Prepare(SubmissionScheduler scheduler, BufferBinding arguments, BufferBinding indices,
        uint indexByteOffset, in IndirectMeshDrawRequest request) =>
        new(this, scheduler, arguments, indices, indexByteOffset, request);

    private GpuBuffer RentOutput(SubmissionScheduler scheduler, ulong size)
    {
        var bucket = Math.Max(4096ul, System.Numerics.BitOperations.RoundUpToPowerOf2(size));
        lock (_resourceGate)
            if (_freeOutputs.TryGetValue(bucket, out var free) && free.TryPop(out var reused)) return reused;
        return new GpuBuffer(_device, scheduler, GpuBufferUsage.DeviceLocal, 0,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, bucket);
    }

    private GpuBuffer RentStatus(SubmissionScheduler scheduler)
    {
        GpuBuffer? status = null;
        lock (_resourceGate) _freeStatuses.TryPop(out status);
        status ??= new GpuBuffer(_device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 4);
        status.Mapped.Clear();
        status.Flush(0, 4);
        return status;
    }

    private void ReturnResources(GpuBuffer? output, GpuBuffer? status, DescriptorSet set)
    {
        lock (_resourceGate)
        {
            if (_disposed)
            {
                output?.Dispose();
                status?.Dispose();
                return;
            }
            if (output is not null)
            {
                if (!_freeOutputs.TryGetValue(output.Size, out var free)) _freeOutputs[output.Size] = free = new Stack<GpuBuffer>();
                free.Push(output);
            }
            if (status is not null) _freeStatuses.Push(status);
            if (set.Handle != 0) _sets!.Return(set);
        }
    }

    public void Dispose()
    {
        lock (_resourceGate)
        {
            _disposed = true;
            foreach (var free in _freeOutputs.Values)
                foreach (var buffer in free) buffer.Dispose();
            _freeOutputs.Clear();
            foreach (var buffer in _freeStatuses) buffer.Dispose();
            _freeStatuses.Clear();
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
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"The indirect mesh resource could not be created: result={result}.");
    }

    internal sealed class PreparedDraw : IDisposable
    {
        private readonly MeshIndirectPipeline _owner;
        private readonly uint[] _push;
        private readonly uint _capacity;
        private GpuBuffer? _output;
        private GpuBuffer? _status;
        private DescriptorSet _set;

        internal PreparedDraw(MeshIndirectPipeline owner, SubmissionScheduler scheduler, BufferBinding arguments,
            BufferBinding indices, uint indexByteOffset, in IndirectMeshDrawRequest request)
        {
            _owner = owner;
            _capacity = request.IndexCapacity;
            _push = new uint[SpirvMeshIndirect.PushWordCount];
            if (_capacity is 0 or > IndirectMeshDrawRequest.MaximumIndexCapacity || request.IndexElementSize is not (2 or 4) ||
                request.PrimitivesPerGroup is 0 or > IndirectMeshDrawRequest.MaximumIndexCapacity)
                throw SubmissionScheduler.Fatal("The indirect mesh preparation has invalid bounds.");
            var device = owner._device;
            try
            {
                _output = owner.RentOutput(scheduler, (SpirvMeshIndirect.HeaderWords + (ulong)_capacity) * 4);
                _status = owner.RentStatus(scheduler);
                _set = owner._sets!.Rent();
                var infos = stackalloc DescriptorBufferInfo[4];
                var alignment = device.MinStorageBufferOffsetAlignment;
                var argumentsBase = arguments.Offset / alignment * alignment;
                var indicesBase = indices.Offset / alignment * alignment;
                var indexSize = (indexByteOffset + (ulong)_capacity * request.IndexElementSize + 3) & ~3ul;
                infos[0] = new(new Silk.NET.Vulkan.Buffer(arguments.Handle), argumentsBase, arguments.Offset - argumentsBase + 20);
                infos[1] = new(new Silk.NET.Vulkan.Buffer(indices.Handle), indicesBase, indices.Offset - indicesBase + indexSize);
                infos[2] = new(_output.Handle, 0, _output.Size);
                infos[3] = new(_status.Handle, 0, 4);
                var writes = stackalloc WriteDescriptorSet[4];
                for (uint index = 0; index < 4; index++)
                {
                    if (infos[index].Range > device.MaxStorageBufferRange)
                        throw SubmissionScheduler.Fatal("The indirect mesh descriptor exceeds the device range limit.");
                    writes[index] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = index,
                        DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + index,
                    };
                }
                device.Vk.UpdateDescriptorSets(device.Device, 4, writes, 0, null);
                _push[0] = checked((uint)((arguments.Offset - argumentsBase) / 4));
                _push[1] = checked((uint)(indices.Offset - indicesBase + indexByteOffset));
                _push[2] = _capacity;
                _push[3] = request.IndexElementSize;
                _push[4] = request.PrimitivesPerGroup;
                _push[5] = request.PointList ? 1u : 0u;
                _push[6] = request.Limits.MaxGroupCountX;
                _push[7] = request.Limits.MaxGroupCountY;
                _push[8] = request.Limits.MaxGroupTotalCount;
                var address = _output.DeviceAddress + SpirvMeshIndirect.HeaderWords * 4;
                _push[9] = (uint)address;
                _push[10] = (uint)(address >> 32);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public ulong ParametersAddress => _output!.DeviceAddress + 16;
        public Silk.NET.Vulkan.Buffer CommandBuffer => _output!.Handle;

        public void Record(CommandBuffer command)
        {
            var vk = _owner._device.Vk;
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
                vk.CmdPushConstants(command, _owner._layout, ShaderStageFlags.ComputeBit, 0, SpirvMeshIndirect.PushWordCount * 4, push);
            NativeBindingEpoch.Advance();
            vk.CmdDispatch(command, (_capacity + 63) / 64, 1, 1);
            var after = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2, SrcAccessMask = AccessFlags2.ShaderWriteBit,
                DstAccessMask = AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderReadBit | AccessFlags2.HostReadBit,
            };
            VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.MeshShaderBitExt | PipelineStageFlags.HostBit,
                0, 1, &after, 0, null, 0, null);
        }

        public uint ReadStatus()
        {
            _status!.Invalidate(0, 4);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_status.Mapped);
        }

        public void Dispose()
        {
            // The owner calls this before recording or after the GPU work completes.
            _owner.ReturnResources(_output, _status, _set);
            _set = default;
            _status = null;
            _output = null;
        }
    }
}
