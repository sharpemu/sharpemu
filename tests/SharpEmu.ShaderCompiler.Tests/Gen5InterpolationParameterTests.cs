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
    [InlineData(0u, false, 1u, true)]
    [InlineData(1u, false, 2u, true)]
    [InlineData(0u, true, 1u, false)]
    [InlineData(1u, true, 2u, false)]
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
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FlatP0Move_UsesFixedFunctionFlatOnlyWithoutCustomInterpolation(
        bool custom,
        bool fixedFunctionFlat)
    {
        var request = Request(2, custom);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Equal(!fixedFunctionFlat, instructions.Any(instruction =>
            instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr));
        Assert.Equal(!fixedFunctionFlat, instructions.Any(instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr));
        Assert.Equal(fixedFunctionFlat, instructions.Any(instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Flat));
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location).Operands[0];
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Location);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonFlatP0Move_StillUsesPerVertexInput(bool custom)
    {
        var request = Request(2, custom, flat: false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr);
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
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(0, true, 0x77), out var shader, out var error), error);
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

    [Fact]
    public void LinearInterpolation_DecoratesOrdinaryPixelInputNoPerspective()
    {
        var request = Request(0, false, opcode: "VInterpP2F32", noPerspective: true, flat: false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.NoPerspective).Operands[0];
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Location);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Flat);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void FlatInterpolation_TakesPrecedenceOverNoPerspective()
    {
        var request = Request(0, false, opcode: "VInterpP2F32", noPerspective: true);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Flat).Operands[0];
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.NoPerspective);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void PullModel_DoesNotSilentlyUseZeroCoordinates()
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(0, true, 8), out _, out var error));
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

    [Fact]
    public void SmoothSlotSharingAPerVertexParameter_InterpolatesThePerVertexInput()
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
            PixelInputAddress = 2,
            PixelInputEnable = 2,
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
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            stage,
            userDataCount: 0,
            waveSize: waveSize);
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

    private static ShaderCompileRequest Request(
        uint selector, bool custom, uint inputs = 2, string opcode = "VInterpMovF32",
        bool noPerspective = false, bool flat = true, uint inputCntl = 0x401, bool supportsPerVertex = true)
    {
        var interpolation = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vintrp, opcode,
            [selector], [Gen5Operand.Vector(selector)], [Gen5Operand.Vector(4)], new Gen5InterpolationControl(1, 2));
        var program = ResourceTestProgram.Program(interpolation, ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = inputs | (noPerspective ? 0x20u : 0u),
            PixelInputEnable = inputs | (noPerspective ? 0x20u : 0u),
            PixelInputCntl = [0, flat ? inputCntl : inputCntl & ~0x400u],
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
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }
        var executable = Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (!File.Exists(executable))
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
