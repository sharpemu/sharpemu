// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class LinkedBranchTests
{
    [Theory]
    [InlineData("SBranch", 12u)]
    [InlineData("SBranch", 0x20000u)]
    [InlineData("SCbranchScc0", 12u)]
    [InlineData("SCbranchScc1", 0x20000u)]
    public void ExplicitTargetOverridesTheEncodedDisplacement(string opcode, uint target)
    {
        var program = new Gen5ShaderProgram(0, [
            new(0, Gen5ShaderEncoding.Sopp, opcode, [0xBF82FFFF], [], [], new ShaderLinkedBranchControl(target)),
            new(4, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
            new(target, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null)]);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var assignment = opcode switch
        {
            "SCbranchScc0" => "pc = (!scc) ? 2u : 1u;",
            "SCbranchScc1" => "pc = (scc) ? 2u : 1u;",
            _ => "pc = 2u;",
        };
        Assert.Contains(assignment, shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 2u:", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitTargetInAnInstructionGapIsRejected()
    {
        var program = new Gen5ShaderProgram(0, [
            new(0, Gen5ShaderEncoding.Sopp, "SBranch", [0xBF82FFFF], [], [], new ShaderLinkedBranchControl(8)),
            new(4, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
            new(12, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null)]);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("branch target outside program", error, StringComparison.Ordinal);
    }
}
