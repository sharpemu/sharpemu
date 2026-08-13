// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5StorageImageSwizzleCacheTests
{
    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageStoreMip")]
    public void StructuralFingerprintSeparatesStorageImageSwizzles(string opcode)
    {
        var identity = CreateEvaluation(
            Gen5ShaderTranslator.IdentityImageDstSelect,
            opcode);
        var yzwx = CreateEvaluation(0x9F5u, opcode);

        Assert.NotEqual(
            AgcExports.ComputeShaderStructuralFingerprint(identity),
            AgcExports.ComputeShaderStructuralFingerprint(yzwx));
    }

    private static Gen5ShaderEvaluation CreateEvaluation(
        uint dstSelect,
        string opcode)
    {
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20;
        descriptor[3] = (9u << 28) | dstSelect;
        var scalarRegisters = new uint[256];
        return new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [new Gen5ImageBinding(0, opcode, control, descriptor, [], null)],
            []);
    }
}
