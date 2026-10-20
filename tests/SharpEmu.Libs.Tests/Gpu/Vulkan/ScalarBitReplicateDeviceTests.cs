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

// S_BITREPLICATE_B64_B32 doubles every source bit into a 64-bit result.
public sealed class ScalarBitReplicateDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0x0000_0000u)]
    [InlineData(0x0000_0001u)]
    [InlineData(0x0000_8000u)]
    [InlineData(0x0001_0000u)]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0xA5A5_5A5Au)]
    [InlineData(0x1234_5678u)]
    public void EveryBitIsDoubled(uint source)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Sop1(0, "SBitreplicateB64B32", 16, Gen5Operand.Scalar(12)),
            MoveVectorFromScalar(4, 6, 16),
            MoveVectorFromScalar(8, 7, 17),
            BufferAccess(12, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 6),
            EndProgram(20));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[12] = source;
        harness.Run(() => runner.Dispatch(
            registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] },
            1));

        var expected = 0ul;
        for (var bit = 0; bit < 32; bit++)
        {
            expected |= ((ulong)((source >> bit) & 1u)) * (3ul << (2 * bit));
        }

        Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(runner.ReadBack(output, 0, 8)));
        harness.AssertNoValidationMessages();
    }
}
