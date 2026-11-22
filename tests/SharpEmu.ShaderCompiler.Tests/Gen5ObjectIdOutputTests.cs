// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The ObjPrimID write: a pixel program that exports one target still fills the target the hardware writes
// VGT_OBJECT_ID (and the primitive index) to. The program stores it at entry from shader data.
public sealed class Gen5ObjectIdOutputTests
{
    private const uint OpDecorate = 71;
    private const uint OpCapability = 17;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationFlat = 14;
    private const uint DecorationLocation = 30;
    private const uint BuiltInPrimitiveId = 7;
    private const uint CapabilityTessellation = 3;

    private static List<uint[]> Compile(Gen5PixelObjectId objectId, out BindingLayout layout)
    {
        var program = new Gen5ShaderProgram(0x1_0000_C000, [ResourceTestProgram.EndProgram(0)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, ResourceTestProgram.Hash, 0, 0);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs([]), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        layout = BindingLayout.Allocate(resources.Info, [], false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false,
            usesObjectId: objectId != Gen5PixelObjectId.None);
        var outputs = new List<Gen5PixelOutputBinding> { new(0, 0, Gen5PixelOutputKind.Float) };
        if (objectId != Gen5PixelObjectId.None)
        {
            outputs.Add(new Gen5PixelOutputBinding(7, 1, Gen5PixelOutputKind.Uint) { ExportTarget = 15, ObjectId = objectId });
        }

        var request = new ShaderCompileRequest(plan, resources, layout) { PixelOutputs = outputs };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var instructions = new List<uint[]>();
        for (var index = 20; index + 4 <= shader.Spirv.Length;)
        {
            var word = BitConverter.ToUInt32(shader.Spirv, index);
            var count = (int)(word >> 16);
            var words = new uint[count];
            Buffer.BlockCopy(shader.Spirv, index, words, 0, count * sizeof(uint));
            instructions.Add(words);
            index += count * sizeof(uint);
        }

        return instructions;
    }

    private static bool Decorated(List<uint[]> instructions, uint decoration, uint value) =>
        instructions.Any(words => (words[0] & 0xFFFF) == OpDecorate && words[2] == decoration && words.Length > 3 && words[3] == value);

    [Fact]
    public void TheObjectIdIsStoredToItsOwnLocationWithThePrimitiveIndexAdded()
    {
        var instructions = Compile(Gen5PixelObjectId.ObjectIdAndPrimitiveId, out var layout);

        Assert.True(layout.UsesObjectId);
        Assert.Equal(layout.HeapViewsDword + 1, layout.ShaderDataDwordCount);
        Assert.True(Decorated(instructions, DecorationLocation, 1));
        Assert.True(Decorated(instructions, DecorationBuiltIn, BuiltInPrimitiveId));
        Assert.Contains(instructions, words => (words[0] & 0xFFFF) == OpDecorate && words[2] == DecorationFlat);
        Assert.Contains(instructions, words => (words[0] & 0xFFFF) == OpCapability && words[1] == CapabilityTessellation);
    }

    [Fact]
    public void WithoutThePrimitiveIndexNoPrimitiveIdIsDeclared()
    {
        var instructions = Compile(Gen5PixelObjectId.ObjectId, out _);

        Assert.True(Decorated(instructions, DecorationLocation, 1));
        Assert.False(Decorated(instructions, DecorationBuiltIn, BuiltInPrimitiveId));
    }

    [Fact]
    public void AProgramWithoutTheObjectIdOutputHasNeitherTheDwordNorTheInput()
    {
        var instructions = Compile(Gen5PixelObjectId.None, out var layout);

        Assert.False(layout.UsesObjectId);
        Assert.False(Decorated(instructions, DecorationBuiltIn, BuiltInPrimitiveId));
    }
}
