// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Checks mixed-precision fused arithmetic through the resource-plan compiler.
public sealed class Gen5FmaMixSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    // GLSL.std.450 extended-instruction numbers used by the lowering.
    private const uint GlslFma = 50;
    private const uint GlslFAbs = 4;

    [Fact]
    public void FmaMixF32_TranslatesToFmaAndDoesNotDropShader()
    {
        // V_FMA_MIX_F32 v3, v0, v1, v2
        //   op_sel_hi = 0b011  -> src0/src1 read as f16, src2 as full f32
        //   op_sel    = 0b010  -> src1 takes its high f16 half (src0 low half)
        //   neg_hi    = 0b001  -> abs(src0)
        //   neg       = 0b100  -> -src2
        var spirv = Compile([0xCC201103u, 0x9C0A0300u]);

        Assert.True(
            ContainsExtInst(spirv, GlslFma),
            "V_FMA_MIX_F32 must lower to a GLSL.std.450 Fma");
        Assert.True(
            ContainsExtInst(spirv, GlslFAbs),
            "the neg_hi modifier on a mix source must lower to an FAbs (abs-then-neg)");
    }

    [Fact]
    public void FmaMixLoF16_TranslatesWithoutDroppingShader()
    {
        // V_FMA_MIXLO_F16 v3, v0, v1, v2 with op_sel_hi = 0b111 (all sources read
        // as f16 low halves). The f32 fma result is narrowed to f16 and merged
        // into the low 16 bits of vdst; the fma itself is still emitted.
        var spirv = Compile([0xCC214003u, 0x1C0A0300u]);

        Assert.True(
            ContainsExtInst(spirv, GlslFma),
            "V_FMA_MIXLO_F16 must still lower its multiply-add to a GLSL.std.450 Fma");
    }

    // True when the module contains an OpExtInst selecting the given GLSL.std.450
    // instruction number.
    private static bool ContainsExtInst(byte[] spirv, uint instruction)
    {
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpExtInst = 12: (opcode, resultType, resultId, set, instruction, ...).
            if (op != 12 || wordCount < 5)
            {
                continue;
            }

            if (ReadWord(spirv, offset + 16) == instruction)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)> EnumerateInstructions(
        byte[] spirv)
    {
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                yield break;
            }

            yield return ((ushort)word, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error), error);
        var request = ResourceTestProgram.Request(program, userDataCount: 16);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        return shader.Spirv;
    }
}
