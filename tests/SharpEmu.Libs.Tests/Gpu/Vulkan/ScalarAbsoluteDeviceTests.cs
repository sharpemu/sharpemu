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

public sealed class ScalarAbsoluteDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void PreservesMagnitudeAndConditionWithEitherExecutionMask(bool emptyExecutionMask, bool overlapDestination)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5ScalarAbsoluteTests.CreateReadbackProgram(emptyExecutionMask, overlapDestination));
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
        foreach (var inputValues in Gen5ScalarAbsoluteTests.Values)
        {
            registers[8] = (uint)inputValues[0];

            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var actual = runner.ReadBack(result, 0, 8);
            Assert.Equal((uint)inputValues[1], BinaryPrimitives.ReadUInt32LittleEndian(actual));
            Assert.Equal(registers[8] == 0 ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)));
        }
        harness.AssertNoValidationMessages();
    }
}
