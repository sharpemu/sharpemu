// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5InterpolationParameterTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(16u)]
    public void PerSampleCustomInterpolation_UsesSampleIdAndOffsets(uint inputs)
    {
        var instructions = new Gen5ShaderInstruction[]
        {
            new(0, Gen5ShaderEncoding.Vintrp, "VInterpMovF32", [0], [Gen5Operand.Vector(0)],
                [Gen5Operand.Vector(6)], new Gen5InterpolationControl(1, 0)),
            new(4, Gen5ShaderEncoding.Vintrp, "VInterpP2F32", [0], [Gen5Operand.Vector(1)],
                [Gen5Operand.Vector(5)], new Gen5InterpolationControl(0, 0)),
            ResourceTestProgram.EndProgram(8),
        };
        var program = ResourceTestProgram.Program(instructions);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = inputs, PixelInputEnable = inputs, PixelInputCntl = [0u, 0u],
            PixelRasterizationSamples = 2,
            PixelCustomSampleOffsets = [(-.25f, 0f), (.25f, 0f), (-.25f, 0f), (.25f, 0f),
                (.25f, 0f), (-.25f, 0f), (.25f, 0f), (-.25f, 0f)],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var spirv = Instructions(shader.Spirv);
        Assert.Contains(spirv, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.SampleId);
        Assert.Contains(spirv, instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 78);
        Assert.DoesNotContain(spirv, instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 77);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(true, 0xFFFEu, true)]
    [InlineData(true, 0xFFFFu, true)]
    [InlineData(true, 0u, false)]
    [InlineData(false, 0xFFFEu, false)]
    public void SampleExclusion_UsesPostDepthCoverageOnlyWithEarlyTests(bool early, uint mask, bool gated)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            EarlyFragmentTests = early,
            PixelShaderSampleExclusionMask = mask,
            PixelRasterizationSamples = 2,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Equal(gated, instructions.Any(instruction => instruction.Opcode == SpirvOp.ExecutionMode &&
            instruction.Operands[1] == (uint)SpirvExecutionMode.PostDepthCoverage));
        Assert.Equal(gated, instructions.Any(instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.SampleMask));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedCustomSampleInterpolation_UsesOffsetsInsteadOfSampleLookup(bool linear)
    {
        var instruction = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vintrp, "VInterpP2F32",
            [0], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(5)], new Gen5InterpolationControl(0, 0));
        var program = ResourceTestProgram.Program(instruction, ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = linear ? 16u : 1u, PixelInputEnable = linear ? 16u : 1u,
            PixelInputCntl = [0u], PixelInterpolationSample = 0,
            PixelCustomSampleOffsets = [(-.25f, 0f), (-.25f, 0f), (.25f, 0f), (.25f, 0f)],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 78);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 77);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(3, 0f, true)]
    [InlineData(4, float.NaN, true)]
    [InlineData(4, .75f, true)]
    [InlineData(4, 0f, false)]
    public void FixedCustomSampleInterpolation_RejectsUnsupportedGrid(int count, float x, bool fixedSample)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInterpolationSample = fixedSample ? 0u : null,
            PixelCustomSampleOffsets = Enumerable.Repeat((x, 0f), count).ToArray(),
        };
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("four finite offsets", error);
    }

    [Theory]
    [InlineData(0u, false, 1u, true)]
    [InlineData(1u, false, 2u, true)]
    [InlineData(2u, false, 0u, false)]
    [InlineData(0u, true, 1u, false)]
    [InlineData(1u, true, 2u, false)]
    [InlineData(2u, true, 0u, false)]
    public void ParameterMove_SelectsVertexAndPreservesCustomData(
        uint selector, bool custom, uint vertex, bool subtractOrigin)
    {
        var request = Request(selector, custom);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr).Operands[0];
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Flat);
        var firstAccess = instructions.First(instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == input);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == firstAccess.Operands[3] && instruction.Operands[2] == vertex);
        Assert.Equal(subtractOrigin, instructions.Any(instruction => instruction.Opcode == SpirvOp.FSub));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(255u)]
    public void ReservedSelector_Fails(uint selector)
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(selector, false), out _, out var error));
        Assert.Contains("reserved interpolation parameter selector", error);
    }

    [Fact]
    public void MixedBarycentricLocations_ShareBuiltIns()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(2, true, 0x77), out var shader, out var error), error);
        var builtIns = Instructions(shader.Spirv)
            .Where(instruction => instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn)
            .Select(instruction => instruction.Operands[2]).ToArray();
        Assert.Equal(builtIns.Length, builtIns.Distinct().Count());
        Assert.Contains((uint)SpirvBuiltIn.BaryCoordKhr, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.BaryCoordNoPerspKhr, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.SampleId, builtIns);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void OrdinaryInterpolation_DoesNotRequireBarycentricFeature()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, opcode: "VInterpP2F32"), out var shader, out var error), error);
        Assert.DoesNotContain(Instructions(shader.Spirv), instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(0x10u, true)]
    public void SingleSampleModeQualifiesOrdinaryInterpolants(uint inputs, bool noPerspective)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, inputs, "VInterpP2F32", inputCntl: 0), out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var sample = Assert.Single(instructions, item => item.Opcode == SpirvOp.Decorate &&
            item.Operands[1] == (uint)SpirvDecoration.Sample);
        Assert.Equal(noPerspective, instructions.Any(item => item.Opcode == SpirvOp.Decorate &&
            item.Operands[0] == sample.Operands[0] && item.Operands[1] == (uint)SpirvDecoration.NoPerspective));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(1u, "VInterpP2F32", 0u)]
    [InlineData(0x10u, "VInterpP2F32", 1u)]
    [InlineData(1u, "VInterpMovF32", 0u)]
    [InlineData(0x77u, "VInterpMovF32", 1u)]
    public void FixedSampleInterpolation_DoesNotRequestSampleInvocations(uint inputs, string opcode, uint sample)
    {
        var request = Request(2, true, inputs, opcode, inputCntl: 0, fixedSample: sample);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.Decorate &&
            (item.Operands[1] == (uint)SpirvDecoration.Sample ||
             (item.Operands[1] == (uint)SpirvDecoration.BuiltIn && item.Operands[2] == (uint)SpirvBuiltIn.SampleId)));
        var interpolations = instructions.Where(item => item.Opcode == SpirvOp.ExtInst && item.Operands[3] == 77).ToArray();
        Assert.NotEmpty(interpolations);
        foreach (var interpolation in interpolations)
            Assert.Contains(instructions, item => item.Opcode == SpirvOp.Constant &&
                item.Operands[1] == interpolation.Operands[5] && item.Operands[2] == sample);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(3u, 0u)]
    [InlineData(0x11u, 0u)]
    [InlineData(1u, 0x400u)]
    [InlineData(0x10u, 0x400u)]
    public void MixedModesAndFlatInputsAreNotSampleQualified(uint inputs, uint inputCntl)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, inputs, "VInterpP2F32", inputCntl), out var shader, out var error), error);
        Assert.DoesNotContain(Instructions(shader.Spirv), item => item.Opcode == SpirvOp.Decorate &&
            item.Operands[1] == (uint)SpirvDecoration.Sample);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyDepthRequest_ControlsFragmentExecutionMode(bool earlyTests)
    {
        var request = Request(0, false, opcode: "VInterpP2F32", earlyTests: earlyTests);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(earlyTests, Instructions(shader.Spirv).Any(item => item.Opcode == SpirvOp.ExecutionMode &&
            item.Operands[1] == (uint)SpirvExecutionMode.EarlyFragmentTests));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void PullModel_DoesNotSilentlyUseZeroCoordinates()
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(2, true, 8), out _, out var error));
        Assert.Contains("Pull-model interpolation", error);
    }

    [Fact]
    public void ProvokingVertexMove_UsesAFlatInputWithoutPerVertexSupport()
    {
        var request = Request(2, false, inputCntl: 0x1, supportsPerVertex: false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location).Operands[0];
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Flat);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void VertexDifferenceMove_FallsBackToInterpolatedInputWithoutPerVertexSupport(uint selector)
    {
        var request = Request(selector, false, inputCntl: 0x1, supportsPerVertex: false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Flat);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void PixelSystemInputs_ReadTheirBuiltIns()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, inputs: 0xF002, opcode: "VInterpP2F32"), out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var builtIns = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn)
            .Select(instruction => instruction.Operands[2]).ToArray();
        Assert.Contains((uint)SpirvBuiltIn.FrontFacing, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.Layer, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.SampleMask, builtIns);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.ShiftLeftLogical);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void SlotsReadingOneParameter_ShareOneInput()
    {
        // PS slots 1 and 2 both read VS parameter 1; the second slot must not move to a
        // location the vertex program never writes.
        Gen5ShaderInstruction Move(uint pc, uint attribute, uint destination) =>
            new(pc, Gen5ShaderEncoding.Vintrp, "VInterpMovF32",
                [1], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(destination)], new Gen5InterpolationControl(attribute, 0));
        var program = ResourceTestProgram.Program(Move(0, 1, 4), Move(4, 2, 5), ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = 2,
            PixelInputEnable = 2,
            PixelInputCntl = [0, 0x1, 0x1],
            PixelCustomInterpolationMask = 6,
            SupportsPerVertexPixelInputs = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr).Operands[0];
        var location = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Location);
        Assert.Equal(1u, location.Operands[2]);
        Assert.Equal(2, instructions.Count(instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == input));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    public void SmoothSlotSharingAPerVertexParameter_InterpolatesThePerVertexInput(uint? fixedSample)
    {
        // Slot 1 is read per vertex, slot 2 interpolates the same VS parameter: one per-vertex
        // input serves both, and slot 2 is rebuilt from the vertices with the barycentrics.
        var move = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vintrp, "VInterpMovF32",
            [1], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(4)], new Gen5InterpolationControl(1, 0));
        var smooth = new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Vintrp, "VInterpP2F32",
            [0], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(5)], new Gen5InterpolationControl(2, 0));
        var program = ResourceTestProgram.Program(move, smooth, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = fixedSample.HasValue ? 1u : 2u,
            PixelInputEnable = fixedSample.HasValue ? 1u : 2u,
            PixelInterpolationSample = fixedSample,
            PixelInputCntl = [0, 0x1, 0x1],
            PixelCustomInterpolationMask = 2,
            SupportsPerVertexPixelInputs = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr).Operands[0];
        var inputLocations = instructions.Where(instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location && instruction.Operands[0] == input).ToArray();
        Assert.Equal(1u, Assert.Single(inputLocations).Operands[2]);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn && instruction.Operands[2] == (uint)SpirvBuiltIn.BaryCoordKhr);
        Assert.Equal(4, instructions.Count(instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == input));
        if (fixedSample.HasValue)
        {
            Assert.Contains(instructions, item => item.Opcode == SpirvOp.ExtInst && item.Operands[3] == 77);
            Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.Decorate &&
                item.Operands[1] == (uint)SpirvDecoration.BuiltIn && item.Operands[2] == (uint)SpirvBuiltIn.SampleId);
        }
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void FrontFace_ReadsTheRasterizerBuiltIn()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, inputs: 0x1002, opcode: "VInterpP2F32"), out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var frontFace = Assert.Single(instructions, instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.FrontFacing).Operands[0];
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Load &&
            instruction.Operands[2] == frontFace);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands.Length == 3 && instruction.Operands[2] == 0x3F800000u);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0x800u, 1)]
    [InlineData(0x400u, 0)]
    public void PositionW_IsTheReciprocalOfTheFragmentCoordinate(uint inputs, int reciprocals)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { PixelInputAddress = inputs, PixelInputEnable = inputs };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var position = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn && instruction.Operands[2] == (uint)SpirvBuiltIn.FragCoord).Operands[0];
        var loaded = instructions.Where(instruction => instruction.Opcode == SpirvOp.Load && instruction.Operands[2] == position)
            .Select(instruction => instruction.Operands[1]).ToHashSet();
        var w = instructions.Where(instruction => instruction.Opcode == SpirvOp.CompositeExtract &&
            loaded.Contains(instruction.Operands[2]) && instruction.Operands[3] == 3).Select(instruction => instruction.Operands[1]).ToHashSet();
        Assert.Equal(reciprocals, instructions.Count(instruction => instruction.Opcode == SpirvOp.FDiv && w.Contains(instruction.Operands[3])));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(ShaderStage.Pixel, 32u)]
    [InlineData(ShaderStage.Pixel, 64u)]
    [InlineData(ShaderStage.Vertex, 32u)]
    [InlineData(ShaderStage.Vertex, 64u)]
    public void LaneSpills_AreReadBackWithoutTheHostSubgroup(ShaderStage stage, uint waveSize)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 5),
            ResourceTestProgram.WriteLane(8, vectorRegister: 18, scalarRegister: 85, lane: 37),
            ResourceTestProgram.ReadLane(16, scalarRegister: 86, vectorRegister: 18, lane: 5),
            ResourceTestProgram.ReadLane(24, scalarRegister: 87, vectorRegister: 18, lane: 37),
            ResourceTestProgram.EndProgram(32));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = waveSize, EnableGraphicsSubgroupOperations = true };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.DoesNotContain(Instructions(shader.Spirv), instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
        var text = System.Text.Encoding.ASCII.GetString(shader.Spirv);
        Assert.Contains("v18_lane5", text, StringComparison.Ordinal);
        Assert.Contains("v18_lane37", text, StringComparison.Ordinal);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(ShaderStage.Pixel, 0)]
    [InlineData(ShaderStage.Vertex, 0)]
    [InlineData(ShaderStage.Compute, 2)]
    public void ReadlaneOfAnUnspilledLane_UsesTheHostSubgroupOnlyInCompute(ShaderStage stage, int broadcasts)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 5),
            ResourceTestProgram.ReadLane(8, scalarRegister: 86, vectorRegister: 18, lane: 6),
            ResourceTestProgram.ReadLane(16, scalarRegister: 87, vectorRegister: 19, lane: 5),
            ResourceTestProgram.EndProgram(24));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = 32, EnableGraphicsSubgroupOperations = true };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(broadcasts, Instructions(shader.Spirv).Count(instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(false, 1u, 1u)]
    [InlineData(false, 2u, 2u)]
    [InlineData(false, 1u, 2u)]
    [InlineData(true, 2u, 2u)]
    [InlineData(true, 1u, 2u)]
    public void MrtzSampleMask_DeclaresAnIntegerOutput(bool compressed, uint exportSamples, uint rasterSamples)
    {
        var request = MaskExportRequest(compressed, exportSamples, rasterSamples);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var output = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.SampleMask).Operands[0];
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Variable &&
            instruction.Operands[1] == output && instruction.Operands[2] == (uint)SpirvStorageClass.Output);
        var pointers = instructions.Where(instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == output).Select(instruction => instruction.Operands[1]).ToHashSet();
        Assert.Equal(2, instructions.Count(instruction => instruction.Opcode == SpirvOp.Store &&
            pointers.Contains(instruction.Operands[0])));
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Select);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(2u, 3u)]
    [InlineData(2u, 4u)]
    [InlineData(4u, 2u)]
    public void MrtzSampleMask_RejectsUnverifiedCountMappings(uint exportSamples, uint rasterSamples)
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(
            MaskExportRequest(false, exportSamples, rasterSamples), out _, out var error));
        Assert.Contains("unsupported pixel sample-mask export counts", error);
    }

    private static ShaderCompileRequest MaskExportRequest(bool compressed, uint exportSamples, uint rasterSamples)
    {
        var export = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(8, compressed ? 12u : 4u, compressed, true, true));
        var program = ResourceTestProgram.Program(export, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelSampleMaskExportEnable = true,
            PixelMaskExportSamples = exportSamples,
            PixelRasterizationSamples = rasterSamples,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MrtzDepth_PreservesDepthAndAnOptionalSampleMask(bool sampleMask)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            DepthExportRequest(sampleMask), out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var depth = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.FragDepth).Operands[0];
        Assert.Equal(2, instructions.Count(instruction => instruction.Opcode == SpirvOp.Store &&
            instruction.Operands[0] == depth));
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.ExecutionMode &&
            instruction.Operands[1] == (uint)SpirvExecutionMode.DepthReplacing);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.ExecutionMode &&
            instruction.Operands[1] == (uint)SpirvExecutionMode.EarlyFragmentTests);
        Assert.Equal(sampleMask, instructions.Any(instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.SampleMask));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(true, false, "late fragment tests")]
    [InlineData(false, true, "compressed MRTZ")]
    public void MrtzDepth_RejectsIncompatibleState(bool early, bool compressed, string reason)
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(
            DepthExportRequest(false, early, compressed), out _, out var error));
        Assert.Contains(reason, error);
    }

    private static ShaderCompileRequest DepthExportRequest(bool sampleMask, bool early = false, bool compressed = false)
    {
        var export = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(8, sampleMask ? 5u : 1u, compressed, true, true));
        var program = ResourceTestProgram.Program(export, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelDepthExportEnable = true,
            PixelSampleMaskExportEnable = sampleMask,
            EarlyFragmentTests = early,
        };
    }

    private static ShaderCompileRequest Request(
        uint selector, bool custom, uint inputs = 2, string opcode = "VInterpMovF32",
        uint inputCntl = 0x401, bool supportsPerVertex = true, uint? fixedSample = null, bool earlyTests = false)
    {
        var interpolation = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vintrp, opcode,
            [selector], [Gen5Operand.Vector(selector)], [Gen5Operand.Vector(4)], new Gen5InterpolationControl(1, 2));
        var program = ResourceTestProgram.Program(interpolation, ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = inputs,
            PixelInputEnable = inputs,
            PixelInterpolationSample = fixedSample,
            EarlyFragmentTests = earlyTests,
            PixelInputCntl = [0, inputCntl],
            PixelCustomInterpolationMask = custom ? 2u : 0u,
            SupportsPerVertexPixelInputs = supportsPerVertex,
        };
    }

    private static List<Instruction> Instructions(byte[] code)
    {
        var words = new uint[code.Length / 4];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            result.Add(new Instruction((SpirvOp)(words[index] & 0xFFFF), words[(index + 1)..(index + count)]));
            index += count;
        }
        return result;
    }

    private static void ValidateWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var name = OperatingSystem.IsWindows() ? "spirv-val.exe" : "spirv-val";
        var sdkExecutable = string.IsNullOrWhiteSpace(sdk) ? null :
            Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin" : "bin", name);
        var executable = sdkExecutable is not null && File.Exists(sdkExecutable) ? sdkExecutable :
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), name)).FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return;
        }
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}
