// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class VectorCompareResourceTests
{
    [Fact]
    public void BitreplicateB64B32Compiles()
    {
        var program = Program(
            Sop1(0, "SBitreplicateB64B32", 0, Operand(7)),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Theory]
    [InlineData("VCmpEqU16")]
    [InlineData("VCmpLtU16")]
    [InlineData("VCmpGeU16")]
    [InlineData("VCmpEqI16")]
    [InlineData("VCmpLtI16")]
    public void Integer16BitCompareCompilesToSpirv(string opcode)
    {
        var program = Program(
            Vopc(0, opcode, Gen5Operand.Vector(0), 1),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }
}
