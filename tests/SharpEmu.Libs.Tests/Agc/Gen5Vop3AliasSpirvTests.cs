// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// VOP3 re-encodes the VOPC and VOP1 opcode spaces so those instructions can carry the modifiers
// the short forms have no room for: abs/neg on a source, a clamp, or a destination other than VCC.
// The compiler emits the _e64 form for exactly those cases, which is why a shader doing anything
// past a plain comparison hits them and a trivial UI shader does not.
//
// Before the aliases were decoded, one such instruction anywhere in a program produced an unnamed
// Vop3Raw stub, translation failed for the whole shader, and every draw bound to it was dropped —
// silently, and for the rest of the session. These compile the real thing end to end.
public sealed class Gen5Vop3AliasSpirvTests
{
    private const ulong ShaderAddress = 0x2_0000_0000;

    // s_endpgm
    private const uint EndProgram = 0xBF810000u;

    [Fact]
    public void Vop3EncodedCompareCompilesToSpirv()
    {
        // v_cmp_lt_f32_e64 s[4:5], v0, v1
        var spirv = Compile([0xD4010104u, 0x00020300u, EndProgram]);

        Assert.NotEmpty(spirv);
    }

    [Fact]
    public void Vop3EncodedVop1CompilesToSpirv()
    {
        // v_rcp_f32_e64 v2, v0
        var spirv = Compile([0xD5AA0002u, 0x00000100u, EndProgram]);

        Assert.NotEmpty(spirv);
    }

    [Fact]
    public void BothAliasFormsCompileTogether()
    {
        var spirv = Compile(
        [
            0xD4010104u, 0x00020300u, // v_cmp_lt_f32_e64 s[4:5], v0, v1
            0xD5AA0002u, 0x00000100u, // v_rcp_f32_e64 v2, v0
            EndProgram,
        ]);

        Assert.NotEmpty(spirv);
    }

    /// <summary>
    /// The destination has to reach the emitter, not just the decoder. Two compares that differ
    /// only in which SGPR pair they name must produce different modules — if the emitter still
    /// hardcoded VCC they would be identical, which is the failure mode of landing the decoder
    /// change without the emitter change.
    /// </summary>
    [Fact]
    public void CompareDestinationSelectsTheNamedScalarPair()
    {
        var toS4 = Compile([0xD4010104u, 0x00020300u, EndProgram]);
        var toS6 = Compile([0xD4010106u, 0x00020300u, EndProgram]);

        Assert.NotEqual(toS4, toS6);
    }

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 1, 1, 1, out var shader, out error),
            error);
        return shader.Spirv;
    }
}
