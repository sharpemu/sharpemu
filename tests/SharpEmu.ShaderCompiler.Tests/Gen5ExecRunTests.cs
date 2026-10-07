// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// A run of plain vector ALU writes may share one EXEC test. Anything the wave runs even where this
// lane is masked off has to end the run.
public sealed class Gen5ExecRunTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint EndProgram = 0xBF810000;
    private const uint MultiplyF32 = 0x10040510;        // v_mul_f32 v2, v16, v2
    private const uint CompareExecNeU32 = 0x7DAA0480;   // v_cmpx_ne_u32 0, v2  (writes EXEC)
    private const uint ConvertF32U32 = 0x7E1C0C10;      // v_cvt_f32_u32 v14, s16
    private const uint ScalarMove = 0xBE8E047E;         // s_mov_b64 s14, exec

    private static Gen5ShaderInstruction Only(uint word)
    {
        var program = Decode([word, EndProgram]);
        return program.Instructions[0];
    }

    [Fact]
    public void PlainVectorArithmeticCanShareAnExecTest()
    {
        Assert.True(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(Only(MultiplyF32)));
        Assert.True(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(Only(ConvertF32U32)));
    }

    // v_cmpx writes EXEC and v_cmp writes a mask register; GFX10 keeps that destination in the
    // instruction's control, so the destination list alone does not show it.
    [Fact]
    public void ACompareThatWritesAMaskRegisterEndsTheRun()
    {
        var compare = Only(CompareExecNeU32);
        Assert.Empty(compare.Destinations);
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(compare));
    }

    // SALU is not EXEC masked on the hardware and every invocation keeps its own scalar registers.
    [Fact]
    public void AScalarWriteEndsTheRun() =>
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(Only(ScalarMove)));

    // The carry forms write VCC without naming it as a destination.
    [Theory]
    [InlineData("VAddCoU32")]
    [InlineData("VAddCoCiU32")]
    [InlineData("VSubCoU32")]
    [InlineData("VSubrevCoCiU32")]
    public void ACarryWritingAddEndsTheRun(string opcode)
    {
        var instruction = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Vop2, opcode, [0], [Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [Gen5Operand.Vector(5)], null);
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(instruction));
    }

    // A VOP3 compare names its mask register in the control, not in the destination list.
    [Fact]
    public void AVop3CompareDestinationInTheControlEndsTheRun()
    {
        var instruction = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Vop3, "VMadU64U32", [0], [Gen5Operand.Vector(2)], [Gen5Operand.Vector(4)],
            new Gen5Vop3Control(0, 0, 0, false, 0, 20));
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(instruction));

        var plain = instruction with { Control = new Gen5Vop3Control(0, 0, 0, false, 0, null) };
        Assert.True(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(plain));
    }

    [Fact]
    public void SharedMemoryEndsTheRun()
    {
        var instruction = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Ds, "VMovB32", [0], [Gen5Operand.Vector(2)], [Gen5Operand.Vector(1)],
            new Gen5DataShareControl(0, 0, false));
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(instruction));
    }

    // Cross-lane reads need the lanes they read from to have run.
    [Theory]
    [InlineData("VReadlaneB32")]
    [InlineData("VWritelaneB32")]
    [InlineData("VReadfirstlaneB32")]
    [InlineData("VPermlane16B32")]
    public void CrossLaneReadsEndTheRun(string opcode)
    {
        var instruction = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Vop1, opcode, [0], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(2)], null);
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(instruction));
    }

    // A neighbour's register is only there if the neighbour ran.
    [Fact]
    public void TheDppEncodingsEndTheRun()
    {
        var dpp = new Gen5ShaderInstruction(
            0, Gen5ShaderEncoding.Vop1, "VMovB32", [0], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(2)],
            new Gen5Dpp8Control(0, false));
        Assert.False(Gen5ExecRunAnalysis.IsExecMaskedVectorAlu(dpp));
    }

    // A run under an EXEC the translator cannot prove full keeps one test for the whole run: the
    // guarded translation selects against the old value of every register it writes instead.
    [Fact]
    public void ARunUnderAnUnknownExecKeepsOneTestInsteadOfOneSelectPerWrite()
    {
        var words = new List<uint> { CompareExecNeU32 };
        for (var index = 0; index < 16; index++)
        {
            words.Add(MultiplyF32);
        }

        words.Add(EndProgram);
        var program = Decode(words);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // One shared test instead of one select per write: the sixteen multiplies contribute no
        // selects of their own, so the module holds far fewer than sixteen.
        Assert.True(
            CountOpcode(shader.Spirv, SelectOpcode) < 16,
            $"the run still selects per write: selects={CountOpcode(shader.Spirv, SelectOpcode)}");
    }

    private const uint SelectOpcode = 169;

    private static int CountOpcode(byte[] spirv, uint opcode)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var count = 0;
        for (var index = 5; index < words.Length;)
        {
            var length = words[index] >> 16;
            if (length == 0)
            {
                break;
            }

            if ((words[index] & 0xFFFF) == opcode)
            {
                count++;
            }

            index += (int)length;
        }

        return count;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress || address + (ulong)destination.Length > baseAddress + (ulong)size)
            {
                return false;
            }

            _storage.AsSpan((int)(address - baseAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress || address + (ulong)source.Length > baseAddress + (ulong)size)
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }
}
