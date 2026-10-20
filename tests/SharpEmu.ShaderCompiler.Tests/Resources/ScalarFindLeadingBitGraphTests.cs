// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// S_FLBIT_I32_B32 counts the zero bits above the most significant set bit and returns -1 for zero.
public sealed class ScalarFindLeadingBitGraphTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x10u)]
    [InlineData(0x8000u)]
    [InlineData(0x0001_0000u)]
    [InlineData(0x7FFF_FFFFu)]
    [InlineData(0x8000_0000u)]
    [InlineData(0xFFFF_FFFFu)]
    public void RuntimeSource_CountsLeadingZeros(uint value)
    {
        var plan = Extract(Program(
            Sop1(0, "SFlbitI32B32", 8, Gen5Operand.Scalar(2)),
            MoveScalar(4, 9, 0),
            MoveScalar(8, 10, 16),
            MoveScalar(12, 11, 0),
            BufferLoad(16, 8),
            EndProgram(24)));

        var registers = new uint[16];
        registers[2] = value;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(registers), out var result));

        Assert.Equal(value == 0 ? uint.MaxValue : (uint)BitOperations.LeadingZeroCount(value), result.Dwords[0]);
    }

    [Theory]
    [InlineData(0u, uint.MaxValue)]
    [InlineData(0x40u, 25u)]
    [InlineData(0x8000_0000u, 0u)]
    public void ConstantSource_IsFolded(uint value, uint expected)
    {
        var plan = Extract(Program(
            MoveScalar(0, 2, value),
            Sop1(4, "SFlbitI32B32", 8, Gen5Operand.Scalar(2)),
            MoveScalar(8, 9, 0),
            MoveScalar(12, 10, 16),
            MoveScalar(16, 11, 0),
            BufferLoad(20, 8),
            EndProgram(28)));

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(new uint[16]), out var result));

        Assert.Equal(expected, result.Dwords[0]);
    }
}
