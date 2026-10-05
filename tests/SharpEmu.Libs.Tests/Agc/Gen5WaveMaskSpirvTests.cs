// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Checks lane-specific wave predicates and debug-condition branch translation.
public sealed class Gen5WaveMaskSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    public void WaveMaskPredicate_IsTestedAtCurrentLaneBit(ShaderStage stage)
    {
        // V_CMP_EQ_F32 vcc, v0, v1 writes VCC at run time, which re-materialises
        // the per-lane _vcc predicate from the wave mask via IsWaveMaskActive.
        var spirv = Compile([0x7C04_0300u], stage);

        // Graphics single-lane emulation uses 1; compute shifts it by the lane ID. The
        // predicate is `(mask & lane_bit) != 0`. The whole-word bug emitted `mask != 0`
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

    // True when OpBitwiseAnd consumes a constant or shifted current-lane bit.
    private static bool ContainsLaneBitMaskedWaveTest(byte[] spirv)
    {
        var laneBitConstIds = new HashSet<uint>();

        // Pass 1: collect OpConstant result-ids whose integer value is 1.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43; a 32-bit constant occupies 4 words and a 64-bit one
            // 5 (opcode, resultType, resultId, valueLow[, valueHigh]).
            if (op != 43 || wordCount is not (4 or 5))
            {
                continue;
            }

            var resultId = ReadWord(spirv, offset + 8);
            var low = ReadWord(spirv, offset + 12);
            var high = wordCount == 5 ? ReadWord(spirv, offset + 16) : 0u;
            if (low == 1 && high == 0)
            {
                laneBitConstIds.Add(resultId);
            }
        }

        // Compute forms the lane bit with OpShiftLeftLogical, then may select
        // zero for lanes outside the guest wave. Follow those result IDs too.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op == 196 && wordCount == 5 &&
                laneBitConstIds.Contains(ReadWord(spirv, offset + 12)))
            {
                laneBitConstIds.Add(ReadWord(spirv, offset + 8));
            }
            else if (op == 169 && wordCount == 6 &&
                laneBitConstIds.Contains(ReadWord(spirv, offset + 16)))
            {
                laneBitConstIds.Add(ReadWord(spirv, offset + 8));
            }
        }

        // Look for an OpBitwiseAnd that consumes a current-lane bit.
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

    private static byte[] Compile(uint[] programWords, ShaderStage stage = ShaderStage.Compute)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error), error);
        var request = ResourceTestProgram.Request(program, stage: stage, userDataCount: 16);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        return shader.Spirv;
    }
}
