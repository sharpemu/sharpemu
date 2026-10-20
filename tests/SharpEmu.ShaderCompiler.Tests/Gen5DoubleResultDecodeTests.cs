// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// F64 results occupy a VGPR pair; both halves must be listed as definitions.
public sealed class Gen5DoubleResultDecodeTests
{
    private const uint EndProgram = 0xBF810000u;

    [Theory]
    [InlineData(0x04u, "VCvtF64I32")]
    [InlineData(0x16u, "VCvtF64U32")]
    [InlineData(0x2Fu, "VRcpF64")]
    [InlineData(0x30u, "VRsqF64")]
    [InlineData(0x34u, "VSqrtF64")]
    public void Vop1_ListsBothDestinationRegisters(uint opcode, string expected)
    {
        // v_xxx v[6:7], v4
        var program = DecodeHarness.Decode(0x7E000000u | (6u << 17) | (opcode << 9) | (256u + 4), EndProgram);

        var instruction = program.Instructions[0];
        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal([Gen5Operand.Vector(6), Gen5Operand.Vector(7)], instruction.Destinations);
    }

    [Theory]
    [InlineData(0x165u, "VMulF64")]
    [InlineData(0x14Cu, "VFmaF64")]
    [InlineData(0x184u, "VCvtF64I32")]
    [InlineData(0x1AFu, "VRcpF64")]
    public void Vop3_ListsBothDestinationRegisters(uint opcode, string expected)
    {
        var program = DecodeHarness.Decode(0xD5000000u | (opcode << 16) | 10u, (256u + 2) | ((256u + 4) << 9) | ((256u + 6) << 18), EndProgram);

        var instruction = program.Instructions[0];
        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal([Gen5Operand.Vector(10), Gen5Operand.Vector(11)], instruction.Destinations);
    }

    [Fact]
    public void SingleResultOpcodesKeepOneDestination()
    {
        // v_cvt_f32_f64 v6, v[4:5]
        var program = DecodeHarness.Decode(0x7E000000u | (6u << 17) | (0x0Fu << 9) | (256u + 4), EndProgram);

        Assert.Equal("VCvtF32F64", program.Instructions[0].Opcode);
        Assert.Equal([Gen5Operand.Vector(6)], program.Instructions[0].Destinations);
    }
}
