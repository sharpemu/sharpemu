// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5DataShareSpirvTests
{
    [Fact]
    public void WriteAddtidComputeUsesM0ImmediateAndSubgroupLaneForLdsStore()
    {
        var initialScalars = new uint[256];
        initialScalars[124] = 0xCAFE_0100;
        var evaluation = CreateEvaluation(initialScalars);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                CreateState(),
                evaluation,
                localSizeX: 32,
                localSizeY: 1,
                localSizeZ: 1,
                out var shader,
                out var error),
            error);

        var instructions = ParseInstructions(shader.Spirv);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] == (uint)SpirvCapability.GroupNonUniformBallot);
        var subgroupInput = FindBuiltInVariable(
            instructions,
            SpirvBuiltIn.SubgroupLocalInvocationId);
        var subgroupLoads = ResultIds(
            instructions,
            SpirvOp.Load,
            instruction => instruction.Operands[2] == subgroupInput);
        var lanes = FindBinaryResults(
            instructions,
            SpirvOp.BitwiseAnd,
            subgroupLoads,
            FindConstantId(instructions, 31));
        var shiftByTwo = FindBinaryResults(
            instructions,
            SpirvOp.BitwiseAnd,
            new HashSet<uint> { FindConstantId(instructions, 2) },
            FindConstantId(instructions, 31));
        var laneOffset = FindBinaryResult(
            instructions,
            SpirvOp.ShiftLeftLogical,
            lanes,
            shiftByTwo,
            commutative: false);

        var scalarRegisters = FindNamedId(instructions, "sgpr");
        var m0Pointers = ResultIds(
            instructions,
            SpirvOp.AccessChain,
            instruction =>
                instruction.Operands[2] == scalarRegisters &&
                instruction.Operands[^1] == FindConstantId(instructions, 124));
        var m0Loads = ResultIds(
            instructions,
            SpirvOp.Load,
            instruction => m0Pointers.Contains(instruction.Operands[2]));
        var maskedM0 = FindBinaryResult(
            instructions,
            SpirvOp.BitwiseAnd,
            m0Loads,
            FindConstantId(instructions, ushort.MaxValue));

        var m0AndLane = FindBinaryResult(
            instructions,
            SpirvOp.IAdd,
            new HashSet<uint> { maskedM0 },
            laneOffset);
        var byteAddress = FindBinaryResult(
            instructions,
            SpirvOp.IAdd,
            new HashSet<uint> { m0AndLane },
            FindConstantId(instructions, 0x1234));
        var dwordAddress = FindBinaryResult(
            instructions,
            SpirvOp.ShiftRightLogical,
            new HashSet<uint> { byteAddress },
            shiftByTwo,
            commutative: false);
        var boundedDwordAddress = FindBinaryResult(
            instructions,
            SpirvOp.BitwiseAnd,
            new HashSet<uint> { dwordAddress },
            FindConstantId(instructions, 8191));

        var lds = FindNamedId(instructions, "lds");
        var ldsPointer = Assert.Single(
            ResultIds(
                instructions,
                SpirvOp.AccessChain,
                instruction =>
                    instruction.Operands[2] == lds &&
                    instruction.Operands[^1] == boundedDwordAddress));
        AssertLdsStoreUsesVector(instructions, ldsPointer, 42);
        Assert.Equal(
            SpirvStorageClass.Workgroup,
            FindVariableStorageClass(instructions, lds));
    }

    [Fact]
    public void WriteAddtidWave64UsesLocalInvocationIndexModulo64()
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                CreateState(),
                CreateEvaluation(new uint[256]),
                localSizeX: 64,
                localSizeY: 1,
                localSizeZ: 1,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);

        var instructions = ParseInstructions(shader.Spirv);
        var localInvocationIndex = FindBuiltInVariable(
            instructions,
            SpirvBuiltIn.LocalInvocationIndex);
        var localIndexLoads = ResultIds(
            instructions,
            SpirvOp.Load,
            instruction => instruction.Operands[2] == localInvocationIndex);
        var lanes = FindBinaryResults(
            instructions,
            SpirvOp.BitwiseAnd,
            localIndexLoads,
            FindConstantId(instructions, 63));
        var shiftByTwo = FindBinaryResults(
            instructions,
            SpirvOp.BitwiseAnd,
            new HashSet<uint> { FindConstantId(instructions, 2) },
            FindConstantId(instructions, 31));

        _ = FindBinaryResult(
            instructions,
            SpirvOp.ShiftLeftLogical,
            lanes,
            shiftByTwo,
            commutative: false);
    }

    [Fact]
    public void WriteAddtidVertexKeepsPerInvocationLdsWithoutSubgroupInterface()
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileVertexShader(
                CreateState(),
                CreateEvaluation(new uint[256]),
                out var shader,
                out var error),
            error);

        var instructions = ParseInstructions(shader.Spirv);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] == (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] == (uint)SpirvCapability.GroupNonUniform);

        var lds = FindNamedId(instructions, "lds");
        Assert.Equal(
            SpirvStorageClass.Private,
            FindVariableStorageClass(instructions, lds));
        var ldsPointer = Assert.Single(
            ResultIds(
                instructions,
                SpirvOp.AccessChain,
                instruction => instruction.Operands[2] == lds));
        AssertLdsStoreUsesVector(instructions, ldsPointer, 42);
    }

    private static Gen5ShaderState CreateState() =>
        new(CreateProgram(), new uint[16], Metadata: null);

    private static Gen5ShaderProgram CreateProgram() =>
        new(
            Address: 0x1000,
            Instructions:
            [
                new Gen5ShaderInstruction(
                    Pc: 0,
                    Encoding: Gen5ShaderEncoding.Ds,
                    Opcode: "DsWriteAddtidB32",
                    Words: [],
                    Sources: [Gen5Operand.Scalar(124), Gen5Operand.Vector(42)],
                    Destinations: [],
                    Control: new Gen5DataShareControl(0x34, 0x12, Gds: false)),
                new Gen5ShaderInstruction(
                    Pc: 8,
                    Encoding: Gen5ShaderEncoding.Sopp,
                    Opcode: "SEndpgm",
                    Words: [0xBF810000u],
                    Sources: [],
                    Destinations: [],
                    Control: null),
            ]);

    private static Gen5ShaderEvaluation CreateEvaluation(uint[] initialScalars) =>
        new(
            initialScalars,
            new uint[256],
            Array.Empty<Gen5ImageBinding>(),
            Array.Empty<Gen5GlobalMemoryBinding>());

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
            instructions.Add(
                new SpirvInstruction(
                    (SpirvOp)(words[index] & 0xFFFF),
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

    private static uint FindBuiltInVariable(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvBuiltIn builtIn) =>
        Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] == (uint)builtIn).Operands[0];

    private static uint FindConstantId(
        IReadOnlyList<SpirvInstruction> instructions,
        uint value) =>
        Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[2] == value).Operands[1];

    private static HashSet<uint> ResultIds(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvOp opcode,
        Func<SpirvInstruction, bool> predicate) =>
        instructions
            .Where(instruction =>
                instruction.Opcode == opcode &&
                instruction.Operands.Length >= 2 &&
                predicate(instruction))
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();

    private static uint FindBinaryResult(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvOp opcode,
        IReadOnlySet<uint> leftCandidates,
        uint right,
        bool commutative = true)
        => FindBinaryResult(
            instructions,
            opcode,
            leftCandidates,
            new HashSet<uint> { right },
            commutative);

    private static uint FindBinaryResult(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvOp opcode,
        IReadOnlySet<uint> leftCandidates,
        IReadOnlySet<uint> rightCandidates,
        bool commutative = true)
    {
        var results = FindBinaryResults(
            instructions,
            opcode,
            leftCandidates,
            rightCandidates,
            commutative);
        Assert.True(
            results.Count == 1,
            $"Expected one {opcode} result, found {results.Count}.");
        return results.Single();
    }

    private static HashSet<uint> FindBinaryResults(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvOp opcode,
        IReadOnlySet<uint> leftCandidates,
        uint right,
        bool commutative = true) =>
        FindBinaryResults(
            instructions,
            opcode,
            leftCandidates,
            new HashSet<uint> { right },
            commutative);

    private static HashSet<uint> FindBinaryResults(
        IReadOnlyList<SpirvInstruction> instructions,
        SpirvOp opcode,
        IReadOnlySet<uint> leftCandidates,
        IReadOnlySet<uint> rightCandidates,
        bool commutative = true) =>
        instructions
            .Where(instruction =>
                instruction.Opcode == opcode &&
                instruction.Operands.Length == 4 &&
                (leftCandidates.Contains(instruction.Operands[2]) &&
                 rightCandidates.Contains(instruction.Operands[3]) ||
                 commutative &&
                 leftCandidates.Contains(instruction.Operands[3]) &&
                 rightCandidates.Contains(instruction.Operands[2])))
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();

    private static SpirvStorageClass FindVariableStorageClass(
        IReadOnlyList<SpirvInstruction> instructions,
        uint variable) =>
        (SpirvStorageClass)Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == variable).Operands[2];

    private static void AssertLdsStoreUsesVector(
        IReadOnlyList<SpirvInstruction> instructions,
        uint ldsPointer,
        uint vectorRegister)
    {
        var vectorRegisters = FindNamedId(instructions, "vgpr");
        var vectorPointers = ResultIds(
            instructions,
            SpirvOp.AccessChain,
            instruction =>
                instruction.Operands[2] == vectorRegisters &&
                instruction.Operands[^1] == FindConstantId(instructions, vectorRegister));
        var vectorLoads = ResultIds(
            instructions,
            SpirvOp.Load,
            instruction => vectorPointers.Contains(instruction.Operands[2]));
        var oldLdsLoads = ResultIds(
            instructions,
            SpirvOp.Load,
            instruction => instruction.Operands[2] == ldsPointer);
        var selectedValues = ResultIds(
            instructions,
            SpirvOp.Select,
            instruction =>
                instruction.Operands.Length == 5 &&
                vectorLoads.Contains(instruction.Operands[3]) &&
                oldLdsLoads.Contains(instruction.Operands[4]));

        var selectedValue = Assert.Single(selectedValues);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[0] == ldsPointer &&
                instruction.Operands[1] == selectedValue);
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
        return Encoding.UTF8.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length);
    }

    private sealed record SpirvInstruction(SpirvOp Opcode, uint[] Operands);
}
