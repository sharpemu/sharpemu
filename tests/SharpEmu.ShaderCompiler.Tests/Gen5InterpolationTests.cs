// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5InterpolationTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public void VInterpMovF32DecodesCoefficientSelector(uint selector)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> words = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(words, EncodeVInterpMov(selector));
        BinaryPrimitives.WriteUInt32LittleEndian(words[sizeof(uint)..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, words));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "VInterpMovF32");
        Assert.Equal(Gen5ShaderEncoding.Vintrp, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(selector), Assert.Single(instruction.Sources));
        Assert.Equal(Gen5Operand.Vector(9), Assert.Single(instruction.Destinations));
        var interpolation = Assert.IsType<Gen5InterpolationControl>(instruction.Control);
        Assert.Equal(7u, interpolation.Attribute);
        Assert.Equal(3u, interpolation.Channel);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public void VInterpMovF32ReconstructsInterpolationCoefficients(uint selector)
    {
        var spirv = CompileVInterpMov(selector);
        var instructions = ReadInstructions(spirv);

        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == (ushort)SpirvOp.Capability &&
                instruction.Operands.SequenceEqual(
                    [(uint)SpirvCapability.FragmentBarycentricKhr]));

        var location = Assert.Single(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Location,
                7));
        var inputVariable = location.Operands[0];
        Assert.DoesNotContain(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.PerVertexKhr,
                expectedOperand: null) &&
                instruction.Operands[0] == inputVariable);

        var loadIds = instructions
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == inputVariable)
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(loadIds);

        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == (ushort)SpirvOp.CompositeExtract &&
                instruction.Operands.Length >= 4 &&
                loadIds.Contains(instruction.Operands[2]) &&
                instruction.Operands[3] == 3);
        Assert.True(
            instructions.Count(
                instruction => instruction.Opcode == (ushort)SpirvOp.DPdx) >= 3);
        Assert.True(
            instructions.Count(
                instruction => instruction.Opcode == (ushort)SpirvOp.DPdy) >= 3);
    }

    [Fact]
    public void PixelBarycentricsPopulateGuestIAndJRegisters()
    {
        var instructions = ReadInstructions(CompileVInterpMov(2));
        var builtIn = Assert.Single(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.BuiltIn,
                (uint)SpirvBuiltIn.BaryCoordKhr));
        var barycentricVariable = builtIn.Operands[0];
        var barycentricLoadIds = instructions
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == barycentricVariable)
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(barycentricLoadIds);

        foreach (var component in new uint[] { 1, 2 })
        {
            Assert.Contains(
                instructions,
                instruction => instruction.Opcode == (ushort)SpirvOp.CompositeExtract &&
                    instruction.Operands.Length >= 4 &&
                    barycentricLoadIds.Contains(instruction.Operands[2]) &&
                    instruction.Operands[3] == component);
        }
    }

    [Fact]
    public void VInterpMovF32SelectorsProduceDistinctLowering()
    {
        var p10 = CompileVInterpMov(0);
        var p20 = CompileVInterpMov(1);
        var p0 = CompileVInterpMov(2);

        Assert.False(p10.SequenceEqual(p20));
        Assert.False(p10.SequenceEqual(p0));
        Assert.False(p20.SequenceEqual(p0));
    }

    [Fact]
    public void VInterpMovF32ReconstructsCoefficientsBeforeDispatcherLoop()
    {
        var instructions = ReadInstructions(CompileVInterpMov(2));
        var loopMergeIndex = Assert.Single(
            instructions
                .Select((instruction, index) => (instruction, index))
                .Where(item =>
                    item.instruction.Opcode == (ushort)SpirvOp.LoopMerge)
                .Select(item => item.index));
        var derivativeIndices = instructions
            .Select((instruction, index) => (instruction, index))
            .Where(item =>
                item.instruction.Opcode is
                    (ushort)SpirvOp.DPdx or
                    (ushort)SpirvOp.DPdy)
            .Select(item => item.index)
            .ToArray();

        Assert.NotEmpty(derivativeIndices);
        Assert.All(derivativeIndices, index => Assert.True(index < loopMergeIndex));
    }

    [Fact]
    public void VInterpMovF32RequiresHostBarycentricSupport()
    {
        Assert.False(
            TryCompileVInterpMov(
                2,
                out _,
                out var error,
                fragmentShaderBarycentric: false));
        Assert.Contains("VK_KHR_fragment_shader_barycentric", error);
    }

    [Fact]
    public void VInterpMovF32RejectsReservedSelector()
    {
        Assert.False(TryCompileVInterpMov(3, out _, out var error));
        Assert.Contains("invalid interpolation move selector 3", error);
    }

    [Fact]
    public void CustomVInterpMovUsesMappedFlatInputWithoutDerivatives()
    {
        Assert.True(
            TryCompileVInterpMov(
                2,
                out var spirv,
                out var error,
                pixelInputControls: [0, 1, 2, 3, 4, 5, 6, 0x423]),
            error);
        var instructions = ReadInstructions(spirv);
        var location = Assert.Single(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Location,
                7));
        var inputVariable = location.Operands[0];
        Assert.Contains(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Flat,
                expectedOperand: null) &&
                instruction.Operands[0] == inputVariable);
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode is
                (ushort)SpirvOp.DPdx or
                (ushort)SpirvOp.DPdy);
    }

    [Fact]
    public void AliasedGuestInputsUseDistinctHostLocations()
    {
        var interpolations = new[]
        {
            new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vintrp,
                "VInterpP1F32",
                [],
                [],
                [Gen5Operand.Vector(0)],
                new Gen5InterpolationControl(0, 0)),
            new Gen5ShaderInstruction(
                4,
                Gen5ShaderEncoding.Vintrp,
                "VInterpP1F32",
                [],
                [],
                [Gen5Operand.Vector(1)],
                new Gen5InterpolationControl(1, 0)),
        };
        var export = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
            ],
            [],
            new Gen5ExportControl(0, 0xFu, false, true, true));
        var end = new Gen5ShaderInstruction(
            16,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(
            ShaderAddress,
            [.. interpolations, export, end]);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error,
                pixelInputControls: [0, 0x400]),
            error);
        var instructions = ReadInstructions(shader.Spirv);
        // Location zero is shared by the first input and MRT0 output; the
        // second aliased guest input must still receive its own host location.
        Assert.Equal(
            2,
            instructions.Count(
                instruction => IsDecoration(
                    instruction,
                    SpirvDecoration.Location,
                    0)));
        var secondInputLocation = Assert.Single(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Location,
                1));
        Assert.Contains(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Flat,
                expectedOperand: null) &&
                instruction.Operands[0] == secondInputLocation.Operands[0]);
    }

    [Fact]
    public void VertexExportFansOutThroughPixelSemanticMapping()
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
            new Gen5ExportControl(35, 0xFu, false, true, true));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(ShaderAddress, [export, end]);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var shader,
                out var error,
                pixelInputControls: [0, 2, 0x423]),
            error);
        var instructions = ReadInstructions(shader.Spirv);
        var mappedLocation = Assert.Single(
            instructions,
            instruction => IsDecoration(
                instruction,
                SpirvDecoration.Location,
                2));
        var mappedOutput = mappedLocation.Operands[0];
        Assert.True(
            instructions.Count(
                instruction =>
                    instruction.Opcode == (ushort)SpirvOp.Store &&
                    instruction.Operands.Length >= 2 &&
                    instruction.Operands[0] == mappedOutput) >= 2);
    }

    private static byte[] CompileVInterpMov(uint selector)
    {
        Assert.True(TryCompileVInterpMov(selector, out var spirv, out var error), error);
        return spirv;
    }

    private static bool TryCompileVInterpMov(
        uint selector,
        out byte[] spirv,
        out string error,
        bool fragmentShaderBarycentric = true,
        IReadOnlyList<uint>? pixelInputControls = null)
    {
        var interpolation = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vintrp,
            "VInterpMovF32",
            [EncodeVInterpMov(selector)],
            [Gen5Operand.Vector(selector)],
            [Gen5Operand.Vector(9)],
            new Gen5InterpolationControl(7, 3));
        var export = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(9),
            ],
            [],
            new Gen5ExportControl(0, 0xFu, false, true, true));
        var end = new Gen5ShaderInstruction(
            12,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [interpolation, export, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        var result = Gen5SpirvTranslator.TryCompilePixelShader(
            state,
            evaluation,
            Gen5PixelOutputKind.Float,
            out var shader,
            out error,
            pixelInputEnable: 1u << 1,
            pixelInputAddress: 1u << 1,
            fragmentShaderBarycentric: fragmentShaderBarycentric,
            pixelInputControls: pixelInputControls);
        spirv = result ? shader.Spirv : [];
        return result;
    }

    private static uint EncodeVInterpMov(uint selector) =>
        0xC8000000u |
        (2u << 16) |
        (9u << 18) |
        (7u << 10) |
        (3u << 8) |
        selector;

    private static bool IsDecoration(
        SpirvInstruction instruction,
        SpirvDecoration decoration,
        uint? expectedOperand)
    {
        if (instruction.Opcode != (ushort)SpirvOp.Decorate ||
            instruction.Operands.Length < 2 ||
            instruction.Operands[1] != (uint)decoration)
        {
            return false;
        }

        return expectedOperand is null ||
            (instruction.Operands.Length >= 3 &&
             instruction.Operands[2] == expectedOperand.Value);
    }

    private static IReadOnlyList<SpirvInstruction> ReadInstructions(byte[] spirv)
    {
        Assert.Equal(0, spirv.Length % sizeof(uint));
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));

        var instructions = new List<SpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint))));
            }

            instructions.Add(new SpirvInstruction((ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private sealed record SpirvInstruction(ushort Opcode, uint[] Operands);

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = checked((int)relative);
            return true;
        }
    }
}
