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

// v_cvt_u32_f32 and v_cvt_i32_f32 saturate and turn NaN into zero, and v_min_f32 and
// v_max_f32 return the other operand when one is NaN. UE's motion blur turns a NaN velocity
// into a sample loop bound this way; with host-defined results the loop never ends.
public sealed class FloatNanSemanticsDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint QuietNan = 0x7FC0_0000;
    private const uint NegativeNan = 0xFFC0_0000;
    private const uint PositiveInfinity = 0x7F80_0000;

    [Theory]
    [InlineData(QuietNan, 0x4000_0000u, 0u, 0u, 0x4000_0000u, 0x4000_0000u)]
    [InlineData(NegativeNan, 0x4080_0000u, 0u, 0u, 0x4080_0000u, 0x4080_0000u)]
    [InlineData(0x4000_0000u, QuietNan, 2u, 2u, 0x4000_0000u, 0x4000_0000u)]
    [InlineData(PositiveInfinity, 0x3F80_0000u, uint.MaxValue, 0x7FFF_FFFFu, 0x3F80_0000u, PositiveInfinity)]
    [InlineData(0xBF80_0000u, 0x3F80_0000u, 0u, 0xFFFF_FFFFu, 0xBF80_0000u, 0x3F80_0000u)]
    [InlineData(0x4F95_02F9u, 0x3F80_0000u, uint.MaxValue, 0x7FFF_FFFFu, 0x3F80_0000u, 0x4F95_02F9u)]
    [InlineData(0xCF32_D05Eu, 0x3F80_0000u, 0u, 0x8000_0000u, 0xCF32_D05Eu, 0x3F80_0000u)]
    [InlineData(0x406C_CCCDu, 0x3F80_0000u, 3u, 3u, 0x3F80_0000u, 0x406C_CCCDu)]
    public void ConversionsAndMinMaxFollowTheGuestNanRules(
        uint value, uint other, uint unsignedResult, uint signedResult, uint minimum, uint maximum)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            MoveVectorFromScalar(0, 1, 9),
            Vop1(4, "VCvtU32F32", 2, Gen5Operand.Scalar(8)),
            Vop1(8, "VCvtI32F32", 3, Gen5Operand.Scalar(8)),
            Vop2(12, "VMinF32", 5, Gen5Operand.Scalar(8), Gen5Operand.Vector(1)),
            Vop2(16, "VMaxF32", 6, Gen5Operand.Scalar(8), Gen5Operand.Vector(1)),
            BufferAccess(20, "BufferStoreDword", 4, offset: 0, vectorData: 2),
            BufferAccess(28, "BufferStoreDword", 4, offset: 4, vectorData: 3),
            BufferAccess(36, "BufferStoreDword", 4, offset: 8, vectorData: 5),
            BufferAccess(44, "BufferStoreDword", 4, offset: 12, vectorData: 6),
            EndProgram(52));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = value;
        registers[9] = other;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var bytes = runner.ReadBack(result, 0, 4 * sizeof(uint));
        Assert.Equal(
            [unsignedResult, signedResult, minimum, maximum],
            Enumerable.Range(0, 4).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)))).ToArray());
        harness.AssertNoValidationMessages();
    }
}
