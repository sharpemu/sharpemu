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

public sealed class ThreeInputXorDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void UsesThreeInputsAndPreservesInactiveDestination(bool conversionEnabled, bool overlapDestination)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5ThreeInputXorTests.CreateReadbackProgram(conversionEnabled, overlapDestination));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = [result],
        };
        var registers = new uint[256];
        registers[6] = 64;
        foreach (var inputValues in Gen5ThreeInputXorTests.Values)
        {
            registers[8] = (uint)inputValues[0];
            registers[9] = (uint)inputValues[1];
            registers[10] = (uint)inputValues[2];
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var actual = runner.ReadBack(result, 0, 4);
            var expected = conversionEnabled ? (uint)inputValues[3] : overlapDestination ? registers[10] : 0xCAFEBABE;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        }
        harness.AssertNoValidationMessages();
    }
}
