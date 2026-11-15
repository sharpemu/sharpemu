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

    // v_cvt_u32_f32 for every sign and exponent with the 64 lowest and 64 highest mantissas: NaN and every negative value
    // (-0 and negative NaN included) give zero, +Inf and values from 2^32 saturate, the rest truncate.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsignedConversionFollowsTheGuestForEveryExponent(bool preserveNan)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        const uint lanes = 1024;
        var rows = new byte[512 * 128 * 8];
        for (var head = 0u; head < 512; head++)
        for (var low = 0u; low < 128; low++)
        {
            var bits = (head << 23) | (low < 64 ? low : 0x7F_FFFF - (low - 64));
            BinaryPrimitives.WriteUInt32LittleEndian(rows.AsSpan((int)((head * 128 + low) * 8)), bits);
        }

        var program = Program(
            Vop2(0, "VAddU32", 1, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            Vop2(4, "VLshlrevB32", 7, Operand(3), Gen5Operand.Vector(1)),
            BufferAccess(8, "BufferLoadDword", 4, vectorData: 2, offsetEnabled: true, vectorAddress: 7),
            Vop1(16, "VCvtU32F32", 3, Gen5Operand.Vector(2)),
            BufferAccess(20, "BufferStoreDword", 4, offset: 4, vectorData: 3, offsetEnabled: true, vectorAddress: 7),
            EndProgram(28));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = lanes, ThreadCountX = lanes, WaveSize = 32, ShaderSignedZeroInfNanPreserveFloat32Supported = preserveNan,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var buffer = runner.CreateBuffer(rows);
        var registers = new uint[256];
        registers[6] = (uint)rows.Length;
        for (uint first = 0; first < rows.Length / 8; first += lanes)
        {
            registers[8] = first;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        }

        var result = runner.ReadBack(buffer, 0, (ulong)rows.Length);
        for (var row = 0; row < rows.Length / 8; row++)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(row * 8));
            var value = BitConverter.UInt32BitsToSingle(bits);
            var expected = float.IsNaN(value) || bits >= 0x8000_0000 ? 0u : value >= 4294967296f ? uint.MaxValue : (uint)value;
            Assert.True(expected == BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(row * 8 + 4)),
                $"0x{bits:X8} converted to 0x{BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(row * 8 + 4)):X8}, not 0x{expected:X8}");
        }

        harness.AssertNoValidationMessages();
    }
}
