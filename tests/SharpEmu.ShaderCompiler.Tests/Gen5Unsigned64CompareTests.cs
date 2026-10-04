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

public sealed class Gen5Unsigned64CompareTests
{
    [Fact]
    public void OpcodeE5DecodesAsNonExecutionMaskUnsigned64Inequality()
    {
        var instruction = Decode(0x7DCA0700);

        Assert.Equal("VCmpNeU64", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vopc, instruction.Encoding);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(3) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Theory]
    [InlineData(0x7DCA0700u, "vcc =")]
    [InlineData(0x7DEA0700u, "exec =")]
    public void BothBackendsCompareTheCompleteRegisterPairs(uint word, string maskAssignment)
    {
        var compare = Decode(word);
        var program = Program(compare with { Pc = 0 }, EndProgram(sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError),
            spirvError);
        Assert.Contains(
            (ushort)SpirvOp.INotEqual,
            new SpirvModuleInspector(spirv.Spirv).Opcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains(
            "((ulong)v[0] | ((ulong)v[1] << 32))",
            metal.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "((ulong)v[3] | ((ulong)v[4] << 32))",
            metal.Source,
            StringComparison.Ordinal);
        Assert.Contains(" != ", metal.Source, StringComparison.Ordinal);
        Assert.Contains(maskAssignment, metal.Source, StringComparison.Ordinal);
    }

    private static Gen5ShaderInstruction Decode(uint word)
    {
        var bytes = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), 0xBF810000);
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
