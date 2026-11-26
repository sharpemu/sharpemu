// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5StorageImageSwizzleCacheTests
{
    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageStoreMip")]
    public void SpecializationSeparatesStorageImageSwizzles(string opcode)
    {
        var identity = CreateSpecialization(Gen5ShaderTranslator.IdentityImageDstSelect, opcode, runtimeImageSwizzles: false);
        var rotated = CreateSpecialization(0x9F5u, opcode, runtimeImageSwizzles: false);
        Assert.NotEqual(identity, rotated);
        Assert.Equal(Gen5ShaderTranslator.IdentityImageDstSelect, Assert.Single(identity.Images).ShaderSwizzle);
        Assert.Equal(0x9F5u, Assert.Single(rotated.Images).ShaderSwizzle);
    }

    // With the select as shader data, the specialization keeps the identity select for every select.
    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageStoreMip")]
    [InlineData("ImageLoad")]
    public void RuntimeSwizzleSpecializationKeepsTheIdentitySelect(string opcode)
    {
        var identity = CreateSpecialization(Gen5ShaderTranslator.IdentityImageDstSelect, opcode, runtimeImageSwizzles: true);
        var rotated = CreateSpecialization(0x9F5u, opcode, runtimeImageSwizzles: true);
        Assert.Equal(identity, rotated);
        Assert.Equal(Gen5ShaderTranslator.IdentityImageDstSelect, Assert.Single(rotated.Images).ShaderSwizzle);
    }

    private static ResourceSpecialization CreateSpecialization(uint destinationSelect, string opcode, bool runtimeImageSwizzles)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Image(0, opcode, resourceRegister: 8), ResourceTestProgram.EndProgram(8));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, ResourceTestProgram.Hash, 0, 16,
            runtimeImageSwizzles: runtimeImageSwizzles);
        var userData = new uint[16];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = (9u << 28) | destinationSelect;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData),
            ref snapshot, ref specialization, out var failure), failure.ToString());
        return specialization;
    }
}
