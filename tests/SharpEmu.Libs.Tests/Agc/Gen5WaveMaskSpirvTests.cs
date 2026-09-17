// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Checks lane-specific wave predicates and debug-condition branch translation.
public sealed class Gen5WaveMaskSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void WaveMaskPredicate_IsTestedAtCurrentLaneBit()
    {
        // V_CMP_EQ_F32 vcc, v0, v1 writes VCC at run time, which re-materialises
        // the per-lane _vcc predicate from the wave mask via IsWaveMaskActive.
        var spirv = Compile([0x7C04_0300u]);

        // The lane's bit in single-lane emulation is the 64-bit constant 1, so the
        // predicate is `(mask & 1) != 0`. The whole-word bug emitted `mask != 0`
        // with no such mask. Require the lane-bit AND to be present.
        Assert.True(
            ContainsLaneBitMaskedWaveTest(spirv),
            "wave-mask predicate must be tested at the current lane bit "
                + "(mask & lane_bit), not as a whole-word non-zero test");
    }

    [Theory]
    [InlineData(0xBF970001u)]
    [InlineData(0xBF980001u)]
    [InlineData(0xBF990001u)]
    [InlineData(0xBF9A0001u)]
    public void DebugConditionBranchesCompileWithoutShaderDebugger(uint branch)
    {
        var spirv = Compile(
        [
            branch,
            0xBF800000, // s_nop 0
        ]);

        Assert.NotEmpty(spirv);
    }

    // True when the module contains an OpBitwiseAnd whose operand is a 64-bit
    // constant of value 1 — the current-lane bit that IsCurrentLaneSet masks the
    // wave mask with before the non-zero test.
    private static bool ContainsLaneBitMaskedWaveTest(byte[] spirv)
    {
        var laneBitConstIds = new HashSet<uint>();

        // Pass 1: collect 64-bit OpConstant result-ids whose value is 1.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43; a 64-bit constant occupies 5 words
            // (opcode, resultType, resultId, valueLow, valueHigh).
            if (op != 43 || wordCount != 5)
            {
                continue;
            }

            var resultId = ReadWord(spirv, offset + 8);
            var low = ReadWord(spirv, offset + 12);
            var high = ReadWord(spirv, offset + 16);
            if (low == 1 && high == 0)
            {
                laneBitConstIds.Add(resultId);
            }
        }

        // Pass 2: look for an OpBitwiseAnd that consumes one of those constants.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpBitwiseAnd = 199 (opcode, resultType, resultId, operand0, operand1).
            if (op != 199 || wordCount != 5)
            {
                continue;
            }

            var operand0 = ReadWord(spirv, offset + 12);
            var operand1 = ReadWord(spirv, offset + 16);
            if (laneBitConstIds.Contains(operand0) || laneBitConstIds.Contains(operand1))
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
