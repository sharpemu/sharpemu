// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelOutputMappingTests
{
    [Fact]
    public void IdentityOutputDoesNotAddComponentShuffle()
    {
        var instructions = ReadInstructions(
            Compile(Gen5ColorComponentMapping.Identity));

        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.VectorShuffle);
    }

    [Fact]
    public void BgraOutputShufflesGuestComponentsToPhysicalOrder()
    {
        var instructions = ReadInstructions(
            Compile(new Gen5ColorComponentMapping(0xC6)));
        var shuffle = Assert.Single(
            instructions,
            instruction => instruction.Opcode == SpirvOp.VectorShuffle);

        Assert.Equal([2u, 1u, 0u, 3u], shuffle.Operands[^4..]);
    }

    [Fact]
    public void BgraPartialExportPreservesPhysicalComponents()
    {
        var instructions = ReadInstructions(
            Compile(new Gen5ColorComponentMapping(0xC6), enableMask: 0x1));
        var preservedComponents = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.CompositeExtract)
            .Select(instruction => instruction.Operands[^1])
            .ToArray();

        Assert.Equal([1u, 0u, 3u], preservedComponents);
    }

    private static byte[] Compile(
        Gen5ColorComponentMapping componentMapping,
        uint enableMask = 0xF)
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(0, enableMask, false, true, true));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x1_0000_D000, [export, end]),
            [],
            null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                [new Gen5PixelOutputBinding(
                    0,
                    0,
                    Gen5PixelOutputKind.Float,
                    componentMapping)],
                out var shader,
                out var error),
            error);
        return shader.Spirv;
    }

    private static IReadOnlyList<ParsedInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private readonly record struct ParsedInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
