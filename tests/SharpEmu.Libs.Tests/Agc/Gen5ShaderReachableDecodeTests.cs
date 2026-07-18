// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderReachableDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SNop = 0xBF800000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void ConditionalBranchOverEarlyEnd_DecodesBothReachablePaths()
    {
        var state = Decode(
            Branch(0x04, 1),
            SEndpgm,
            SNop,
            SEndpgm);

        Assert.Equal(
            new uint[] { 0, 4, 8, 12 },
            state.Program.Instructions.Select(instruction => instruction.Pc));
    }

    [Fact]
    public void BackwardBranchToDecodedBoundary_ConvergesAndKeepsExit()
    {
        var state = Decode(
            SNop,
            Branch(0x04, -2),
            SEndpgm);

        Assert.Equal(
            new uint[] { 0, 4, 8 },
            state.Program.Instructions.Select(instruction => instruction.Pc));
    }

    [Fact]
    public void UnconditionalBranch_DoesNotDecodeUnreachableWords()
    {
        var state = Decode(
            Branch(0x02, 1),
            0xFFFF_FFFF,
            SEndpgm);

        Assert.Equal(
            new uint[] { 0, 8 },
            state.Program.Instructions.Select(instruction => instruction.Pc));
    }

    [Fact]
    public void NegativeBranchTarget_IsRejected()
    {
        Assert.False(
            TryDecode(
                [Branch(0x02, -2)],
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains("invalid branch target", error, StringComparison.Ordinal);
        Assert.Contains("target=-4", error, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchIntoSecondDword_IsRejectedAsInteriorTarget()
    {
        Assert.False(
            TryDecode(
                [
                    Branch(0x04, 1),
                    0xF800_0000,
                    0,
                    SEndpgm,
                ],
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains("interior instruction target", error, StringComparison.Ordinal);
        Assert.Contains("pc=0x8", error, StringComparison.Ordinal);
        Assert.Contains("owner=0x4", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InstructionRangeOverlappingEarlierDecodedTarget_IsRejected()
    {
        Assert.False(
            TryDecode(
                [
                    Branch(0x04, 4),
                    Branch(0x02, 1),
                    0,
                    0xE000_0000,
                    0xFF00_0000,
                    SEndpgm,
                ],
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains("overlapping instruction", error, StringComparison.Ordinal);
        Assert.Contains("pc=0xC", error, StringComparison.Ordinal);
        Assert.Contains("owner=0x14", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MisalignedShaderAddress_IsRejected()
    {
        Assert.False(
            TryDecode(
                [SEndpgm],
                ShaderAddress + 2,
                out _,
                out var error));
        Assert.Contains("misaligned address", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReachableFallthroughWithoutTerminator_IsRejected()
    {
        Assert.False(
            TryDecode(
                [SNop, SNop],
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains("unterminated-path", error, StringComparison.Ordinal);
        Assert.Contains("pc=0x8", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GuestAddressOverflow_IsRejectedAtFailingPc()
    {
        const ulong address = ulong.MaxValue - 3;
        var memory = new FakeCpuMemory(address, sizeof(uint));
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, SNop);
        Assert.True(memory.TryWrite(address, buffer));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.False(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                address,
                0,
                new Dictionary<uint, uint>(),
                0,
                out _,
                out var error));
        Assert.Contains("invalid guest address", error, StringComparison.Ordinal);
        Assert.Contains("pc=0x4", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeFailure_ReportsInstructionPc()
    {
        Assert.False(
            TryDecode(
                [0xBF83_0000],
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains("decode-failed pc=0x0", error, StringComparison.Ordinal);
        Assert.Contains("unknown-sopp", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x20u)]
    [InlineData(0x21u)]
    public void IndirectControlTransfer_TerminatesReachableDecode(uint opcode)
    {
        var state = Decode(0xBE80_0000u | opcode << 8);

        Assert.Single(state.Program.Instructions);
    }

    private static Gen5ShaderState Decode(params uint[] words)
    {
        Assert.True(
            TryDecode(words, ShaderAddress, out var state, out var error),
            error);
        return state;
    }

    private static bool TryDecode(
        IReadOnlyList<uint> words,
        ulong address,
        out Gen5ShaderState state,
        out string error)
    {
        var addressOffset = checked((int)(address - ShaderAddress));
        var memory = new FakeCpuMemory(
            ShaderAddress,
            checked(addressOffset + words.Count * sizeof(uint)));
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, words[index]);
            Assert.True(
                memory.TryWrite(
                    address + checked((uint)index * sizeof(uint)),
                    buffer));
        }

        var ctx = new CpuContext(memory, Generation.Gen5);
        return Gen5ShaderTranslator.TryCreateState(
            ctx,
            address,
            0,
            new Dictionary<uint, uint> { [0x213] = 0 },
            0x240,
            out state,
            out error);
    }

    private static uint Branch(uint opcode, short offset) =>
        0xBF80_0000u | opcode << 16 | unchecked((ushort)offset);
}
