// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class WaveMaskSelectorBoundsTests
{
    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(7u, 33u)]
    [InlineData(31u, 65u)]
    [InlineData(0u, 1024u)]
    [InlineData(uint.MaxValue, 2u)]
    public void ValidMaskIncludesEveryRecord(uint first, uint count)
    {
        var fixture = CreateProgram();
        var plan = Extract(fixture.Program, userDataCount: 2);
        var proof = Assert.IsType<WaveMaskSelectorBounds>(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
        Assert.True(proof.TryEvaluate(Evaluator(plan, first, count), new(64, 8, 8, 1, false, 32, 2), out var values));
        Assert.Equal(Enumerable.Range(0, (int)count).Select(index => unchecked(first + (uint)index)), values);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1025u)]
    [InlineData(uint.MaxValue)]
    public void UnprovedCountRetainsGeneralAnalysis(uint count)
    {
        var fixture = CreateProgram();
        var plan = Extract(fixture.Program, userDataCount: 2);
        var proof = Assert.IsType<WaveMaskSelectorBounds>(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
        Assert.False(proof.TryEvaluate(Evaluator(plan, 0, count), new(64, 8, 8, 1, false, 32, 2), out _));
    }

    [Theory]
    [InlineData(32u, 8u, 8u, 1u, false)]
    [InlineData(64u, 4u, 16u, 1u, false)]
    [InlineData(64u, 8u, 16u, 1u, false)]
    [InlineData(64u, 8u, 8u, 2u, false)]
    [InlineData(64u, 8u, 8u, 1u, true)]
    public void OtherExecutionLayoutsRetainGeneralAnalysis(uint wave, uint width, uint height, uint depth, bool partial)
    {
        var fixture = CreateProgram();
        var plan = Extract(fixture.Program, userDataCount: 2);
        var proof = Assert.IsType<WaveMaskSelectorBounds>(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
        Assert.False(proof.TryEvaluate(Evaluator(plan, 0, 1), new(wave, width, height, depth, partial, 32, 2), out _));
    }

    [Fact]
    public void MissingCleanMemoryDoesNotNarrow()
    {
        var fixture = CreateProgram();
        var plan = Extract(fixture.Program, userDataCount: 2);
        var proof = Assert.IsType<WaveMaskSelectorBounds>(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
        var evaluator = new RuntimeValueEvaluator(plan, Inputs([0x3000, 0]));
        Assert.False(proof.TryEvaluate(evaluator, new(64, 8, 8, 1, false, 32, 2), out _));
        Assert.False(proof.TryEvaluate(Evaluator(plan, 0, 1), null, out _));
    }

    [Theory]
    [InlineData(1u, true, true)]
    [InlineData(5u, true, true)]
    [InlineData(6u, true, false)]
    [InlineData(1u, false, false)]
    public void MaterializationRetainsIncompatibleReachableImages(uint count, bool provideState, bool expectedSuccess)
    {
        var fixture = CreateProgram(includeImage: true);
        var plan = Extract(fixture.Program, userDataCount: 96);
        var registers = new uint[96];
        registers[0] = 0x3000;
        new uint[] { 0x1000, 224 << 16, 20, 0 }.CopyTo(registers, 60);
        new uint[] { 0x2000, 32 << 16, 2, 0 }.CopyTo(registers, 80);
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x3000) = 0;
        memory.At(0x3004) = count;
        memory.At(0x1000 + 5 * 224 + 4) = 1;
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        second[3] = (second[3] & 0x0FFFFFFF) | (10u << 28);
        ResourceTrackerTests.WriteImage(memory, 0x2000, first);
        ResourceTrackerTests.WriteImage(memory, 0x2020, second);
        var inputs = new ResourceRuntimeInputs
        {
            UserData = registers,
            ReadMemory = memory.Read,
            ReadCleanMemory = memory.Read,
            ComputeState = provideState ? new(64, 8, 8, 1, false, 32, 2) : null,
        };
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var failures = new List<IndirectImageFailure>();
        Assert.Equal(expectedSuccess, ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, failures.Add));
        if (expectedSuccess) Assert.Empty(failures);
        else Assert.Single(failures);
    }

    [Theory]
    [InlineData("count_guard")]
    [InlineData("mask_predicate")]
    [InlineData("clear_word")]
    [InlineData("clear_summary")]
    [InlineData("word_increment")]
    [InlineData("record_increment")]
    [InlineData("invalid_predicate")]
    [InlineData("lds_store")]
    [InlineData("lane_index")]
    [InlineData("initial_summary")]
    [InlineData("word_guard")]
    [InlineData("consumer")]
    [InlineData("invalid_lanes")]
    [InlineData("restore_inner")]
    [InlineData("inner_tail")]
    [InlineData("outer_tail")]
    public void ChangedInvariantDeclinesProof(string label)
    {
        var fixture = CreateProgram();
        var instructions = fixture.Program.Instructions.Select(instruction => instruction.Pc == fixture.Labels[label]
            ? Nop(instruction.Pc) with { Words = instruction.Words } : instruction).ToArray();
        var plan = Extract(Program(instructions), userDataCount: 2);
        Assert.Null(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
    }

    [Fact]
    public void InsufficientLocalStorageDoesNotNarrow()
    {
        var fixture = CreateProgram();
        var plan = Extract(fixture.Program, userDataCount: 2);
        var proof = Assert.IsType<WaveMaskSelectorBounds>(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
        Assert.False(proof.TryEvaluate(Evaluator(plan, 0, 65), new(64, 8, 8, 1, false, 2, 2), out _));
        Assert.False(proof.TryEvaluate(Evaluator(plan, 0, 1), new(64, 8, 8, 1, false, 32, 1), out _));
    }

    [Fact]
    public void ExtraLocalWriteDoesNotNarrow()
    {
        var fixture = CreateProgram();
        var extra = DataShare(fixture.Program.Instructions[^1].Pc + 4, "DsWriteB32", false,
            [Gen5Operand.Vector(2), Gen5Operand.Vector(3)], []);
        var plan = Extract(Program([.. fixture.Program.Instructions, extra]), userDataCount: 2);
        Assert.Null(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
    }

    [Fact]
    public void BranchPastInitializationDoesNotNarrow()
    {
        var fixture = CreateProgram();
        var first = fixture.Program.Instructions[0];
        var branch = Branch(first.Pc, "SCbranchScc0", checked((short)((fixture.Labels["consumer"] - first.Pc - 4) / 4)));
        var plan = Extract(Program([branch, .. fixture.Program.Instructions.Skip(1)]), userDataCount: 2);
        Assert.Null(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
    }

    [Fact]
    public void SavedExecutionCannotAliasLoopCounter()
    {
        var fixture = CreateProgram();
        Gen5Operand Rename(Gen5Operand operand) => operand == Gen5Operand.Scalar(4) ? Gen5Operand.Scalar(2) : operand;
        var program = Program(fixture.Program.Instructions.Select(instruction => instruction with
        {
            Sources = instruction.Sources.Select(Rename).ToArray(),
            Destinations = instruction.Destinations.Select(Rename).ToArray(),
        }).ToArray());
        var plan = Extract(program, userDataCount: 2);
        Assert.Null(WaveMaskSelectorBounds.TryCreate(plan, program.Instructions.Single(instruction => instruction.Pc == fixture.Selector.Pc)));
    }

    [Fact]
    public void UnbalancedExecutionRestoreDeclinesWithoutThrowing()
    {
        var fixture = CreateProgram();
        var program = Program(fixture.Program.Instructions.Select(instruction => instruction.Pc == fixture.Labels["consumer_work"]
            ? Sop1(instruction.Pc, "SMovB64", 126, Gen5Operand.Scalar(0)) : instruction).ToArray());
        var plan = Extract(program, userDataCount: 2);
        Assert.Null(WaveMaskSelectorBounds.TryCreate(plan, fixture.Selector));
    }

    private static RuntimeValueEvaluator Evaluator(ShaderResourcePlan plan, uint first, uint count)
    {
        bool Read(ulong address, out uint value)
        {
            value = address == 0x3000 ? first : count;
            return address is 0x3000 or 0x3004;
        }
        return new(plan, Inputs([0x3000, 0], readMemory: Read, readCleanMemory: Read));
    }

    private sealed record Fixture(Gen5ShaderProgram Program, Gen5ShaderInstruction Selector, Dictionary<string, uint> Labels);

    private static Fixture CreateProgram(bool includeImage = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var labels = new Dictionary<string, uint>();
        var branches = new List<(int Index, string Target)>();
        uint address = 0;
        void Label(string label) => labels.Add(label, address);
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = address });
            address += (uint)instruction.Words.Count * 4;
        }
        void Jump(string opcode, string target)
        {
            branches.Add((instructions.Count, target));
            Add(Branch(0, opcode, 0));
        }
        Gen5Operand Scalar(uint register) => Gen5Operand.Scalar(register);
        Gen5Operand Vector(uint register) => Gen5Operand.Vector(register);
        Add(ScalarLoad(0, 0, 52, 2));
        Label("lane_index"); Add(Vop3(0, "VLshlAddU32", 23, Vector(1), Operand(3), Vector(0)));
        Add(MoveScalar(0, 4, 0));
        Add(MoveScalar(0, 5, 0));
        Label("initial_summary"); Add(MoveScalar(0, 55, 0));
        Label("producer"); Add(Vop2(0, "VAddI32", 0, Scalar(5), Vector(23)));
        Label("count_guard"); Add(Sopc(0, "SCmpLtU32", Scalar(5), Scalar(53)));
        Add(Vopc(0, "VCmpGtU32", Scalar(53), 0));
        Jump("SCbranchScc0", "consumer");
        Add(Sop1(0, "SAndSaveexecB64", 2, Scalar(106)));
        Jump("SCbranchExecz", "invalid_lanes");
        Add(Vop2(0, "VAddI32", 0, Scalar(52), Vector(0)));
        Add(MoveVector(0, 1, 1));
        Add(Vopc(0, "VCmpGtF32", Operand(0), 7));
        Add(Sop1(0, "SAndSaveexecB64", 12, Scalar(106)));
        Jump("SCbranchExecz", "restore_inner");
        Add(Vop2(0, "VMulF32", 1, Vector(8), Vector(9)));
        Label("restore_inner"); Add(Sop1(0, "SMovB64", 126, Scalar(12)));
        Label("invalid_lanes"); Add(Sop2(0, "SAndn2B64", 126, Scalar(2), Scalar(126)));
        Add(Vopc(0, "VCmpGtU32", Scalar(53), 0));
        Label("invalid_predicate"); Add(Vop2(0, "VCndmaskB32", 1, Operand(0), Operand(1)));
        Add(Sop1(0, "SMovB64", 126, Scalar(2)));
        Add(Sop2(0, "SAddI32", 14, Scalar(4), Operand(1)));
        Add(Vopc(0, "VCmpEqU32", Operand(0), 23));
        Label("mask_predicate"); Add(Vopc(0, "VCmpNeU32", Operand(0), 1) with
        {
            Destinations = [Scalar(2)],
            Control = new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 2),
        });
        Add(Sop1(0, "SAndSaveexecB64", 12, Scalar(106)));
        Jump("SCbranchExecz", "summary_update");
        Add(Vop2(0, "VLshlrevB32", 0, Operand(2), Scalar(4)));
        Add(Vop1(0, "VMovB32", 1, Scalar(2)));
        Add(Vop1(0, "VMovB32", 2, Scalar(3)));
        Label("lds_store"); Add(DataShare(0, "DsWrite2B32", false, [Vector(0), Vector(1), Vector(2)], [], 0, 1));
        Label("summary_update"); Add(Sop1(0, "SMovB32", 106, Scalar(55)));
        Add(Sopc(0, "SCmpLgU32", Scalar(2), Operand(0)));
        Add(Sop1(0, "SBitset1B32", 106, Scalar(4)));
        Add(Sop1(0, "SMovB64", 126, Scalar(12)));
        Add(Sop2(0, "SCselectB32", 107, Scalar(106), Scalar(55)));
        Add(Sopc(0, "SCmpLgU32", Scalar(3), Operand(0)));
        Add(Sop1(0, "SMovB32", 106, Scalar(107)));
        Add(Sop1(0, "SBitset1B32", 106, Scalar(14)));
        Add(Sop2(0, "SCselectB32", 55, Scalar(106), Scalar(107)));
        Label("word_increment"); Add(Sop2(0, "SAddI32", 4, Scalar(4), Operand(2)));
        Label("record_increment"); Add(Sop2(0, "SAddI32", 5, Scalar(5), Operand(64)));
        Jump("SBranch", "producer");
        Label("consumer"); Add(Sopc(0, "SCmpLgU32", Operand(0), Scalar(55)));
        Jump("SCbranchScc0", "end");
        Add(Vopc(0, "VCmpGtF32", Operand(0), 5));
        Add(Sop2(0, "SAndB64", 106, Scalar(106), Scalar(126)));
        Jump("SCbranchScc0", "end");
        Add(Sop1(0, "SFF1I32B32", 40, Scalar(55)));
        Add(Vop2(0, "VLshlrevB32", 1, Operand(2), Scalar(40)));
        Add(Sop2(0, "SLshlB32", 41, Scalar(40), Operand(5)));
        Add(DataShare(0, "DsReadB32", false, [Vector(1)], [1]));
        Label("word_guard"); Add(Vopc(0, "VCmpNeU32", Operand(0), 1));
        Jump("SCbranchVccz", "outer_tail");
        Add(Vopc(0, "VCmpGtF32", Operand(0), 5));
        Add(Vop1(0, "VFfblB32", 13, Vector(1)));
        Add(Sop1(0, "SAndSaveexecB64", 38, Scalar(106)));
        Jump("SCbranchExecz", "inner_tail");
        Add(Vop3(0, "VAdd3U32", 27, Scalar(52), Scalar(41), Vector(13)));
        Label("consumer_work"); Add(Nop(0));
        var selectorIndex = instructions.Count;
        if (includeImage)
        {
            foreach (var instruction in ResourceTrackerTests.IndirectImageProgram(false).Instructions.Skip(1).SkipLast(1))
            {
                var relocated = instruction;
                if (instruction.Opcode == "VReadfirstlaneB32") relocated = instruction with { Sources = [Vector(27)] };
                if (instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal))
                    relocated = instruction with { Sources = [Scalar(instruction.Sources[0].Value == 0 ? 60u : 80u), instruction.Sources[1]] };
                Add(relocated);
            }
        }
        else Add(ReadFirstLane(0, 106, 27));
        Label("inner_tail"); Add(Sop1(0, "SMovB64", 126, Scalar(38)));
        Add(Vop2(0, "VLshlrevB32", 6, Vector(13), Operand(1)));
        Label("clear_word"); Add(Vop2(0, "VXorB32", 1, Vector(6), Vector(1)));
        Jump("SBranch", "word_guard");
        Label("outer_tail"); Add(Sop2(0, "SLshlB32", 106, Operand(1), Scalar(40)));
        Label("clear_summary"); Add(Sop2(0, "SXorB32", 55, Scalar(106), Scalar(55)));
        Jump("SBranch", "consumer");
        Label("end"); Add(EndProgram(0));
        foreach (var branch in branches)
        {
            var instruction = instructions[branch.Index];
            var offset = checked((short)(((long)labels[branch.Target] - instruction.Pc - 4) / 4));
            instructions[branch.Index] = Branch(instruction.Pc, instruction.Opcode, offset);
        }
        return new(Program(instructions.ToArray()), instructions[selectorIndex], labels);
    }
}
