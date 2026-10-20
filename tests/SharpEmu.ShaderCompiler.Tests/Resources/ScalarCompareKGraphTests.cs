// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// SOPK compares set SCC from an SGPR and a 16-bit immediate and leave the SGPR alone.
public sealed class ScalarCompareKGraphTests
{
    [Theory]
    [InlineData(0xD, 0x0Fu, 0x0010u, true)]   // s_cmpk_lt_u32
    [InlineData(0xD, 0x10u, 0x0010u, false)]
    [InlineData(0xD, 0xFFFF_FFFEu, 0xFFFFu, false)] // zero-extended immediate: 0xFFFFFFFE > 0xFFFF
    [InlineData(0x7, 0xFFFF_FFFEu, 0xFFFFu, true)] // s_cmpk_lt_i32: -2 < -1
    [InlineData(0x7, 0x0000_0001u, 0xFFFFu, false)]
    [InlineData(0x3, 0xFFFF_FFFFu, 0xFFFFu, true)] // s_cmpk_eq_i32: -1 == sext(0xFFFF)
    [InlineData(0x9, 0xFFFF_FFFFu, 0xFFFFu, false)] // s_cmpk_eq_u32: needs zero extension
    [InlineData(0xA, 0x0000_FFFFu, 0xFFFFu, false)] // s_cmpk_lg_u32
    public void SccFollowsTheCompareAndTheRegisterSurvives(int op, uint value, uint immediate, bool expectedScc)
    {
        var word = 0xB000_0000u | ((uint)op << 23) | (2u << 16) | immediate;
        var compare = DecodeHarness.Decode(word, 0xBF810000u).Instructions[0] with { Pc = 0 };
        Assert.StartsWith("SCmpk", compare.Opcode);
        var program = Program(
            compare,
            Sop2(4, "SCselectB32", 8, Operand(100), Operand(200)),
            MoveScalarRegister(8, 9, 2),
            MoveScalar(12, 10, 16),
            MoveScalar(16, 11, 0),
            BufferLoad(20, 8),
            EndProgram(28));
        var plan = Extract(program);

        var registers = new uint[16];
        registers[2] = value;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(registers), out var result));

        Assert.Equal(expectedScc ? 100u : 200u, result.Dwords[0]);
        Assert.Equal(value, result.Dwords[1]);
    }
}
