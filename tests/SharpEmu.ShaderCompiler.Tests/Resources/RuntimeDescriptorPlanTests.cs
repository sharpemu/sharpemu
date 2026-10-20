// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Which image accesses read their descriptors from the shader registers at run time, and which
// keep the planned binding.
public sealed class RuntimeDescriptorPlanTests
{
    private static ShaderResourcePlan Plan(Gen5ShaderProgram program, RuntimeDescriptorMode mode) =>
        ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64, runtimeDescriptors: mode);

    // The T# and S# of each iteration are loaded through a table pointer at an offset the loop advances:
    // no plan-time source exists for either.
    private static Gen5ShaderProgram LoopLoadedDescriptorProgram(string opcode = "ImageSample", uint dimension = 1, bool r128 = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Func<uint, Gen5ShaderInstruction> make)
        {
            var instruction = make(pc);
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        Add(at => ScalarLoad(at, 2, destination: 8, count: 2));
        Add(at => MoveScalar(at, 20, 0));
        var loopHead = pc;
        Add(at => ScalarLoad(at, 8, destination: 16, count: 8, dynamicOffsetRegister: 20));
        Add(at => ScalarLoad(at, 8, destination: 24, count: 4, immediateOffset: 32, dynamicOffsetRegister: 20));
        Add(at => Image(at, opcode, 16, 24, dimension: dimension, r128: r128));
        Add(at => Sop2(at, "SAddU32", 20, Gen5Operand.Scalar(20), Operand(48)));
        Add(at => Branch(at, "SCbranchScc1", (short)((int)loopHead - (int)(at + 4) >> 2)));
        Add(EndProgram);
        return Program([.. instructions]);
    }

    private static MemoryAccessInfo ImageAccess(ShaderResourcePlan plan)
    {
        var access = plan.Memory.Entries.Single(memory => memory.Kind == MemoryResourceKind.Image);
        return access;
    }

    [Fact]
    public void LoopLoadedDescriptor_KeepsTheNullFallbackWhenDisabled()
    {
        var plan = Plan(LoopLoadedDescriptorProgram(), RuntimeDescriptorMode.Disabled);

        Assert.False(plan.Info.UsesRuntimeDescriptors);
        Assert.False(ImageAccess(plan).RuntimeDescriptor);
        // The historical fallback binds a null descriptor for the access.
        Assert.Single(plan.Info.Images);
    }

    [Theory]
    [InlineData(RuntimeDescriptorMode.OnDemand)]
    [InlineData(RuntimeDescriptorMode.Preferred)]
    public void LoopLoadedDescriptor_ReadsItsRegisters(RuntimeDescriptorMode mode)
    {
        var plan = Plan(LoopLoadedDescriptorProgram(), mode);

        var access = ImageAccess(plan);
        Assert.True(access.RuntimeDescriptor);
        Assert.Equal(MemoryAccessInfo.NoResource, access.Resource);
        Assert.True(plan.Info.UsesRuntimeDescriptors);
        Assert.Empty(plan.Info.Images);
        Assert.Empty(plan.Info.Samplers);
        Assert.Empty(plan.Info.SampledPairs);
        // The loads that fill the registers stay in the shader (the loop head is at pc 12).
        Assert.True(plan.Memory.TryGetIndex(12, 0, out var tableLoad));
        Assert.False(plan.Memory[tableLoad].PlanningOnly);
    }

    [Fact]
    public void LoopLoadedImageLoad_ReadsItsRegisters()
    {
        var program = LoopLoadedDescriptorProgram("ImageLoad");
        var plan = Plan(program, RuntimeDescriptorMode.OnDemand);

        Assert.True(ImageAccess(plan).RuntimeDescriptor);
        Assert.Empty(plan.Info.Images);
    }

    [Theory]
    [InlineData("ImageSampleCLz", 1u)]
    [InlineData("ImageSample", 3u)]
    public void CompareAndCubeAccesses_KeepTheNullFallback(string opcode, uint dimension)
    {
        var plan = Plan(LoopLoadedDescriptorProgram(opcode, dimension), RuntimeDescriptorMode.OnDemand);

        Assert.False(ImageAccess(plan).RuntimeDescriptor);
        Assert.False(plan.Info.UsesRuntimeDescriptors);
        Assert.Single(plan.Info.Images);
    }

    [Fact]
    public void Resource128_ReadsItsRegisters()
    {
        var plan = Plan(LoopLoadedDescriptorProgram(r128: true), RuntimeDescriptorMode.OnDemand);

        Assert.True(ImageAccess(plan).RuntimeDescriptor);
        Assert.True(ImageAccess(plan).ImageR128);
        Assert.Empty(plan.Info.Images);
    }

    [Fact]
    public void StorageStore_KeepsThePlannedPath()
    {
        var program = Program(
            [.. Enumerable.Range(0, 8).Select(index => MoveScalar((uint)index * 4, 16 + (uint)index, 0)),
             Vop1(32, "VMovB32", 2, Gen5Operand.Scalar(0)),
             Image(36, "ImageStore", 16),
             EndProgram(44)]);
        var plan = Plan(program, RuntimeDescriptorMode.Preferred);

        Assert.False(plan.Info.UsesRuntimeDescriptors);
        Assert.Single(plan.Info.Images);
    }

    [Theory]
    [InlineData(RuntimeDescriptorMode.Disabled)]
    [InlineData(RuntimeDescriptorMode.OnDemand)]
    [InlineData(RuntimeDescriptorMode.Preferred)]
    public void StaticDescriptors_BindExactlyAsBefore(RuntimeDescriptorMode mode)
    {
        var program = Program(
            Image(0, "ImageSample", 8, 16),
            EndProgram(8));
        var baseline = Plan(program, RuntimeDescriptorMode.Disabled);
        var plan = Plan(program, mode);

        Assert.False(plan.Info.UsesRuntimeDescriptors);
        Assert.False(ImageAccess(plan).RuntimeDescriptor);
        Assert.Equal(baseline.Info.Images.Count, plan.Info.Images.Count);
        Assert.Equal(baseline.Info.Samplers.Count, plan.Info.Samplers.Count);
        Assert.Equal(baseline.Info.SampledPairs.Count, plan.Info.SampledPairs.Count);
        Assert.Equal(ImageAccess(baseline).Resource, ImageAccess(plan).Resource);
    }

    [Fact]
    public void MaterialTable_KeepsItsPlanUntilRuntimeIsPreferred()
    {
        // The key is a lane value: no plan-time source, so only the table plan or the run-time path can serve it.
        var program = ResourceTrackerTests.IndirectImageProgram(malformed: false, laneKey: true);

        var onDemand = Plan(program, RuntimeDescriptorMode.OnDemand);
        Assert.False(onDemand.Info.UsesRuntimeDescriptors);
        Assert.Single(onDemand.IndirectImages);
        Assert.NotNull(onDemand.DescriptorSources[(int)onDemand.Info.Images[0].Source].IndirectImage);

        var preferred = Plan(program, RuntimeDescriptorMode.Preferred);
        Assert.True(preferred.Info.UsesRuntimeDescriptors);
        Assert.Empty(preferred.IndirectImages);
        Assert.Empty(preferred.Info.Images);
        Assert.True(ImageAccess(preferred).RuntimeDescriptor);
        // The loads of the heap record stay in the shader: the lookup reads them from its registers.
        Assert.DoesNotContain(preferred.Memory.Entries, memory => memory.PlanningOnly);
    }

    [Fact]
    public void BindingLayout_AllocatesTheTableAndMissBindingsOnlyWhenUsed()
    {
        var runtimePlan = Plan(LoopLoadedDescriptorProgram(), RuntimeDescriptorMode.OnDemand);
        var runtimeLayout = BindingLayout.Allocate(runtimePlan.Info, [], false, false, false);
        Assert.NotNull(runtimeLayout.Find(DescriptorBindingKind.RuntimeDescriptorTable));
        Assert.NotNull(runtimeLayout.Find(DescriptorBindingKind.RuntimeDescriptorMisses));

        var staticPlan = Plan(Program(Image(0, "ImageSample", 8, 16), EndProgram(8)), RuntimeDescriptorMode.OnDemand);
        var staticInfo = ResourceMaterializer.ApplyTo(staticPlan, ResourceSpecialization.Default(staticPlan.Info)).Info;
        var staticLayout = BindingLayout.Allocate(staticInfo, [], false, false, false);
        Assert.Null(staticLayout.Find(DescriptorBindingKind.RuntimeDescriptorTable));
        Assert.Null(staticLayout.Find(DescriptorBindingKind.RuntimeDescriptorMisses));
    }
}
