// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class VectorCompareResourceTests
{
    [Fact]
    public void BitreplicateB64B32Compiles()
    {
        var program = Program(
            Sop1(0, "SBitreplicateB64B32", 0, Operand(7)),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Theory]
    [InlineData("VCmpEqU16")]
    [InlineData("VCmpLtU16")]
    [InlineData("VCmpGeU16")]
    [InlineData("VCmpEqI16")]
    [InlineData("VCmpLtI16")]
    public void Integer16BitCompareCompilesToSpirv(string opcode)
    {
        var program = Program(
            Vopc(0, opcode, Gen5Operand.Vector(0), 1),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void ExecutionComparePreservesResourcePointerWithUnknownVectorInputs()
    {
        var program = Program(
            ScalarLoad(0, 0, 106, count: 2),
            Vopc(8, "VCmpxLtU32", Gen5Operand.Vector(0), 1),
            Branch(12, "SCbranchExecz", 4),
            ScalarLoad(16, 106, 8, count: 4),
            BufferLoad(24, 8),
            EndProgram(32));
        var plan = Extract(program, userDataCount: 2);
        var memory = new TestWordMemory
        {
            Base = 0x2_00001000,
            Words = [0x1010, 2, 0, 0, 0x3000, 0, 256, 0],
        };
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 2], memory.Read),
            ref snapshot, ref specialization, out var failure));
        Assert.Equal(ResourceMaterializationFailure.None, failure);
        Assert.Equal(new uint[] { 0x3000, 0, 256, 0 }, Assert.Single(snapshot.Buffers));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompareUpdatesOnlyItsDestinationMask(bool updatesExecutionMask, bool comparisonPasses)
    {
        var program = Program(
            MoveScalar(0, 106, 0x12345678),
            MoveScalar(4, 107, 0x87654321),
            MoveVector(8, 0, comparisonPasses ? 2u : 0u),
            Vopc(12, updatesExecutionMask ? "VCmpxLtU32" : "VCmpLtU32", Operand(1), 0),
            MoveScalarRegister(16, 8, 106),
            MoveScalarRegister(20, 9, 107),
            MoveScalarRegister(24, 10, 126),
            MoveScalarRegister(28, 11, 127),
            BufferLoad(32, 8),
            EndProgram(40));
        var plan = Extract(program, userDataCount: 0, waveSize: 32);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan,
            Assert.Single(plan.Info.Buffers).Source, Inputs([]), out var descriptor));
        var comparisonMask = comparisonPasses ? 1u : 0u;
        Assert.Equal(new uint[]
        {
            updatesExecutionMask ? 0x12345678u : comparisonMask,
            0x87654321u,
            updatesExecutionMask ? comparisonMask : uint.MaxValue,
            0,
        }, descriptor.Dwords);
    }

    [Fact]
    public void Wave32SaveexecB32PreservesExecHighRegister()
    {
        var program = Program(
            MoveScalar(0, 127, 0x8765_4321),
            Sop1(4, "SAndSaveexecB32", 4, Operand(uint.MaxValue)),
            MoveScalarRegister(8, 8, 126),
            MoveScalarRegister(12, 9, 127),
            MoveScalar(16, 10, 256),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(32));
        var plan = Extract(program, userDataCount: 0, waveSize: 32);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(
            plan,
            Assert.Single(plan.Info.Buffers).Source,
            Inputs([]),
            out var descriptor));
        Assert.Equal(
            new uint[] { uint.MaxValue, 0x8765_4321, 256, 0 },
            descriptor.Dwords);
    }

    [Fact]
    public void Wave64InitialExecCoversBothMaskDwords()
    {
        var program = Program(
            MoveScalarRegister(0, 8, 126),
            MoveScalarRegister(4, 9, 127),
            MoveScalar(8, 10, 256),
            MoveScalar(12, 11, 0),
            BufferLoad(16, 8),
            EndProgram(24));
        var plan = Extract(program, userDataCount: 0, waveSize: 64);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(
            plan,
            Assert.Single(plan.Info.Buffers).Source,
            Inputs([]),
            out var descriptor));
        Assert.Equal(
            new uint[] { uint.MaxValue, uint.MaxValue, 256, 0 },
            descriptor.Dwords);
    }

    [Fact]
    public void Wave32SpirvSaveexecB64PreservesRawExecHigh()
    {
        var program = Program(
            Sop1(0, "SAndSaveexecB64", 4, Gen5Operand.Source(193)),
            EndProgram(4));
        var request = Request(program, userDataCount: 0, waveSize: 32);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Assert.True(StoreToSgprDependsOnLoadFromSgpr(shader.Spirv, storedRegister: 5, loadedRegister: 127));
    }

    private static bool StoreToSgprDependsOnLoadFromSgpr(
        byte[] spirv,
        uint storedRegister,
        uint loadedRegister)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var instructions = new List<(SpirvOp Opcode, uint[] Words)>();
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = checked((int)(words[offset] >> 16));
            if (wordCount <= 0 || offset + wordCount > words.Length)
            {
                return false;
            }

            instructions.Add(((SpirvOp)(words[offset] & 0xFFFF), words[offset..(offset + wordCount)]));
            offset += wordCount;
        }

        var scalarRegisters = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Name && instruction.Words.Length >= 3)
            .Single(instruction => DecodeSpirvString(instruction.Words.AsSpan(2)) == "sgpr")
            .Words[1];
        HashSet<uint> ConstantIds(uint value) => instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant &&
                instruction.Words.Length >= 4 && instruction.Words[3] == value)
            .Select(instruction => instruction.Words[2])
            .ToHashSet();
        HashSet<uint> Pointers(uint register)
        {
            var constants = ConstantIds(register);
            return instructions
                .Where(instruction => instruction.Opcode == SpirvOp.AccessChain &&
                    instruction.Words.Length >= 5 && instruction.Words[3] == scalarRegisters &&
                    constants.Contains(instruction.Words[^1]))
                .Select(instruction => instruction.Words[2])
                .ToHashSet();
        }

        var loadedPointers = Pointers(loadedRegister);
        var loadedValues = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Load &&
                instruction.Words.Length >= 4 && loadedPointers.Contains(instruction.Words[3]))
            .Select(instruction => instruction.Words[2])
            .ToHashSet();
        var storedPointers = Pointers(storedRegister);
        var storedValues = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Store &&
                instruction.Words.Length >= 3 && storedPointers.Contains(instruction.Words[1]))
            .Select(instruction => instruction.Words[2])
            .ToArray();
        var dependencies = new Dictionary<uint, uint[]>();
        foreach (var instruction in instructions)
        {
            if (instruction.Opcode is SpirvOp.UConvert or SpirvOp.BitwiseAnd or SpirvOp.BitwiseOr or
                SpirvOp.ShiftLeftLogical or SpirvOp.ShiftRightLogical)
            {
                dependencies[instruction.Words[2]] = instruction.Words[3..];
            }
        }

        bool DependsOnLoadedHigh(uint value, HashSet<uint> active)
        {
            if (loadedValues.Contains(value))
            {
                return true;
            }

            if (!active.Add(value) || !dependencies.TryGetValue(value, out var inputs))
            {
                return false;
            }

            var result = inputs.Any(input => DependsOnLoadedHigh(input, active));
            active.Remove(value);
            return result;
        }

        return storedValues.Any(value => DependsOnLoadedHigh(value, []));
    }

    private static string DecodeSpirvString(ReadOnlySpan<uint> words)
    {
        Span<byte> bytes = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes[(index * sizeof(uint))..],
                words[index]);
        }

        var terminator = bytes.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(bytes[..(terminator < 0 ? bytes.Length : terminator)]);
    }
}
