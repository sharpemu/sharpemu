// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class StableImageBindingTests
{
    private static readonly StableImageDescriptorLimits Limits = new(16, 16, 4, 3, 19);

    private static Gen5ShaderProgram ImageProgram(int count = 16, string opcode = "ImageSampleLz")
    {
        var instructions = new List<Gen5ShaderInstruction> { ScalarLoad(0, 0, 24, 4, count * 32) };
        for (uint index = 0; index < count; index++)
        {
            var pc = 8 + index * 16;
            instructions.Add(ScalarLoad(pc, 0, 16, 8, (int)index * 32));
            instructions.Add(Image(pc + 8, opcode, 16, 24));
        }
        instructions.Add(EndProgram(8 + (uint)count * 16));
        return Program([.. instructions]);
    }

    private static ShaderResourcePlan Plan(StableImageDescriptorLimits limits, ShaderStage stage = ShaderStage.Compute) =>
        ShaderResourcePlan.Extract(ImageProgram(), stage, Hash, 0, 2, stableImageLimits: limits);

    [Theory]
    [InlineData(16u, 4u, 3u, 19u, true)]
    [InlineData(15u, 4u, 3u, 19u, false)]
    [InlineData(16u, 3u, 3u, 19u, false)]
    [InlineData(16u, 4u, 2u, 19u, false)]
    [InlineData(16u, 4u, 3u, 18u, false)]
    public void AdmissionIncludesSamplerCopiesAndAllOrdinaryHelpers(
        uint images, uint samplers, uint buffers, uint resources, bool expected)
    {
        var plan = Plan(new(images, 16, samplers, buffers, resources));
        Assert.Equal(16, plan.Info.Images.Count);
        Assert.Single(plan.Info.Samplers);
        Assert.Equal(expected, plan.StableImageBindings);
    }

    // A graphics stage binds its images one per slot within the budget its caller passes for that stage.
    [Theory]
    [InlineData(ShaderStage.Pixel)]
    [InlineData(ShaderStage.Vertex)]
    public void AGraphicsStageFitsItsOwnBudget(ShaderStage stage)
    {
        Assert.True(Plan(Limits, stage).StableImageBindings);
        Assert.False(Plan(Limits with { SampledImages = 15 }, stage).StableImageBindings);
        Assert.False(Plan(default, stage).StableImageBindings);
    }

    [Fact]
    public void DynamicMipAndIndirectPlansStayCompact()
    {
        var mip = ShaderResourcePlan.Extract(ImageProgram(1, "ImageStoreMip"), ShaderStage.Compute, Hash, 0, 2,
            stableImageLimits: Limits);
        Assert.Equal(ImageMipMode.DynamicStorage, Assert.Single(mip.Info.Images).MipMode);
        Assert.False(mip.StableImageBindings);
        var indirect = ShaderResourcePlan.Extract(ResourceTrackerTests.IndirectImageProgram(false), ShaderStage.Compute,
            Hash, 0, 64, stableImageLimits: new(1024, 1024, 1024, 1024, 4096));
        Assert.NotEmpty(indirect.IndirectImages);
        Assert.False(indirect.StableImageBindings);
    }

    [Fact]
    public void StorageImagesUseTheirOwnLimit()
    {
        var program = ImageProgram(2, "ImageStore");
        // The budget leaves no room for the device-address buffers that device scalar data reads would add.
        ShaderResourcePlan ExtractWith(uint storage) => ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2,
            stableImageLimits: new(0, storage, 0, 3, 5), scalarDataOnDevice: false);
        Assert.True(ExtractWith(2).StableImageBindings);
        Assert.False(ExtractWith(1).StableImageBindings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AliasPartitionChangesOnlySplitCompactSpecializations(bool stable)
    {
        var plan = Plan(stable ? Limits : Limits with { SampledImages = 15 });
        var memory = new TestWordMemory { Words = new uint[0x10000 / 4] };
        uint[][] partitions =
        [
            [0, 1, 2, 1, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2],
            [0, 1, 1, 1, 4, 1, 1, 1, 8, 8, 8, 8, 8, 8, 8, 8],
        ];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var versions = new List<ResourceSpecialization>();
        var modules = new List<byte[]>();
        foreach (var partition in partitions)
        {
            for (var index = 0; index < partition.Length; index++)
            {
                var image = ResourceTrackerTests.ImageDescriptor();
                image[0] += partition[index];
                ResourceTrackerTests.WriteImage(memory, 0x1000 + (ulong)index * 32, image);
            }
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], memory.Read, memory.Read),
                ref snapshot, ref specialization));
            Assert.Equal(stable, specialization.StableImageBindings);
            Assert.Equal(0x20u + partition[4], snapshot.Images[4][0]);
            if (stable) Assert.Empty(specialization.ImageDescriptorGroups);
            else Assert.Equal(partition, specialization.ImageDescriptorGroups);
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(resources.Info, [0, 1], false, true, false);
            Assert.Equal(stable ? 16 : partition.Distinct().Count(), layout.Descriptors
                .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) == ImageResourceClass.Sampled)
                .Sum(binding => binding.Resources.Count));
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
                out var module, out var error), error);
            modules.Add(module.Spirv);
            versions.Add(specialization.Clone());
        }
        Assert.Equal(stable, versions[0].Equals(versions[1]));
        if (stable)
        {
            Assert.Equal(versions[0].GetHashCode(), versions[1].GetHashCode());
            Assert.Equal(modules[0], modules[1]);
            // A semantic image-type change still requires a new specialization.
            memory.At(0x1000 + 32 + 4) = 75u << 20;
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], memory.Read, memory.Read),
                ref snapshot, ref specialization));
            Assert.NotEqual(versions[0], specialization);
            Assert.True(specialization.StableImageBindings);
        }
    }

    [Fact]
    public void GuardAndApplyRejectTheOtherPolicyEvenWithEqualPayloads()
    {
        var compact = Plan(default);
        var stable = Plan(Limits);
        var memory = new TestWordMemory { Words = new uint[0x10000 / 4] };
        for (var index = 0; index < 16; index++)
            ResourceTrackerTests.WriteImage(memory, 0x1000 + (ulong)index * 32, ResourceTrackerTests.ImageDescriptor());
        var inputs = Inputs([0x1000, 0], memory.Read, memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(compact, inputs, ref snapshot, ref specialization));
        var compactSpecialization = specialization.Clone();
        Assert.NotNull(compact.SpecializationGuard);
        stable.SpecializationGuard = compact.SpecializationGuard;
        Assert.True(ResourceMaterializer.Materialize(stable, inputs, ref snapshot, ref specialization));
        Assert.True(specialization.StableImageBindings);
        Assert.Empty(specialization.ImageDescriptorGroups);
        Assert.Throws<ResourcePlanException>(() => ResourceMaterializer.ApplyTo(stable, compactSpecialization));
        Assert.Throws<ResourcePlanException>(() => ResourceMaterializer.ApplyTo(compact, specialization));
        specialization.ImageDescriptorGroups.AddRange(Enumerable.Repeat(0u, 16));
        Assert.Throws<ResourcePlanException>(() => ResourceMaterializer.ApplyTo(stable, specialization));
        Assert.NotEqual(new ResourceSpecialization(), new ResourceSpecialization { StableImageBindings = true });
    }
}
