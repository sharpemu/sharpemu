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

public sealed class ScalarBitClearDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ClearsSelectedBitAndPreservesCondition(bool emptyExecutionMask, bool overlapDestination, bool condition)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Gen5ScalarBitClearTests.CreateReadbackProgram(emptyExecutionMask, overlapDestination, condition));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] };
        var registers = new uint[256];
        registers[6] = 64;
        foreach (var values in Gen5ScalarBitClearTests.Values)
        {
            registers[8] = (uint)values[0];
            registers[9] = (uint)values[1];
            var expected = overlapDestination ? registers[8] & ~(1u << (int)(registers[8] & 31)) : (uint)values[2];
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var actual = runner.ReadBack(output, 0, 8);
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual));
            Assert.Equal(condition ? 0u : 1u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)));
        }
        harness.AssertNoValidationMessages();
    }
}
