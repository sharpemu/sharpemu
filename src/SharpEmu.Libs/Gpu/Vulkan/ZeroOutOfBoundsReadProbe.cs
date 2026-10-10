// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Measures whether a storage-buffer read past the end of its descriptor range reads zero on this
// device. Every guest buffer word the translator emits carries a range test, an address clamp and
// a zero select, which exist only to produce that zero; with robustBufferAccess2 the device owes
// it already and the three operations are pure overhead. The capability bit alone does not settle
// it — MoltenVK runs on Metal, which has no hardware bounds checking — so the probe reads a bound
// range it deliberately undershoots and checks the words past it. Never run on a device that did
// not enable robustBufferAccess2: an out-of-range read there is undefined and can fault.
internal static unsafe class ZeroOutOfBoundsReadProbe
{
    // The descriptor binds this many words of a larger allocation; the words past it must read
    // zero, and the words inside it must still read the sentinel the probe wrote.
    private const uint BoundWords = 256;
    private const uint ProbeWords = 1024;
    private const uint Sentinel = 0xA5C3_F00Du;

    public readonly record struct Result(bool Zeroed, int Mismatches, int Tested, string? Note);

    // SHARPEMU_ZERO_OOB_READS=0 keeps the per-access checks, =1 drops them without measuring.
    public static bool? ReadOverride() =>
        Environment.GetEnvironmentVariable("SHARPEMU_ZERO_OOB_READS") switch
        {
            "0" => false,
            "1" => true,
            _ => null,
        };

    public static Result Run(GpuDeviceInfo device, SubmissionScheduler scheduler, Func<CommandBuffer> beginCommand)
    {
        try
        {
            var results = Measure(device, scheduler, beginCommand);
            var mismatches = 0;
            for (var index = 0u; index < ProbeWords; index++)
            {
                var expected = index < BoundWords ? Sentinel + index : 0u;
                if (results[index] != expected)
                {
                    mismatches++;
                }
            }

            return new Result(mismatches == 0, mismatches, (int)ProbeWords, null);
        }
        catch (Exception exception)
        {
            return new Result(false, -1, (int)ProbeWords, exception.Message);
        }
    }

    private static uint[] Measure(GpuDeviceInfo device, SubmissionScheduler scheduler, Func<CommandBuffer> beginCommand)
    {
        var vk = device.Vk;
        var allocationBytes = (ulong)ProbeWords * sizeof(uint);
        var boundBytes = (ulong)BoundWords * sizeof(uint);
        var outputBytes = allocationBytes;

        // The allocation is larger than the bound range and carries a sentinel everywhere, so a
        // device that simply ignores the range reads the sentinel back instead of zero.
        using var input = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, allocationBytes);
        using var output = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, outputBytes);
        var words = new uint[ProbeWords];
        for (var index = 0u; index < ProbeWords; index++)
        {
            words[index] = Sentinel + index;
        }

        MemoryMarshal.AsBytes<uint>(words).CopyTo(input.Mapped);
        input.Flush(0, allocationBytes);
        output.Mapped.Clear();
        output.Flush(0, outputBytes);

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint index = 0; index < 2; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        Require(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out var setLayout), "descriptor layout");

        DescriptorPool pool = default;
        PipelineLayout pipelineLayout = default;
        Pipeline pipeline = default;
        try
        {
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            Require(vk.CreateDescriptorPool(device.Device, &poolInfo, null, out pool), "descriptor pool");

            var boundLayout = setLayout;
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &boundLayout,
            };
            Require(vk.AllocateDescriptorSets(device.Device, &allocateInfo, out DescriptorSet set), "descriptor set");

            var infos = stackalloc DescriptorBufferInfo[2];
            infos[0] = new DescriptorBufferInfo(input.Handle, 0, boundBytes);
            infos[1] = new DescriptorBufferInfo(output.Handle, 0, outputBytes);
            var writes = stackalloc WriteDescriptorSet[2];
            for (uint binding = 0; binding < 2; binding++)
            {
                writes[binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = binding,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = infos + binding,
                };
            }

            vk.UpdateDescriptorSets(device.Device, 2, writes, 0, null);

            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &boundLayout,
            };
            Require(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out pipelineLayout), "pipeline layout");

            var spirv = SpirvFixedShaders.CreateUncheckedBufferCopyProbe();
            ShaderModule module;
            fixed (byte* code = spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code,
                };
                Require(vk.CreateShaderModule(device.Device, &moduleInfo, null, out module), "shader module");
            }

            ReadOnlySpan<byte> entryPoint = "main\0"u8;
            Silk.NET.Vulkan.Result created;
            fixed (byte* entry = entryPoint)
            {
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Layout = pipelineLayout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = module,
                        PName = entry,
                    },
                };
                created = vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out pipeline);
            }

            vk.DestroyShaderModule(device.Device, module, null);
            Require(created, "compute pipeline");

            var command = beginCommand();
            vk.CmdBindPipeline(command, PipelineBindPoint.Compute, pipeline);
            var boundSet = set;
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, pipelineLayout, 0, 1, &boundSet, 0, null);
            vk.CmdDispatch(command, ProbeWords / 64, 1, 1);
            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcAccessMask = AccessFlags2.ShaderWriteBit,
                DstAccessMask = AccessFlags2.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = output.Handle,
                Offset = 0,
                Size = outputBytes,
            };
            VulkanSynchronization.PipelineBarrier(vk, command,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, DependencyFlags.None,
                0, null, 1, &barrier, 0, null);
            scheduler.FlushAndWait();

            output.Invalidate(0, outputBytes);
            return MemoryMarshal.Cast<byte, uint>(output.Mapped).ToArray();
        }
        finally
        {
            if (pipeline.Handle != 0)
            {
                vk.DestroyPipeline(device.Device, pipeline, null);
            }

            if (pipelineLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device.Device, pipelineLayout, null);
            }

            if (pool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device.Device, pool, null);
            }

            vk.DestroyDescriptorSetLayout(device.Device, setLayout, null);
        }
    }

    private static void Require(Silk.NET.Vulkan.Result result, string operation)
    {
        if (result != Silk.NET.Vulkan.Result.Success)
        {
            throw new InvalidOperationException($"The out-of-range read probe could not create its {operation}: {result}");
        }
    }
}
