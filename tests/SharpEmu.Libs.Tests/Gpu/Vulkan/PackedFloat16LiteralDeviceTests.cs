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

// The 32-bit literal of a packed f16 instruction is a pair of halves, like a register: op_sel and op_sel_hi pick the half that feeds
// each lane. It used to be read as one f32 (0x000073FF is a denormal), so the clamp constants and weights of UE5's TSR passes became
// zero and the pass wrote nothing but zeros and NaNs. A float inline constant is a pair of halves too: its f16 value in the low half
// and zero in the high half.
public sealed class PackedFloat16LiteralDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    // min(16376, x) with the low half of the literal for both lanes (op_sel_hi picks the low half for source 0)
    [InlineData("VPkMinF16", 0x0000_73FFu, 0u, 0b110u, 0x5640_7800u, 0x5640_73FFu)]
    // the high half of the literal feeds the low lane and the low half feeds the high lane: (2 + 3, 1 + 8)
    [InlineData("VPkAddF16", 0x4000_3C00u, 0b001u, 0b110u, 0x4800_4200u, 0x4880_4500u)]
    // a weight in the low half scales both lanes: (0.25 * 3, 0.25 * 8)
    [InlineData("VPkMulF16", 0x0000_3400u, 0u, 0b110u, 0x4800_4200u, 0x4000_3A00u)]
    public void LiteralOperandIsAPairOfHalves(string opcode, uint literal, uint opSel, uint opSelHi, uint input, uint expected) =>
        Check(opcode, Operand(literal), [literal], opSel, opSelHi, input, expected);

    // op_sel_hi clear for the constant: both lanes read its low half, (1 + 3, 1 + 8).
    [Fact]
    public void InlineConstantFeedsBothLanes() =>
        Check("VPkAddF16", Gen5Operand.Source(242), [], 0u, 0b110u, 0x4800_4200u, 0x4880_4400u);

    // op_sel_hi set for the constant: the high lane reads its zero half, (1 + 3, 0 + 8).
    [Fact]
    public void InlineConstantHighHalfIsZero() =>
        Check("VPkAddF16", Gen5Operand.Source(242), [], 0u, 0b111u, 0x4800_4200u, 0x4800_4400u);

    // op_sel picks the zero half for the low lane and op_sel_hi the low half for the high lane, the form compilers use
    // for a (0, 1) vector: (0 + 3, 1 + 8).
    [Fact]
    public void OpSelReadsTheZeroHalfOfAnInlineConstant() =>
        Check("VPkAddF16", Gen5Operand.Source(242), [], 0b001u, 0b110u, 0x4800_4200u, 0x4880_4200u);

    // The mixed-precision multiply-add reads a literal whose op_sel_hi bit marks the source as f16 as a pair of halves too:
    // the low half 2.0 gives 2 * 3, and the high half 1.0 (op_sel) gives 1 * 3.
    [Fact]
    public void MixedPrecisionLiteralUsesTheLowHalf() =>
        Check("VFmaMixF32", Operand(0x3C00_4000u), [0x3C00_4000u], 0u, 0b001u, 0x4040_0000u, 0x40C0_0000u);

    [Fact]
    public void MixedPrecisionLiteralUsesTheHighHalfWhenOpSelSaysSo() =>
        Check("VFmaMixF32", Operand(0x3C00_4000u), [0x3C00_4000u], 0b001u, 0b001u, 0x4040_0000u, 0x4040_0000u);

    private void Check(string opcode, Gen5Operand first, uint[] literalWords, uint opSel, uint opSelHi, uint input, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var words = new[] { 0u, 0u }.Concat(literalWords).ToArray();
        var storePc = 4 + (uint)words.Length * 4;
        var program = Program(
            MoveVectorFromScalar(0, 1, 9),
            new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Vop3p, opcode, words,
                [first, Gen5Operand.Vector(1), Gen5Operand.Scalar(0)], [Gen5Operand.Vector(2)],
                new Gen5Vop3pControl(opSel, opSelHi, 0, 0, false)),
            BufferAccess(storePc, "BufferStoreDword", 4, offset: 0, vectorData: 2),
            EndProgram(storePc + 8));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[9] = input;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint))));
        harness.AssertNoValidationMessages();
    }
}
