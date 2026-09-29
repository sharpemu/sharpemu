// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarImmediateCompareTests
{
    [Theory]
    [InlineData("Eq", "==", false)]
    [InlineData("Lg", "!=", false)]
    [InlineData("Gt", ">", false)]
    [InlineData("Ge", ">=", false)]
    [InlineData("Lt", "<", false)]
    [InlineData("Le", "<=", false)]
    [InlineData("Eq", "==", true)]
    [InlineData("Lg", "!=", true)]
    [InlineData("Gt", ">", true)]
    [InlineData("Ge", ">=", true)]
    [InlineData("Lt", "<", true)]
    [InlineData("Le", "<=", true)]
    public void ImmediateExtensionMatchesComparisonType(string comparison, string operation, bool signed)
    {
        foreach (var immediate in new uint[] { 0x7FFF, 0x8000, 0xFFFF })
        {
            var suffix = comparison + (signed ? "I32" : "U32");
            var expected = signed ? unchecked((uint)(short)immediate) : immediate;
            var immediateProgram = Program(new Gen5ShaderInstruction(
                0, Gen5ShaderEncoding.Sopk, "SCmpk" + suffix, [immediate],
                [new(Gen5OperandKind.LiteralConstant, immediate)], [Gen5Operand.Scalar(4)], null),
                EndProgram(4));
            var request = CreateRequest(immediateProgram);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var actualShader, out var error), error);
            var comparisonOpcode = comparison switch
            {
                "Eq" => SpirvOp.IEqual,
                "Lg" => SpirvOp.INotEqual,
                "Gt" => signed ? SpirvOp.SGreaterThan : SpirvOp.UGreaterThan,
                "Ge" => signed ? SpirvOp.SGreaterThanEqual : SpirvOp.UGreaterThanEqual,
                "Lt" => signed ? SpirvOp.SLessThan : SpirvOp.ULessThan,
                _ => signed ? SpirvOp.SLessThanEqual : SpirvOp.ULessThanEqual,
            };
            Assert.Equal(expected, ReadComparisonImmediate(actualShader.Spirv, comparisonOpcode));

            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metalShader, out error), error);
            var constant = $"0x{expected:X}u";
            var condition = signed
                ? $"scc = as_type<int>(s[4]) {operation} as_type<int>({constant});"
                : $"scc = (s[4]) {operation} ({constant});";
            Assert.Contains(condition, metalShader.Source);
        }
    }

    private static uint ReadComparisonImmediate(byte[] spirv, SpirvOp comparisonOpcode)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var constants = new Dictionary<uint, uint>();
        var comparisons = new Dictionary<uint, uint>();
        uint? comparisonOperand = null;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (opcode == SpirvOp.Constant && (words[offset] >> 16) == 4)
                constants[words[offset + 2]] = words[offset + 3];
            else if (opcode == SpirvOp.Bitcast && constants.TryGetValue(words[offset + 3], out var value))
                constants[words[offset + 2]] = value;
            else if (opcode == comparisonOpcode)
                comparisons[words[offset + 2]] = words[offset + 4];
            else if (opcode == SpirvOp.Select && comparisons.TryGetValue(words[offset + 3], out var selectedOperand) &&
                     constants.TryGetValue(words[offset + 4], out var trueValue) && trueValue == 1 &&
                     constants.TryGetValue(words[offset + 5], out var falseValue) && falseValue == 0)
                comparisons[words[offset + 2]] = selectedOperand;
            else if (opcode == SpirvOp.Store && comparisons.TryGetValue(words[offset + 2], out var operand))
                comparisonOperand = operand;
        }
        Assert.NotNull(comparisonOperand);
        return constants[comparisonOperand.Value];
    }

    private static ShaderCompileRequest CreateRequest(Gen5ShaderProgram program)
    {
        var (plan, resources, layout) = Prepare(program);
        return new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1 };
    }
}
