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

public sealed class DataShareThreadReadDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(32u, 0u, 0u, false)]
    [InlineData(32u, 0xABCD0010u, 0x100u, false)]
    [InlineData(32u, 0xFFFF0100u, 0x204u, true)]
    [InlineData(64u, 0u, 0u, false)]
    [InlineData(64u, 0xABCD0010u, 0x100u, true)]
    public void ThreadOffsetReadUsesScalarBaseOffsetAndExecutionMask(uint waveSize, uint scalarBase,
        uint byteOffset, bool maskOddLanes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareThreadReadTests.CreateReadbackProgram(scalarBase, byteOffset, maskOddLanes);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 256);
        for (var lane = 0; lane < waveSize; lane++)
        {
            var expected = maskOddLanes && (lane & 1) != 0 ? Gen5DataShareThreadReadTests.InitialWord : 1000u + (uint)lane;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }
        harness.AssertNoValidationMessages();
    }
}
