// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(1u, SpirvImageDim.Dim2D, 2u)]
    [InlineData(2u, SpirvImageDim.Dim3D, 3u)]
    public void ImageStoreDimensionControlsImageAndCoordinateTypes(
        uint dimension,
        SpirvImageDim expectedImageDimension,
        uint expectedCoordinateComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageStore", dimension));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)expectedImageDimension, imageType.Operands[2]);
        Assert.Equal(2u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageWrite,
            coordinateOperand: 1,
            expectedComponents: expectedCoordinateComponents);

        var sizeQuery = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageQuerySize);
        AssertVectorTypeWidth(
            instructions,
            sizeQuery.Operands[0],
            expectedCoordinateComponents);
    }

    [Fact]
    public void ImageSampleDim3DUsesThreeComponentSampleCoordinates()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageSampleLz", dimension: 2));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim3D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageSampleExplicitLod,
            coordinateOperand: 3,
            expectedComponents: 3);
    }

    private static byte[] CompileImageOperation(string opcode, uint dimension)
    {
        var addressRegisters = dimension == 2
            ? new uint[] { 0, 1, 2 }
            : [0, 1];
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20; // FORMAT_16_16_16_16_FLOAT
        descriptor[3] = (dimension == 2 ? 10u : 9u) << 28;
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    imageInstruction.Pc,
                    imageInstruction.Opcode,
                    control,
                    descriptor,
                    new uint[4],
                    null),
            ],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);
        return shader.Spirv;
    }

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void AssertCoordinateVectorWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp operation,
        int coordinateOperand,
        uint expectedComponents)
    {
        var imageOperation = Assert.Single(
            instructions,
            item => item.Opcode == operation);
        var coordinateId = imageOperation.Operands[coordinateOperand];
        var coordinate = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.CompositeConstruct &&
                item.Operands.Length >= 2 &&
                item.Operands[1] == coordinateId);
        AssertVectorTypeWidth(
            instructions,
            coordinate.Operands[0],
            expectedComponents);
    }

    private static void AssertVectorTypeWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint vectorTypeId,
        uint expectedComponents)
    {
        var vectorType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeVector &&
                item.Operands[0] == vectorTypeId);
        Assert.Equal(expectedComponents, vectorType.Operands[2]);
    }

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
