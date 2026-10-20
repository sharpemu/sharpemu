// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MubufDecodeTests
{
    // MUBUF word 0: offset[11:0], offen[12], idxen[13], glc[14], op[25:18], encoding 0x38.
    private static uint Word0(uint opcode, uint offset) => 0xE000_0000u | (opcode << 18) | offset;

    [Theory]
    [InlineData(0x00u, 0u)]
    [InlineData(0x00u, 1u)]
    [InlineData(0x03u, 0x31u)]
    [InlineData(0x0Cu, 0xFFFu)]
    public void OddOffsetsDoNotChangeTheOpcode(uint opcode, uint offset)
    {
        var program = DecodeHarness.Decode(Word0(opcode, offset), 0x80020400u, 0xBF810000u);

        var baseline = DecodeHarness.Decode(Word0(opcode, 0), 0x80020400u, 0xBF810000u);
        Assert.Equal(Gen5ShaderEncoding.Mubuf, program.Instructions[0].Encoding);
        Assert.Equal(baseline.Instructions[0].Opcode, program.Instructions[0].Opcode);
        Assert.DoesNotContain("Raw", program.Instructions[0].Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(program.Instructions[0].Control);
        Assert.Equal((int)offset, control.OffsetBytes);
    }
}
