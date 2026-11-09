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

public sealed class XorAddDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(6u, true)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(6u, false)]
    public void XorAddWrapsAndPreservesInactiveDestinations(uint destination, bool operationEnabled)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5XorAddTests.CreateReadbackProgram(destination, operationEnabled));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        var registers = new uint[256];
        registers[6] = 64;
        foreach (var testCase in Gen5XorAddTests.Results)
        {
            registers[8] = (uint)testCase[0];
            registers[9] = (uint)testCase[1];
            registers[10] = (uint)testCase[2];
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var actual = runner.ReadBack(result, 0, sizeof(uint));
            var expected = operationEnabled ? (uint)testCase[3] : destination switch
            {
                2 => registers[8],
                3 => registers[9],
                4 => registers[10],
                _ => Gen5XorAddTests.Sentinel,
            };
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        }
        harness.AssertNoValidationMessages();
    }
}
