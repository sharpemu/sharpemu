// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelOutputMappingTests
{
    [Theory]
    [InlineData(Gen5PixelOutputKind.Uint, 3u)]
    [InlineData(Gen5PixelOutputKind.Uint, 12u)]
    [InlineData(Gen5PixelOutputKind.Uint, 15u)]
    [InlineData(Gen5PixelOutputKind.Sint, 3u)]
    [InlineData(Gen5PixelOutputKind.Sint, 12u)]
    [InlineData(Gen5PixelOutputKind.Sint, 15u)]
    public void CompressedIntegerExportExtractsFieldsAndPreservesDisabledComponents(
        Gen5PixelOutputKind kind, uint enableMask)
    {
        var request = CompressedPixelRequest(kind, enableMask);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = ReadInstructions(shader.Spirv);
        var constants = instructions.Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(instruction => instruction.Operands[1], instruction => instruction.Operands[2]);
        var signed = kind == Gen5PixelOutputKind.Sint;
        var extracts = instructions.Where(instruction => instruction.Opcode ==
            (signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract)).ToArray();
        var enabledComponents = Enumerable.Range(0, 4)
            .Where(component => (enableMask & (1u << component)) != 0).ToArray();
        Assert.Equal(enabledComponents.Length, extracts.Length);
        for (var index = 0; index < extracts.Length; index++)
        {
            var extract = extracts[index];
            var component = enabledComponents[index];
            Assert.Equal((uint)(component & 1) * 16, constants[extract.Operands[3]]);
            Assert.Equal(16u, constants[extract.Operands[4]]);
            var integerType = Assert.Single(instructions, instruction =>
                instruction.Opcode == SpirvOp.TypeInt && instruction.Operands[0] == extract.Operands[0]);
            Assert.Equal([32u, signed ? 1u : 0u], integerType.Operands[1..]);
            var packedValue = extract.Operands[2];
            if (signed)
            {
                packedValue = Assert.Single(instructions, instruction =>
                    instruction.Opcode == SpirvOp.Bitcast && instruction.Operands[1] == packedValue).Operands[2];
            }
            var load = Assert.Single(instructions, instruction =>
                instruction.Opcode == SpirvOp.Load && instruction.Operands[1] == packedValue);
            var name = Assert.Single(instructions, instruction =>
                instruction.Opcode == SpirvOp.Name && instruction.Operands[0] == load.Operands[2]);
            Assert.Equal($"v{component >> 1}", DecodeString(name.Operands[1..]));
        }
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode is SpirvOp.ConvertFToU or SpirvOp.ConvertFToS);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 62);
        var preserved = instructions.Where(instruction => instruction.Opcode == SpirvOp.CompositeExtract)
            .Select(instruction => instruction.Operands[^1]).ToArray();
        Assert.Equal(Enumerable.Range(0, 4).Where(component => (enableMask & (1u << component)) == 0)
            .Select(component => (uint)component), preserved);

        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out error), error);
        var componentType = signed ? "int" : "uint";
        var packedType = signed ? "short2" : "ushort2";
        var components = Enumerable.Range(0, 4).Select(component =>
            (enableMask & (1u << component)) != 0
                ? $"{componentType}(as_type<{packedType}>(v[{component >> 1}])[{component & 1}])"
                : $"sharpemu_out.mrt0[{component}]");
        Assert.Contains($"vec<{componentType}, 4>({string.Join(", ", components)})", metal.Source);
        Assert.DoesNotContain("as_type<half2>(v[", metal.Source);
    }

    [Fact]
    public void CompressedFloatExportStillUnpacksHalfFloats()
    {
        var request = CompressedPixelRequest(Gen5PixelOutputKind.Float, 15);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = ReadInstructions(shader.Spirv);
        Assert.Equal(4, instructions.Count(instruction =>
            instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 62));
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out error), error);
        Assert.Contains("(float)as_type<half2>(v[0])[0]", metal.Source);
        Assert.Contains("(float)as_type<half2>(v[1])[1]", metal.Source);
    }

    private static ShaderCompileRequest CompressedPixelRequest(Gen5PixelOutputKind kind, uint enableMask)
    {
        var export = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [], new Gen5ExportControl(0, enableMask, true, true, true));
        var program = ResourceTestProgram.Program(export, ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new(0, 0, kind)],
        };
    }

    [Theory]
    [InlineData(0u, 7u, false)]
    [InlineData(1u, 2u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(1u, 0u, true)]
    [InlineData(0u, 1u, true)]
    public void BothEmittersRequireUniqueContiguousHostLocations(uint firstLocation, uint secondLocation, bool valid)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs =
            [
                new(0, firstLocation, Gen5PixelOutputKind.Float),
                new(3, secondLocation, Gen5PixelOutputKind.Float),
            ],
        };
        Assert.Equal(valid, Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError));
        Assert.Equal(valid, Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError));
        if (!valid)
        {
            Assert.Contains("host locations", spirvError);
            Assert.Contains("host locations", metalError);
        }
    }

    [Fact]
    public void IdentityOutputDoesNotAddComponentShuffle()
    {
        var instructions = ReadInstructions(
            CompilePixelProgram(Gen5ColorComponentMapping.Identity));

        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.VectorShuffle);
    }

    [Fact]
    public void BgraOutputShufflesGuestComponentsToPhysicalOrder()
    {
        var instructions = ReadInstructions(
            CompilePixelProgram(new Gen5ColorComponentMapping(0xC6)));
        var shuffle = Assert.Single(
            instructions,
            instruction => instruction.Opcode == SpirvOp.VectorShuffle);

        Assert.Equal([2u, 1u, 0u, 3u], shuffle.Operands[^4..]);
    }

    [Fact]
    public void BgraPartialExportPreservesPhysicalComponents()
    {
        var instructions = ReadInstructions(
            CompilePixelProgram(new Gen5ColorComponentMapping(0xC6), enableMask: 0x1));
        var preservedComponents = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.CompositeExtract)
            .Select(instruction => instruction.Operands[^1])
            .ToArray();

        Assert.Equal([1u, 0u, 3u], preservedComponents);
    }

    [Fact]
    public void NullValidMaskExportControlsFragmentValidity()
    {
        var instructions = ReadInstructions(
            CompilePixelProgram(
                Gen5ColorComponentMapping.Identity,
                target: 9,
                outputs: []));
        var validMaskName = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands[1..]) == "pixelValidMaskActive");
        var validMaskVariable = validMaskName.Operands[0];

        // One function store initializes the mask and a second publishes EXEC
        // from the NULL EXP.VM. The epilogue reads it before OpKill.
        Assert.True(
            instructions.Count(instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[0] == validMaskVariable) >= 2);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[^1] == validMaskVariable);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.Kill);
    }

    [Fact]
    public void PixelWaveMaskControlUsesSubgroupBallot()
    {
        var moveVcc = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sop1,
            "SMovB64",
            [0u],
            [Gen5Operand.Scalar(106)],
            [Gen5Operand.Scalar(0)],
            null);
        var instructions = ReadInstructions(
            CompilePixelProgram(
                Gen5ColorComponentMapping.Identity,
                prefix: [moveVcc]));

        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.GroupNonUniformBallot);
        var subgroupBuiltIn = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[0] == subgroupBuiltIn.Operands[0] &&
                instruction.Operands[1] == (uint)SpirvDecoration.Flat);
    }

    [Fact]
    public void PixelWaveMaskControlCanDisableGraphicsSubgroups()
    {
        var moveVcc = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sop1,
            "SMovB64",
            [0u],
            [Gen5Operand.Scalar(106)],
            [Gen5Operand.Scalar(0)],
            null);
        var instructions = ReadInstructions(
            CompilePixelProgram(
                Gen5ColorComponentMapping.Identity,
                prefix: [moveVcc],
                enableGraphicsSubgroupOperations: false));

        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.GroupNonUniformBallot);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
    }

    private static byte[] CompilePixelProgram(
        Gen5ColorComponentMapping componentMapping,
        uint enableMask = 0xF,
        uint target = 0,
        IReadOnlyList<Gen5PixelOutputBinding>? outputs = null,
        IReadOnlyList<Gen5ShaderInstruction>? prefix = null,
        bool enableGraphicsSubgroupOperations = true)
    {
        var prefixInstructions = prefix ?? [];
        var export = new Gen5ShaderInstruction(
            (uint)(prefixInstructions.Count * sizeof(uint)),
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
            new Gen5ExportControl(target, enableMask, false, true, true));
        var end = new Gen5ShaderInstruction(
            (uint)((prefixInstructions.Count + 2) * sizeof(uint)),
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(0x1_0000_D000, [.. prefixInstructions, export, end]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = outputs ?? [new Gen5PixelOutputBinding(
                    0,
                    0,
                    Gen5PixelOutputKind.Float,
                    componentMapping)],
            EnableGraphicsSubgroupOperations = enableGraphicsSubgroupOperations,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        return shader.Spirv;
    }

    private static string DecodeString(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
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
