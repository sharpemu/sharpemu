// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// v_cmp_class_f32 classifies the value after its abs/neg input modifiers.
public sealed class Gen5VCmpClassSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ushort OpBitcast = 124;
    private const ushort OpFNegate = 127;
    private const ushort OpBitwiseAnd = 199;
    private const uint SignMask = 0x8000_0000;

    // v_cmp_class_f32 s[106:107], -|v36|, 0x38 (mask: -normal | -denormal | -zero), VOP3 form.
    // word0: VOP3 prefix, opcode 0x88, abs on src0, vdst/sdst 106. word1: src0 = v36, src1 = inline 56, neg on src0.
    private static readonly uint[] NegatedAbsoluteValue =
    [
        0xD4000000u | (0x88u << 16) | (1u << 8) | 106u,
        (256u + 36u) | ((128u + 56u) << 9) | (1u << 29),
        0xBF810000u,
    ];

    private static readonly uint[] PlainValue =
    [
        0xD4000000u | (0x88u << 16) | 106u,
        (256u + 36u) | ((128u + 56u) << 9),
        0xBF810000u,
    ];

    [Fact]
    public void ClassSignComesFromTheValueAfterAbsAndNegModifiers()
    {
        // The sign test is `bitcast(float source) & 0x80000000`; with -|v| the float source is an
        // OpFNegate, so the bitcast under the mask must consume it (the raw register bits would
        // still carry the original sign and classify +0.5 as positive).
        var spirv = Compile(NegatedAbsoluteValue);
        Assert.True(
            SignTestReadsModifiedValue(spirv),
            "the class sign must be taken from the abs/neg-modified float, not the raw register bits");
    }

    [Fact]
    public void ClassWithoutModifiersTakesTheSignFromTheRegisterBits()
    {
        var spirv = Compile(PlainValue);
        Assert.False(SignTestReadsModifiedValue(spirv));
    }

    // True when some OpBitwiseAnd(x, 0x80000000) has x = OpBitcast(OpFNegate(...)).
    private static bool SignTestReadsModifiedValue(byte[] spirv)
    {
        var negations = new HashSet<uint>();
        var constants = new HashSet<uint>();
        var bitcastsOfNegations = new HashSet<uint>();
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            switch (op)
            {
                // OpConstant: (type, result, value).
                case 43 when wordCount == 4 && ReadWord(spirv, offset + 12) == SignMask:
                    constants.Add(ReadWord(spirv, offset + 8));
                    break;
                case OpFNegate when wordCount >= 4:
                    negations.Add(ReadWord(spirv, offset + 8));
                    break;
                case OpBitcast when wordCount == 4 && negations.Contains(ReadWord(spirv, offset + 12)):
                    bitcastsOfNegations.Add(ReadWord(spirv, offset + 8));
                    break;
            }
        }

        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op == OpBitwiseAnd && wordCount == 5 &&
                bitcastsOfNegations.Contains(ReadWord(spirv, offset + 12)) &&
                constants.Contains(ReadWord(spirv, offset + 16)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)> EnumerateInstructions(byte[] spirv)
    {
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
