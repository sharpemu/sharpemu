// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DirectImageTableTests
{
    public static Gen5ShaderProgram CreateProgram(uint mask = 1, bool split = true, bool bitScan = true)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vop2(0, "VAddU32", 12, Operand(mask), Gen5Operand.Vector(0)),
            ReadFirstLane(4, 18, 12),
            Sop1(8, bitScan ? "SFF1I32B32" : "SMovB32", 19, Gen5Operand.Scalar(18)),
            Sop2(12, "SLshlB32", 106, Gen5Operand.Scalar(19), Operand(5)),
            new(16, Gen5ShaderEncoding.Sopk, "SAddkI32", [0xB7EA0158], [new(Gen5OperandKind.LiteralConstant, 344)], [Gen5Operand.Scalar(106)], null),
            Sop2(20, "SAddI32", 107, Gen5Operand.Scalar(106), Operand(16)),
            ScalarLoad(24, 0, 4, split ? 4u : 8u, dynamicOffsetRegister: 106),
        };
        if (split) instructions.Add(ScalarLoad(32, 0, 8, 4, dynamicOffsetRegister: 107));
        instructions.AddRange([
            MoveScalar(40, 106, 999),
            MoveVector(44, 1, 0), MoveVector(48, 2, 0),
            Image(52, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
            MoveScalar(60, 20, 0), MoveScalar(64, 21, 0), MoveScalar(68, 22, 64), MoveScalar(72, 23, 0),
            BufferAccess(76, "BufferStoreDword", 20, vectorData: 4), EndProgram(84),
        ]);
        return Program([.. instructions]);
    }

    public static bool ReadDescriptor(ulong address, out uint word)
    {
        word = 0;
        if (address < 0x1000 + 312 || address >= 0x1000 + 344 + 32 * 32) return false;
        var offset = address - 0x1000 - 312;
        var record = offset / 32;
        var component = offset % 32 / 4;
        word = component switch
        {
            0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
            1 => 20u << 20,
            3 => 0xFACu | (9u << 28),
            _ => 0,
        };
        return true;
    }

    public static Gen5ShaderProgram CreateGuardedProgram(uint mask = 1, bool split = true)
    {
        var program = CreateProgram(mask, split);
        return program with
        {
            Instructions = program.Instructions.Where(instruction => instruction.Pc < 8)
                .Concat([
                    Sopc(8, "SCmpLgU32", Operand(0), Gen5Operand.Scalar(18)),
                    Branch(12, "SCbranchScc0", 19),
                ])
                .Concat(program.Instructions.Where(instruction => instruction.Pc >= 8)
                    .Select(instruction => instruction with { Pc = instruction.Pc + 8 })).ToArray(),
        };
    }

    public static (ShaderResourcePlan Plan, ResourceSnapshot Snapshot, ShaderCompileRequest Request) PrepareDirect(uint mask = 1, bool split = true, bool guarded = false)
    {
        var program = guarded ? CreateGuardedProgram(mask, split) : CreateProgram(mask, split);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: ReadDescriptor), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 2),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        return (plan, snapshot, new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectTableRetainsAllBitScanResultsAndCompiles(bool split)
    {
        var (plan, snapshot, request) = PrepareDirect(split: split);
        Assert.Single(plan.IndirectImages);
        Assert.True(plan.IndirectImages[0].KeyIsAddressOffset);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.DynamicReads);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.Equal(33, selector.DirectCandidates!.Count);
        Assert.Contains(selector.DirectCandidates, candidate => candidate.Offset == 312);
        Assert.Contains(selector.DirectCandidates, candidate => candidate.Offset == 344 + 31 * 32);
        Assert.Equal(2, snapshot.Images.Length);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void DirectImageCapacityPreservesTheLimitAndPublishedState(int distinctCount)
    {
        var (plan, snapshot, _) = PrepareDirect();
        var specialization = new ResourceSpecialization();
        var previousSnapshot = snapshot;
        var previousSpecialization = specialization;
        bool Read(ulong address, out uint word)
        {
            var success = ReadDescriptor(address, out word);
            if (success && (address - 0x1000 - 312) % 32 == 0)
                word = 0x2000 + (uint)(((address - 0x1000 - 312) / 32) % (ulong)distinctCount);
            return success;
        }

        var success = ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read),
            ref snapshot, ref specialization, out var failure);
        Assert.Equal(distinctCount <= ShaderResourceInfo.MaxImages, success);
        if (success)
        {
            Assert.Equal(ResourceMaterializationFailure.None, failure);
            Assert.Equal(distinctCount, snapshot.Images.Length);
        }
        else
        {
            Assert.Equal(ResourceMaterializationFailure.ImageCapacityExceeded, failure);
            Assert.Same(previousSnapshot, snapshot);
            Assert.Same(previousSpecialization, specialization);
        }
    }

    [Fact]
    public void UnboundedDirectTableIsStillRejected()
    {
        Assert.Throws<ResourcePlanException>(() => ShaderResourcePlan.Extract(CreateProgram(bitScan: false), ShaderStage.Compute, Hash, 0, 2));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonZeroGuardExcludesTheUnreachableDescriptor(bool split, bool equality)
    {
        var program = CreateGuardedProgram(split: split);
        if (equality)
            program = program with
            {
                Instructions = program.Instructions.Select(instruction => instruction.Pc switch
                {
                    8 => Sopc(8, "SCmpEqI32", Gen5Operand.Scalar(18), Operand(0)),
                    12 => Branch(12, "SCbranchScc1", 19),
                    _ => instruction,
                }).ToArray(),
            };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var candidates = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.DirectCandidates!;
        Assert.Equal(32, candidates.Count);
        Assert.DoesNotContain(candidates, candidate => candidate.Offset == 312);
        bool Read(ulong address, out uint word)
        {
            Assert.True(address >= 0x1000 + 344);
            return ReadDescriptor(address, out word);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        var (_, _, request) = PrepareDirect(split: split, guarded: true);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopMustRecheckTheBitScanInput(bool bypassGuard)
    {
        var program = CreateGuardedProgram();
        program = program with
        {
            Instructions = program.Instructions.Where(instruction => instruction.Pc < 92)
                .Select(instruction => instruction.Pc == 12 ? Branch(12, "SCbranchScc0", 21) : instruction)
                .Concat([
                    ReadFirstLane(92, 18, 13),
                    Branch(96, "SBranch", bypassGuard ? (short)-21 : (short)-23),
                    EndProgram(100),
                ]).ToArray(),
        };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var candidates = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.DirectCandidates!;
        Assert.Equal(bypassGuard ? 33 : 32, candidates.Count);
        Assert.Equal(bypassGuard, candidates.Any(candidate => candidate.Offset == 312));
    }

    [Theory]
    [InlineData("opposite-branch")]
    [InlineData("different-register")]
    [InlineData("guard-bypass")]
    [InlineData("condition-write")]
    [InlineData("input-write")]
    public void UnprovenGuardRetainsTheZeroInputDescriptor(string variation)
    {
        var program = CreateGuardedProgram();
        var instructions = program.Instructions.ToList();
        switch (variation)
        {
            case "opposite-branch":
                instructions[instructions.FindIndex(instruction => instruction.Pc == 12)] = Branch(12, "SCbranchScc1", 19);
                break;
            case "different-register":
                instructions[instructions.FindIndex(instruction => instruction.Pc == 8)] =
                    Sopc(8, "SCmpLgU32", Operand(0), Gen5Operand.Scalar(17));
                break;
            case "guard-bypass":
                instructions[0] = Branch(0, "SCbranchScc1", 3);
                break;
            case "condition-write":
                instructions = instructions.Select(instruction => instruction.Pc >= 12
                    ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
                instructions.Add(Sopc(12, "SCmpEqU32", Operand(0), Operand(0)));
                break;
            case "input-write":
                instructions = instructions.Select(instruction => instruction.Pc >= 16
                    ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
                instructions[instructions.FindIndex(instruction => instruction.Pc == 12)] = Branch(12, "SCbranchScc0", 20);
                instructions.Add(ReadFirstLane(16, 18, 13));
                break;
        }
        program = program with { Instructions = instructions.OrderBy(instruction => instruction.Pc).ToArray() };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var candidates = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.DirectCandidates!;
        Assert.Equal(33, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Offset == 312);
    }

    [Fact]
    public void DescriptorWordsUsedByArithmeticAreNotRemoved()
    {
        var program = CreateProgram();
        program = program with
        {
            Instructions = program.Instructions.Select(instruction => instruction.Pc == 40
                ? Vop1(40, "VMovB32", 15, Gen5Operand.Scalar(4)) : instruction).ToArray(),
        };
        Assert.Throws<ResourcePlanException>(() => ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedReadOrIncompatibleCandidateDoesNotPublish(bool incompatible, bool guarded)
    {
        var (plan, original, _) = PrepareDirect(guarded: guarded);
        var snapshot = original;
        var specialization = new ResourceSpecialization();
        var originalSpecialization = specialization;
        bool Read(ulong address, out uint word)
        {
            var success = ReadDescriptor(address, out word);
            if (address == 0x1000 + 344 + 12)
            {
                if (!incompatible) return false;
                word = (word & 0x0FFFFFFF) | (11u << 28);
            }
            return success;
        }
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization,
            out var failure));
        Assert.Equal(incompatible ? ResourceMaterializationFailure.IncompatibleImageCandidates : ResourceMaterializationFailure.Other, failure);
        Assert.Same(original, snapshot);
        Assert.Same(originalSpecialization, specialization);
    }
}
