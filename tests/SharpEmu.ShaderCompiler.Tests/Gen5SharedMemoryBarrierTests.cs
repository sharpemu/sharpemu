// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SharedMemoryBarrierTests
{
    [Fact]
    public void PairedWave64_UsesOneSubgroupWithoutBridgeBarriers()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Vopc(
                0,
                "VCmpxLtU32",
                ResourceTestProgram.Operand(1),
                0),
            ResourceTestProgram.Branch(4, "SCbranchExecz", 1),
            ResourceTestProgram.Nop(8),
            ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 4,
            LocalSizeY = 4,
            LocalSizeZ = 4,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var ballots = 0;
        var barriers = 0;
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var opcode = instruction & 0xFFFF;
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset + index * 4));
            if (opcode == (uint)SpirvOp.GroupNonUniformBallot) ballots++;
            if (opcode == (uint)SpirvOp.ControlBarrier) barriers++;
            if (opcode == (uint)SpirvOp.ExecutionMode &&
                Operand(2) == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [Operand(3), Operand(4), Operand(5)];
            }

            offset += checked((int)(instruction >> 16) * 4);
        }

        Assert.Equal(4, ballots);
        Assert.Equal(0, barriers);
        Assert.NotNull(localSize);
        Assert.Equal([32u, 1u, 1u], localSize);
    }

    [Theory]
    [InlineData("DsSwizzleB32")]
    [InlineData("DsBpermuteB32")]
    public void PairedWave64_BankLocalDataShareOpsUseIndependentGuestHalves(
        string opcode)
    {
        var dataShare = opcode == "DsSwizzleB32"
            ? ResourceTestProgram.DataShare(
                0,
                opcode,
                gds: false,
                [Gen5Operand.Vector(0)],
                [2],
                offset0: 0x1B,
                offset1: 0x80)
            : ResourceTestProgram.DataShare(
                0,
                opcode,
                gds: false,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [2]);
        var program = ResourceTestProgram.Program(
            dataShare,
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 64,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var shuffles = 0;
        var barriers = 0;
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var opcodeValue = instruction & 0xFFFF;
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset + index * 4));
            if (opcodeValue == (uint)SpirvOp.GroupNonUniformShuffle) shuffles++;
            if (opcodeValue == (uint)SpirvOp.ControlBarrier) barriers++;
            if (opcodeValue == (uint)SpirvOp.ExecutionMode &&
                Operand(2) == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [Operand(3), Operand(4), Operand(5)];
            }

            offset += checked((int)(instruction >> 16) * 4);
        }

        Assert.Equal(2, shuffles);
        Assert.Equal(0, barriers);
        Assert.NotNull(localSize);
        Assert.Equal([32u, 1u, 1u], localSize);
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(39u)]
    public void PairedWave64_ReadlaneSelectsGuestBankWithinOneSubgroup(uint lane)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(
                0,
                destination: 4,
                value: lane),
            new Gen5ShaderInstruction(
                4,
                Gen5ShaderEncoding.Vop3,
                "VReadlaneB32",
                [0u, 0u],
                [
                    Gen5Operand.Vector(3),
                    Gen5Operand.Scalar(4),
                    Gen5Operand.Scalar(0),
                ],
                [Gen5Operand.Scalar(2)],
                null),
            ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 8,
            LocalSizeY = 8,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var constants = new Dictionary<uint, uint>();
        var definitions = new Dictionary<uint, (SpirvOp Opcode, uint[] Operands)>();
        var broadcasts = new List<uint[]>();
        var barriers = 0;
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            var opcode = (SpirvOp)(instruction & 0xFFFF);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    shader.Spirv.AsSpan(offset + (index + 1) * 4));
            }

            if (opcode == SpirvOp.Constant)
                constants[operands[1]] = operands[2];
            if (opcode is SpirvOp.BitwiseAnd or SpirvOp.ULessThan or SpirvOp.Select)
                definitions[operands[1]] = (opcode, operands);
            if (opcode == SpirvOp.GroupNonUniformBroadcast)
                broadcasts.Add(operands);
            if (opcode == SpirvOp.ControlBarrier)
                barriers++;
            if (opcode == SpirvOp.ExecutionMode &&
                operands[1] == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [operands[2], operands[3], operands[4]];
            }

            offset += wordCount * 4;
        }

        bool IsConstant(uint id, uint value) =>
            constants.TryGetValue(id, out var actual) && actual == value;

        var broadcast = Assert.Single(broadcasts);
        var hostIndex = definitions[broadcast[4]];
        Assert.Equal(SpirvOp.BitwiseAnd, hostIndex.Opcode);
        Assert.True(
            IsConstant(hostIndex.Operands[2], 31) ||
            IsConstant(hostIndex.Operands[3], 31));

        var bankSelect = definitions[broadcast[3]];
        Assert.Equal(SpirvOp.Select, bankSelect.Opcode);
        Assert.NotEqual(bankSelect.Operands[3], bankSelect.Operands[4]);
        var bankCondition = definitions[bankSelect.Operands[2]];
        Assert.Equal(SpirvOp.ULessThan, bankCondition.Opcode);
        Assert.True(
            IsConstant(bankCondition.Operands[2], 32) ||
            IsConstant(bankCondition.Operands[3], 32));

        Assert.Equal(0, barriers);
        Assert.NotNull(localSize);
        Assert.Equal([32u, 1u, 1u], localSize);
    }

    [Fact]
    public void PairedWave64_ReadfirstlanePrefersLowGuestBankAcrossMultipleWaves()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.ReadFirstLane(
                0,
                scalarDestination: 2,
                vectorSource: 3),
            ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 16,
            LocalSizeY = 8,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var constants = new Dictionary<uint, uint>();
        var definitions = new Dictionary<uint, (SpirvOp Opcode, uint[] Operands)>();
        var ballots = new List<uint[]>();
        var broadcasts = new List<uint[]>();
        var barriers = 0;
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            var opcode = (SpirvOp)(instruction & 0xFFFF);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    shader.Spirv.AsSpan(offset + (index + 1) * 4));
            }

            if (opcode == SpirvOp.Constant)
                constants[operands[1]] = operands[2];
            if (operands.Length >= 2 && opcode is
                SpirvOp.Select or
                SpirvOp.INotEqual or
                SpirvOp.CompositeExtract or
                SpirvOp.ExtInst)
                definitions[operands[1]] = (opcode, operands);
            if (opcode == SpirvOp.GroupNonUniformBallot)
                ballots.Add(operands);
            if (opcode == SpirvOp.GroupNonUniformBroadcast)
                broadcasts.Add(operands);
            if (opcode == SpirvOp.ControlBarrier)
                barriers++;
            if (opcode == SpirvOp.ExecutionMode &&
                operands[1] == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [operands[2], operands[3], operands[4]];
            }

            offset += wordCount * 4;
        }

        // Two ballots initialize the paired EXEC mask and two more implement
        // READFIRSTLANE's low/high guest-bank selection.
        Assert.Equal(4, ballots.Count);
        Assert.All(ballots, ballot =>
            Assert.Equal(3u, constants[ballot[2]]));
        var ballotResults = ballots
            .Select(static ballot => ballot[1])
            .ToHashSet();
        var broadcast = Assert.Single(broadcasts);
        Assert.Equal(3u, constants[broadcast[2]]);
        var bankSelect = definitions[broadcast[3]];
        Assert.Equal(SpirvOp.Select, bankSelect.Opcode);
        Assert.NotEqual(bankSelect.Operands[3], bankSelect.Operands[4]);
        var useLowBank = definitions[bankSelect.Operands[2]];
        Assert.Equal(SpirvOp.INotEqual, useLowBank.Opcode);
        Assert.Contains(
            useLowBank.Operands[2..],
            operand =>
                constants.TryGetValue(operand, out var value) && value == 0);
        var lowMask = Assert.Single(
            useLowBank.Operands[2..]
                .Where(operand =>
                    definitions.TryGetValue(operand, out var definition) &&
                    definition.Opcode == SpirvOp.CompositeExtract &&
                    ballotResults.Contains(definition.Operands[2]))
                .Select(operand => definitions[operand]));

        var maskSelect = Assert.Single(
            definitions.Values,
            definition =>
                definition.Opcode == SpirvOp.Select &&
                definition.Operands[2] == bankSelect.Operands[2] &&
                definition.Operands[3] == lowMask.Operands[1] &&
                definitions.TryGetValue(
                    definition.Operands[4],
                    out var highDefinition) &&
                highDefinition.Opcode == SpirvOp.CompositeExtract &&
                ballotResults.Contains(highDefinition.Operands[2]));
        var highMask = definitions[maskSelect.Operands[4]];
        Assert.NotEqual(
            ballots.Single(ballot => ballot[1] == lowMask.Operands[2])[3],
            ballots.Single(ballot => ballot[1] == highMask.Operands[2])[3]);
        var firstActive = Assert.Single(
            definitions.Values,
            definition =>
                definition.Opcode == SpirvOp.ExtInst &&
                definition.Operands[3] == 73 &&
                definition.Operands[4] == maskSelect.Operands[1]);
        var safeLaneSelect = definitions[broadcast[4]];
        Assert.Equal(SpirvOp.Select, safeLaneSelect.Opcode);
        Assert.Equal(firstActive.Operands[1], safeLaneSelect.Operands[3]);
        Assert.Equal(0, barriers);
        Assert.NotNull(localSize);
        Assert.Equal([64u, 1u, 1u], localSize);
    }

    [Fact]
    public void PairedWave64_OrdersLdsIndependentlyAcrossTwoGuestWaves()
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Ds, "DsWriteB32", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], new Gen5DataShareControl(0, 0, false)),
            ResourceTestProgram.Branch(8, "SCbranchExecz", 1),
            ResourceTestProgram.Nop(12),
            new(16, Gen5ShaderEncoding.Ds, "DsReadB32", [0u, 0u],
                [Gen5Operand.Vector(0)], [Gen5Operand.Vector(2)], new Gen5DataShareControl(0, 0, false)),
            new(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null),
        };
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            new Gen5ShaderProgram(0, instructions),
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 16,
            LocalSizeY = 8,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var barriers = 0;
        var subgroupBarriers = 0;
        var constants = new Dictionary<uint, uint>();
        var sharedPointerTypes = new HashSet<uint>();
        var sharedPointers = new HashSet<uint>();
        var sharedReads = 0;
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset + index * 4));
            var opcode = instruction & 0xFFFF;
            if (opcode == (uint)SpirvOp.Constant)
                constants[Operand(2)] = Operand(3);
            if (opcode == (uint)SpirvOp.TypePointer && Operand(2) == 4)
                sharedPointerTypes.Add(Operand(1));
            if (opcode == (uint)SpirvOp.AccessChain && sharedPointerTypes.Contains(Operand(1)))
                sharedPointers.Add(Operand(2));
            if (opcode == (uint)SpirvOp.Load && sharedPointers.Contains(Operand(3)))
                sharedReads++;
            if (opcode == (uint)SpirvOp.ExecutionMode &&
                Operand(2) == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [Operand(3), Operand(4), Operand(5)];
            }
            if (opcode == (uint)SpirvOp.ControlBarrier)
            {
                barriers++;
                if (constants.GetValueOrDefault(Operand(1)) == 3 &&
                    constants.GetValueOrDefault(Operand(2)) == 3 &&
                    constants.GetValueOrDefault(Operand(3)) == 0x108)
                    subgroupBarriers++;
            }
            offset += checked((int)(instruction >> 16) * 4);
        }

        Assert.Equal(2, barriers);
        Assert.Equal(barriers, subgroupBarriers);
        Assert.Equal(2, sharedReads);
        Assert.NotNull(localSize);
        Assert.Equal([64u, 1u, 1u], localSize);
    }

    [Theory]
    [InlineData(32u, 64u)]
    [InlineData(64u, 128u)]
    public void MultiWave64_BarrierPhasesHandleConsecutiveSitesAndEarlyExit(
        uint hostSubgroupSize,
        uint expectedHostLocalSize)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Branch(0, "SCbranchScc0", 3),
            new Gen5ShaderInstruction(
                4,
                Gen5ShaderEncoding.Sopp,
                "SBarrier",
                [0u],
                [],
                [],
                null),
            new Gen5ShaderInstruction(
                8,
                Gen5ShaderEncoding.Sopp,
                "SBarrier",
                [0u],
                [],
                [],
                null),
            ResourceTestProgram.Branch(12, "SBranch", 2),
            new Gen5ShaderInstruction(
                16,
                Gen5ShaderEncoding.Sopp,
                "SBarrier",
                [0u],
                [],
                [],
                null),
            ResourceTestProgram.EndProgram(20),
            ResourceTestProgram.EndProgram(24));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = hostSubgroupSize,
            LocalSizeX = 128,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var instructions = new List<(SpirvOp Opcode, uint[] Operands)>();
        var constants = new Dictionary<uint, uint>();
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            var opcode = (SpirvOp)(header & 0xFFFF);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    shader.Spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            }

            instructions.Add((opcode, operands));
            if (opcode == SpirvOp.Constant)
                constants[operands[1]] = operands[2];
            if (opcode == SpirvOp.ExecutionMode &&
                operands[1] == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [operands[2], operands[3], operands[4]];
            }

            offset += wordCount * sizeof(uint);
        }

        var barriers = instructions
            .Where(static instruction =>
                instruction.Opcode == SpirvOp.ControlBarrier)
            .ToArray();
        Assert.Equal(2, barriers.Length);
        Assert.All(barriers, barrier =>
        {
            Assert.Equal(2u, constants[barrier.Operands[0]]);
            Assert.Equal(2u, constants[barrier.Operands[1]]);
            Assert.Equal(0x108u, constants[barrier.Operands[2]]);
        });
        Assert.Equal(
            2,
            instructions.Count(static instruction =>
                instruction.Opcode == SpirvOp.LoopMerge));

        var switchIndex = instructions.FindIndex(static instruction =>
            instruction.Opcode == SpirvOp.Switch);
        Assert.True(switchIndex > 0);
        // Every barrier ends its own block, including the adjacent pair at
        // PCs 4 and 8. The alternate site at PC 16 and both exits remain
        // separate cases: selector/default plus seven literal/label pairs.
        Assert.Equal(16, instructions[switchIndex].Operands.Length);
        Assert.Equal(SpirvOp.SelectionMerge, instructions[switchIndex - 1].Opcode);
        var switchMerge = instructions[switchIndex - 1].Operands[0];
        var switchMergeIndex = instructions.FindIndex(
            switchIndex + 1,
            instruction =>
                instruction.Opcode == SpirvOp.Label &&
                instruction.Operands[0] == switchMerge);
        Assert.True(switchMergeIndex > switchIndex);
        Assert.DoesNotContain(
            instructions
                .Skip(switchIndex + 1)
                .Take(switchMergeIndex - switchIndex - 1),
            static instruction =>
                instruction.Opcode == SpirvOp.ControlBarrier);

        Assert.NotNull(localSize);
        Assert.Equal([expectedHostLocalSize, 1u, 1u], localSize);
    }

    [Theory]
    [InlineData(uint.MaxValue, 1)]
    [InlineData(48u, 2)]
    public void Wave64Masks_AvoidRedundantInitializationAndBranchBallots(
        uint threadCount, int expectedDynamicBallots)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Vopc(
                0,
                "VCmpxLtU32",
                ResourceTestProgram.Operand(1),
                0),
            ResourceTestProgram.Branch(4, "SCbranchExecz", 1),
            ResourceTestProgram.Nop(8),
            ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            LocalSizeX = 64,
            ThreadCountX = threadCount,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var ballots = 0;
        var barriers = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var opcode = instruction & 0xFFFF;
            if (opcode == (uint)SpirvOp.GroupNonUniformBallot) ballots++;
            if (opcode == (uint)SpirvOp.ControlBarrier) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }

        // VCC and EXEC each need one ballot to initialize both emulated
        // Wave64 halves. Only the later mask-producing operations need the
        // workgroup exchange barrier counted by expectedDynamicBallots.
        Assert.Equal(expectedDynamicBallots + 2, ballots);
        Assert.Equal(expectedDynamicBallots, barriers);
    }

    [Theory]
    [InlineData(63u)]
    [InlineData(128u)]
    public void BlockedSubgroup32Wave64_RejectsUnsupportedComputeShapes(uint localSize)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.DataShare(
                0,
                "DsPermuteB32",
                false,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [2]),
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = localSize,
        };

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("requires exactly one 64-lane guest wave", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedSubgroup32Wave64_AllowsSingleLaneGdsAppend()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.DataShare(
                0,
                "DsAppend",
                gds: true,
                [Gen5Operand.Scalar(124)],
                [0]),
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalSizeX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var opcodes = new List<SpirvOp>();
        uint[]? localSize = null;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            var opcode = (SpirvOp)(instruction & 0xFFFF);
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(
                shader.Spirv.AsSpan(offset + index * sizeof(uint)));
            opcodes.Add(opcode);
            if (opcode == SpirvOp.ExecutionMode &&
                Operand(2) == (uint)SpirvExecutionMode.LocalSize)
            {
                localSize = [Operand(3), Operand(4), Operand(5)];
            }

            offset += wordCount * sizeof(uint);
        }

        Assert.Contains(SpirvOp.AtomicIAdd, opcodes);
        Assert.DoesNotContain(SpirvOp.ControlBarrier, opcodes);
        Assert.NotNull(localSize);
        Assert.Equal([1u, 1u, 1u], localSize);
    }

    [Theory]
    [InlineData(32u, 0u)]
    [InlineData(64u, 0u)]
    [InlineData(32u, 8192u)]
    [InlineData(64u, 8192u)]
    public void EmulatedWave64_DoesNotAliasAnUnknownOrFullGuestLdsAllocation(
        uint hostSubgroupSize,
        uint localDataShareDwords)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.DataShare(
                0,
                "DsPermuteB32",
                false,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [2]),
            ResourceTestProgram.DataShareWrite(8, gds: false),
            ResourceTestProgram.EndProgram(16));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            userDataCount: 0,
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            HostSubgroupSize = hostSubgroupSize,
            LocalSizeX = 64,
            LocalDataShareDwords = localDataShareDwords,
        };

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("private exchange dwords", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64u, 64u, false, false, 2)]
    [InlineData(64u, 64u, true, false, 2)]
    [InlineData(32u, 64u, false, false, 0)]
    [InlineData(64u, 128u, false, false, 0)]
    [InlineData(64u, 64u, false, true, 2)]
    [InlineData(32u, 64u, false, true, 0)]
    public void SharedMemoryPhases_SynchronizeOnlySingleWaveGroups(
        uint waveSize,
        uint threadCount,
        bool explicitBarrier,
        bool splitBlocks,
        int expectedBarriers)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Ds, "DsWriteB32", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], new Gen5DataShareControl(0, 0, false)),
        };
        if (explicitBarrier)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        if (splitBlocks)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBranch", [0u], [], [], null));
        instructions.Add(new(12, Gen5ShaderEncoding.Ds, "DsReadB32", [0u, 0u],
            [Gen5Operand.Vector(0)], [Gen5Operand.Vector(2)], new Gen5DataShareControl(0, 0, false)));
        instructions.Add(new(20, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            new Gen5ShaderProgram(0, instructions),
            userDataCount: 0,
            waveSize: waveSize);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            LocalSizeX = threadCount,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        var sharedPointerTypes = new HashSet<uint>();
        var sharedPointers = new HashSet<uint>();
        var sharedReads = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset + index * 4));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.TypePointer && Operand(2) == 4)
                sharedPointerTypes.Add(Operand(1));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.AccessChain && sharedPointerTypes.Contains(Operand(1)))
                sharedPointers.Add(Operand(2));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.Load && sharedPointers.Contains(Operand(3)))
                sharedReads++;
            if ((instruction & 0xFFFF) == (uint)SpirvOp.ControlBarrier) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }
        Assert.Equal(expectedBarriers, barriers);
        Assert.Equal(1, sharedReads);
    }
}
