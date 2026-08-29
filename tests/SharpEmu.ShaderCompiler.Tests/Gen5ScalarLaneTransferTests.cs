// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
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

        var state = new Gen5ShaderState(program, new uint[10], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var compileError),
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
            EndProgram(28),
        ];
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, instructions),
            [0, 0, 1, 0, 0, 0x100, 0, 0, 0x10, 0, 0, 0xF0F0_F0F0, 0x8000_0000],
            null,
            UserDataScalarRegisterBase: 0);
        var ctx = new CpuContext(
            new TestCpuMemory(0x1000, 0x100),
            Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Equal(40u, evaluation.ScalarRegisters[6]);
        Assert.Equal(0xAAu, evaluation.ScalarRegisters[7]);
        Assert.Equal(0xF0u, evaluation.ScalarRegisters[9]);
        Assert.Equal(1u, evaluation.ScalarRegisters[10]);
        Assert.Equal(17u, evaluation.ScalarRegisters[13]);
        Assert.Equal(1u, evaluation.ScalarRegisters[14]);
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

        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var compileError),
            compileError);

        var opcodes = ReadSpirvOpcodes(compiled.Spirv);
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.ULessThan, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);
    }

    [Fact]
    public void FixedLaneSaveRestorePreservesScalarValues()
    {
        List<Gen5ShaderInstruction> instructions =
        [
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            WriteLane(8, vectorRegister: 18, scalarRegister: 85, lane: 5),
            MoveScalar(16, scalarRegister: 84, value: 0xDEAD_BEEF),
            MoveScalar(20, scalarRegister: 85, value: 0xBAD0_CAFE),
            ReadLane(24, scalarRegister: 84, vectorRegister: 18, lane: 2),
            ReadLane(32, scalarRegister: 85, vectorRegister: 18, lane: 5),
            EndProgram(40),
        ];
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, instructions),
            [0x413B_A5B0, 0x0000_0004],
            null,
            UserDataScalarRegisterBase: 84);
        var ctx = new CpuContext(
            new TestCpuMemory(0x1000, 0x100),
            Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Equal(0x413B_A5B0u, evaluation.ScalarRegisters[84]);
        Assert.Equal(0x0000_0004u, evaluation.ScalarRegisters[85]);
    }

    [Fact]
    public void FullVectorWriteInvalidatesSavedLane()
    {
        List<Gen5ShaderInstruction> instructions =
        [
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            new Gen5ShaderInstruction(
                8,
                Gen5ShaderEncoding.Vop1,
                "VMovB32",
                [0u],
                [Gen5Operand.Scalar(0)],
                [Gen5Operand.Vector(18)],
                null),
            MoveScalar(12, scalarRegister: 84, value: 0xDEAD_BEEF),
            ReadLane(16, scalarRegister: 84, vectorRegister: 18, lane: 2),
            EndProgram(24),
        ];
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, instructions),
            [0x413B_A5B0],
            null,
            UserDataScalarRegisterBase: 84);
        var ctx = new CpuContext(
            new TestCpuMemory(0x1000, 0x100),
            Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Equal(0xDEAD_BEEFu, evaluation.ScalarRegisters[84]);
    }

    [Fact]
    public void RestoredPointerBypassesFalseControlFlowMerge()
    {
        const ulong pointer = 0x1000;
        const uint expected = 0x1234_5678;
        List<Gen5ShaderInstruction> instructions =
        [
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            WriteLane(8, vectorRegister: 18, scalarRegister: 85, lane: 5),
            MoveScalar(16, scalarRegister: 84, value: 0),
            MoveScalar(20, scalarRegister: 85, value: 0),
            Branch(24, "SCbranchScc0", wordOffset: 4),
            ReadLane(28, scalarRegister: 84, vectorRegister: 18, lane: 2),
            ReadLane(36, scalarRegister: 85, vectorRegister: 18, lane: 5),
            new Gen5ShaderInstruction(
                44,
                Gen5ShaderEncoding.Smem,
                "SLoadDword",
                [0u, 0u],
                [Gen5Operand.Scalar(84), Gen5Operand.Source(125)],
                [Gen5Operand.Scalar(4)],
                new Gen5ScalarMemoryControl(1, 0, null)),
            EndProgram(52),
        ];
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, instructions),
            [unchecked((uint)pointer), unchecked((uint)(pointer >> 32))],
            null,
            UserDataScalarRegisterBase: 84);
        var memory = new TestCpuMemory(pointer, 256 * 1024);
        Span<byte> expectedBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expectedBytes, expected);
        Assert.True(memory.TryWrite(pointer, expectedBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Equal(expected, evaluation.ScalarRegisters[4]);
    }

    private static Gen5ShaderInstruction WriteLane(
        uint pc,
        uint vectorRegister,
        uint scalarRegister,
        uint lane) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop3,
            "VWritelaneB32",
            [0u, 0u],
            [
                Gen5Operand.Scalar(scalarRegister),
                Gen5Operand.Source(128 + lane),
                Gen5Operand.Scalar(0),
            ],
            [Gen5Operand.Vector(vectorRegister)],
            null);

    private static Gen5ShaderInstruction ReadLane(
        uint pc,
        uint scalarRegister,
        uint vectorRegister,
        uint lane) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop3,
            "VReadlaneB32",
            [0u, 0u],
            [
                Gen5Operand.Vector(vectorRegister),
                Gen5Operand.Source(128 + lane),
                Gen5Operand.Scalar(0),
            ],
            [Gen5Operand.Scalar(scalarRegister)],
            null);

    private static Gen5ShaderInstruction MoveScalar(
        uint pc,
        uint scalarRegister,
        uint value) =>
        new(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SMovB32",
            [0u],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, value)],
            [Gen5Operand.Scalar(scalarRegister)],
            null);

    private static Gen5ShaderInstruction Branch(
        uint pc,
        string opcode,
        short wordOffset) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            opcode,
            [unchecked((uint)(ushort)wordOffset)],
            [],
            [],
            null);

    private static Gen5ShaderInstruction EndProgram(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0u],
            [],
            [],
            null);

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
