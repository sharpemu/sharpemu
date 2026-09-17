// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Runs one compute module compiled through a compile request: the descriptor set follows
// the request's binding layout and push data carries the user-data registers.
internal sealed unsafe class LayoutComputeRunner : IDisposable
{
    private readonly ImageTestHarness _harness;
    private readonly ShaderCompileRequest _request;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly DescriptorPool _pool;
    private readonly Pipeline _pipeline;
    private readonly List<GpuBuffer> _buffers = [];
    private readonly List<Sampler> _samplers = [];

    // The descriptor type of one layout kind: images by class, samplers, else a storage buffer.
    public static DescriptorType DescriptorTypeOf(DescriptorBindingKind kind) =>
        kind == DescriptorBindingKind.Samplers ? DescriptorType.Sampler
        : ImageDescriptorBinding.ResourceClass(kind) == ImageResourceClass.Sampled ? DescriptorType.SampledImage
        : ImageDescriptorBinding.ResourceClass(kind) == ImageResourceClass.Storage ? DescriptorType.StorageImage
        : DescriptorType.StorageBuffer;

    public LayoutComputeRunner(ImageTestHarness harness, ShaderCompileRequest request, byte[] spirv)
    {
        _harness = harness;
        _request = request;
        var vk = harness.Vk;
        var device = harness.Device.Device;
        var descriptors = request.Bindings.Descriptors;
        var bindings = stackalloc DescriptorSetLayoutBinding[Math.Max(descriptors.Count, 1)];
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            bindings[index] = new DescriptorSetLayoutBinding(
                BindingLayout.NativeBindingIndex(request.Stage, descriptor.Kind),
                DescriptorTypeOf(descriptor.Kind),
                (uint)Math.Max(descriptor.Resources.Count, 1),
                ShaderStageFlags.ComputeBit);
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)descriptors.Count,
            PBindings = bindings,
        };
        Require(vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");
        var push = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, PushData.ByteSize);
        fixed (DescriptorSetLayout* layout = &_setLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = layout,
                PushConstantRangeCount = request.Bindings.UsesPushData ? 1u : 0u,
                PPushConstantRanges = &push,
            };
            Require(vk.CreatePipelineLayout(device, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");
        }

        var sizes = stackalloc DescriptorPoolSize[4];
        sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 256);
        sizes[1] = new DescriptorPoolSize(DescriptorType.SampledImage, 64);
        sizes[2] = new DescriptorPoolSize(DescriptorType.StorageImage, 64);
        sizes[3] = new DescriptorPoolSize(DescriptorType.Sampler, 64);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 64, PoolSizeCount = 4, PPoolSizes = sizes };
        Require(vk.CreateDescriptorPool(device, &poolInfo, null, out _pool), "vkCreateDescriptorPool");

        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Require(vk.CreateShaderModule(device, &moduleInfo, null, out module), "vkCreateShaderModule");
        }

        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
                Layout = _pipelineLayout,
            };
            Require(vk.CreateComputePipelines(device, default, 1, &info, null, out _pipeline), "vkCreateComputePipelines");
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
            vk.DestroyShaderModule(device, module, null);
        }
    }

    private static void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed with {result}");
        }
    }

    // A host-visible storage buffer with a device address, kept until the runner is disposed.
    public GpuBuffer CreateBuffer(ReadOnlySpan<byte> data, ulong minimumSize = 4)
    {
        var size = Math.Max((ulong)data.Length, minimumSize);
        size = Math.Max((size + 3) & ~3UL, 4);
        var buffer = new GpuBuffer(
            _harness.Device,
            _harness.Scheduler,
            GpuBufferUsage.Upload,
            0,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit,
            size);
        buffer.Mapped.Clear();
        buffer.Write(0, data);
        _buffers.Add(buffer);
        return buffer;
    }

    public GpuBuffer CreateBuffer(ulong size) => CreateBuffer([], size);

    // A clamp-to-edge sampler with one filter for both directions, kept until the runner is disposed.
    public Sampler CreateSampler(Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 1,
        };
        Require(_harness.Vk.CreateSampler(_harness.Device.Device, &info, null, out var sampler), "vkCreateSampler");
        _samplers.Add(sampler);
        return sampler;
    }

    // The page table entries for guest pages that map onto a buffer's device address.
    public GpuBuffer CreatePageTable(ulong entryCount, IEnumerable<(ulong GuestAddress, GpuBuffer Buffer, ulong BufferOffset)> pages)
    {
        var table = new ulong[entryCount];
        foreach (var (guestAddress, buffer, bufferOffset) in pages)
        {
            table[guestAddress >> Gen5SpirvTranslator.DeviceAddressPageBits] = buffer.DeviceAddress + bufferOffset;
        }

        return CreateBuffer(MemoryMarshal.AsBytes<ulong>(table), (ulong)table.Length * sizeof(ulong));
    }

    // Records one dispatch: user data from the register file, buffers per binding kind, and
    // image views or samplers per image or sampler kind.
    public void Dispatch(
        uint[] scalarRegisters,
        IReadOnlyDictionary<DescriptorBindingKind, GpuBuffer[]> boundBuffers,
        uint groupsX,
        uint groupsY = 1,
        uint groupsZ = 1,
        uint[]? flattenedTable = null,
        IReadOnlyDictionary<DescriptorBindingKind, DescriptorImageInfo[]>? boundImages = null,
        ulong shaderBase = 0,
        uint[]? dispatchThreadLimits = null)
    {
        var vk = _harness.Vk;
        var device = _harness.Device.Device;
        var layout = _request.Bindings;
        var shaderData = new uint[layout.ShaderDataDwordCount];
        for (var index = 0; index < layout.UserDataRegisters.Count; index++)
        {
            var register = layout.UserDataRegisters[index];
            shaderData[index] = register < scalarRegisters.Length ? scalarRegisters[register] : 0;
        }

        if (layout.UsesShaderBase)
        {
            shaderData[layout.ShaderBaseDword] = (uint)shaderBase;
            shaderData[layout.ShaderBaseDword + 1] = (uint)(shaderBase >> 32);
        }

        if (layout.UsesDispatchThreadLimits)
        {
            if (dispatchThreadLimits is not { Length: 3 }) throw new InvalidOperationException("The dispatch requires three thread limits.");
            dispatchThreadLimits.CopyTo(shaderData, (int)layout.DispatchThreadLimitsDword);
        }

        var setLayout = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        Require(vk.AllocateDescriptorSets(device, &allocateInfo, out var set), "vkAllocateDescriptorSets");

        var infos = new List<DescriptorBufferInfo>();
        var imageInfos = new List<DescriptorImageInfo>();
        var writes = new List<(uint Binding, DescriptorType Type, int First, int Count)>();
        foreach (var descriptor in layout.Descriptors)
        {
            var count = Math.Max(descriptor.Resources.Count, 1);
            var type = DescriptorTypeOf(descriptor.Kind);
            var binding = BindingLayout.NativeBindingIndex(_request.Stage, descriptor.Kind);
            if (type != DescriptorType.StorageBuffer)
            {
                if (boundImages is null || !boundImages.TryGetValue(descriptor.Kind, out var images) || images.Length != count)
                {
                    throw new InvalidOperationException($"binding {descriptor.Kind} needs {count} image or sampler entries");
                }

                writes.Add((binding, type, imageInfos.Count, count));
                imageInfos.AddRange(images);
                continue;
            }

            GpuBuffer[] buffers;
            if (descriptor.Kind == DescriptorBindingKind.FlattenedResourceTable && !boundBuffers.ContainsKey(descriptor.Kind))
            {
                buffers = [CreateBuffer(MemoryMarshal.AsBytes<uint>(flattenedTable ?? new uint[1]))];
            }
            else if (descriptor.Kind == DescriptorBindingKind.ShaderData && !boundBuffers.ContainsKey(descriptor.Kind))
            {
                buffers = [CreateBuffer(MemoryMarshal.AsBytes<uint>(shaderData))];
            }
            else if (!boundBuffers.TryGetValue(descriptor.Kind, out buffers!))
            {
                buffers = [CreateBuffer(16)];
            }

            if (buffers.Length != count)
            {
                throw new InvalidOperationException($"binding {descriptor.Kind} needs {count} buffers, got {buffers.Length}");
            }

            writes.Add((binding, type, infos.Count, count));
            foreach (var buffer in buffers)
            {
                infos.Add(new DescriptorBufferInfo(buffer.Handle, 0, buffer.Size));
            }
        }

        var infoArray = infos.ToArray();
        var imageInfoArray = imageInfos.ToArray();
        var writeArray = new WriteDescriptorSet[writes.Count];
        fixed (DescriptorBufferInfo* infoPointer = infoArray)
        fixed (DescriptorImageInfo* imageInfoPointer = imageInfoArray)
        {
            for (var index = 0; index < writes.Count; index++)
            {
                var (binding, type, first, count) = writes[index];
                writeArray[index] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = binding,
                    DescriptorCount = (uint)count,
                    DescriptorType = type,
                    PBufferInfo = type == DescriptorType.StorageBuffer ? infoPointer + first : null,
                    PImageInfo = type == DescriptorType.StorageBuffer ? null : imageInfoPointer + first,
                };
            }

            fixed (WriteDescriptorSet* writePointer = writeArray)
            {
                vk.UpdateDescriptorSets(device, (uint)writeArray.Length, writePointer, 0, null);
            }
        }

        var pushData = new uint[PushData.DwordCount];
        if (layout.UsesPushData)
        {
            shaderData.CopyTo(pushData, (int)layout.PushDataStartDword);
        }

        var command = new CommandBuffer(_harness.Scheduler.Current.Handle);
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.HostWriteBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        };
        vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &barrier, 0, null, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        if (layout.UsesPushData)
        {
            fixed (uint* pointer = pushData)
            {
                vk.CmdPushConstants(command, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, PushData.ByteSize, pointer);
            }
        }

        vk.CmdDispatch(command, groupsX, groupsY, groupsZ);
        var after = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit | AccessFlags.HostReadBit,
        };
        vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, 0, 1, &after, 0, null, 0, null);
    }

    public byte[] ReadBack(GpuBuffer buffer, ulong offset, ulong size) => _harness.ReadBack(buffer.Handle, offset, size);

    public void Dispose()
    {
        var vk = _harness.Vk;
        var device = _harness.Device.Device;
        _harness.Run(() =>
        {
            _harness.Scheduler.Finish();
            foreach (var buffer in _buffers)
            {
                buffer.Dispose();
            }
        });
        foreach (var sampler in _samplers)
        {
            vk.DestroySampler(device, sampler, null);
        }

        vk.DestroyPipeline(device, _pipeline, null);
        vk.DestroyDescriptorPool(device, _pool, null);
        vk.DestroyPipelineLayout(device, _pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, _setLayout, null);
    }
}
