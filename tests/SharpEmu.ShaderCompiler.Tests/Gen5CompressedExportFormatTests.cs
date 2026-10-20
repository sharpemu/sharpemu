// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// SPI_SHADER_COL_FORMAT decides what the two halves of a compressed pixel export mean.
public sealed class Gen5CompressedExportFormatTests
{
    private const uint GlslUnpackSnorm2x16 = 60;
    private const uint GlslUnpackUnorm2x16 = 61;
    private const uint GlslUnpackHalf2x16 = 62;

    [Theory]
    [InlineData(Gen5PixelExportFormat.Float16, GlslUnpackHalf2x16)]
    [InlineData(Gen5PixelExportFormat.Unorm16, GlslUnpackUnorm2x16)]
    [InlineData(Gen5PixelExportFormat.Snorm16, GlslUnpackSnorm2x16)]
    public void FloatingAndNormalizedFormats_UnpackThroughTheirGlslInstruction(
        Gen5PixelExportFormat format, uint expectedInstruction)
    {
        var instructions = Compile(format, Gen5PixelOutputKind.Float);

        var unpacks = ExtendedInstructions(instructions);
        Assert.Contains(expectedInstruction, unpacks);
        Assert.DoesNotContain(
            new[] { GlslUnpackHalf2x16, GlslUnpackUnorm2x16, GlslUnpackSnorm2x16 }.Except([expectedInstruction]),
            other => unpacks.Contains(other));
    }

    [Theory]
    [InlineData(Gen5PixelExportFormat.Uint16, Gen5PixelOutputKind.Uint, SpirvOp.BitFieldUExtract)]
    [InlineData(Gen5PixelExportFormat.Sint16, Gen5PixelOutputKind.Sint, SpirvOp.BitFieldSExtract)]
    [InlineData(Gen5PixelExportFormat.Uint16, Gen5PixelOutputKind.Float, SpirvOp.BitFieldUExtract)]
    public void IntegerFormats_ExtractSixteenBitFieldsWithoutAHalfFloatUnpack(
        Gen5PixelExportFormat format, Gen5PixelOutputKind kind, SpirvOp extract)
    {
        var instructions = Compile(format, kind);

        Assert.Contains(instructions, instruction => instruction.Opcode == extract);
        Assert.Empty(ExtendedInstructions(instructions));
    }

    private static List<uint> ExtendedInstructions(IReadOnlyList<ParsedInstruction> instructions) =>
        instructions
            .Where(instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] is >= 60 and <= 62)
            .Select(instruction => instruction.Operands[3])
            .ToList();

    private static IReadOnlyList<ParsedInstruction> Compile(Gen5PixelExportFormat format, Gen5PixelOutputKind kind)
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [],
            new Gen5ExportControl(0, 0xF, Compressed: true, Done: true, ValidMask: true));
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);
        var program = new Gen5ShaderProgram(0x1_0000_D000, [export, end]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, kind) { ExportFormat = format }],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var parsed = new List<ParsedInstruction>();
        var spirv = shader.Spirv;
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            }

            parsed.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return parsed;
    }

    private readonly record struct ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
