// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Structural checks over the emitted MSL — these run on every platform because
/// translation is pure text generation; only the runtime tests need a Metal device.
/// </summary>
public sealed class MslTranslationTests
{
    [Fact]
    public void EveryFixtureTranslates()
    {
        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);
            Assert.Equal(Gen5MslStage.Compute, shader.Stage);
            Assert.Equal("gen5_cs", shader.EntryPoint);
            Assert.Contains("kernel void gen5_cs(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("while (active)", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecMaskedStoresAreGuarded()
    {
        var shader = Gen5ComputeFixtures.CompileOrThrow(Gen5ComputeFixtures.ExecStore);

        // Every buffer store must sit behind the per-lane EXEC guard.
        Assert.Contains("if (exec)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_store_bytes(b0,", shader.Source, StringComparison.Ordinal);

        // s_mov_b32 exec_lo, 0 / -1 must drive the per-lane bool.
        Assert.Contains("exec = ((", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopFixtureProducesMultipleDispatcherBlocks()
    {
        var shader = Gen5ComputeFixtures.CompileOrThrow(Gen5ComputeFixtures.Loop);

        // The backward branch splits the program into at least three blocks and
        // the conditional branch selects between loop head and fallthrough.
        Assert.Contains("case 0u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 1u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 2u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("pc = (scc) ?", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherIsBoundedByDefault()
    {
        var shader = Gen5ComputeFixtures.CompileOrThrow(Gen5ComputeFixtures.Fmac);
        Assert.Contains("if (++steps >=", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void UniformsCarryDispatchLimitAndBufferLengths()
    {
        var shader = Gen5ComputeFixtures.CompileOrThrow(Gen5ComputeFixtures.ExecStore);
        Assert.Contains("struct SharpEmuUniforms", shader.Source, StringComparison.Ordinal);
        Assert.Contains("dispatch_limit_x", shader.Source, StringComparison.Ordinal);
        Assert.Contains("buffer_bytes[", shader.Source, StringComparison.Ordinal);

        // One global binding: b0 at [[buffer(0)]], uniforms at [[buffer(1)]].
        Assert.Contains("device uint* b0 [[buffer(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[buffer(1)]]", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelStageEmitsFragmentInterface()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();

        Assert.Equal(Gen5MslStage.Pixel, shader.Stage);
        Assert.Equal("gen5_ps", shader.EntryPoint);
        Assert.Equal(1u, shader.AttributeCount);
        Assert.Contains("fragment Gen5PsOut gen5_ps(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 attr0 [[user(locn0)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[color(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[position]]", shader.Source, StringComparison.Ordinal);

        // Interpolation reads land in VGPRs; the export writes MRT0 under EXEC
        // and inactive lanes discard at the end.
        Assert.Contains("as_type<uint>(sharpemu_in.attr0[0])", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.mrt0 = exec ?", shader.Source, StringComparison.Ordinal);
        Assert.Contains("discard_fragment();", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelOutputKindsSelectTheAttachmentType()
    {
        var uintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Uint);
        Assert.Contains("uint4 mrt0 [[color(0)]];", uintShader.Source, StringComparison.Ordinal);

        var sintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Sint);
        Assert.Contains("int4 mrt0 [[color(0)]];", sintShader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedOpcodeFailsLoudlyWithPc()
    {
        // v_cubeid_f32 is real but outside the phase-1 ALU set: the translator
        // must name the opcode and pc instead of emitting wrong code.
        var fixture = new Gen5ComputeFixture(
            "unsupported",
            [
                0xD5C40000, 0x04060501, // v_cubeid_f32 v0, v1, v2, v3
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Gen5ComputeFixtures.CompileOrThrow(fixture));
        Assert.Contains("pc=0x", exception.Message, StringComparison.Ordinal);
    }
}
