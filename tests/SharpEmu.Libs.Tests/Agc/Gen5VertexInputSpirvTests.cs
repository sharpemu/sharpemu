// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5VertexInputSpirvTests
{
    [Theory]
    [InlineData(0u, null)]
    [InlineData(4u, 0u)]
    [InlineData(5u, 1u)]
    public void VertexInputTypeMatchesGuestNumberFormat(
        uint numberFormat,
        uint? expectedIntegerSignedness)
    {
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXyzw",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                4,
                5,
                0,
                0,
                0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var shaderBytes = CompileVertexProgram(
            new Gen5ShaderProgram(0, [instruction, end]),
            new ShaderVertexInput(0, 0, 4, numberFormat, false, []));

        var module = ParseModule(shaderBytes);
        var inputVariable = module.Single(candidate =>
            candidate.Opcode == SpirvOp.Decorate &&
            candidate.Operands.Length >= 3 &&
            candidate.Operands[1] == (uint)SpirvDecoration.Location &&
            candidate.Operands[2] == 0).Operands[0];
        var pointerType = module.Single(candidate =>
            candidate.Opcode == SpirvOp.Variable &&
            candidate.Operands[1] == inputVariable).Operands[0];
        var vectorType = module.Single(candidate =>
            candidate.Opcode == SpirvOp.TypePointer &&
            candidate.Operands[0] == pointerType).Operands[2];
        var componentType = module.Single(candidate =>
            candidate.Opcode == SpirvOp.TypeVector &&
            candidate.Operands[0] == vectorType).Operands[1];

        if (expectedIntegerSignedness is { } signedness)
        {
            Assert.Contains(
                module,
                candidate =>
                    candidate.Opcode == SpirvOp.TypeInt &&
                    candidate.Operands[0] == componentType &&
                    candidate.Operands[1] == 32 &&
                    candidate.Operands[2] == signedness);
        }
        else
        {
            Assert.Contains(
                module,
                candidate =>
                    candidate.Opcode == SpirvOp.TypeFloat &&
                    candidate.Operands[0] == componentType &&
                    candidate.Operands[1] == 32);
        }
    }

    [Fact]
    public void AliasedFetchInstructionsShareOneAttributeLocation()
    {
        // Fetches for one stream view share one location to preserve the attribute budget.
        var firstFetch = CreateVertexFetch(0, 0);
        var secondFetch = CreateVertexFetch(4, 8);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var shaderBytes = CompileVertexProgram(
            new Gen5ShaderProgram(0, [firstFetch, secondFetch, end]),
            new ShaderVertexInput(0, 0, 4, 0, false, [4u]));

        var module = ParseModule(shaderBytes);
        var locations = module
            .Where(candidate =>
                candidate.Opcode == SpirvOp.Decorate &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == (uint)SpirvDecoration.Location)
            .ToArray();
        var inputVariable = Assert.Single(locations).Operands[0];

        Assert.Equal(
            1,
            module.Count(candidate =>
                candidate.Opcode == SpirvOp.Load &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[2] == inputVariable));

        var names = new SpirvModuleInspector(shaderBytes).Names;
        for (var component = 0; component < 4; component++)
        {
            var firstRegister = Assert.Single(names, entry => entry.Value == $"v{component}").Key;
            var secondRegister = Assert.Single(names, entry => entry.Value == $"v{component + 8}").Key;
            var firstValue = module.Last(instruction => instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[0] == firstRegister).Operands[1];
            var secondValue = module.Last(instruction => instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[0] == secondRegister).Operands[1];
            Assert.Equal(firstValue, secondValue);
        }
    }

    [Fact]
    public void FormattedFetchZeroFillsMissingComponents()
    {
        var fetch = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXyz",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                3,
                5,
                0,
                0,
                0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var shaderBytes = CompileVertexProgram(
            new Gen5ShaderProgram(0, [fetch, end]),
            new ShaderVertexInput(0, 0, 2, 7, false, []));

        var module = ParseModule(shaderBytes);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[2] == 0);
    }

    private static byte[] CompileVertexProgram(Gen5ShaderProgram program, ShaderVertexInput vertexInput)
    {
        var replacedFetchOffsets = vertexInput.AliasPcs.Append(vertexInput.Pc).ToHashSet();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 0, 0, replacedFetchOffsets);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexInputs = [vertexInput],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static Gen5ShaderInstruction CreateVertexFetch(uint pc, uint destination) =>
        new(
            pc,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXyzw",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                4,
                5,
                destination,
                0,
                0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5; offset < spirv.Length / sizeof(uint);)
        {
            var header = BitConverter.ToUInt32(spirv, offset * sizeof(uint));
            var wordCount = (int)(header >> 16);
            Assert.True(wordCount > 0);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BitConverter.ToUInt32(
                    spirv,
                    (offset + index + 1) * sizeof(uint));
            }

            instructions.Add(
                new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount;
        }

        return instructions;
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
