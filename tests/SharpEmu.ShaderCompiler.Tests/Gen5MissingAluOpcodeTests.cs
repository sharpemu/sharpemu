// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MissingAluOpcodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void SAbsdiffI32_DecodesAndCompiles()
    {
        // s_absdiff_i32 s0, s1, s2
        // SOP2: [31:30]=0b10, [29:23]=0x2D, [22:16]=sdst(0), [15:8]=ssrc1(2), [7:0]=ssrc0(1)
        const uint sAbsdiff = (0b10u << 30) | (0x2Du << 23) | (0u << 16) | (2u << 8) | 1u;
        var program = Decode([sAbsdiff, SEndpgm]);

        Assert.Equal(["SAbsdiffI32", "SEndpgm"], program.Instructions.Select(i => i.Opcode));

        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        var opcodes = ReadOpcodes(shader.Spirv);
        // SAbsdiffI32 should emit signed subtraction (ISub) and SAbs
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
        // SAbs is GLSL.std.450 extended opcode 5 (4 would be float-only FAbs)
        Assert.Contains(
            (5u, "GLSL.std.450"),
            ReadExtendedInstructions(shader.Spirv, "GLSL.std.450"));
    }

    [Fact]
    public void SMulHiI32_DecodesAndCompiles()
    {
        // s_mul_hi_i32 s0, s1, s2
        // SOP2: [31:30]=0b10, [29:23]=0x36, [22:16]=sdst(0), [15:8]=ssrc1(2), [7:0]=ssrc0(1)
        const uint sMulHiI32 = (0b10u << 30) | (0x36u << 23) | (0u << 16) | (2u << 8) | 1u;
        var program = Decode([sMulHiI32, SEndpgm]);

        Assert.Equal(["SMulHiI32", "SEndpgm"], program.Instructions.Select(i => i.Opcode));

        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        var opcodes = ReadOpcodes(shader.Spirv);
        // SMulHiI32 should multiply 64-bit signed integers and shift right arithmetic
        Assert.Contains((ushort)SpirvOp.IMul, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftRightArithmetic, opcodes);
    }

    [Fact]
    public void VSadU32_DecodesAndCompiles()
    {
        // v_sad_u32 v0, v1, v2, v3
        // VOP3: word0: [31:26]=0x35, [25:16]=0x15D, [7:0]=vdst(0)
        //       word1: [8:0]=src0(v1=257), [17:9]=src1(v2=258), [26:18]=src2(v3=259)
        const uint vSadWord0 = (0x35u << 26) | (0x15Du << 16) | 0u;
        const uint vSadWord1 = (256u + 1u) | ((256u + 2u) << 9) | ((256u + 3u) << 18);
        var program = Decode([vSadWord0, vSadWord1, SEndpgm]);

        Assert.Equal(["VSadU32", "SEndpgm"], program.Instructions.Select(i => i.Opcode));

        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(scalarRegisters, scalarRegisters, [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        var opcodes = ReadOpcodes(shader.Spirv);
        // VSadU32 calculates abs difference + accumulator
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static IReadOnlyList<(uint Opcode, string Import)> ReadExtendedInstructions(
        byte[] spirv,
        string importName)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);

        var imports = new Dictionary<uint, string>();
        var extended = new List<(uint, string)>();
        for (var offset = 5; offset < words.Length;)
        {
            var word = words[offset];
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0 || offset + wordCount > words.Length)
            {
                break;
            }

            var opcode = (ushort)word;
            if (opcode == (ushort)SpirvOp.ExtInstImport)
            {
                var nameWords = words.AsSpan(offset + 2, wordCount - 2);
                var nameBytes = new byte[nameWords.Length * sizeof(uint)];
                Buffer.BlockCopy(nameWords.ToArray(), 0, nameBytes, 0, nameBytes.Length);
                var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                imports[words[offset + 1]] = name;
            }
            else if (opcode == (ushort)SpirvOp.ExtInst)
            {
                var importId = words[offset + 3];
                extended.Add((words[offset + 4], imports.GetValueOrDefault(importId, string.Empty)));
            }

            offset += wordCount;
        }

        return extended.Where(e => e.Item2 == importName).ToList();
    }

    private static IReadOnlyList<ushort> ReadOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = (int)(word >> 16);
            opcodes.Add((ushort)word);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            var offset = (int)(virtualAddress - baseAddress);
            if (offset < 0 || offset + destination.Length > _storage.Length)
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            var offset = (int)(virtualAddress - baseAddress);
            if (offset < 0 || offset + source.Length > _storage.Length)
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }
    }
}
