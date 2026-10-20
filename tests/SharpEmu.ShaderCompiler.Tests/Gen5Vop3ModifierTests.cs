// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Vop3ModifierTests
{
    private const uint HalfAndThreeQuarters = 0x3F40_0000; // 0.75f
    private const uint Half = 0x3F00_0000; // 0.5f

    // abs/neg/clamp/omod are float modifiers: the f32 opcodes the graph models keep them.
    [Theory]
    [InlineData("VAddF32", 0u, 0u, 0u, false, 0x3FA0_0000u)] // 0.75 + 0.5
    [InlineData("VAddF32", 0u, 0u, 0u, true, 0x3F80_0000u)] // clamped to 1.0
    [InlineData("VAddF32", 0u, 0u, 1u, false, 0x4020_0000u)] // x2
    [InlineData("VAddF32", 0u, 0u, 3u, false, 0x3F20_0000u)] // x0.5
    [InlineData("VAddF32", 0u, 1u, 0u, false, 0xBE80_0000u)] // -0.75 + 0.5
    [InlineData("VAddF32", 1u, 1u, 0u, false, 0xBE80_0000u)] // -|0.75| + 0.5
    [InlineData("VMulF32", 0u, 0u, 0u, true, 0x3EC0_0000u)] // 0.375 stays inside [0, 1]
    [InlineData("VSubF32", 0u, 0u, 0u, true, 0x3E80_0000u)] // 0.25 stays inside [0, 1]
    public void FloatOpcodesApplyTheirModifiers(string opcode, uint absoluteMask, uint negateMask, uint outputModifier, bool clamp, uint expected)
    {
        Assert.True(TryEvaluate(opcode, new Gen5Vop3Control(absoluteMask, negateMask, outputModifier, clamp, 0, null), out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FloatClampSaturatesOutOfRangeResults()
    {
        Assert.True(TryEvaluate("VAddF32", new Gen5Vop3Control(0, 0, 0, true, 0, null), out var high, 0x4000_0000, 0x4000_0000));
        Assert.Equal(0x3F80_0000u, high); // 2 + 2 clamps to 1
        Assert.True(TryEvaluate("VAddF32", new Gen5Vop3Control(0, 0, 0, true, 0, null), out var low, 0xBF80_0000, 0x3F00_0000));
        Assert.Equal(0u, low); // -1 + 0.5 clamps to 0
    }

    [Theory]
    [InlineData("VAddCoU32")]
    [InlineData("VSubCoU32")]
    [InlineData("VAddI32")]
    [InlineData("VMulLoU32")]
    [InlineData("VXorB32")]
    public void IntegerOpcodeWithClampIsUndefinedNotAFloatClamp(string opcode)
    {
        Assert.True(TryEvaluate(opcode, new Gen5Vop3Control(0, 0, 0, false, 0, null), out _));
        Assert.False(TryEvaluate(opcode, new Gen5Vop3Control(0, 0, 0, true, 0, null), out _));
    }

    [Theory]
    [InlineData("VAddCoU32", 1u, 0u, 0u)]
    [InlineData("VAddCoU32", 0u, 1u, 0u)]
    [InlineData("VAddCoU32", 0u, 0u, 2u)]
    [InlineData("VSubCoU32", 0u, 0u, 1u)]
    [InlineData("VAddI32", 0u, 1u, 0u)]
    public void IntegerOpcodeWithFloatOnlyModifiersIsUndefined(string opcode, uint absoluteMask, uint negateMask, uint outputModifier)
    {
        Assert.False(TryEvaluate(opcode, new Gen5Vop3Control(absoluteMask, negateMask, outputModifier, false, 0, null), out _));
    }

    [Fact]
    public void IntegerAddWithoutModifiersIsUnchanged()
    {
        Assert.True(TryEvaluate("VAddCoU32", new Gen5Vop3Control(0, 0, 0, false, 0, null), out var sum, 7, 0xFFFF_FFFF));
        Assert.Equal(6u, sum);
    }

    private static bool TryEvaluate(string opcode, Gen5Vop3Control control, out uint result,
        uint left = HalfAndThreeQuarters, uint right = Half)
    {
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9),
            Vop3(8, opcode, 6, Gen5Operand.Vector(2), Gen5Operand.Vector(3)) with { Control = control },
            ReadFirstLane(16, 4, 6),
            MoveScalar(20, 5, 0), MoveScalar(24, 6, 16), MoveScalar(28, 7, 0),
            BufferLoad(32, 4), EndProgram(40));
        ShaderResourcePlan plan;
        try
        {
            plan = Extract(program);
        }
        catch (ResourcePlanException)
        {
            // The buffer handle is undefined: the graph refused to model the value.
            result = 0;
            return false;
        }

        var registers = new uint[16];
        registers[8] = left;
        registers[9] = right;
        var evaluated = RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var value);
        result = evaluated ? value.Dwords[0] : 0;
        return evaluated;
    }
}
