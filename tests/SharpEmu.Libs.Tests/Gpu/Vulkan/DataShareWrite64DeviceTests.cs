// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class DataShareWrite64DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [MemberData(nameof(Gen5DataShareWrite64Tests.PairedWriteCases), MemberType = typeof(Gen5DataShareWrite64Tests))]
    public void PairedWriteUsesOffsetsSourcePairsAndExecutionMask(bool global, uint firstOffset, uint secondOffset,
        bool writeEnabled, uint secondSource, bool largeStride)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareWrite64Tests.CreateReadbackProgram(global, firstOffset, secondOffset, writeEnabled, secondSource, largeStride);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        if (global) bindings[DescriptorBindingKind.GlobalDataShare] = [runner.CreateBuffer(65536)];
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        var actual = runner.ReadBack(result, 0, 16);
        var expected = Gen5DataShareWrite64Tests.ExpectedWords(firstOffset, secondOffset, writeEnabled, secondSource);
        for (var component = 0; component < expected.Length; component++)
            Assert.Equal(expected[component], BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(component * sizeof(uint))));
        harness.AssertNoValidationMessages();
    }
}
