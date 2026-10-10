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

// Float VOP2 SDWA with DWORD selects, used only for its source negates, output modifier and
// clamp. The instruction words are taken from Ghost of Yotei's TAA resolve (0xD42889155B0A6B9F).
public sealed class SdwaFloatModifierDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void NegateOutputModifierAndClampApply()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        const float x = 1.5f, y = 0.25f, z = 3f, w = -2f;
        var program = Program(
            MoveVectorFromScalar(0, 42, 8),
            MoveVectorFromScalar(4, 41, 9),
            MoveVectorFromScalar(8, 12, 10),
            MoveVectorFromScalar(12, 2, 11),
            MoveVectorFromScalar(16, 15, 8),
            MoveVectorFromScalar(20, 11, 10),
            // v_add_f32_sdwa v4, -v42, -v41
            Sdwa(24, 0x060852F9u, 0x1616062Au),
            // v_mul_f32_sdwa v13, v12, v2 div:2
            Sdwa(32, 0x101A04F9u, 0x0606C60Cu),
            // v_mul_f32_sdwa v1, v15, v11 clamp
            Sdwa(40, 0x100216F9u, 0x0606260Fu),
            Vop2(48, "VOrB32", 20, Gen5Operand.Vector(4), Gen5Operand.Vector(4)),
            Vop2(52, "VOrB32", 21, Gen5Operand.Vector(13), Gen5Operand.Vector(13)),
            Vop2(56, "VOrB32", 22, Gen5Operand.Vector(1), Gen5Operand.Vector(1)),
            Vop2(60, "VAndB32", 1, Operand(0), Gen5Operand.Vector(1)),
            BufferAccess(64, "BufferStoreDwordx3", 4, dwords: 3, vectorData: 20, offsetEnabled: true, vectorAddress: 1),
            EndProgram(72));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(16);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
        };
        var registers = new uint[256];
        registers[6] = 16;
        registers[8] = BitConverter.SingleToUInt32Bits(x);
        registers[9] = BitConverter.SingleToUInt32Bits(y);
        registers[10] = BitConverter.SingleToUInt32Bits(z);
        registers[11] = BitConverter.SingleToUInt32Bits(w);
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        harness.AssertNoValidationMessages();
        var bytes = runner.ReadBack(output, 0, 12);
        Assert.Equal(-(x + y), BitConverter.ToSingle(bytes, 0));
        Assert.Equal(z * w * 0.5f, BitConverter.ToSingle(bytes, 4));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 8));
    }

    private static Gen5ShaderInstruction Sdwa(uint pc, uint word0, uint word1) =>
        Gen5Float16ArithmeticTests.Decode([word0, word1, 0xBF81_0000u]).Instructions[0] with { Pc = pc };
}
