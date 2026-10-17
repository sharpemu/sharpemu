// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MultiplyHighTests
{
    [Theory]
    [InlineData("VMulHiU32", false)]
    [InlineData("VMulHiU32U24", false)]
    [InlineData("VMulHiI32", true)]
    [InlineData("VMadU64U32", false)]
    public void VectorProducts_UseExtendedMultiplies(string opcode, bool signed)
    {
        var program = Program(
            Vop3(0, opcode, 2, Gen5Operand.Vector(4), Gen5Operand.Scalar(8), Gen5Operand.Scalar(10)),
            EndProgram(8));
        AssertExtendedMultiply(program, signed);
    }

    [Theory]
    [InlineData("SMulHiU32", false)]
    [InlineData("SMulHiI32", true)]
    public void ScalarProducts_UseExtendedMultiplies(string opcode, bool signed)
    {
        var program = Program(
            Sop2(0, opcode, 12, Gen5Operand.Scalar(8), Gen5Operand.Scalar(9)),
            EndProgram(4));
        AssertExtendedMultiply(program, signed);
    }

    private static void AssertExtendedMultiply(Gen5ShaderProgram program, bool signed)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var inspector = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((ushort)(signed ? SpirvOp.SMulExtended : SpirvOp.UMulExtended), inspector.Opcodes);
        Assert.Empty(WideMultiplies(shader.Spirv));
    }

    private static List<uint> WideMultiplies(byte[] spirv)
    {
        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        var wideTypes = new HashSet<uint>();
        var multiplies = new List<uint>();
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            var opcode = (ushort)(words[index] & 0xFFFF);
            if (opcode == (ushort)SpirvOp.TypeInt && words[index + 2] == 64)
            {
                wideTypes.Add(words[index + 1]);
            }
            else if (opcode == (ushort)SpirvOp.IMul && wideTypes.Contains(words[index + 1]))
            {
                multiplies.Add(words[index + 2]);
            }
        }

        return multiplies;
    }
}
