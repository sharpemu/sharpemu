// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DescriptorBindingAliasTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(96)]
    public void SourceTableSharesBindingsAndSeparatesChangedDescriptors(int descriptorCount)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint index = 0; index < descriptorCount; index++)
        {
            var pc = index * 24;
            instructions.Add(ScalarLoad(pc, 0, 16, 8, (int)index * 48));
            instructions.Add(ScalarLoad(pc + 8, 0, 24, 4, (int)index * 48 + 32));
            instructions.Add(Image(pc + 16, "ImageSampleLz", 16, 24));
        }
        instructions.Add(EndProgram((uint)descriptorCount * 24));
        var plan = Extract(Program([.. instructions]), userDataCount: 2);
        Assert.Equal(descriptorCount, plan.Info.Images.Count);
        Assert.Equal(descriptorCount, plan.Info.Samplers.Count);
        var memory = new TestWordMemory { Words = new uint[0x10000 / 4] };
        for (var index = 0; index < descriptorCount; index++)
            ResourceTrackerTests.WriteImage(memory, 0x1000 + (ulong)index * 48, ResourceTrackerTests.ImageDescriptor());
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], memory.Read, memory.Read), ref snapshot, ref specialization));
        var original = specialization.Clone();
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [], false, true, false);
        Assert.Single(layout.Descriptors.Single(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) == ImageResourceClass.Sampled).Resources);
        Assert.Single(layout.Find(DescriptorBindingKind.Samplers)!.Resources);
        Assert.All(resources.Info.ImageBindings, binding => Assert.Equal(0u, binding));
        Assert.Equal(original, specialization);
        Assert.Equal(original.GetHashCode(), specialization.GetHashCode());
        Compile(plan, resources);

        var lastDescriptor = (uint)descriptorCount - 1;
        memory.At(0x1000 + lastDescriptor * 48) += 1;
        memory.At(0x1000 + lastDescriptor * 48 + 32) = 1;
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], memory.Read, memory.Read), ref snapshot, ref specialization));
        Assert.NotEqual(original, specialization);
        resources = ResourceMaterializer.ApplyTo(plan, specialization);
        layout = BindingLayout.Allocate(resources.Info, [], false, true, false);
        Assert.Equal(2, layout.Descriptors.Single(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) == ImageResourceClass.Sampled).Resources.Count);
        Assert.Equal(new uint[] { 0, lastDescriptor }, layout.Find(DescriptorBindingKind.Samplers)!.Resources);
        Assert.Equal(lastDescriptor, resources.Info.GetCanonicalImageBinding(lastDescriptor));
        Compile(plan, resources);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(96)]
    public void MixedDepthSamplersKeepSnapshotCopiesBeyondTheFormerLimit(int descriptorCount)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint index = 0; index < descriptorCount; index++)
        {
            var pc = index * 32;
            instructions.Add(ScalarLoad(pc, 0, 16, 8, (int)index * 48));
            instructions.Add(ScalarLoad(pc + 8, 0, 24, 4, (int)index * 48 + 32));
            instructions.Add(Image(pc + 16, "ImageSampleLz", 16, 24));
            instructions.Add(Image(pc + 24, "ImageSampleCLz", 16, 24));
        }
        instructions.Add(EndProgram((uint)descriptorCount * 32));
        var plan = Extract(Program([.. instructions]), userDataCount: 2);
        Assert.Equal(descriptorCount, plan.Info.Samplers.Count);
        var memory = new TestWordMemory { Words = new uint[0x10000 / 4] };
        for (var index = 0; index < descriptorCount; index++)
        {
            var address = 0x1000 + (ulong)index * 48;
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[1] = 22u << 20;
            ResourceTrackerTests.WriteImage(memory, address, descriptor);
            memory.At(address + 32) = (uint)index;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], memory.Read, memory.Read),
            ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(descriptorCount * 2, snapshot.Samplers.Length);
        Assert.Equal(snapshot.Samplers.Length, resources.Info.Samplers.Count);
        for (var index = 0; index < descriptorCount; index++)
        {
            Assert.Equal((uint)index, snapshot.Samplers[index][0]);
            Assert.Equal(snapshot.Samplers[index], snapshot.Samplers[index + descriptorCount]);
            Assert.False(resources.Info.Samplers[index].DepthCompare);
            Assert.True(resources.Info.Samplers[index + descriptorCount].DepthCompare);
        }
    }

    private static void Compile(ShaderResourcePlan plan, SpecializedResourceInfo resources)
    {
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 2), false, true, false);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Fact]
    public void EqualWordsKeepDifferentViewsAndSamplerModesSeparate()
    {
        var source = new ShaderResourceInfo
        {
            Images = [new() { ResourceClass = ImageResourceClass.Sampled }, new() { ResourceClass = ImageResourceClass.Storage }],
            Samplers = [new() { Source = 0 }, new() { Source = 1 }],
        };
        var info = source.Clone();
        info.Samplers[1].DepthCompare = true;
        var defaults = ResourceSpecialization.Default(source);
        var specialization = new ResourceSpecialization
        {
            Images = defaults.Images,
            ImageDescriptorGroups = [0, 0], SamplerDescriptorGroups = [0, 0],
        };
        DescriptorBindingAliases.Apply(info, source, specialization);
        Assert.Equal(new uint[] { 0, 1 }, info.ImageBindings);
        Assert.Equal(new uint[] { 0, 1 }, info.SamplerBindings);
    }
}
