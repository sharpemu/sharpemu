// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class RuntimeDescriptorEmissionTests
{
    // A sample whose sampler words come from a lane-dependent value: no plan-time source.
    private static Gen5ShaderProgram RuntimeSamplerProgram() => Program(
        MoveScalarRegister(0, 16, 0),
        MoveScalarRegister(4, 17, 1),
        MoveScalarRegister(8, 18, 2),
        Vop2(12, "VLshlrevB32", 1, Operand(12), Gen5Operand.Vector(0)),
        ReadFirstLane(16, 20, 1),
        Sop2(20, "SOrB32", 19, Gen5Operand.Scalar(3), Gen5Operand.Scalar(20)),
        Image(0x200, "ImageSample", 8, 16),
        EndProgram(0x208));

    [Fact]
    public void RuntimeDescriptorSample_EmitsValidHeapLookups()
    {
        var plan = Extract(RuntimeSamplerProgram());
        Assert.True(plan.Info.UsesRuntimeDescriptors);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false,
            usesBindlessImages: true);
        Assert.NotNull(layout.Find(DescriptorBindingKind.RuntimeDescriptorTable));
        Assert.NotNull(layout.Find(DescriptorBindingKind.RuntimeDescriptorMisses));

        var request = new ShaderCompileRequest(plan, resources, layout) { ThreadCountX = 1, ThreadCountY = 1, ThreadCountZ = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
    }

    // Runtime descriptors index the persistent heap, so a host without it rejects the layout.
    [Fact]
    public void RuntimeDescriptors_NeedTheBindlessHeap()
    {
        var plan = Extract(RuntimeSamplerProgram());
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var error = Assert.Throws<ResourcePlanException>(() => BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false));
        Assert.Contains("bindless heap", error.Message);
    }

    // The host hashes keys exactly like the shader: FNV-1a over the dwords.
    [Fact]
    public void Hash_IsFnv1aOverTheKeyDwords()
    {
        Assert.Equal(RuntimeDescriptorTable.HashSeed, RuntimeDescriptorTable.Hash([]));
        var expected = unchecked(((RuntimeDescriptorTable.HashSeed ^ 1u) * RuntimeDescriptorTable.HashPrime ^ 2u) * RuntimeDescriptorTable.HashPrime);
        Assert.Equal(expected, RuntimeDescriptorTable.Hash([1u, 2u]));
    }

    // The host's table answers the shader's probe for every key, through collisions.
    [Fact]
    public void BuiltTable_AnswersTheShadersProbeForEveryKey()
    {
        var images = Enumerable.Range(0, 200)
            .Select(index => (Key: new uint[] { 3, (uint)index * 7919, 0xABCD, 0, 0, (uint)index, 0, 0, 0 }, Slot: (uint)index + 1))
            .ToList();
        var samplers = Enumerable.Range(0, 40)
            .Select(index => (Key: new uint[] { (uint)index, 0x1234, 0, 1 }, Slot: (uint)index + 1))
            .ToList();
        var table = RuntimeDescriptorTable.Build(images, samplers);
        foreach (var (key, slot) in images)
        {
            Assert.Equal(slot + 1, Probe(table, key, image: true));
        }

        foreach (var (key, slot) in samplers)
        {
            Assert.Equal(slot + 1, Probe(table, key, image: false));
        }

        Assert.Equal(0u, Probe(table, [3, 1, 2, 3, 4, 5, 6, 7, 8], image: true));
        Assert.Equal(0u, Probe(RuntimeDescriptorTable.Build([], []), [1, 2, 3, 4], image: false));
    }

    // The lookup the translator emits (LookupRuntimeDescriptor), over the table words: slot + 1, 0 when missing.
    private static uint Probe(uint[] table, uint[] key, bool image)
    {
        uint Word(uint index) => index < table.Length ? table[index] : 0;
        var capacity = Word(image ? RuntimeDescriptorTable.ImageCapacityDword : RuntimeDescriptorTable.SamplerCapacityDword);
        var entries = Word(image ? RuntimeDescriptorTable.ImageEntriesDword : RuntimeDescriptorTable.SamplerEntriesDword);
        var entryDwords = image ? RuntimeDescriptorTable.ImageEntryDwords : RuntimeDescriptorTable.SamplerEntryDwords;
        if (capacity == 0) return 0;
        var hash = RuntimeDescriptorTable.Hash(key);
        for (uint probe = 0; probe < RuntimeDescriptorTable.ProbeCount; probe++)
        {
            var entry = entries + ((hash + probe) & (capacity - 1)) * entryDwords;
            var stored = Word(entry);
            if (stored != 0 && key.Select((word, index) => Word(entry + 1 + (uint)index) == word).All(match => match))
            {
                return stored;
            }
        }

        return 0;
    }
}
