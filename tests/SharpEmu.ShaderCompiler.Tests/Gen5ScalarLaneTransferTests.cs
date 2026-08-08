// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarLaneTransferTests
{
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
