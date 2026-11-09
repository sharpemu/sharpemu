// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderGatherDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void ImageGather4L_DecodesAsAGatherWithTheCoordinatesFirst()
    {
        // IMAGE_GATHER4_L v2, v[0:2], s[4:11], s[0:3] dmask:0x1 dim:2D (opcode 0x44: word0[24:18])
        var instruction = DecodeSingle(0xF1100108, 0x00010200);

        Assert.Equal("ImageGather4L", instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(1u, control.Dmask);
        Assert.Equal(0u, control.VectorAddress);
        Assert.Equal(2u, control.VectorData);
        Assert.Equal(4u, control.ScalarResource);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, instruction.Destinations);
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program.Instructions[0];
    }
}
