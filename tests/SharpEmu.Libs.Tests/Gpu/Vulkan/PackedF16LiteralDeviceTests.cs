// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A 32-bit literal in a packed f16 (VOP3P) op holds two f16 halves that op_sel / op_sel_hi
// pick per lane, like a register. The instruction words are from Ghost of Yotei's TAA resolve
// (0xD42889155B0A6B9F), where reading the literal as one f32 turned the image black.
public sealed class PackedF16LiteralDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void LiteralHalvesFollowOperandSelect()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            MoveVectorFromScalar(0, 6, 8),
            MoveVectorFromScalar(4, 63, 9),
            MoveVectorFromScalar(8, 62, 10),
            // v_pk_fma_f16 v0, 0x34003800, v6, v63 op_sel:[1,1,0] op_sel_hi:[0,1,1] neg_hi:[0,0,1]
            Vop3p(12, 0xCC0E5C00u, 0x14FE0CFFu, 0x34003800u),
            // v_pk_min_f16 v4, 0x77FF, v62 op_sel_hi:[0,1]
            Vop3p(24, 0xCC114004u, 0x10027CFFu, 0x000077FFu),
            Vop2(36, "VOrB32", 20, Gen5Operand.Vector(0), Gen5Operand.Vector(0)),
            Vop2(40, "VOrB32", 21, Gen5Operand.Vector(4), Gen5Operand.Vector(4)),
            Vop2(44, "VAndB32", 1, Operand(0), Gen5Operand.Vector(1)),
            BufferAccess(48, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 20, offsetEnabled: true, vectorAddress: 1),
            EndProgram(56));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(8);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
        };
        var registers = new uint[256];
        registers[6] = 8;
        registers[8] = 0x4000_0000;  // v6: high 2.0, low 0
        registers[9] = 0x3C00_3C00;  // v63: 1.0 in both halves
        registers[10] = 0x4000_7C00; // v62: high 2.0, low +Inf
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        harness.AssertNoValidationMessages();
        var bytes = runner.ReadBack(output, 0, 8);
        // Low lane: 0.25 (literal high half) * 2 + 1 = 1.5; high lane: 0.5 (literal low half) * 2 - 1 = 0.
        // Read as one f32 (1.2e-7 in both lanes) the lanes would be 1 and -1.
        Assert.Equal(0x0000_3E00u, BitConverter.ToUInt32(bytes, 0));
        // Both lanes read the literal's low half, 32752: min(32752, +Inf), min(32752, 2).
        Assert.Equal(0x4000_77FFu, BitConverter.ToUInt32(bytes, 4));
    }

    private static Gen5ShaderInstruction Vop3p(uint pc, uint word0, uint word1, uint literal) =>
        Gen5Float16ArithmeticTests.Decode([word0, word1, literal, 0xBF81_0000u]).Instructions[0] with { Pc = pc };
}
