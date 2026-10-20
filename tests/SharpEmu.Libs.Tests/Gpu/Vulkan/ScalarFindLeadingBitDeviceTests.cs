// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// S_FLBIT_I32_B32/B64 treat the source as unsigned: leading zero bits, -1 for zero.
public sealed class ScalarFindLeadingBitDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData("SFlbitI32B32", 0ul)]
    [InlineData("SFlbitI32B32", 1ul)]
    [InlineData("SFlbitI32B32", 0x10ul)]
    [InlineData("SFlbitI32B32", 0x7FFF_FFFFul)]
    [InlineData("SFlbitI32B32", 0x8000_0000ul)]
    [InlineData("SFlbitI32B32", 0xF000_0000ul)]
    [InlineData("SFlbitI32B32", 0xFFFF_FFFFul)]
    [InlineData("SFlbitI32B64", 0ul)]
    [InlineData("SFlbitI32B64", 1ul)]
    [InlineData("SFlbitI32B64", 0x8000_0000ul)]
    [InlineData("SFlbitI32B64", 0xF000_0000ul)]
    [InlineData("SFlbitI32B64", 0x1_0000_0000ul)]
    [InlineData("SFlbitI32B64", 0x8000_0000_0000_0000ul)]
    [InlineData("SFlbitI32B64", 0xF000_0000_0000_0000ul)]
    public void CountsLeadingZerosOfAnUnsignedValue(string opcode, ulong value)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Sop1(0, opcode, 16, Gen5Operand.Scalar(12)),
            MoveVectorFromScalar(4, 6, 16),
            BufferAccess(8, "BufferStoreDword", 4, vectorData: 6),
            EndProgram(16));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[12] = (uint)value;
        registers[13] = (uint)(value >> 32);
        harness.Run(() => runner.Dispatch(
            registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] },
            1));

        var isWide = opcode.EndsWith("B64", StringComparison.Ordinal);
        var expected = value == 0 ? uint.MaxValue : (uint)(isWide ? BitOperations.LeadingZeroCount(value) : BitOperations.LeadingZeroCount((uint)value));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(output, 0, 4)));
        harness.AssertNoValidationMessages();
    }
}
