// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// Tessellation-only translation changes must not alter the SPIR-V of any other stage. These
// programs cover vertex, pixel and compute stages in both wave sizes, with private and workgroup
// LDS, LDS phase barriers, divergent branches, buffer and image accesses. The hashes are the
// translator's output before the tessellation changes, except the vertex and pixel programs
// with private LDS, which the lazy LDS zeroing changed deliberately; a change that is meant to
// alter non-tessellated output updates them deliberately.
public sealed class Gen5NonTessellationSpirvStabilityTests
{
    [Theory]
    [InlineData("lds-readback", ShaderStage.Vertex, 64u, "323381B93C0B997C")]
    [InlineData("lds-readback", ShaderStage.Vertex, 32u, "DB401A77E0A525B2")]
    [InlineData("lds-readback", ShaderStage.Pixel, 64u, "F9EA08A9C10BF8AD")]
    [InlineData("lds-readback", ShaderStage.Pixel, 32u, "B5074529505A1A86")]
    [InlineData("lds-readback", ShaderStage.Compute, 64u, "BA28D2A4E971E71D")]
    [InlineData("lds-readback", ShaderStage.Compute, 32u, "51372843E048E260")]
    [InlineData("lds-staging-export", ShaderStage.Vertex, 64u, "5F9A422A22E90099")]
    [InlineData("lds-staging-export", ShaderStage.Vertex, 32u, "A9279EB23BC28CD7")]
    [InlineData("lds-barrier", ShaderStage.Compute, 64u, "21C77F85AA632DEB")]
    [InlineData("lds-barrier", ShaderStage.Compute, 32u, "1831808E63C7C40F")]
    [InlineData("divergent", ShaderStage.Vertex, 64u, "C9F0996EAD34F8B6")]
    [InlineData("divergent", ShaderStage.Pixel, 32u, "53BE56C18476D489")]
    [InlineData("divergent", ShaderStage.Compute, 64u, "5CD8E2752F0745FE")]
    [InlineData("image-load", ShaderStage.Pixel, 64u, "578AC52FA115090E")]
    [InlineData("image-load", ShaderStage.Compute, 32u, "D5D535F158180FB0")]
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
