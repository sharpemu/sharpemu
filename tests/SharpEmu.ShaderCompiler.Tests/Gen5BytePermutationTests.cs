// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5BytePermutationTests
{
    public static TheoryData<uint, uint, uint, uint> Values => new()
    {
        { 0x80FF7F01, 0x7F0080FE, 0x03020100, 0x7F0080FE },
        { 0x80FF7F01, 0x7F0080FE, 0x07060504, 0x80FF7F01 },
        { 0x80FF7F01, 0x7F0080FE, 0x07030501, 0x807F7F80 },
        { 0x80FF7F01, 0x7F0080FE, 0x0B0A0908, 0xFF0000FF },
        { 0x7F008001, 0x800000FE, 0x0B0A0908, 0x00FFFF00 },
        { 0x80FF7F01, 0x7F0080FE, 0x0C0C0C0C, 0 },
        { 0x80FF7F01, 0x7F0080FE, 0x0D0D0D0D, 0xFFFFFFFF },
        { 0x80FF7F01, 0x7F0080FE, 0xFFFFFFFF, 0xFFFFFFFF },
        { 0x80FF7F01, 0x7F0080FE, 0xFF0D0C08, 0xFFFF00FF },
    };

    [Fact]
    public void DecodePreservesLiteralAndOperandOrder()
    {
        var instruction = Decode(0xD744000A, 0x03FE1516, 0x07030501);
        Assert.Equal("VPermB32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Vector(22), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(10), instruction.Sources[1]);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x07030501), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(10), Assert.Single(instruction.Destinations));
        Assert.Equal(3, instruction.Words.Count);
    }

    [Fact]
    public void Compiles()
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(true, false));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool enabled, bool overlapDestination)
    {
        var instruction = Decode(0xD7440006, 0x04101208);
        if (overlapDestination)
            instruction = instruction with { Destinations = [Gen5Operand.Vector(4)] };
        return Program(
            MoveVectorFromScalar(0, 4, 10),
            MoveVector(8, 6, 0xCAFEBABE), MoveScalar(12, 126, enabled ? 1u : 0u),
            instruction with { Pc = 16 }, MoveScalar(24, 126, 1),
            BufferAccess(28, "BufferStoreDword", 4, vectorData: overlapDestination ? 4u : 6u), EndProgram(36));
    }

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(words.Length * sizeof(uint)), 0xBF810000);
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
