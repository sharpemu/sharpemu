// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarArithmeticShift64Tests
{
    [Fact]
    public void DecodeGfx10Encoding()
    {
        var instruction = Decode(0x91880604);
        Assert.Equal("SAshrI64", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sop2, instruction.Encoding);
        Assert.Equal(Gen5Operand.Scalar(4), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(6), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(8), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void CompilesOnBothBackends()
    {
        var program = Program(
            Sop2(0, "SAshrI64", 8, Gen5Operand.Scalar(4), Gen5Operand.Scalar(6)),
            EndProgram(4));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError), spirvError);
        var inspector = new SpirvModuleInspector(spirv.Spirv);
        Assert.Contains((ushort)SpirvOp.ShiftRightArithmetic, inspector.Opcodes);

        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        Assert.Contains("as_type<long>", metal.Source);
        Assert.Contains("& 63u", metal.Source);
    }

    private static Gen5ShaderInstruction Decode(uint word)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xBF810000);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
