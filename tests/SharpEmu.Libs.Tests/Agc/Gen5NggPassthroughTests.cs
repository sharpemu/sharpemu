// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5NggPassthroughTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint VgtShaderStagesEn = 0x2D5;

    [Fact]
    public void GraphicsStageClassifier_MissingRegisterUsesResetVertexState()
    {
        Assert.True(
            Gen5GraphicsStageClassifier.TryClassify(
                new Dictionary<uint, uint>(),
                out var stage,
                out var error),
            error);
        Assert.Equal(Gen5GraphicsStageMode.Vertex, stage);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x8000_0000u, 0)]
    [InlineData(0x0200_2000u, 1)]
    public void GraphicsStageClassifier_DecodesPrimitiveGenerationBits(
        uint value,
        int expectedStage)
    {
        var registers = new Dictionary<uint, uint>
        {
            [VgtShaderStagesEn] = value,
        };

        Assert.True(
            Gen5GraphicsStageClassifier.TryClassify(
                registers,
                out var stage,
                out var error),
            error);
        Assert.Equal(expectedStage, (int)stage);
    }

    [Theory]
    [InlineData(0x0000_2000u)]
    [InlineData(0x0200_0000u)]
    public void GraphicsStageClassifier_RejectsUnsupportedOrInvalidNggState(uint value)
    {
        var registers = new Dictionary<uint, uint>
        {
            [VgtShaderStagesEn] = value,
        };

        Assert.False(
            Gen5GraphicsStageClassifier.TryClassify(
                registers,
                out _,
                out var error));
        Assert.Contains("VGT_SHADER_STAGES_EN", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryVertexCompiler_RejectsNggPrimitiveProtocol(bool primitiveExport)
    {
        var words = primitiveExport
            ? new uint[] { 0xF800_0941, 0 }
            : [0xBF90_0009];
        var (_, state, evaluation) = CreateShader(words);

        Assert.False(
            Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out _,
                out var error));
        Assert.Contains("NGG primitive-generation", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NggPassthroughCompiler_SeedsMergedWaveInfoAndPreservesVertexInputs()
    {
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5GraphicsAbi.MergedWaveInfoScalarRegister] = 0xAABB_CC00,
        };
        var (_, state, evaluation) = CreateShader(
            [0xBF90_0009, 0xF800_0941, 0],
            Gen5GraphicsStageMode.NggPassthrough,
            shaderRegisters);

        Assert.Equal(
            0xAABB_CC20u,
            evaluation.InitialScalarRegisters[
                (int)Gen5GraphicsAbi.MergedWaveInfoScalarRegister]);
        Assert.True(
            Gen5SpirvTranslator.TryCompileNggPassthroughVertexShader(
                state,
                evaluation,
                out var shader,
                out var error,
                totalGlobalBufferCount: 1,
                scalarRegisterBufferIndex: 0),
            error);
        Assert.True(shader.SubgroupRequirements.RequiresWave32);

        var module = ParseModule(shader.Spirv);
        AssertMergedWaveInfoSeed(module);
        AssertVertexSystemValue(module, 5, SpirvBuiltIn.VertexIndex);
        AssertVertexSystemValue(module, 8, SpirvBuiltIn.InstanceIndex);
    }

    private static (
        CpuContext Context,
        Gen5ShaderState State,
        Gen5ShaderEvaluation Evaluation) CreateShader(
            uint[] words,
            Gen5GraphicsStageMode graphicsStageMode = Gen5GraphicsStageMode.Vertex,
            IReadOnlyDictionary<uint, uint>? shaderRegisters = null)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        var syntheticUserData = shaderRegisters ?? new Dictionary<uint, uint>();
        var hardwareRegisters = new Dictionary<uint, uint>
        {
            [0x4B] = (uint)(syntheticUserData.Count == 0
                ? 0
                : syntheticUserData.Keys.Max() + 1) << 1,
        };
        foreach (var (register, value) in syntheticUserData)
        {
            hardwareRegisters[0x4C + register] = value;
        }

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                context,
                ShaderAddress,
                0,
                hardwareRegisters,
                0x4C,
                out var state,
                out var error,
                graphicsStageMode: graphicsStageMode),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                context,
                state,
                out var evaluation,
                out error),
            error);
        return (context, state, evaluation);
    }

    private static void AssertMergedWaveInfoSeed(IReadOnlyList<ParsedInstruction> module)
    {
        var constants = Constants(module);
        var scalarRegisters = FindNamedVariable(module, "sgpr");
        var registerPointers = FindRegisterPointers(
            module,
            constants,
            scalarRegisters,
            Gen5GraphicsAbi.MergedWaveInfoScalarRegister);
        var finalStore = module.Last(instruction =>
            instruction.Opcode == SpirvOp.Store &&
            registerPointers.Contains(instruction.Operands[0]));
        var merge = module.Single(instruction =>
            instruction.Opcode == SpirvOp.BitwiseOr &&
            instruction.Operands[1] == finalStore.Operands[1]);
        var mergeInputs = merge.Operands.Skip(2).ToArray();
        var laneCount = mergeInputs.Single(id =>
            constants.GetValueOrDefault(id) == Gen5GraphicsAbi.Wave32LaneCount);
        var maskedUpper = mergeInputs.Single(id => id != laneCount);
        var mask = module.Single(instruction =>
            instruction.Opcode == SpirvOp.BitwiseAnd &&
            instruction.Operands[1] == maskedUpper);

        Assert.Contains(
            mask.Operands.Skip(2),
            id => constants.GetValueOrDefault(id) == 0xFFFF_FF00u);
    }

    private static void AssertVertexSystemValue(
        IReadOnlyList<ParsedInstruction> module,
        uint vectorRegister,
        SpirvBuiltIn builtIn)
    {
        var constants = Constants(module);
        var vectorRegisters = FindNamedVariable(module, "vgpr");
        var registerPointers = FindRegisterPointers(
            module,
            constants,
            vectorRegisters,
            vectorRegister);
        var builtInVariable = module.Single(instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)builtIn).Operands[0];
        var loadedValues = module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == builtInVariable)
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();

        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                registerPointers.Contains(instruction.Operands[0]) &&
                loadedValues.Contains(instruction.Operands[1]));
    }

    private static HashSet<uint> FindRegisterPointers(
        IReadOnlyList<ParsedInstruction> module,
        IReadOnlyDictionary<uint, uint> constants,
        uint registers,
        uint register)
    {
        return module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.AccessChain &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[2] == registers &&
                constants.GetValueOrDefault(instruction.Operands[3]) == register)
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();
    }

    private static uint FindNamedVariable(
        IReadOnlyList<ParsedInstruction> module,
        string name) =>
        module.Single(instruction =>
            instruction.Opcode == SpirvOp.Name &&
            DecodeString(instruction.Operands, 1) == name).Operands[0];

    private static IReadOnlyDictionary<uint, uint> Constants(
        IReadOnlyList<ParsedInstruction> module) =>
        module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(header >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(
                        offset + ((index + 1) * sizeof(uint)),
                        sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static string DecodeString(uint[] operands, int startIndex)
    {
        var bytes = new List<byte>();
        foreach (var word in operands.Skip(startIndex))
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
