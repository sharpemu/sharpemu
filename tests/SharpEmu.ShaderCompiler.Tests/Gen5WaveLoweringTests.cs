// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// Checks the SPIR-V each wave lowering (NativeWave32, NativeWave64, EmulatedWave64) produces for the
// same guest programs: which subgroup instructions appear, how a ballot is split into a 64-bit mask,
// and whether the workgroup exchange / rendezvous machinery exists.
public sealed class Gen5WaveLoweringTests
{
    public enum Lowering { Wave32, NativeWave64, EmulatedWave64 }

    private sealed record Module(
        IReadOnlyList<(SpirvOp Op, uint[] Operands)> Instructions,
        IReadOnlyList<string> Names)
    {
        public int Count(SpirvOp op) => Instructions.Count(instruction => instruction.Op == op);

        // CompositeExtract of a ballot result: its literal index is the last operand.
        public IReadOnlyList<uint> BallotExtractIndices()
        {
            var ballots = Instructions.Where(i => i.Op == SpirvOp.GroupNonUniformBallot).Select(i => i.Operands[1]).ToHashSet();
            return Instructions
                .Where(i => i.Op == SpirvOp.CompositeExtract && ballots.Contains(i.Operands[2]))
                .Select(i => i.Operands[^1])
                .ToList();
        }
    }

    private static Module Parse(byte[] spirv)
    {
        var instructions = new List<(SpirvOp, uint[])>();
        var names = new List<string>();
        for (var offset = 20; offset < spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var length = (int)(word >> 16);
            var op = (SpirvOp)(word & 0xFFFF);
            var operands = new uint[length - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + 4 + index * 4));
            }

            instructions.Add((op, operands));
            if (op == SpirvOp.Name)
            {
                var text = new List<byte>();
                for (var index = 1; index < operands.Length; index++)
                {
                    text.AddRange(BitConverter.GetBytes(operands[index]));
                }

                names.Add(Encoding.UTF8.GetString(text.ToArray()).TrimEnd('\0'));
            }

            offset += length * 4;
        }

        return new Module(instructions, names);
    }

    private static Module Compile(Gen5ShaderProgram program, Lowering lowering, uint threads = 64)
    {
        var (plan, resources, layout) = Prepare(program);
        var configured = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = lowering == Lowering.Wave32 ? 32u : 64u,
            HostWave64Supported = lowering == Lowering.NativeWave64,
            LocalSizeX = threads,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(configured, out var shader, out var error), error);
        return Parse(shader.Spirv);
    }

    // v_cmp_lt_u32 vcc, v0, v1 : a per-lane compare whose result becomes a wave mask.
    private static Gen5ShaderProgram CompareProgram() => Program(
        Vopc(0, "VCmpLtU32", Operand(5), 1),
        EndProgram(4));

    private static Gen5ShaderProgram ReadFirstLaneProgram() => Program(
        ReadFirstLane(0, 4, 0),
        EndProgram(4));

    private static Gen5ShaderProgram ReadLaneProgram(uint lane) => Program(
        ReadLane(0, 4, 0, lane),
        EndProgram(4));

    private static Gen5ShaderProgram DataShareProgram(bool barrierBetween) =>
        Program(
            new[]
            {
                DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], 0, 0),
            }
            .Concat(barrierBetween
                ? new[] { new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null) }
                : [])
            .Concat(new[]
            {
                DataShare(8, "DsReadB32", false, [Gen5Operand.Vector(0)], [2u], 0, 0),
                EndProgram(16),
            })
            .ToArray());

    [Theory]
    [InlineData(Lowering.Wave32)]
    [InlineData(Lowering.NativeWave64)]
    [InlineData(Lowering.EmulatedWave64)]
    public void EveryLoweringCompilesTheBasicWavePrograms(Lowering lowering)
    {
        foreach (var program in new[] { CompareProgram(), ReadFirstLaneProgram(), ReadLaneProgram(0), ReadLaneProgram(33), DataShareProgram(false), DataShareProgram(true) })
        {
            Compile(program, lowering);
        }
    }

    [Fact]
    public void Wave32_BallotUsesOnlyTheLowWord()
    {
        var module = Compile(CompareProgram(), Lowering.Wave32);
        Assert.True(module.Count(SpirvOp.GroupNonUniformBallot) > 0);
        Assert.All(module.BallotExtractIndices(), index => Assert.Equal(0u, index));
    }

    [Fact]
    public void NativeWave64_BallotCombinesBothWords()
    {
        var module = Compile(CompareProgram(), Lowering.NativeWave64);
        var indices = module.BallotExtractIndices();
        Assert.Contains(0u, indices);
        Assert.Contains(1u, indices);
    }

    [Fact]
    public void OnlyTheEmulatedLoweringDeclaresTheExchange()
    {
        foreach (var lowering in new[] { Lowering.Wave32, Lowering.NativeWave64 })
        {
            var module = Compile(ReadFirstLaneProgram(), lowering);
            Assert.DoesNotContain("wave64Exchange", module.Names);
            Assert.DoesNotContain("wave64ExchangeParity", module.Names);
        }

        var emulated = Compile(ReadFirstLaneProgram(), Lowering.EmulatedWave64);
        Assert.Contains("wave64ExchangeParity", emulated.Names);
        Assert.Contains("wave64Exchange", emulated.Names);
    }

    [Fact]
    public void EmulatedWave64_NeedsNoExchangeWithoutSubgroupOperations()
    {
        var module = Compile(Program(Vop2(0, "VAddI32", 1, Operand(1), Gen5Operand.Vector(0)), EndProgram(4)), Lowering.EmulatedWave64);
        Assert.DoesNotContain("wave64ExchangeParity", module.Names);
    }

    [Theory]
    [InlineData(Lowering.Wave32, 64u)]
    [InlineData(Lowering.NativeWave64, 64u)]
    [InlineData(Lowering.NativeWave64, 256u)]
    public void NativeLoweringsAddNoBarriersAroundSharedMemory(Lowering lowering, uint threads)
    {
        var module = Compile(DataShareProgram(false), lowering, threads);
        Assert.Equal(0, module.Count(SpirvOp.ControlBarrier));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void EmulatedWave64_OrdersSharedMemoryPhasesWithinOneWave(bool explicitBarrier, int expectedBarriers)
    {
        var module = Compile(DataShareProgram(explicitBarrier), Lowering.EmulatedWave64, threads: 64);
        Assert.Equal(expectedBarriers, module.Count(SpirvOp.ControlBarrier));
    }

    [Fact]
    public void EmulatedWave64_MultiWaveGroupsUseNoWorkgroupBarrierForSharedMemoryPhases()
    {
        var module = Compile(DataShareProgram(false), Lowering.EmulatedWave64, threads: 128);
        Assert.Equal(0, module.Count(SpirvOp.ControlBarrier));
    }

    [Fact]
    public void EmulatedWave64_MultiWaveExchangeMeetsAtAnArrivalCounter()
    {
        var single = Compile(ReadFirstLaneProgram(), Lowering.EmulatedWave64, threads: 64);
        var multi = Compile(ReadFirstLaneProgram(), Lowering.EmulatedWave64, threads: 256);
        Assert.DoesNotContain("wave64RendezvousEpoch", single.Names);
        Assert.Contains("wave64RendezvousEpoch", multi.Names);
        Assert.True(multi.Count(SpirvOp.AtomicIAdd) > single.Count(SpirvOp.AtomicIAdd));
    }

    [Theory]
    [InlineData(Lowering.Wave32)]
    [InlineData(Lowering.NativeWave64)]
    public void NativeLoweringsNeverDeclareARendezvous(Lowering lowering)
    {
        var module = Compile(ReadFirstLaneProgram(), lowering, threads: 256);
        Assert.DoesNotContain("wave64RendezvousEpoch", module.Names);
        Assert.Equal(0, module.Count(SpirvOp.AtomicIAdd));
    }

    [Fact]
    public void HostWave64SupportDoesNotChangeAWave32Shader()
    {
        var program = ReadFirstLaneProgram();
        var (plan, resources, layout) = Prepare(program);
        byte[] Compile32(bool hostWave64)
        {
            var configured = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = 32,
                HostWave64Supported = hostWave64,
                LocalSizeX = 64,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(configured, out var shader, out var error), error);
            return shader.Spirv.ToArray();
        }

        Assert.Equal(Compile32(false), Compile32(true));
    }

    [Theory]
    [InlineData(Lowering.Wave32)]
    [InlineData(Lowering.NativeWave64)]
    [InlineData(Lowering.EmulatedWave64)]
    public void ReadLaneBroadcastsFromTheRequestedLane(Lowering lowering)
    {
        var module = Compile(ReadLaneProgram(33), lowering);
        Assert.True(
            module.Count(SpirvOp.GroupNonUniformBroadcast) > 0 || module.Count(SpirvOp.GroupNonUniformShuffle) > 0 ||
            module.Count(SpirvOp.Store) > 0);
    }

    [Theory]
    [InlineData(Lowering.Wave32)]
    [InlineData(Lowering.NativeWave64)]
    [InlineData(Lowering.EmulatedWave64)]
    public void LoweringsAreDeterministic(Lowering lowering)
    {
        var first = Compile(CompareProgram(), lowering);
        var second = Compile(CompareProgram(), lowering);
        Assert.Equal(first.Instructions.Count, second.Instructions.Count);
    }
}
