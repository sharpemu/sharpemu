// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class Gen5MslF16ConversionTests
{
    [Theory]
    [InlineData("VCvtF16U16")]
    [InlineData("VCvtF16I16")]
    [InlineData("VCvtI16F16")]
    public void Float16IntegerConversionLowersToMsl(string opcode)
    {
        var conversion = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop1,
            opcode,
            [0u],
            [Gen5Operand.Vector(1)],
            [Gen5Operand.Vector(0)],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0x1000, [conversion]), ShaderStage.Compute, localSizeX: 1);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        Assert.Contains("half", shader.Source, StringComparison.Ordinal);
    }
}
