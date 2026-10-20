// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// S_BITREPLICATE_B64_B32 doubles every bit of the source into a 64-bit result.
public sealed class ScalarBitReplicateGraphTests
{
    // The reference: bit i of the source lands in bits 2i and 2i+1 of the 64-bit result.
    private static (uint Low, uint High) Reference(uint source)
    {
        var result = 0ul;
        for (var bit = 0; bit < 32; bit++)
        {
            result |= ((ulong)((source >> bit) & 1u)) * (3ul << (2 * bit));
        }

        return ((uint)result, (uint)(result >> 32));
    }

    [Theory]
    [InlineData(0x0000_0000u)]
    [InlineData(0x0000_0001u)]
    [InlineData(0x0000_8000u)]
    [InlineData(0x0001_0000u)]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0xA5A5_5A5Au)]
    [InlineData(0x1234_5678u)]
    public void ConstantSource_IsFoldedBitByBit(uint source) => Check(source, constant: true);

    [Theory]
    [InlineData(0x0000_0001u)]
    [InlineData(0x0001_0000u)]
    [InlineData(0xA5A5_5A5Au)]
    [InlineData(0x1234_5678u)]
    public void RuntimeSource_IsEvaluatedBitByBit(uint source) => Check(source, constant: false);

    private static void Check(uint source, bool constant)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction);
            pc += 4;
        }

        if (constant)
        {
            Add(MoveScalar(pc, 2, source));
        }

        Add(Sop1(pc, "SBitreplicateB64B32", 6, Gen5Operand.Scalar(2)));
        Add(MoveScalarRegister(pc, 8, 6));
        Add(MoveScalarRegister(pc, 9, 7));
        Add(MoveScalar(pc, 10, 16));
        Add(MoveScalar(pc, 11, 0));
        instructions.Add(BufferLoad(pc, 8));
        pc += 8;
        Add(EndProgram(pc));
        var plan = Extract(Program([.. instructions]));

        var registers = new uint[16];
        registers[2] = source;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(registers), out var result));

        var (low, high) = Reference(source);
        Assert.Equal(low, result.Dwords[0]);
        Assert.Equal(high, result.Dwords[1]);
    }
}
