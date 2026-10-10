// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class PortableBufferFormatDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint MaxFloatUlps = 4;
    private static readonly uint[] Swizzles = [0xFAC, 0x3AC, 0xA04, 0xF2E, 0x249];

    [Fact]
    public void RuntimeDescriptorWordsReadEveryFormatLikeABakedDescriptor()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        var program = Program(BufferLoad(0, 0, dwords: 4, formatted: true), BufferStore(8, 4, dwords: 4), EndProgram(16));
        var plan = Extract(program);
        var portable = Compile(plan, program, ResourceSpecialization.Default(plan.Info), portableBuffers: true);
        var source = Enumerable.Range(0, 64).Select(static index => (byte)(index * 37 + 11)).ToArray();
        using var harness = new ImageTestHarness(vulkan!);
        for (uint format = 1; format < 128; format++)
        {
            if (!Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out var numberFormat) || dataFormat == 0)
            {
                continue;
            }

            foreach (var swizzle in Swizzles)
            {
                var specialization = ResourceSpecialization.Default(plan.Info);
                specialization.Buffers[0] = new BufferSpecialization(0, format, swizzle);
                var baked = Run(harness, Compile(plan, program, specialization, portableBuffers: false), source, null);
                var runtime = Run(harness, portable, source, [0, (format << 12) | swizzle, 0, 0]);
                for (var component = 0; component < 4; component++)
                {
                    var expected = BinaryPrimitives.ReadUInt32LittleEndian(baked.AsSpan(component * sizeof(uint)));
                    var actual = BinaryPrimitives.ReadUInt32LittleEndian(runtime.AsSpan(component * sizeof(uint)));
                    Assert.True(Matches(expected, actual, numberFormat),
                        $"format {format} swizzle 0x{swizzle:X3} component {component}: baked 0x{expected:X8}, runtime 0x{actual:X8}");
                }
            }
        }

        harness.AssertNoValidationMessages();
    }

    private static bool Matches(uint expected, uint actual, uint numberFormat)
    {
        if (expected == actual)
        {
            return true;
        }

        if (numberFormat is 4 or 5 || float.IsNaN(BitConverter.UInt32BitsToSingle(expected)) != float.IsNaN(BitConverter.UInt32BitsToSingle(actual)))
        {
            return false;
        }

        static long Ordered(uint bits) => (bits & 0x8000_0000) != 0 ? -(long)(bits & 0x7FFF_FFFF) : bits;
        return Math.Abs(Ordered(expected) - Ordered(actual)) <= MaxFloatUlps;
    }

    private static (ShaderCompileRequest Request, byte[] Spirv) Compile(
        ShaderResourcePlan plan, Gen5ShaderProgram program, ResourceSpecialization specialization, bool portableBuffers)
    {
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 64),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            portableBuffers: portableBuffers);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return (request, shader.Spirv);
    }

    private static byte[] Run(ImageTestHarness harness, (ShaderCompileRequest Request, byte[] Spirv) shader, byte[] source, uint[]? bufferWords)
    {
        using var runner = new LayoutComputeRunner(harness, shader.Request, shader.Spirv);
        var input = runner.CreateBuffer(source);
        var output = runner.CreateBuffer(16);
        harness.Run(() => runner.Dispatch(new uint[64],
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [input, output] }, 1,
            bufferWords: bufferWords));
        return runner.ReadBack(output, 0, 16);
    }
}
