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

public sealed class DataShareFloatMinMaxDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [MemberData(nameof(Gen5DataShareFloatMinMaxTests.ReductionCases), MemberType = typeof(Gen5DataShareFloatMinMaxTests))]
    public void EveryLaneReducesIntoTheSharedMinimumAndMaximum(uint waveSize, uint negativeLaneMask, uint signOfEveryLane)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareFloatMinMaxTests.CreateReductionProgram(negativeLaneMask, signOfEveryLane);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 8);
        var (minimum, maximum) = Gen5DataShareFloatMinMaxTests.ExpectedBits(waveSize, negativeLaneMask, signOfEveryLane);
        Assert.Equal(minimum, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(maximum, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(sizeof(uint))));
        harness.AssertNoValidationMessages();
    }
}
