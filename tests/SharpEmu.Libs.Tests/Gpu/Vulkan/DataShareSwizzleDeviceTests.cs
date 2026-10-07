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

public sealed class DataShareSwizzleDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0x801Bu)]
    [InlineData(0x041Fu)]
    [InlineData(0xC020u)]
    [InlineData(0xC420u)]
    [InlineData(0xC021u)]
    [InlineData(0x0050u)]
    [InlineData(0x8000u)]
    public void SwizzlePreservesLaneGroupsAndExecutionMasks(uint selection)
    {
        foreach (var waveSize in new[] { 32u, 64u })
        foreach (var maskOddLanes in new[] { false, true })
            CheckSwizzle(selection, waveSize, waveSize, maskOddLanes);
        CheckSwizzle(selection, 32, 17, false);
    }

    private void CheckSwizzle(uint selection, uint waveSize, uint threadCount, bool maskOddLanes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareSwizzleTests.CreateReadbackProgram(selection, maskOddLanes);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = threadCount, ThreadCountX = threadCount, WaveSize = waveSize,
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
        for (var lane = 0u; lane < threadCount; lane++)
        {
            var sourceLane = selection switch
            {
                0x801B => lane ^ 3,
                0x041F => lane ^ 1,
                0xC020 => (lane & ~31u) | ((lane + 1) & 31),
                0xC420 => (lane & ~31u) | (unchecked(lane - 1) & 31),
                0xC021 => (lane & ~31u) | (lane & 1) | ((lane + 1) & 30),
                0x0050 => (lane & ~15u) | 2,
                0x8000 => lane & ~3u,
                _ => throw new ArgumentOutOfRangeException(nameof(selection)),
            };
            var expected = maskOddLanes && (lane & 1) != 0 ? 1000 + lane :
                sourceLane >= threadCount || (maskOddLanes && (sourceLane & 1) != 0) ? 0u : 1000 + sourceLane;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan((int)lane * sizeof(uint))));
        }
        harness.AssertNoValidationMessages();
    }
}
