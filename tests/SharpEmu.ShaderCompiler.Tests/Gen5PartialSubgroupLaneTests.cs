// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// A fragment subgroup can be only partly populated, so a DPP/DPP8/PERMLANE partner
// may be absent from the host. The fetch-inactive-off forms judge the partner with a
// subgroup ballot of EXEC (absent invocations have no bit) instead of shuffling an
// activity word out of a lane that may not exist.
public sealed class Gen5PartialSubgroupLaneTests
{
    private const uint GroupNonUniformBallot = 339;
    private const uint GroupNonUniformShuffle = 345;

    private static uint[] CompilePixel(Gen5ShaderInstruction instruction)
    {
        var program = ResourceTestProgram.Program(instruction, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        return words;
    }

    private static uint[] CompilePixel(Gen5InstructionControl control) =>
        CompilePixel(ResourceTestProgram.Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with { Control = control });

    private static int Count(uint[] words, uint opcode)
    {
        var count = 0;
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            if ((words[index] & 0xFFFF) == opcode) count++;
        }

        return count;
    }

    [Fact]
    public void PixelDpp_WithoutFetchInactive_JudgesThePartnerWithAnExecBallot()
    {
        foreach (var control in new Gen5InstructionControl[]
        {
            new Gen5Dpp8Control(0x053977, false),
            new Gen5DppControl(0x104, false, true, 0, 0, 15, 15),
        })
        {
            var words = CompilePixel(control);
            Assert.Equal(1, Count(words, GroupNonUniformShuffle));
            Assert.True(Count(words, GroupNonUniformBallot) >= 1);
        }
    }

    [Fact]
    public void PixelDpp_WithoutBoundControl_AlsoGatesTheWriteOnThePartner()
    {
        // Bound control clear: a lane whose partner is inactive or absent keeps its old
        // destination, which needs the partner test for the write mask as well.
        var keep = CompilePixel(new Gen5DppControl(0x104, false, false, 0, 0, 15, 15));
        var zero = CompilePixel(new Gen5DppControl(0x104, false, true, 0, 0, 15, 15));
        Assert.True(Count(keep, GroupNonUniformBallot) > Count(zero, GroupNonUniformBallot));
    }

    [Fact]
    public void PixelDpp_WithFetchInactive_ShufflesOnlyTheValue()
    {
        Assert.Equal(1, Count(CompilePixel(new Gen5DppControl(0x104, true, false, 0, 0, 15, 15)), GroupNonUniformShuffle));
        Assert.Equal(1, Count(CompilePixel(new Gen5Dpp8Control(0x053977, true)), GroupNonUniformShuffle));
    }

    [Fact]
    public void PixelPermlane_WithoutFetchInactive_JudgesThePartnerWithAnExecBallot()
    {
        var words = CompilePixel(ResourceTestProgram.Vop3(0, "VPermlane16B32", 4,
            Gen5Operand.Vector(3), ResourceTestProgram.Operand(uint.MaxValue), ResourceTestProgram.Operand(uint.MaxValue)));
        Assert.Equal(1, Count(words, GroupNonUniformShuffle));
        Assert.True(Count(words, GroupNonUniformBallot) >= 1);
    }
}
