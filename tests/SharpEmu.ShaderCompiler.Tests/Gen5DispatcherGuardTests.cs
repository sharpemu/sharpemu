// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DispatcherGuardTests
{
    [Fact]
    public void ForwardOnlyPathLongerThanGuardLimitIsNotCharged()
    {
        var instructions = Enumerable.Range(0, 40)
            .Select(index => Branch((uint)(index * sizeof(uint)), "SBranch", 0))
            .Append(EndProgram(40u * sizeof(uint)))
            .ToArray();

        var (spirv, metal) = CompileGraphics(Program(instructions));

        AssertForwardOnlyVulkanPath(spirv);
        Assert.Single(StoresToNamedVariable(
            spirv,
            "dispatcherBackedgeGuardLimit"));
        Assert.Single(StoresToNamedVariable(spirv, "dispatcherGuardHit"));
        Assert.DoesNotContain("++backedges", metal);
    }

    [Fact]
    public void ForwardDiamondUsesAcyclicVulkanPath()
    {
        var program = Program(
            Branch(0, "SCbranchScc1", 1),
            Branch(4, "SBranch", 1),
            Nop(8),
            EndProgram(12));

        var (spirv, _) = CompileGraphics(program);

        AssertForwardOnlyVulkanPath(spirv);
    }

    [Fact]
    public void FixedBarrierDiamondsUseAcyclicVulkanPhases()
    {
        var program = Program(
            Branch(0, "SCbranchScc1", 1),
            Nop(4),
            Barrier(8),
            Branch(12, "SCbranchScc0", 1),
            Nop(16),
            Barrier(20),
            EndProgram(24));

        var spirv = CompileMultiWaveCompute(program);

        AssertForwardOnlyVulkanPath(spirv);
        AssertWorkgroupBarriers(spirv, expectedCount: 2);
    }

    [Fact]
    public void BranchSkippingBarrierUsesDispatcher()
    {
        var program = Program(
            Branch(0, "SCbranchScc1", 2),
            Nop(4),
            Barrier(8),
            Nop(12),
            EndProgram(16));

        var spirv = CompileMultiWaveCompute(program);

        AssertDispatcherVulkanPath(spirv);
    }

    [Fact]
    public void BarrierBackedgeUsesDispatcher()
    {
        var program = Program(
            Nop(0),
            Barrier(4),
            Nop(8),
            Branch(12, "SBranch", -3),
            EndProgram(16));

        var spirv = CompileMultiWaveCompute(program);

        AssertDispatcherVulkanPath(spirv);
    }

    [Fact]
    public void IndirectControlUsesDispatcher()
    {
        var program = Program(
            // The hand-built Sopp shape lets this dispatcher-selection test
            // reach module emission; real decoded S_SETPC_B64 is Sop1 and is
            // rejected later because indirect targets are not translated.
            new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Sopp,
                "SSetpcB64",
                [0u],
                [],
                [],
                null),
            Barrier(4),
            EndProgram(8));

        var spirv = CompileMultiWaveCompute(program);

        AssertDispatcherVulkanPath(spirv);
    }

    [Fact]
    public void ConditionalBackwardEdgeChargesOnlyTheTakenPath()
    {
        var program = Program(
            Nop(0),
            Branch(4, "SBranch", 0),
            Nop(8),
            Branch(12, "SCbranchScc1", -4),
            EndProgram(16));

        var (spirv, metal) = CompileGraphics(program);
        var (guard, instructions, stores) = GuardStores(
            spirv,
            "dispatcherBackedgeGuardLimit");

        AssertStructuredLoopVulkanPath(spirv);
        Assert.Equal(2, stores.Count);
        Assert.Equal(2, StoresToNamedVariable(spirv, "dispatcherGuardHit").Count);
        AssertIncrementSelect(
            instructions,
            guard,
            stores[^1].Operands[1],
            SpirvOp.INotEqual);
        Assert.Equal(1, CountOccurrences(metal, "++backedges"));
        Assert.Contains("if (scc)", metal);
    }

    [Fact]
    public void UnconditionalSelfLoopIsCharged()
    {
        var program = Program(
            Branch(0, "SBranch", -1),
            EndProgram(4));

        var (spirv, metal) = CompileGraphics(program);
        var (guard, instructions, stores) = GuardStores(
            spirv,
            "dispatcherBackedgeGuardLimit");

        AssertStructuredLoopVulkanPath(spirv);
        Assert.Equal(2, stores.Count);
        Assert.Equal(2, StoresToNamedVariable(spirv, "dispatcherGuardHit").Count);
        AssertFunctionVariable(spirv, guard);
        AssertFunctionVariable(
            spirv,
            Assert.Single(
                new SpirvModuleInspector(spirv).Names,
                pair => pair.Value == "dispatcherGuardHit").Key);
        AssertNoZeroMemoryPointers(spirv);
        AssertIncrementSelect(
            instructions,
            guard,
            stores[^1].Operands[1],
            SpirvOp.ConstantTrue);
        Assert.Equal(
            "dispatcherBackedgeGuardLimit32",
            new SpirvModuleInspector(spirv).Names[guard]);
        Assert.Equal(1, CountOccurrences(metal, "++backedges"));
        Assert.Contains(
            "sharpemu_dispatch_guard total_steps=0 backedges=32",
            metal,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (++backedges >= 32u)",
            metal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("if (scc)", metal);
    }

    [Fact]
    public void FinalConditionalBackedgeCannotReactivateMetalDispatcher()
    {
        var program = Program(
            Nop(0),
            Branch(4, "SCbranchScc1", -2));

        var (_, metal) = CompileGraphics(program);

        Assert.Contains("if (scc)", metal, StringComparison.Ordinal);
        Assert.Contains("active = false;", metal, StringComparison.Ordinal);
        Assert.Contains("active = active && (scc);", metal, StringComparison.Ordinal);
    }

    private static (byte[] Spirv, string Metal) CompileGraphics(
        Gen5ShaderProgram program)
    {
        var request = ResourceTestProgram.Request(
            program,
            ShaderStage.Pixel,
            userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var spirv,
                out var spirvError),
            spirvError);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var metal,
                out var metalError),
            metalError);
        return (spirv.Spirv, metal.Source);
    }

    private static byte[] CompileMultiWaveCompute(Gen5ShaderProgram program)
    {
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 128,
        };
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var spirv,
                out var error),
            error);
        return spirv.Spirv;
    }

    private static Gen5ShaderInstruction Barrier(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            "SBarrier",
            [0u],
            [],
            [],
            null);

    private static void AssertForwardOnlyVulkanPath(byte[] spirv)
    {
        var instructions = ReadInstructions(spirv);
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.LoopMerge);
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.Switch);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.SelectionMerge);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.BranchConditional);
    }

    private static void AssertDispatcherVulkanPath(byte[] spirv)
    {
        var instructions = ReadInstructions(spirv);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.LoopMerge);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.Switch);
    }

    private static void AssertStructuredLoopVulkanPath(byte[] spirv)
    {
        var instructions = ReadInstructions(spirv);
        Assert.Contains(
            instructions,
            instruction => instruction.Opcode == SpirvOp.LoopMerge);
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.Switch);
    }

    private static void AssertFunctionVariable(byte[] spirv, uint variable)
    {
        var declaration = Assert.Single(
            ReadInstructions(spirv),
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[1] == variable);
        Assert.Equal(
            (uint)SpirvStorageClass.Function,
            declaration.Operands[2]);
    }

    private static void AssertNoZeroMemoryPointers(byte[] spirv)
    {
        Assert.DoesNotContain(
            ReadInstructions(spirv),
            instruction =>
                (instruction.Opcode == SpirvOp.Load &&
                 instruction.Operands.Length >= 3 &&
                 instruction.Operands[2] == 0) ||
                (instruction.Opcode == SpirvOp.Store &&
                 instruction.Operands.Length >= 1 &&
                 instruction.Operands[0] == 0));
    }

    private static void AssertWorkgroupBarriers(
        byte[] spirv,
        int expectedCount)
    {
        var instructions = ReadInstructions(spirv);
        var constants = instructions
            .Where(static instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length >= 3)
            .ToDictionary(
                static instruction => instruction.Operands[1],
                static instruction => instruction.Operands[2]);
        var barrierIndices = instructions
            .Select(static (instruction, index) => (instruction, index))
            .Where(static pair => pair.instruction.Opcode == SpirvOp.ControlBarrier)
            .ToArray();
        var selectionMerges = instructions
            .Where(static instruction =>
                instruction.Opcode == SpirvOp.SelectionMerge)
            .Select(static instruction => instruction.Operands[0])
            .ToHashSet();

        Assert.Equal(expectedCount, barrierIndices.Length);
        Assert.All(barrierIndices, pair =>
        {
            var precedingLabel = instructions[pair.index - 1];
            Assert.Equal(SpirvOp.Label, precedingLabel.Opcode);
            Assert.Contains(precedingLabel.Operands[0], selectionMerges);
            Assert.Equal(2u, constants[pair.instruction.Operands[0]]);
            Assert.Equal(2u, constants[pair.instruction.Operands[1]]);
            Assert.Equal(0x108u, constants[pair.instruction.Operands[2]]);
        });
    }

    private static void AssertIncrementSelect(
        IReadOnlyList<SpirvInstruction> instructions,
        uint guard,
        uint storedValue,
        SpirvOp expectedConditionOpcode)
    {
        var stored = FindResult(instructions, storedValue);
        SpirvInstruction increment;
        if (expectedConditionOpcode == SpirvOp.ConstantTrue)
        {
            // The module builder folds select(true, increment, current).
            Assert.Equal(SpirvOp.IAdd, stored.Opcode);
            increment = stored;
        }
        else
        {
            Assert.Equal(SpirvOp.Select, stored.Opcode);
            var condition = FindResult(instructions, stored.Operands[2]);
            Assert.Equal(expectedConditionOpcode, condition.Opcode);
            if (condition.Opcode == SpirvOp.INotEqual)
            {
                Assert.Equal(
                    SpirvOp.Load,
                    FindResult(instructions, condition.Operands[2]).Opcode);
            }

            increment = FindResult(instructions, stored.Operands[3]);
        }

        Assert.Equal(SpirvOp.IAdd, increment.Opcode);
        var current = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == guard &&
                increment.Operands[2..].Contains(instruction.Operands[1]));
        if (stored.Opcode == SpirvOp.Select)
        {
            Assert.Equal(current.Operands[1], stored.Operands[4]);
        }

        Assert.Contains(current.Operands[1], increment.Operands[2..]);
    }

    private static IReadOnlyList<SpirvInstruction> StoresToNamedVariable(
        byte[] spirv,
        string namePrefix) => GuardStores(spirv, namePrefix).Stores;

    private static (
        uint Guard,
        IReadOnlyList<SpirvInstruction> Instructions,
        IReadOnlyList<SpirvInstruction> Stores) GuardStores(
            byte[] spirv,
            string namePrefix)
    {
        var inspector = new SpirvModuleInspector(spirv);
        var guard = Assert.Single(
            inspector.Names,
            pair => pair.Value.StartsWith(namePrefix, StringComparison.Ordinal)).Key;
        var instructions = ReadInstructions(spirv);
        var stores = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[0] == guard)
            .ToArray();
        return (guard, instructions, stores);
    }

    private static SpirvInstruction FindResult(
        IReadOnlyList<SpirvInstruction> instructions,
        uint result) => Assert.Single(
            instructions,
            instruction =>
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] == result &&
                instruction.Opcode is SpirvOp.Load or
                    SpirvOp.IAdd or
                    SpirvOp.Select or
                    SpirvOp.INotEqual or
                    SpirvOp.ConstantTrue or
                    SpirvOp.ConstantFalse or
                    SpirvOp.Constant);

    private static IReadOnlyList<SpirvInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<SpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint))));
            }

            instructions.Add(
                new SpirvInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static int CountOccurrences(string value, string text)
    {
        var count = 0;
        for (var offset = 0;
             (offset = value.IndexOf(text, offset, StringComparison.Ordinal)) >= 0;
             offset += text.Length)
        {
            count++;
        }

        return count;
    }

    private readonly record struct SpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
