// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderTrapTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    [InlineData(65535u)]
    public void DecodePreservesTrapWordAndContinues(uint immediate)
    {
        var word = 0xBF920000u | immediate;
        var context = new CpuContext(new InstructionMemory([word, 0xBE800381, 0xBF810000]), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        Assert.Equal(3, program.Instructions.Count);
        var instruction = program.Instructions[0];
        Assert.Equal("STrap", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sopp, instruction.Encoding);
        Assert.Equal(word, Assert.Single(instruction.Words));
        Assert.Equal(new Gen5Operand(Gen5OperandKind.LiteralConstant, immediate & 0xFF), Assert.Single(instruction.Sources));
        Assert.Empty(instruction.Destinations);
        Assert.Equal("SMovB32", program.Instructions[1].Opcode);
        Assert.Equal(4u, program.Instructions[1].Pc);
        Assert.Equal("SEndpgm", program.Instructions[2].Opcode);
    }

    [Fact]
    public void UnknownScalarOpcodeStillFails()
    {
        var context = new CpuContext(new InstructionMemory([0xBFFF0000, 0xBF810000]), Generation.Gen5);
        Assert.False(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out _, out var error));
        Assert.Contains("unknown-sopp op=0x7F", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrapWithoutHandlerMatchesNoOperationOnBothBackends(bool inactiveAtTrap)
    {
        var trapRequest = CreateRequest(CreateReadbackProgram(inactiveAtTrap));
        var noOperationProgram = CreateReadbackProgram(inactiveAtTrap, useNoOperation: true);
        var noOperationRequest = CreateRequest(noOperationProgram);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(trapRequest, out var trapShader, out var trapError), trapError);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(noOperationRequest, out var noOperationShader, out var noOperationError), noOperationError);
        Assert.Equal(noOperationShader.Spirv, trapShader.Spirv);
        Assert.True(Gen5MslTranslator.TryCompileProgram(trapRequest, out var trapMetal, out var trapMetalError), trapMetalError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(noOperationRequest, out var noOperationMetal, out var noOperationMetalError), noOperationMetalError);
        Assert.Equal(noOperationMetal.Source, trapMetal.Source);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool inactiveAtTrap, bool useNoOperation = false)
    {
        var trap = useNoOperation ? Nop(8) : new Gen5ShaderInstruction(
            8, Gen5ShaderEncoding.Sopp, "STrap", [0xBF920001], [], [], null);
        return Program(
            MoveScalar(0, 8, 0x12345678), MoveScalar(4, 126, inactiveAtTrap ? 0u : 1u), trap,
            MoveScalarRegister(12, 9, 8), MoveScalar(16, 126, 1),
            MoveVectorFromScalar(20, 2, 9), BufferAccess(24, "BufferStoreDword", 4, vectorData: 2),
            EndProgram(32));
    }

    private static ShaderCompileRequest CreateRequest(Gen5ShaderProgram program)
    {
        var (plan, resources, layout) = Prepare(program);
        return new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
    }

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint))
                return false;
            var index = (address - 0x1000) / sizeof(uint);
            if (index >= (ulong)words.Length) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
