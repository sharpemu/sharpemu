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

public sealed class Gen5FindFirstBitHighTests
{
    private const uint Vop1Encoding = 0x7E000000;

    [Fact]
    public void DecodeOpcode39AsUnsignedFindFirstBitHigh()
    {
        var word = Vop1Encoding | (5u << 17) | (0x39u << 9) | (256u + 3u);
        var instruction = Decode(word);

        Assert.Equal("VFfbhU32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop1, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Sources));
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void DecodeExtendedOpcode1B9AsUnsignedFindFirstBitHigh()
    {
        var instruction = Decode(0xD1B90005, 256u + 3u);

        Assert.Equal("VFfbhU32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(3), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(instruction.Destinations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothBackendsPreserveLeadingZeroAndZeroInputSemantics(bool extendedEncoding)
    {
        var operation = extendedEncoding
            ? Decode(0xD1B90005, 256u + 3u)
            : Decode(Vop1Encoding | (5u << 17) | (0x39u << 9) | (256u + 3u));
        var program = Program(
            operation with { Pc = 0 },
            EndProgram((uint)(operation.Words.Count * sizeof(uint))));
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
            (ushort)SpirvOp.BitReverse,
            new SpirvModuleInspector(spirv.Spirv).Opcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains("clz(", metal.Source, StringComparison.Ordinal);
        Assert.Contains("== 0u", metal.Source, StringComparison.Ordinal);
        Assert.Contains("0xFFFFFFFFu", metal.Source, StringComparison.Ordinal);
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
