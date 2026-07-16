// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5VertexInputSpirvTests
{
    [Theory]
    [InlineData(0u, Gen5VertexInputKind.Float)]
    [InlineData(1u, Gen5VertexInputKind.Float)]
    [InlineData(2u, Gen5VertexInputKind.Float)]
    [InlineData(3u, Gen5VertexInputKind.Float)]
    [InlineData(4u, Gen5VertexInputKind.Uint)]
    [InlineData(5u, Gen5VertexInputKind.Sint)]
    [InlineData(6u, Gen5VertexInputKind.Float)]
    [InlineData(7u, Gen5VertexInputKind.Float)]
    [InlineData(9u, Gen5VertexInputKind.Float)]
    public void NumberFormatResolvesVertexInterfaceKind(
        uint numberFormat,
        Gen5VertexInputKind expectedKind)
    {
        Assert.Equal(expectedKind, Gen5VertexInputFormat.ResolveKind(numberFormat));
    }

    [Theory]
    [InlineData(0u, SpirvOp.TypeFloat, 0u, true)]
    [InlineData(2u, SpirvOp.TypeFloat, 0u, true)]
    [InlineData(4u, SpirvOp.TypeInt, 0u, false)]
    [InlineData(5u, SpirvOp.TypeInt, 1u, true)]
    [InlineData(7u, SpirvOp.TypeFloat, 0u, true)]
    [InlineData(9u, SpirvOp.TypeFloat, 0u, true)]
    public void VertexInterfaceTypeMatchesNumberFormatAndPreservesRawVgprBits(
        uint numberFormat,
        SpirvOp expectedScalarType,
        uint expectedSignedness,
        bool expectsBitcast)
    {
        var program = CreateVertexFetchProgram();
        var state = new Gen5ShaderState(program, new uint[16], Metadata: null);
        Gen5VertexInputBinding[] vertexInputs =
        [
            new(
                Pc: 0,
                Location: 2,
                ComponentCount: 2,
                DataFormat: 4,
                NumberFormat: numberFormat,
                BaseAddress: 0,
                Stride: 2 * sizeof(uint),
                OffsetBytes: 0,
                Data: new byte[2 * sizeof(uint)],
                DataLength: 2 * sizeof(uint),
                DataPooled: false),
        ];
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            Array.Empty<Gen5ImageBinding>(),
            Array.Empty<Gen5GlobalMemoryBinding>(),
            VertexInputs: vertexInputs);

        Assert.True(
            Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var shader,
                out var error),
            error);

        var instructions = ParseInstructions(shader.Spirv);
        var attributeVariable = FindNamedId(instructions, "attr2");
        var pointerType = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[1] == attributeVariable).Operands[0];
        var scalarType = ResolveScalarType(instructions, pointerType);
        Assert.Equal(expectedScalarType, scalarType.Opcode);
        Assert.Equal(32u, scalarType.Operands[1]);
        if (expectedScalarType == SpirvOp.TypeInt)
        {
            Assert.Equal(expectedSignedness, scalarType.Operands[2]);
        }

        var attributeLoad = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[2] == attributeVariable);
        var loadedId = attributeLoad.Operands[1];
        var scalarTypeId = scalarType.Operands[0];
        var extracts = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.CompositeExtract &&
                instruction.Operands[2] == loadedId)
            .ToArray();
        Assert.Equal(2, extracts.Length);
        Assert.All(extracts, extract => Assert.Equal(scalarTypeId, extract.Operands[0]));
        Assert.All(
            extracts,
            extract =>
                Assert.Equal(
                    expectsBitcast,
                    instructions.Any(instruction =>
                        instruction.Opcode == SpirvOp.Bitcast &&
                        instruction.Operands[2] == extract.Operands[1])));
    }

    private static Gen5ShaderProgram CreateVertexFetchProgram() =>
        new(
            Address: 0x1000,
            Instructions:
            [
                new Gen5ShaderInstruction(
                    Pc: 0,
                    Encoding: Gen5ShaderEncoding.Mubuf,
                    Opcode: "BufferLoadFormatXy",
                    Words: [],
                    Sources: [],
                    Destinations: [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                    Control: new Gen5BufferMemoryControl(
                        DwordCount: 2,
                        VectorAddress: 0,
                        VectorData: 0,
                        ScalarResource: 0,
                        OffsetBytes: 0,
                        IndexEnabled: false,
                        OffsetEnabled: false,
                        Glc: false,
                        Slc: false)),
                new Gen5ShaderInstruction(
                    Pc: 4,
                    Encoding: Gen5ShaderEncoding.Sopp,
                    Opcode: "SEndpgm",
                    Words: [0xBF810000u],
                    Sources: [],
                    Destinations: [],
                    Control: null),
            ]);

    private static IReadOnlyList<SpirvInstruction> ParseInstructions(byte[] spirv)
    {
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(0, spirv.Length % sizeof(uint));
        var words = new uint[spirv.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(index * sizeof(uint), sizeof(uint)));
        }

        var instructions = new List<SpirvInstruction>();
        for (var index = 5; index < words.Length;)
        {
            var wordCount = checked((int)(words[index] >> 16));
            Assert.True(wordCount > 0);
            Assert.True(index + wordCount <= words.Length);
            var opcode = (SpirvOp)(words[index] & 0xFFFF);
            instructions.Add(
                new SpirvInstruction(
                    opcode,
                    words.AsSpan(index + 1, wordCount - 1).ToArray()));
            index += wordCount;
        }

        return instructions;
    }

    private static uint FindNamedId(
        IReadOnlyList<SpirvInstruction> instructions,
        string name) =>
        Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands.AsSpan(1)) == name).Operands[0];

    private static SpirvInstruction ResolveScalarType(
        IReadOnlyList<SpirvInstruction> instructions,
        uint pointerType)
    {
        var pointer = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.TypePointer &&
                instruction.Operands[0] == pointerType);
        var pointeeType = pointer.Operands[2];
        var pointee = Assert.Single(
            instructions,
            instruction =>
                instruction.Operands.Length > 0 &&
                instruction.Operands[0] == pointeeType &&
                instruction.Opcode is SpirvOp.TypeInt or SpirvOp.TypeFloat or SpirvOp.TypeVector);
        if (pointee.Opcode != SpirvOp.TypeVector)
        {
            return pointee;
        }

        var componentType = pointee.Operands[1];
        return Assert.Single(
            instructions,
            instruction =>
                instruction.Operands.Length > 0 &&
                instruction.Operands[0] == componentType &&
                instruction.Opcode is SpirvOp.TypeInt or SpirvOp.TypeFloat);
    }

    private static string DecodeString(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                words[index]);
        }

        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(
            bytes,
            0,
            terminator >= 0 ? terminator : bytes.Length);
    }

    private sealed record SpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
