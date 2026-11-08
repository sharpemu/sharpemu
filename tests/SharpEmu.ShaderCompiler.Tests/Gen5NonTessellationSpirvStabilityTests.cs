// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class Gen5NonTessellationSpirvStabilityTests
{
    [Theory]
    [InlineData("lds-readback", ShaderStage.Vertex, 64u, "9BCF02647F837E55")]
    [InlineData("lds-readback", ShaderStage.Vertex, 32u, "BF4344E36DEA217E")]
    [InlineData("lds-readback", ShaderStage.Pixel, 64u, "D576CD6C4222535C")]
    [InlineData("lds-readback", ShaderStage.Pixel, 32u, "CB5C1CDED8103BC2")]
    [InlineData("lds-readback", ShaderStage.Compute, 64u, "51DC6ACDCA96B659")]
    [InlineData("lds-readback", ShaderStage.Compute, 32u, "7461E219AEAFAC53")]
    [InlineData("lds-staging-export", ShaderStage.Vertex, 64u, "D6060044105BFC27")]
    [InlineData("lds-staging-export", ShaderStage.Vertex, 32u, "02D77B4078336EDC")]
    [InlineData("lds-barrier", ShaderStage.Compute, 64u, "6002AC8936414DF5")]
    [InlineData("lds-barrier", ShaderStage.Compute, 32u, "FC521693C8309CEE")]
    [InlineData("divergent", ShaderStage.Vertex, 64u, "4075CAB9F922B279")]
    [InlineData("divergent", ShaderStage.Pixel, 32u, "143C3C99F37CBD63")]
    [InlineData("divergent", ShaderStage.Compute, 64u, "341B4840D46530B4")]
    [InlineData("image-load", ShaderStage.Pixel, 64u, "FAE742FAEE7FE228")]
    [InlineData("image-load", ShaderStage.Compute, 32u, "E725B8040EEACCDD")]
    public void SpirvIsUnchanged(string name, ShaderStage stage, uint waveSize, string expected)
    {
        var (plan, resources, layout) = Prepare(Programs[name](), stage);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
            WaveSize = waveSize,
            LocalSizeX = stage == ShaderStage.Compute ? 128u : 0u,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(shader.Spirv), 0, 8));
    }

    private static readonly Dictionary<string, Func<Gen5ShaderProgram>> Programs = new()
    {
        ["lds-readback"] = () => Gen5DataShareThreadWriteTests.CreateReadbackProgram(0xABCD0010, 0x100, true),
        // NGG-style staging: write two values at a lane-derived address, read them back, export.
        ["lds-staging-export"] = () => Program(
            Vop2(0, "VLshlrevB32", 10, Operand(3), Gen5Operand.Vector(7)),
            DataShare(4, "DsWriteB32", false, [Gen5Operand.Vector(10), Gen5Operand.Vector(5)], []),
            DataShare(12, "DsWriteB32", false, [Gen5Operand.Vector(10), Gen5Operand.Vector(6)], [], 4),
            DataShare(20, "DsReadB32", false, [Gen5Operand.Vector(10)], [11]),
            DataShare(28, "DsReadB32", false, [Gen5Operand.Vector(10)], [12], 4),
            new Gen5ShaderInstruction(36, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
                [Gen5Operand.Vector(11), Gen5Operand.Vector(12), Gen5Operand.Vector(8), Gen5Operand.Source(242)], [],
                new Gen5ExportControl(12, 15, false, true, false)),
            EndProgram(44)),
        ["lds-barrier"] = () => Program(
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            Vop2(4, "VAddI32", 4, Operand(37), Gen5Operand.Vector(0)),
            DataShare(8, "DsWriteB32", false, [Gen5Operand.Vector(5), Gen5Operand.Vector(4)], []),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null),
            Vop2(20, "VXorB32", 6, Operand(64), Gen5Operand.Vector(0)),
            Vop2(24, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(6)),
            DataShare(28, "DsReadB32", false, [Gen5Operand.Vector(6)], [4]),
            BufferAccess(36, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(44)),
        ["divergent"] = () => Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop1, "VReadfirstlaneB32", [0u],
                [Gen5Operand.Vector(0)], [Gen5Operand.Scalar(20)], null),
            Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(20), Operand(64)),
            Branch(8, "SCbranchScc0", 5),
            MoveScalar(12, 21, 111),
            Vop1(16, "VMovB32", 4, Operand(333)),
            Branch(20, "SBranch", 4),
            Nop(24), Nop(28),
            MoveScalar(32, 21, 222),
            Vop1(36, "VMovB32", 4, Operand(444)),
            Vop2(40, "VAddI32", 4, Gen5Operand.Scalar(21), Gen5Operand.Vector(4)),
            Vop2(44, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(48, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(56)),
        ["image-load"] = () => Program(
            Image(0, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
            Vop2(8, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(12, "BufferStoreDword", 12, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(20)),
    };
}
