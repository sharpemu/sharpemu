// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ExecutionClassCompareTests
{
    [Fact]
    public void Opcode98DecodesAsExecutionMaskClassCompare()
    {
        // Exact Little Nightmares instruction: SDWA selects complete dwords,
        // reads the value from v0 and the IEEE class mask from s106 (VCC_LO).
        var instruction = Decode(0x7D30D4F9, 0x86060000);

        Assert.Equal("VCmpxClassF32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vopc, instruction.Encoding);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Scalar(106) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
        var control = Assert.IsType<Gen5SdwaControl>(instruction.Control);
        Assert.Equal(6u, control.Source0Select);
        Assert.Equal(6u, control.Source1Select);
    }

    [Fact]
    public void BothBackendsEvaluateClassAndUpdateExec()
    {
        var compare = Decode(0x7D30D4F9, 0x86060000);
        var program = Program(compare with { Pc = 0 }, EndProgram(2 * sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError),
            spirvError);
        var opcodes = new SpirvModuleInspector(spirv.Spirv).Opcodes;
        Assert.DoesNotContain((ushort)SpirvOp.IsNan, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.IsInf, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.FOrdLessThan, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.FOrdGreaterThan, opcodes);
        Assert.Contains((ushort)SpirvOp.BitwiseAnd, opcodes);
        Assert.Contains((ushort)SpirvOp.IEqual, opcodes);
        Assert.Contains((ushort)SpirvOp.INotEqual, opcodes);
        var constants = ReadConstants32(spirv.Spirv);
        Assert.Contains(0x001u, constants); // signaling NaN class mask
        Assert.Contains(0x002u, constants); // quiet NaN class mask
        Assert.Contains(0x0040_0000u, constants); // quiet NaN payload bit
        Assert.Contains(0x007F_FFFFu, constants); // mantissa mask
        Assert.Contains(0x7F80_0000u, constants); // exponent mask

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains("isnan(", metal.Source, StringComparison.Ordinal);
        Assert.Contains("isinf(", metal.Source, StringComparison.Ordinal);
        Assert.Contains("exec =", metal.Source, StringComparison.Ordinal);
    }

    private static HashSet<uint> ReadConstants32(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var constants = new HashSet<uint>();
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = checked((int)(words[offset] >> 16));
            if ((SpirvOp)(words[offset] & 0xFFFF) == SpirvOp.Constant &&
                wordCount == 4)
            {
                constants.Add(words[offset + 3]);
            }

            offset += Math.Max(wordCount, 1);
        }

        return constants;
    }

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(words.Length * sizeof(uint)),
            0xBF810000);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error),
            error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
