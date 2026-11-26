// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarDataReadPolicyTests
{
    private static Gen5ShaderProgram BoundsProgram() => Program(
        ScalarLoad(0, 0, 16, 4), ScalarLoad(8, 0, 106, 2, immediateOffset: 16),
        Vop3(16, "VSubF32", 0, Gen5Operand.Scalar(106), Gen5Operand.Scalar(17)),
        Vop3(24, "VSubF32", 1, Gen5Operand.Scalar(107), Gen5Operand.Scalar(18)),
        Vop3(32, "VSubF32", 2, Gen5Operand.Scalar(19), Gen5Operand.Scalar(16)), EndProgram(40));

    private static ShaderResourcePlan Plan(Gen5ShaderProgram program, bool enabled, ShaderStage stage = ShaderStage.Compute) =>
        ShaderResourcePlan.Extract(program, stage, Hash, 0, 2, scalarDataOnDevice: enabled);

    [Fact]
    public void SixBoundsWordsUseOneExactGpuRangeWithoutHostDataReads()
    {
        var program = BoundsProgram();
        var ordinary = Plan(program, false);
        Assert.Equal(6, ordinary.TableReads.Count);
        var plan = Plan(program, true);
        Assert.Empty(plan.TableReads);
        Assert.Empty(plan.FlattenedSlotByMemoryIndex);
        Assert.True(plan.Info.UsesDeviceAddresses);
        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.True(range.Bounded);
        Assert.True(range.Plannable);
        Assert.False(range.Written);
        Assert.Equal(0, range.FirstByte);
        Assert.Equal(24, range.EndByte);
        Assert.All(plan.Memory.Entries, memory => Assert.False(memory.PlanningOnly));

        static bool UnexpectedRead(ulong address, out uint value) => throw new InvalidOperationException($"Host read at {address:X}");
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], UnexpectedRead, UnexpectedRead),
            ref snapshot, ref specialization));
        var actual = Assert.Single(snapshot.DeviceAddressRanges);
        Assert.Equal(0x1000ul, actual.Base);
        Assert.Equal(24ul, actual.Size);
        Assert.True(actual.Planned);
        Assert.False(actual.Written);

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 2),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout) { WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void DescriptorWordsStillUseTheHostPlan()
    {
        var program = Program(ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), EndProgram(16));
        var plan = Plan(program, true);
        Assert.Equal(4, plan.TableReads.Count);
        Assert.Empty(plan.DeviceAddressRanges);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.Single(plan.Info.Buffers);
    }

    [Fact]
    public void BranchPredicatesStillUseTheHostPlan()
    {
        var program = Program(ScalarLoad(0, 0, 4), Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(4), Operand(0)),
            Branch(12, "SCbranchScc1", 1), Vop1(16, "VMovB32", 0, Gen5Operand.Scalar(4)), EndProgram(20));
        var plan = Plan(program, true);
        Assert.Single(plan.TableReads);
        Assert.Empty(plan.DeviceAddressRanges);
    }

    [Fact]
    public void MemoryDependentPointersStillUseTheHostPlan()
    {
        var program = Program(ScalarLoad(0, 0, 4, 2), ScalarLoad(8, 4, 16),
            Vop1(16, "VMovB32", 0, Gen5Operand.Scalar(16)), EndProgram(20));
        var plan = Plan(program, true);
        Assert.Equal(3, plan.TableReads.Count);
        Assert.Empty(plan.DeviceAddressRanges);
    }

    [Fact]
    public void GraphicsKeepsTheOrdinaryPolicy()
    {
        var plan = Plan(BoundsProgram(), true, ShaderStage.Pixel);
        Assert.Equal(6, plan.TableReads.Count);
        Assert.Empty(plan.DeviceAddressRanges);
    }

    [Fact]
    public void ASeparateScalarOffsetKeepsTheOrdinaryPolicy()
    {
        // The existing range planner bounds the instruction's immediate offset, not an extra SGPR offset.
        var program = Program(Sop1(0, "SMovB32", 4, Operand(16)),
            ScalarLoad(4, 0, 16, dynamicOffsetRegister: 4), Vop1(12, "VMovB32", 0, Gen5Operand.Scalar(16)), EndProgram(16));
        var plan = Plan(program, true);
        Assert.Single(plan.TableReads);
        Assert.Empty(plan.DeviceAddressRanges);
    }

    [Fact]
    public void ResidencyIncludesTheFirstDwordOfAnUnalignedScalarRead()
    {
        var plan = Plan(BoundsProgram(), true);
        var range = Assert.Single(DeviceAddressRangePlanner.EvaluateInterpreted(plan, Inputs([0x1003, 0])));
        Assert.Equal(0x1000ul, range.Base);
        Assert.Equal(27ul, range.Size);
        plan.CompileEvaluatorNow();
        Assert.Equal(range, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1003, 0]))));
    }
}
