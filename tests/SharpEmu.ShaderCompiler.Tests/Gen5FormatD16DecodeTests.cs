// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Formatted D16 buffer accesses pack two components per register; the _HI_X forms move one.
public sealed class Gen5FormatD16DecodeTests
{
    private const uint EndProgram = 0xBF810000u;

    [Theory]
    [InlineData(0x80u, "BufferLoadFormatD16X", 1u, 1u, true)]
    [InlineData(0x81u, "BufferLoadFormatD16Xy", 1u, 2u, true)]
    [InlineData(0x82u, "BufferLoadFormatD16Xyz", 2u, 3u, true)]
    [InlineData(0x83u, "BufferLoadFormatD16Xyzw", 2u, 4u, true)]
    [InlineData(0x84u, "BufferStoreFormatD16X", 1u, 1u, true)]
    [InlineData(0x85u, "BufferStoreFormatD16Xy", 1u, 2u, true)]
    [InlineData(0x86u, "BufferStoreFormatD16Xyz", 2u, 3u, true)]
    [InlineData(0x87u, "BufferStoreFormatD16Xyzw", 2u, 4u, true)]
    [InlineData(0x26u, "BufferLoadFormatD16HiX", 1u, 1u, false)]
    [InlineData(0x27u, "BufferStoreFormatD16HiX", 1u, 1u, false)]
    public void UntypedD16Opcodes_DecodeRegistersAndComponents(
        uint opcode, string expected, uint registers, uint components, bool packed)
    {
        // buffer_xxx v4, v0, s[8:11], 0 idxen
        var program = DecodeHarness.Decode(0xE0002000u | (opcode << 18), 0x80020400u, EndProgram);

        var instruction = program.Instructions[0];
        Assert.Equal(expected, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(registers, control.DwordCount);
        Assert.Equal(components, control.ComponentCount);
        Assert.Equal(packed, control.PackedD16);
        Assert.False(control.Typed);
    }

    [Theory]
    [InlineData(0u, false, 1u, 1u)]
    [InlineData(1u, false, 1u, 2u)]
    [InlineData(2u, false, 2u, 3u)]
    [InlineData(3u, false, 2u, 4u)]
    [InlineData(7u, true, 2u, 4u)]
    public void TypedD16Opcodes_ArePackedAndKeepTheirComponentCount(
        uint opcodeLow, bool store, uint registers, uint components)
    {
        var low = store ? opcodeLow & 3 | 4 : opcodeLow;
        var word0 = 0xE800_0000u | (77u << 19) | (low << 16);
        var program = DecodeHarness.Decode(word0, (0x80u << 24) | (1u << 21) | (2u << 16) | (4u << 8), EndProgram);

        var control = Assert.IsType<Gen5BufferMemoryControl>(program.Instructions[0].Control);
        Assert.True(control.PackedD16);
        Assert.Equal(registers, control.DwordCount);
        Assert.Equal(components, control.ComponentCount);
    }

    [Fact]
    public void PlainFormattedAccesses_AreNotPacked()
    {
        var program = DecodeHarness.Decode(0xE0002000u | (3u << 18), 0x80020400u, EndProgram);

        var control = Assert.IsType<Gen5BufferMemoryControl>(program.Instructions[0].Control);
        Assert.False(control.PackedD16);
        Assert.Equal(4u, control.ComponentCount);
        Assert.Equal(control.DwordCount, control.ComponentCount);
    }
}
