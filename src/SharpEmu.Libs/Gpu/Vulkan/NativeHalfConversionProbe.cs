// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// The CPU mirror of the two integer f16 conversion sequences the Gen5 translator emits by
// default, plus the vectors the device probe measures the native GLSL ext instructions with.
// Kept next to the probe because it is the reference the probe compares against: native is
// only used when it reproduces these results bit for bit.
internal static class HalfConversionReference
{
    // Mirrors Gen5SpirvTranslator.EmitHalfToFloat: the f32 bits of the f16 in the low 16 bits.
    public static uint HalfToFloatBits(uint halfBits)
    {
        var sign = (halfBits & 0x8000u) << 16;
        var exponent = (halfBits >> 10) & 0x1Fu;
        var mantissa = halfBits & 0x3FFu;

        var normal = ((exponent + 112u) << 23) | (mantissa << 13);
        var infinityNan = 0x7F80_0000u | (mantissa << 13);

        var highBit = (uint)BitOperations.Log2(mantissa | 1u);
        var shift = (int)(23u - highBit);
        var subFraction = (mantissa << shift) & 0x7F_FFFFu;
        var subnormal = mantissa != 0 ? (((highBit + 103u) << 23) | subFraction) : 0u;

        var magnitude = exponent == 0 ? subnormal : exponent == 31 ? infinityNan : normal;
        return sign | magnitude;
    }

    // Mirrors Gen5SpirvTranslator.EmitFloatToHalf: the f16 bits, round to nearest even.
    // NaN collapses to one quiet payload (sign | 0x7C00 | 0x200) by construction.
    public static uint FloatToHalfBits(uint bits)
    {
        var sign = (bits >> 16) & 0x8000u;
        var absolute = bits & 0x7FFF_FFFFu;

        var isInfinityNan = absolute >= 0x7F80_0000u;
        var isNan = absolute > 0x7F80_0000u;
        var infinityNan = sign | 0x7C00u | (isNan ? 0x200u : 0u);

        var exponent = absolute >> 23;
        var mantissa = absolute & 0x7F_FFFFu;
        var significand = mantissa | 0x80_0000u;

        var roundBit = (significand >> 13) & 1u;
        var rounded = (significand + 0xFFFu + roundBit) >> 13;
        var halfExponent = exponent - 112u;
        var normalBits = (halfExponent << 10) + (rounded - 0x400u);
        var normal = exponent >= 113u ? normalBits >= 0x7C00u ? 0x7C00u : normalBits : 0u;

        var distance = 126u - exponent;
        var shift = (int)(distance > 25u ? 25u : distance);
        var shiftMask = (1u << shift) - 1u;
        var halfWay = 1u << (shift - 1);
        var lowBits = significand & shiftMask;
        var quotient = significand >> shift;
        var roundUp = lowBits > halfWay || (lowBits == halfWay && (quotient & 1u) != 0u);
        var subnormal = quotient + (roundUp ? 1u : 0u);

        var finite = exponent <= 112u ? subnormal : normal;
        return isInfinityNan ? infinityNan : sign | finite;
    }

    // Every f16 bit pattern, every f16 value widened to f32, the midpoint between adjacent
    // f16 values and that midpoint one f32 ulp either way (the rounding decisions), the
    // overflow boundary, signed zeroes, infinities, NaNs and f32 subnormals. Padded to a
    // multiple of 64 so the probe dispatch needs no bounds check. Each entry is measured
    // twice: as the f16 in its low 16 bits, and as an f32 bit pattern.
    public static uint[] BuildTestVectors()
    {
        var values = new List<uint>(1 << 19);
        for (uint bits = 0; bits <= 0xFFFFu; bits++)
        {
            values.Add(bits);
            values.Add(HalfToFloatBits(bits));
        }

        // 0x7BFF is the largest finite f16; pair it with its successor to cover the overflow edge.
        for (uint bits = 0; bits <= 0x7BFFu; bits++)
        {
            var low = (double)BitConverter.UInt32BitsToSingle(HalfToFloatBits(bits));
            var high = (double)BitConverter.UInt32BitsToSingle(HalfToFloatBits(bits + 1));
            var midpoint = BitConverter.SingleToUInt32Bits((float)(low + (high - low) / 2));
            for (var offset = -1; offset <= 1; offset++)
            {
                var candidate = (uint)(midpoint + offset);
                values.Add(candidate);
                values.Add(candidate | 0x8000_0000u);
            }
        }

        float[] specials =
        [
            0f, -0f, 1f, -1f, 65504f, -65504f, 65520f, -65520f, 65536f, -65536f,
            1e30f, -1e30f, float.MaxValue, float.MinValue, float.Epsilon, -float.Epsilon,
            float.PositiveInfinity, float.NegativeInfinity,
            5.96046448e-8f, 6.09755516e-5f, -6.09755516e-5f, 3.05175781e-5f, 2.98023224e-8f,
        ];
        foreach (var value in specials)
        {
            values.Add(BitConverter.SingleToUInt32Bits(value));
        }

        uint[] rawSpecials =
        [
            0x7F80_0001u, 0xFF80_0001u, 0x7FC0_0000u, 0xFFC0_0000u, 0x7FFF_FFFFu, 0xFFFF_FFFFu,
            0x0000_0001u, 0x8000_0001u, 0x007F_FFFFu, 0x807F_FFFFu, 0x0080_0000u,
        ];
        values.AddRange(rawSpecials);

        while (values.Count % 64 != 0)
        {
            values.Add(0);
        }

        return [.. values];
    }

    // The probe's match rule. Every finite value, every zero and every infinity must be
    // bit-exact, sign included. A NaN only has to stay a NaN: the payload and the sign of a
    // NaN are not defined by either side. The emulation picks its own payload for f32->f16
    // (sign | 0x7C00 | 0x200) and carries the f16 payload through for f16->f32, while the
    // measured hardware convert canonicalizes to a positive quiet NaN and drops the sign
    // (Apple M4: every one of the 2314 disagreements out of 643200 is a negative NaN whose
    // sign the hardware cleared). Nothing a shader computes from a NaN can see that
    // difference short of inspecting the bits, and the alternative is paying ~20 integer
    // ops on every f16 conversion in the program to define a NaN payload the ISA leaves open.
    public static bool Matches(uint expected, uint actual, bool asHalf)
    {
        if (expected == actual)
        {
            return true;
        }

        return asHalf
            ? IsHalfNan(expected) && IsHalfNan(actual)
            : IsFloatNan(expected) && IsFloatNan(actual);
    }

    private static bool IsHalfNan(uint bits) => (bits & 0x7C00u) == 0x7C00u && (bits & 0x3FFu) != 0;

    private static bool IsFloatNan(uint bits) => (bits & 0x7F80_0000u) == 0x7F80_0000u && (bits & 0x7F_FFFFu) != 0;
}

// Runs the fixed f16 conversion probe once per device: the native GLSL UnpackHalf2x16 /
// PackHalf2x16 are used by the translator only when this says the device reproduces the
// emulator's own conversion. Never fatal: a failure leaves the exact emulation in place.
internal static unsafe class NativeHalfConversionProbe
{
    private const int ReportedExamples = 6;

    // Note carries the reason the probe could not run at all; Detail the first few disagreements.
    public readonly record struct Result(bool Exact, int Mismatches, int Tested, string? Note, string? Detail = null);

    // SHARPEMU_NATIVE_HALF=0 forces the emulation, =1 forces native without probing.
    public static bool? ReadOverride() =>
        Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_HALF") switch
        {
            "0" => false,
            "1" => true,
            _ => null,
        };

    public static Result Run(GpuDeviceInfo device, SubmissionScheduler scheduler, Func<CommandBuffer> beginCommand)
    {
        var vectors = HalfConversionReference.BuildTestVectors();
        try
        {
            var results = Measure(device, scheduler, beginCommand, vectors);
            var mismatches = 0;
            var examples = new List<string>(ReportedExamples);
            for (var index = 0; index < vectors.Length; index++)
            {
                var wide = HalfConversionReference.HalfToFloatBits(vectors[index] & 0xFFFFu);
                if (!HalfConversionReference.Matches(wide, results[2 * index], asHalf: false))
                {
                    mismatches++;
                    if (examples.Count < ReportedExamples)
                    {
                        examples.Add($"h2f(0x{vectors[index] & 0xFFFF:X4})=0x{results[2 * index]:X8} want 0x{wide:X8}");
                    }
                }

                var narrow = HalfConversionReference.FloatToHalfBits(vectors[index]);
                if (!HalfConversionReference.Matches(narrow, results[2 * index + 1], asHalf: true))
                {
                    mismatches++;
                    if (examples.Count < ReportedExamples)
                    {
                        examples.Add($"f2h(0x{vectors[index]:X8})=0x{results[2 * index + 1]:X4} want 0x{narrow:X4}");
                    }
                }
            }

            return new Result(
                mismatches == 0,
                mismatches,
                2 * vectors.Length,
                null,
                examples.Count == 0 ? null : string.Join("; ", examples));
        }
        catch (Exception exception)
        {
            return new Result(false, -1, 2 * vectors.Length, exception.Message);
        }
    }

    private static uint[] Measure(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        Func<CommandBuffer> beginCommand,
        uint[] vectors)
    {
        var vk = device.Vk;
        var inputBytes = (ulong)vectors.Length * sizeof(uint);
        var outputBytes = 2 * inputBytes;

        using var input = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, inputBytes);
        using var output = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, outputBytes);
        MemoryMarshal.AsBytes<uint>(vectors).CopyTo(input.Mapped);
        input.Flush(0, inputBytes);
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
            infos[0] = new DescriptorBufferInfo(input.Handle, 0, inputBytes);
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

            var spirv = SpirvFixedShaders.CreateHalfConversionProbe();
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
            vk.CmdDispatch(command, (uint)vectors.Length / 64, 1, 1);
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
            throw new InvalidOperationException($"The f16 conversion probe could not create its {operation}: {result}");
        }
    }
}
