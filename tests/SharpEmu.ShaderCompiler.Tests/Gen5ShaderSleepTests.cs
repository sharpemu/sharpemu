// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderSleepTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(15u)]
    public void DecodePreservesSleepWordAndContinues(uint immediate)
    {
        var word = 0xBF8E0000u | immediate;
        var context = new CpuContext(new InstructionMemory([word, 0xBE800381, 0xBF810000]), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        Assert.Equal(3, program.Instructions.Count);
        var instruction = program.Instructions[0];
        Assert.Equal("SSleep", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sopp, instruction.Encoding);
        Assert.Equal(word, Assert.Single(instruction.Words));
        Assert.Empty(instruction.Destinations);
        Assert.Equal("SMovB32", program.Instructions[1].Opcode);
        Assert.Equal(4u, program.Instructions[1].Pc);
        Assert.Equal("SEndpgm", program.Instructions[2].Opcode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SleepPreservesShaderOutputAndExecutionMask(bool inactiveAtSleep)
    {
        ShaderCompileRequest CreateRequest(bool sleep)
        {
            var instruction = sleep
                ? new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SSleep", [0xBF8E000F], [], [], null)
                : Nop(8);
            var program = Program(
                MoveScalar(0, 8, 0x12345678), MoveScalar(4, 126, inactiveAtSleep ? 0u : 1u), instruction,
                MoveScalarRegister(12, 9, 8), MoveScalar(16, 126, 1),
                MoveVectorFromScalar(20, 2, 9), BufferAccess(24, "BufferStoreDword", 4, vectorData: 2),
                EndProgram(32));
            var (plan, resources, layout) = Prepare(program);
            return new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        }

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(CreateRequest(true), out var sleepShader, out var sleepError), sleepError);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(CreateRequest(false), out var nopShader, out var nopError), nopError);
        Assert.Equal(nopShader.Spirv, sleepShader.Spirv);
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
