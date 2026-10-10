// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ConstantFillDetectorTests
{
    private static Gen5ShaderProgram ImmediateProgram() => new(0,
    [
        new(0, default, "VLshlAddU32", [], [Gen5Operand.Scalar(8),
            new(Gen5OperandKind.EncodedConstant, 134), Gen5Operand.Vector(0)], [Gen5Operand.Vector(2)], null),
        new(8, default, "VMovB32", [], [new(Gen5OperandKind.LiteralConstant, 0x40404040)], [Gen5Operand.Vector(1)], null),
        new(12, default, "BufferStoreFormatX", [], [Gen5Operand.Vector(2), Gen5Operand.Scalar(0),
            new(Gen5OperandKind.EncodedConstant, 128)], [Gen5Operand.Vector(1)],
            new Gen5BufferMemoryControl(1, 2, 1, 0, 0, true, false, false, false)),
        new(20, default, "SEndpgm", [], [], [], null),
    ]);

    [Fact]
    public void AnImmediateStoreAtGroupTimes64PlusThreadIsRecognized()
    {
        Assert.Equal(new ImmediateConstantFill(8, 0), ConstantFillDetector.DetectImmediate(ImmediateProgram()));
    }

    [Fact]
    public void AConstantStoreAtAnotherAddressIsRejected()
    {
        var program = ImmediateProgram();
        var instructions = program.Instructions.ToArray();
        instructions[2] = instructions[2] with
        {
            Control = ((Gen5BufferMemoryControl)instructions[2].Control!) with { VectorAddress = 0 },
        };
        Assert.Null(ConstantFillDetector.DetectImmediate(program with { Instructions = instructions }));
    }
}
