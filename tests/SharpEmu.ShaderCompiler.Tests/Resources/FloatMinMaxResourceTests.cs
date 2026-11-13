// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class FloatMinMaxResourceTests
{
    public static TheoryData<uint, uint, uint, uint> Values => new()
    {
        { 0x7FC00000, 0x40000000, 0x40000000, 0x40000000 },
        { 0x40000000, 0x7FC00000, 0x40000000, 0x40000000 },
        { 0xFFC00000, 0x40800000, 0x40800000, 0x40800000 },
        { 0x40800000, 0xFFC00000, 0x40800000, 0x40800000 },
        { 0x7FC00001, 0xFFC00002, 0xFFC00002, 0xFFC00002 },
        { 0x7F800000, 0x3F800000, 0x3F800000, 0x7F800000 },
        { 0xFF800000, 0x3F800000, 0xFF800000, 0x3F800000 },
        { 0xBF800000, 0x40000000, 0xBF800000, 0x40000000 },
        { 0, 0x80000000, 0x80000000, 0 },
        { 0x80000000, 0, 0x80000000, 0 },
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var values in Values)
        foreach (var maximum in new[] { false, true })
        for (var mode = 0; mode < 3; mode++)
            yield return [values[0], values[1], values[maximum ? 3 : 2], maximum, mode];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ResourceValuesMatchTheShaderMinMaxNanRules(uint left, uint right, uint expected, bool maximum, int mode)
    {
        // Exercise constant folding, interpreted runtime values, and compiled runtime values.
        var constants = mode == 0;
        var program = Program(
            constants ? MoveVector(0, 1, right) : MoveVectorFromScalar(0, 1, 9),
            Vop2(8, maximum ? "VMaxF32" : "VMinF32", 2,
                constants ? Operand(left) : Gen5Operand.Scalar(8), Gen5Operand.Vector(1)),
            ReadFirstLane(16, 4, 2),
            MoveScalar(20, 5, 0), MoveScalar(24, 6, 16), MoveScalar(28, 7, 0),
            BufferLoad(32, 4), EndProgram(40));
        var plan = Extract(program);
        if (mode == 2) Assert.NotNull(plan.CompileEvaluatorNow());
        var registers = new uint[16];
        registers[8] = left;
        registers[9] = right;
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, Inputs(registers), useCompiled: mode != 1);

        Assert.True(evaluator.Evaluate(plan.DescriptorSources[0].Dwords[0], out var result));
        Assert.Equal(expected, result);
    }
}
