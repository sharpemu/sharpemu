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

public sealed class Gen5Integer16CompareTests
{
    [Fact]
    public void Opcode99DecodesLittleNightmaresSignedSdwaCompare()
    {
        // The first dword is the exact Little Nightmares failure. Use a
        // controlled SDWA extension that selects complete dwords so the test
        // also proves the opcode itself narrows and sign-extends to I16.
        var instruction = Decode(0x7D32AAF9, 0x06060000);

        Assert.Equal("VCmpxLtI16", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vopc, instruction.Encoding);
        Assert.Equal(2, instruction.Words.Count);
        Assert.Equal(
            new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(85) },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
        var control = Assert.IsType<Gen5SdwaControl>(instruction.Control);
        Assert.Equal(6u, control.Source0Select);
        Assert.Equal(6u, control.Source1Select);
    }

    [Theory]
    [InlineData(0x89u, "VCmpLtI16")]
    [InlineData(0x8Au, "VCmpEqI16")]
    [InlineData(0x8Bu, "VCmpLeI16")]
    [InlineData(0x8Cu, "VCmpGtI16")]
    [InlineData(0x8Du, "VCmpNeI16")]
    [InlineData(0x8Eu, "VCmpGeI16")]
    [InlineData(0x99u, "VCmpxLtI16")]
    [InlineData(0x9Au, "VCmpxEqI16")]
    [InlineData(0x9Bu, "VCmpxLeI16")]
    [InlineData(0x9Cu, "VCmpxGtI16")]
    [InlineData(0x9Du, "VCmpxNeI16")]
    [InlineData(0x9Eu, "VCmpxGeI16")]
    [InlineData(0xA9u, "VCmpLtU16")]
    [InlineData(0xAAu, "VCmpEqU16")]
    [InlineData(0xABu, "VCmpLeU16")]
    [InlineData(0xACu, "VCmpGtU16")]
    [InlineData(0xADu, "VCmpNeU16")]
    [InlineData(0xAEu, "VCmpGeU16")]
    [InlineData(0xB9u, "VCmpxLtU16")]
    [InlineData(0xBAu, "VCmpxEqU16")]
    [InlineData(0xBBu, "VCmpxLeU16")]
    [InlineData(0xBCu, "VCmpxGtU16")]
    [InlineData(0xBDu, "VCmpxNeU16")]
    [InlineData(0xBEu, "VCmpxGeU16")]
    public void Rdna2Integer16CompareFamilyDecodes(uint opcode, string expected)
    {
        var word = 0x7C000100u | (opcode << 17) | (1u << 9);
        Assert.Equal(expected, Decode(word).Opcode);
    }

    [Fact]
    public void BothBackendsNarrowSignExtendAndUpdateExec()
    {
        var compare = Decode(0x7D32AAF9, 0x06060000);
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
        Assert.Contains((ushort)SpirvOp.BitFieldSExtract, opcodes);
        Assert.Contains((ushort)SpirvOp.SLessThan, opcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains("extract_bits(as_type<int>", metal.Source, StringComparison.Ordinal);
        Assert.Contains(" < ", metal.Source, StringComparison.Ordinal);
        Assert.Contains("exec =", metal.Source, StringComparison.Ordinal);
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
