// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// S_CODE_END pads the end of a shader. A block placed after the final S_ENDPGM that loops back
// with S_BRANCH leads the linear decoder into that padding.
public sealed class Gen5CodeEndTests
{
    private const uint BranchScc0Over = 0xBF840002; // s_cbranch_scc0 to 0x0C
    private const uint MoveScalar = 0xBE800381;     // s_mov_b32 s0, 1
    private const uint EndProgram = 0xBF810000;     // s_endpgm
    private const uint BranchBack = 0xBF82FFFC;     // s_branch to 0x04
    private const uint CodeEnd = 0xBF9F0000;        // s_code_end

    [Fact]
    public void DecodeEndsAtCodeEndAfterTheLastReachableBlock()
    {
        var context = Context(BranchScc0Over, MoveScalar, EndProgram, MoveScalar, BranchBack, CodeEnd, CodeEnd);

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);

        Assert.Equal(
            ["SCbranchScc0", "SMovB32", "SEndpgm", "SMovB32", "SBranch"],
            program.Instructions.Select(static instruction => instruction.Opcode));
    }

    [Fact]
    public void CodeEndBeforeAForwardBranchTargetFails()
    {
        var context = Context(BranchScc0Over, CodeEnd, EndProgram, MoveScalar, EndProgram);

        Assert.False(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out _, out var error));
        Assert.Contains("code-end-inside-program", error);
    }

    private static CpuContext Context(params uint[] words) => new(new InstructionMemory(words), Generation.Gen5);

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint))
            {
                return false;
            }

            var index = (address - 0x1000) / sizeof(uint);
            if (index >= (ulong)words.Length)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
