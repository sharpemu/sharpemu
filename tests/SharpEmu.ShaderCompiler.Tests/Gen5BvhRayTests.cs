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

public sealed class Gen5BvhRayTests
{
    private static readonly uint[] Bvh64Words =
    [
        0xF19C9F07u,
        0x00022001u,
        0x05040302u,
        0x09080706u,
        0xEE0C0B0Au,
    ];

    [Theory]
    [InlineData(0xF1989F07u, "ImageBvhIntersectRay")]
    [InlineData(0xF19C9F07u, "ImageBvh64IntersectRay")]
    public void SplitMimgOpcodeBitDecodesBvhInstructions(
        uint word,
        string expectedOpcode)
    {
        var context = new CpuContext(new EmptyMemory(), Generation.Gen5);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeInstructionForPreflight(
                context,
                0,
                word,
                out var opcode,
                out var sizeDwords,
                out var error),
            error);
        Assert.Equal(expectedOpcode, opcode);
        Assert.Equal(5u, sizeDwords);
    }

    [Fact]
    public void Bvh64NsaOperandsUseTwelveAddressesAndFourResults()
    {
        var instruction = DecodeBvh64Program().Instructions[0];

        Assert.Equal("ImageBvh64IntersectRay", instruction.Opcode);
        Assert.Equal(5, instruction.Words.Count);
        var control = Assert.IsType<Gen5BvhRayControl>(instruction.Control);
        Assert.True(control.Is64Bit);
        Assert.False(control.A16);
        Assert.Equal(Enumerable.Range(1, 12).Select(static value => (uint)value), control.AddressRegisters);
        Assert.Equal(
            Enumerable.Range(32, 4).Select(static value => Gen5Operand.Vector((uint)value)),
            instruction.Destinations);
        Assert.Equal(16, instruction.Sources.Count);
    }

    [Fact]
    public void BvhFallbackDoesNotCreateAnOrdinaryImageResource()
    {
        var program = DecodeBvh64Program();

        Assert.Empty(MemoryAccessTable.Build(program).Entries);
        var request = Request(program);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var spirv,
                out var spirvError),
            spirvError);
        var opcodes = new SpirvModuleInspector(spirv.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.ULessThan, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var metal,
                out var metalError),
            metalError);
        Assert.Contains("0xFFFFFFFFu", metal.Source, StringComparison.Ordinal);
        Assert.Contains("0x01000000u", metal.Source, StringComparison.Ordinal);
    }

    private static Gen5ShaderProgram DecodeBvh64Program()
    {
        var words = Bvh64Words.Append(0xBF810000u).ToArray();
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                0x1000,
                out var program,
                out var error),
            error);
        return program;
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 ||
                destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    private sealed class EmptyMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => false;

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
