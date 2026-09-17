// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Typed buffer instructions keep their own unified format; formatted untyped ones do not carry one.
public sealed class Gen5TypedBufferLoadDecodeTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint Format32x4Float = 77;

    [Theory]
    [InlineData(0u, false, "TBufferLoadFormatX", 1u)]
    [InlineData(1u, false, "TBufferLoadFormatXy", 2u)]
    [InlineData(2u, false, "TBufferLoadFormatXyz", 3u)]
    [InlineData(3u, false, "TBufferLoadFormatXyzw", 4u)]
    [InlineData(4u, false, "TBufferStoreFormatX", 1u)]
    [InlineData(7u, false, "TBufferStoreFormatXyzw", 4u)]
    [InlineData(0u, true, "TBufferLoadFormatD16X", 1u)]
    [InlineData(1u, true, "TBufferLoadFormatD16Xy", 1u)]
    [InlineData(2u, true, "TBufferLoadFormatD16Xyz", 2u)]
    [InlineData(3u, true, "TBufferLoadFormatD16Xyzw", 2u)]
    [InlineData(7u, true, "TBufferStoreFormatD16Xyzw", 2u)]
    public void TypedBufferInstructions_DecodeTheirOpcodeAndFormat(
        uint opcodeLow,
        bool opcodeHigh,
        string expectedOpcode,
        uint expectedDwordCount)
    {
        var program = Decode(
        [
            TypedWord0(Format32x4Float, opcodeLow, offset: 0x30, indexEnabled: true),
            TypedWord1(opcodeHigh, scalarResource: 8, vectorData: 4),
            0xBF810000,
        ]);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Mtbuf, instruction.Encoding);
        Assert.Equal(expectedOpcode, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.True(control.Typed);
        Assert.Equal(Format32x4Float, control.TypedFormat);
        Assert.Equal(expectedDwordCount, control.DwordCount);
        Assert.Equal(0x30, control.OffsetBytes);
        Assert.True(control.IndexEnabled);
        Assert.Equal(8u, control.ScalarResource);
        Assert.Equal(4u, control.VectorData);
    }

    [Fact]
    public void FormattedUntypedInstructions_CarryNoInstructionFormat()
    {
        // buffer_load_format_xyzw v[4:7], v0, s[8:11], 0 idxen
        var program = Decode(
        [
            0xE0002000 | (3u << 18),
            0x80020400,
            0xBF810000,
        ]);

        var control = Assert.IsType<Gen5BufferMemoryControl>(program.Instructions[0].Control);
        Assert.Equal("BufferLoadFormatXyzw", program.Instructions[0].Opcode);
        Assert.False(control.Typed);
        Assert.Equal(0u, control.TypedFormat);
    }

    private static Gen5ShaderProgram Decode(uint[] words)
    {
        var context = WriteProgram(new TestCpuMemory(ShaderAddress, 0x100), words);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }

    private static CpuContext WriteProgram(TestCpuMemory memory, uint[] words)
    {
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shader[(index * sizeof(uint))..], words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        return new CpuContext(memory, Generation.Gen5);
    }

    // MTBUF word 0: offset[11:0], offen[12], idxen[13], op[18:16], format[25:19], encoding 0x3A.
    private static uint TypedWord0(uint format, uint opcodeLow, uint offset, bool indexEnabled) =>
        0xE800_0000u | (format << 19) | (opcodeLow << 16) | (indexEnabled ? 1u << 13 : 0) | offset;

    // MTBUF word 1: vaddr[7:0], vdata[15:8], srsrc[20:16], op[21], soffset[31:24] (0x80 = constant 0).
    private static uint TypedWord1(bool opcodeHigh, uint scalarResource, uint vectorData) =>
        (0x80u << 24) | (opcodeHigh ? 1u << 21 : 0) | ((scalarResource / 4) << 16) | (vectorData << 8);

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

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress || virtualAddress - baseAddress + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)(virtualAddress - baseAddress);
            return true;
        }
    }
}
