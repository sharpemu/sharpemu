// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarLaneTransferTests
{
    [Fact]
    public void DecoderContinuesPastEndProgramForForwardBranchTarget()
    {
        const ulong shaderAddress = 0x1000;
        var memory = new TestCpuMemory(shaderAddress, 0x100);
        uint[] words =
        [
            0xBF880001, // s_cbranch_execz +1 -> pc 0x8
            0xBF810000, // s_endpgm on the fallthrough path
            0xBF800000, // s_nop 0 at the taken target
            0xBF810000, // s_endpgm on the taken path
        ];
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(shaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                shaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        Assert.Equal(
            ["SCbranchExecz", "SEndpgm", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
        Assert.Equal(
            [0u, 4u, 8u, 12u],
            program.Instructions.Select(static instruction => instruction.Pc));
    }

    [Fact]
    public void ScalarBlockerOpcodesDecodeAndCompile()
    {
        const ulong shaderAddress = 0x1000;
        var memory = new TestCpuMemory(shaderAddress, 0x100);
        uint[] words =
        [
            0xBF130200, // s_cmp_lg_u64 s[0:1], s[2:3]
            0xBE861404, // s_ff1_i32_b64 s6, s[4:5]
            0xBEEB106A, // s_bcnt1_i32_b64 s107, s[106:107]
            0xBE890908, // s_wqm_b32 s9, s8
            0xBF810000, // s_endpgm
        ];
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(shaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                shaderAddress,
                out var program,
                out var decodeError),
            decodeError);
        Assert.Collection(
            program.Instructions,
            instruction => Assert.Equal("SCmpLgU64", instruction.Opcode),
            instruction => Assert.Equal("SFF1I32B64", instruction.Opcode),
            instruction => Assert.Equal("SBcnt1I32B64", instruction.Opcode),
            instruction => Assert.Equal("SWqmB32", instruction.Opcode),
            instruction => Assert.Equal("SEndpgm", instruction.Opcode));

        var request = ResourceTestProgram.Request(program);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var compileError),
            compileError);

        var opcodes = ReadSpirvOpcodes(compiled.Spirv);
        Assert.Contains((ushort)SpirvOp.INotEqual, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);
        Assert.Contains((ushort)SpirvOp.IMul, opcodes);
        Assert.Contains((ushort)SpirvOp.BitCount, opcodes);
    }

    [Fact]
    public void ScalarBlockerOpcodesMatchRdna2Semantics()
    {
        List<Gen5ShaderInstruction> instructions =
        [
            ScalarInstruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpLgU64",
                [Gen5Operand.Scalar(0), Gen5Operand.Scalar(2)]),
            ScalarInstruction(
                4,
                Gen5ShaderEncoding.Sop1,
                "SFF1I32B64",
                [Gen5Operand.Scalar(4)],
                Gen5Operand.Scalar(6)),
            ScalarInstruction(
                8,
                Gen5ShaderEncoding.Sop2,
                "SCselectB32",
                [
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 0xAA),
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 0xBB),
                ],
                Gen5Operand.Scalar(7)),
            ScalarInstruction(
                12,
                Gen5ShaderEncoding.Sop1,
                "SWqmB32",
                [Gen5Operand.Scalar(8)],
                Gen5Operand.Scalar(9)),
            ScalarInstruction(
                16,
                Gen5ShaderEncoding.Sop2,
                "SCselectB32",
                [
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 1),
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 0),
                ],
                Gen5Operand.Scalar(10)),
            ScalarInstruction(
                20,
                Gen5ShaderEncoding.Sop1,
                "SBcnt1I32B64",
                [Gen5Operand.Scalar(11)],
                Gen5Operand.Scalar(13)),
            ScalarInstruction(
                24,
                Gen5ShaderEncoding.Sop2,
                "SCselectB32",
                [
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 1),
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 0),
                ],
                Gen5Operand.Scalar(14)),
            ResourceTestProgram.EndProgram(28),
        ];
        uint[] userData = [0, 0, 1, 0, 0, 0x100, 0, 0, 0x10, 0, 0, 0xF0F0_F0F0, 0x8000_0000];
        uint[] resultRegisters = [6, 7, 9, 10, 13, 14];
        instructions.RemoveAt(instructions.Count - 1);
        for (var index = 0; index < resultRegisters.Length; index++)
        {
            instructions.Add(ResourceTestProgram.ScalarLoad(
                28 + (uint)index * 8, resultRegisters[index], destination: 100));
        }
        instructions.Add(ResourceTestProgram.EndProgram(28 + (uint)resultRegisters.Length * 8));
        var inputBytes = (uint)userData.Length * 8;
        var initialized = userData.Select((value, index) =>
            ResourceTestProgram.MoveScalar((uint)index * 8, (uint)index, value)).ToList();
        initialized.AddRange(instructions.Select(instruction => instruction with { Pc = instruction.Pc + inputBytes }));
        var plan = ResourceTestProgram.Extract(new Gen5ShaderProgram(0, initialized), userDataCount: 0);
        var evaluator = new RuntimeValueEvaluator(plan, ResourceTestProgram.Inputs([]));
        var actual = new List<uint>();
        foreach (var access in plan.Accesses)
        {
            Assert.True(evaluator.Evaluate(access!.Handle!.Operands[0], out var value),
                $"Cannot resolve s{resultRegisters[actual.Count]}: {access.Handle.Operands[0].Kind}.");
            actual.Add(value);
        }
        Assert.Equal([40u, 0xAAu, 0xF0u, 1u, 17u, 1u], actual);
    }

    [Fact]
    public void RelativeVectorSourceTracksM0AndCompilesDynamicRead()
    {
        const ulong shaderAddress = 0x1000;
        var memory = new TestCpuMemory(shaderAddress, 0x100);
        uint[] words =
        [
            0x7E6E870C, // v_movrels_b32 v55, v12
            0xBF810000, // s_endpgm
        ];
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(shaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                shaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        var move = Assert.Single(
            program.Instructions,
            instruction => instruction.Opcode == "VMovrelsB32");
        Assert.Equal(
            [Gen5Operand.Vector(12), Gen5Operand.Scalar(124)],
            move.Sources);
        Assert.Equal([Gen5Operand.Vector(55)], move.Destinations);

        var request = ResourceTestProgram.Request(program);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var compileError),
            compileError);

        var opcodes = ReadSpirvOpcodes(compiled.Spirv);
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.ULessThan, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);
    }

    private static Gen5ShaderInstruction ScalarInstruction(
        uint pc,
        Gen5ShaderEncoding encoding,
        string opcode,
        IReadOnlyList<Gen5Operand> sources,
        Gen5Operand? destination = null) =>
        new(
            pc,
            encoding,
            opcode,
            [0u],
            sources,
            destination.HasValue ? [destination.Value] : [],
            null);

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.True(wordCount > 0);
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(
            ulong virtualAddress,
            int length,
            out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
