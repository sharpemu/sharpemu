// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Checks that relative register accesses resolve without a dynamically indexed register
// file, and rejects invalid relative sources.
public sealed class Gen5MoveRelativeSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    // VOP1: [31:25]=0b0111111, [24:17]=vdst, [16:9]=op, [8:0]=src0
    // (src0 >= 256 selects a VGPR).
    private const uint Vop1 = 0x7E000000;

    // SOP1 s_mov_b32 m0, <inline 2>: [31:23]=0b101111101, [22:16]=sdst,
    // [15:8]=op(0x03), [7:0]=ssrc0. m0 is SGPR 124, inline constant 2 is 130.
    private const uint SMovM0 = 0xBE800000u | (124u << 16) | (0x03u << 8) | 130u;

    // s_mov_b32 m0, s3: a value the translator cannot bound.
    private const uint SMovM0FromUserData = 0xBE800000u | (124u << 16) | (0x03u << 8) | 3u;

    [Fact]
    public void MovrelsB32_ReadsTheSourceRegisterThroughAComputedIndex()
    {
        // s_mov_b32 m0, s3 ; v_movrels_b32 v5, v3   ->   v5 = vgpr[3 + m0]
        var spirv = Compile([SMovM0FromUserData, Vop1 | (5u << 17) | (0x43u << 9) | (256u + 3u)]);

        AssertResolvesRelativeRegistersWithSelects(spirv, "V_MOVRELS_B32");
    }

    [Fact]
    public void MovreldB32_WritesTheDestinationRegisterThroughAComputedIndex()
    {
        // s_mov_b32 m0, s3 ; v_movreld_b32 v5, v3   ->   vgpr[5 + m0] = v3
        var spirv = Compile([SMovM0FromUserData, Vop1 | (5u << 17) | (0x42u << 9) | (256u + 3u)]);

        AssertResolvesRelativeRegistersWithSelects(spirv, "V_MOVRELD_B32");
    }

    [Fact]
    public void MovrelsdB32_TranslatesWithoutDroppingShader()
    {
        // s_mov_b32 m0, s3 ; v_movrelsd_b32 v5, v3  ->  vgpr[5 + m0] = vgpr[3 + m0]
        var spirv = Compile([SMovM0FromUserData, Vop1 | (5u << 17) | (0x44u << 9) | (256u + 3u)]);

        AssertResolvesRelativeRegistersWithSelects(spirv, "V_MOVRELSD_B32");
    }

    [Fact]
    public void Movrelsd2B32_TranslatesWithoutDroppingShader()
    {
        // s_mov_b32 m0, 2 ; v_movrelsd_2_b32 v5, v3, which splits m0 into two
        // 10-bit halves (source index in [9:0], destination index in [25:16]).
        var spirv = Compile([SMovM0, Vop1 | (5u << 17) | (0x48u << 9) | (256u + 3u)]);

        AssertResolvesRelativeRegistersWithSelects(spirv, "V_MOVRELSD_2_B32");
    }

    [Theory]
    [InlineData(0x43u)]
    [InlineData(0x42u)]
    [InlineData(0x44u)]
    public void RelativeMovesWithAKnownM0_UseConstantRegisterNumbers(uint opcode)
    {
        // s_mov_b32 m0, 2 lets the translator pick the register directly, so the VGPR
        // array is never indexed at run time.
        var spirv = Compile([SMovM0, Vop1 | (5u << 17) | (opcode << 9) | (256u + 3u)]);

        Assert.False(
            HasDynamicVectorRegisterAccess(spirv),
            "a relative move with a known M0 must not index the VGPR array at run time");
    }

    [Fact]
    public void MovrelsB32_RejectsANonVectorSource()
    {
        // v_movrels_b32 v5, s3. The relative source is architecturally a VGPR;
        // an SGPR encoding is malformed and must fail translation rather than
        // silently read the wrong register file.
        Assert.False(
            TryCompile(
                [SMovM0, Vop1 | (5u << 17) | (0x43u << 9) | 3u],
                out _,
                out var error));
        Assert.Contains("vector register", error, StringComparison.Ordinal);
    }

    // A dynamically indexed private register array is lowered to thread memory on Metal, so no
    // access chain may take a computed index; the relative access compares the computed register
    // number with each candidate register (OpIEqual) and selects its value (OpSelect).
    private static void AssertResolvesRelativeRegistersWithSelects(byte[] spirv, string opcode)
    {
        var constants = new HashSet<uint>();
        var equalities = 0;
        var selects = 0;
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43, OpConstantNull = 46: (opcode, resultType, resultId, ...).
            if (op is 43 or 46 && wordCount >= 3)
            {
                constants.Add(ReadWord(spirv, offset + 8));
            }

            // OpIEqual = 170, OpSelect = 169.
            equalities += op == 170 ? 1 : 0;
            selects += op == 169 ? 1 : 0;
        }

        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpAccessChain = 65: (opcode, resultType, resultId, base, index...).
            if (op != 65)
            {
                continue;
            }

            for (var index = 4; index < wordCount; index++)
            {
                Assert.True(
                    constants.Contains(ReadWord(spirv, offset + index * sizeof(uint))),
                    $"{opcode} must not index a register array with a computed index");
            }
        }

        Assert.True(equalities > 1 && selects > 1, $"{opcode} must select the register its computed index names");
    }

    // True when some access chain takes a computed index, i.e. registers are indexed at run time.
    private static bool HasDynamicVectorRegisterAccess(byte[] spirv)
    {
        var constants = new HashSet<uint>();
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43, OpConstantNull = 46: (opcode, resultType, resultId, ...).
            if (op is 43 or 46 && wordCount >= 3)
            {
                constants.Add(ReadWord(spirv, offset + 8));
            }
        }

        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpAccessChain = 65: (opcode, resultType, resultId, base, index...).
            for (var index = 4; op == 65 && index < wordCount; index++)
            {
                if (!constants.Contains(ReadWord(spirv, offset + index * sizeof(uint))))
                {
                    return true;
                }
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
        Assert.True(TryCompile(programWords, out var spirv, out var error), error);
        return spirv;
    }

    private static bool TryCompile(uint[] programWords, out byte[] spirv, out string error)
    {
        spirv = [];
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out error))
        {
            return false;
        }

        var request = ResourceTestProgram.Request(program, userDataCount: 16);
        if (!Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error))
        {
            return false;
        }

        spirv = shader.Spirv;
        return true;
    }
}
