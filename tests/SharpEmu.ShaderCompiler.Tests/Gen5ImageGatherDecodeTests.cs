// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageGatherDecodeTests
{
    [Theory]
    [InlineData(0x40u, "ImageGather4")]
    [InlineData(0x44u, "ImageGather4L")]
    [InlineData(0x47u, "ImageGather4Lz")]
    [InlineData(0x48u, "ImageGather4C")]
    [InlineData(0x4Cu, "ImageGather4CL")]
    [InlineData(0x4Fu, "ImageGather4CLz")]
    public void GatherOpcodesDecodeAsImageOperations(uint opcode, string expected)
    {
        // image_gather4* v[0:3], v[4:7], s[8:15], s[0:3] dmask:0xf
        var program = DecodeHarness.Decode(0xF0000000u | (opcode << 18) | (0xFu << 8), 4u | (2u << 16), 0xBF810000u);

        var instruction = program.Instructions[0];
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
        Assert.IsType<Gen5ImageControl>(instruction.Control);
    }
}
