// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderDecoderBoundaryTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint Export = 0xF8000000;
    private const uint Nop = 0xBF800000;
    private const uint EndPgm = 0xBF810000;
    private const int MaximumInstructionCount = 1024 * 1024 / sizeof(uint);

    [Fact]
    public void MissingAddress_IsRejectedWithoutReadingGuestMemory()
    {
        var memory = new RecordingCpuMemory(ShaderAddress, []);

        var decoded = Decode(memory, 0, out var program, out var error);

        Assert.False(decoded);
        Assert.Empty(program.Instructions);
        Assert.Equal("missing", error);
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public void UnreadableFirstWord_IsRejectedAtProgramStart()
    {
        var memory = new RecordingCpuMemory(ShaderAddress, []);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.False(decoded);
        Assert.Empty(program.Instructions);
        Assert.Equal("read-failed pc=0x0", error);
        Assert.Collection(
            memory.Reads,
            read => Assert.Equal(new MemoryRead(ShaderAddress, sizeof(uint), false), read));
    }

    [Fact]
    public void TruncatedTwoDwordInstruction_IsRejectedAtMissingWord()
    {
        var memory = RecordingCpuMemory.FromWords(ShaderAddress, Export);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.False(decoded);
        Assert.Empty(program.Instructions);
        Assert.Equal("read-failed pc=0x4", error);
        Assert.Collection(
            memory.Reads,
            read => Assert.Equal(new MemoryRead(ShaderAddress, sizeof(uint), true), read),
            read => Assert.Equal(
                new MemoryRead(ShaderAddress + sizeof(uint), sizeof(uint), false),
                read));
    }

    [Fact]
    public void UnknownTopLevelEncoding_IsRejectedWithoutSpeculativeReads()
    {
        const uint unknownTopLevel = 0xE4000000;
        var memory = RecordingCpuMemory.FromWords(ShaderAddress, unknownTopLevel);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.False(decoded);
        Assert.Empty(program.Instructions);
        Assert.Equal("unknown-top pc=0x0 word=0xE4000000", error);
        Assert.Collection(
            memory.Reads,
            read => Assert.Equal(new MemoryRead(ShaderAddress, sizeof(uint), true), read));
    }

    [Fact]
    public void MaximumInstructionCountWithoutEndPgm_IsRejectedAsUnterminated()
    {
        var words = new uint[MaximumInstructionCount];
        Array.Fill(words, Nop);
        var memory = RecordingCpuMemory.FromWords(ShaderAddress, words);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.False(decoded);
        Assert.Empty(program.Instructions);
        Assert.StartsWith("unterminated", error, StringComparison.Ordinal);
        Assert.Equal(MaximumInstructionCount, memory.Reads.Count);
        Assert.All(memory.Reads, read => Assert.True(read.Succeeded));
        Assert.Equal(
            new MemoryRead(
                ShaderAddress + ((MaximumInstructionCount - 1) * sizeof(uint)),
                sizeof(uint),
                true),
            memory.Reads[^1]);
    }

    [Fact]
    public void ProgramMayEndAfterPreviousDecoderLimit()
    {
        const int previousDecoderLimit = 4096;
        var words = new uint[previousDecoderLimit + 1];
        Array.Fill(words, Nop);
        words[^1] = EndPgm;
        var memory = RecordingCpuMemory.FromWords(ShaderAddress, words);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.True(decoded, error);
        Assert.Equal(words.Length, program.Instructions.Count);
        Assert.Equal("SEndpgm", program.Instructions[^1].Opcode);
        Assert.Equal(words.Length, memory.Reads.Count);
    }

    [Fact]
    public void TrailingBackwardBranch_StopsBeforeMetadataTrailer()
    {
        // Astro Bot: an out-of-line loop block after S_ENDPGM ends with an
        // S_BRANCH back into the loop, followed by the "sl00" metadata block.
        const uint CBranchScc0OverEndPgm = 0xBF840001u;
        const uint BranchBackToStart = 0xBF82FFFCu;
        const uint MetadataWord = 0x5D000040u;
        var memory = RecordingCpuMemory.FromWords(
            ShaderAddress,
            [CBranchScc0OverEndPgm, EndPgm, Nop, BranchBackToStart, MetadataWord]);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.True(decoded, error);
        Assert.Equal(4, program.Instructions.Count);
        Assert.Equal("SBranch", program.Instructions[^1].Opcode);
    }

    [Fact]
    public void DebuggerBranchPastEndPgm_DoesNotDecodeDebuggerStub()
    {
        // Astro Bot: S_CBRANCH_CDBGSYS jumps to a debugger stub after
        // S_ENDPGM that loads descriptors from a devkit-only address.
        const uint CBranchCdbgsysOverEndPgm = 0xBF970001u;
        const uint StubMove = 0xBE8003FFu;
        var memory = RecordingCpuMemory.FromWords(
            ShaderAddress,
            [CBranchCdbgsysOverEndPgm, EndPgm, StubMove]);

        var decoded = Decode(memory, ShaderAddress, out var program, out var error);

        Assert.True(decoded, error);
        Assert.Equal(2, program.Instructions.Count);
        Assert.Equal("SEndpgm", program.Instructions[^1].Opcode);
    }

    private static bool Decode(
        RecordingCpuMemory memory,
        ulong address,
        out Gen5ShaderProgram program,
        out string error) =>
        Gen5ShaderTranslator.TryDecodeProgram(
            new CpuContext(memory, Generation.Gen5),
            address,
            out program,
            out error);

    private readonly record struct MemoryRead(ulong Address, int Length, bool Succeeded);

    private sealed class RecordingCpuMemory(ulong baseAddress, byte[] storage) : ICpuMemory
    {
        public List<MemoryRead> Reads { get; } = [];

        public static RecordingCpuMemory FromWords(ulong baseAddress, params uint[] words)
        {
            var storage = new byte[words.Length * sizeof(uint)];
            for (var index = 0; index < words.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    storage.AsSpan(index * sizeof(uint), sizeof(uint)),
                    words[index]);
            }

            return new RecordingCpuMemory(baseAddress, storage);
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            var succeeded = TryResolve(virtualAddress, destination.Length, out var offset);
            Reads.Add(new MemoryRead(virtualAddress, destination.Length, succeeded));
            if (succeeded)
            {
                storage.AsSpan(offset, destination.Length).CopyTo(destination);
            }

            return succeeded;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative > (ulong)storage.Length ||
                (ulong)length > (ulong)storage.Length - relative)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
