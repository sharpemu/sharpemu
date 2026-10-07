// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class IndirectSelectorValuesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void SelectorInitializationAfterRetryRequiresRestorationOnEveryEntry(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 32, Gen5Operand.Scalar(126)),
            Vop1(4, "VSinF32", 3, Gen5Operand.Vector(0)),
            Sop1(8, "SMovB64", 32, Gen5Operand.Scalar(126)),
            change == 1 ? Branch(12, "SCbranchScc1", 2) : Nop(12),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(change == 3 ? 34u : 32u)),
            Branch(20, "SCbranchScc1", change == 2 ? (short)2 : (short)-5),
            Vop1(24, "VMovB32", 3, Operand(0)),
            Vopc(28, "VCmpxEqU32", Operand(0), 0),
            Branch(32, "SCbranchExecz", 2),
            Vop2(36, "VCndmaskB32", 3, Operand(1), Operand(2)),
            change == 4 ? Sop1(40, "SMovB64", 32, Gen5Operand.Scalar(34)) : Nop(40),
            Sop1(44, "SMovB64", 126, Gen5Operand.Scalar(32)),
            ReadFirstLane(48, 20, 3), EndProgram(52));
        var plan = Extract(program);
        var selector = plan.Graph.FirstLane(plan.Graph.Undefined(ScalarValueType.U32),
            plan.Graph.Undefined(ScalarValueType.Bool), 48);
        Assert.Equal(expected, IndirectSelectorValues.TryGetConstantValues(plan, selector, out var values));
        if (expected) Assert.Equal(new uint[] { 0, 1, 2 }, values.Order().ToArray());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void SavedMaskOverwriteKillsEarlierUnknownLoopValuesOnlyWhenDominating(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 32, Gen5Operand.Scalar(126)),
            Vop1(4, "VSinF32", 3, Gen5Operand.Vector(0)),
            Branch(8, "SCbranchScc1", -2),
            change == 1 ? Branch(12, "SCbranchScc1", 2) : Nop(12),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(32)),
            change == 4 ? Sop2(20, "SAndSaveexecB64", 40, Gen5Operand.Scalar(42), Gen5Operand.Scalar(126)) :
                Vop1(20, "VMovB32", 3, change == 2 ? Gen5Operand.Vector(0) : Operand(0)),
            Vopc(24, "VCmpxEqU32", Operand(0), 0),
            Branch(28, "SCbranchExecz", 2),
            Vop2(32, "VCndmaskB32", 3, Operand(1), Operand(2)),
            change == 3 ? Sop1(36, "SMovB64", 32, Gen5Operand.Scalar(34)) : Nop(36),
            Sop1(40, "SMovB64", 126, Gen5Operand.Scalar(32)),
            ReadFirstLane(44, 20, 3), EndProgram(48));
        var plan = Extract(program);
        var selector = plan.Graph.FirstLane(plan.Graph.Undefined(ScalarValueType.U32),
            plan.Graph.Undefined(ScalarValueType.Bool), 44);
        Assert.Equal(expected, IndirectSelectorValues.TryGetConstantValues(plan, selector, out var values));
        if (expected) Assert.Equal(new uint[] { 0, 1, 2 }, values.Order().ToArray());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void PackedFormattedStoresRequireStableSupportedFieldsAndDisjointWrites(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)), Vop1(4, "VMovB32", 5, Operand(0)),
            BufferAccess(8, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(20, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u], [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(28, 16, 6), Sop2(32, "SMulI32", 17, Operand(16), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(36, 4, 20, 4, dynamicOffsetRegister: 17),
            BufferAccess(44, "BufferStoreFormatX", 20, vectorData: 4), EndProgram(52));
        var plan = Extract(program, userDataCount: 8);
        Assert.Single(plan.Info.DeviceStoreValidationSources);
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12, 0x3000, 16u << 16, 2, 1u << 12];
        var memory = new TestWordMemory { Base = 0, Words = new uint[0x10000 / 4], RequireAlignment = true };
        memory.At(0x100C) = 1u << 16;
        for (uint candidate = 0; candidate < 2; candidate++)
        {
            var address = 0x3000ul + candidate * 16;
            memory.At(address) = change == 2 ? 0x1000u : 0x8000u + candidate * 0x1000;
            memory.At(address + 4) = change == 4 && candidate == 1 ? 2u << 16 : 1u << 16;
            memory.At(address + 8) = 4 + candidate;
            memory.At(address + 12) = ((change == 1 && candidate == 1 ? 5u : change == 7 ? 18u : 6u) << 12) |
                (change == 6 ? 5u : 4u) | (change == 5 ? 1u << 28 : 0u);
        }
        if (change == 3) memory.FailAddress = 0x301C;
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        Assert.Equal(expected, ResourceMaterializer.Materialize(plan, Inputs(registers, readCleanMemory: memory.Read),
            ref snapshot, ref specialization));
    }

    internal static void AssertPackedPointerEvaluates(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var packed = plan.DescriptorSources[(int)plan.Info.Images[0].Source].PackedPointer;
        Assert.NotNull(packed);
        Assert.True(packed.TryEvaluate(plan, inputs, out _));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PackedPointerSamplerRequiresIdenticalRawWords(bool differs, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)), Vop1(4, "VMovB32", 5, Operand(0)),
            BufferAccess(8, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(20, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(28, 16, 6), Sop2(32, "SMulI32", 17, Operand(16), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(36, 4, 20, 2, dynamicOffsetRegister: 17),
            ScalarLoad(44, 20, 24, 8), ScalarLoad(52, 20, 32, 4, immediateOffset: 32),
            Image(60, "ImageSampleL", 24, 32), EndProgram(68));
        var plan = Extract(program, userDataCount: 8);
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12, 0x3000, 16u << 16, 2, 1u << 12];
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x9000 / 4], RequireAlignment = true };
        memory.At(0x1004) = 1u << 16;
        memory.At(0x100C) = 1u << 16;
        memory.At(0x3000) = 0x5000;
        memory.At(0x3010) = 0x6000;
        memory.At(0x5020) = 0x92;
        memory.At(0x6020) = differs ? 0x93u : 0x92u;
        Assert.Equal(expected, RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Samplers[0].Source,
            Inputs(registers, readCleanMemory: memory.Read), out var result));
        if (expected) Assert.Equal(new uint[] { 0x92, 0, 0, 0 }, result.Dwords);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PackedPointerRejectsAWriteFromAnotherMaterialRecord(bool aliasesOtherRecord, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)),
            Vop1(4, "VMovB32", 5, Operand(0)),
            BufferAccess(8, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(20, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(28, 16, 6),
            Sop2(32, "SMulI32", 17, Operand(32), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(36, 4, 20, 2, dynamicOffsetRegister: 17),
            ScalarLoad(44, 20, 24, 8), Image(52, "ImageLoad", 24),
            ScalarBufferLoad(60, 4, 8, 4, immediateOffset: 8, dynamicOffsetRegister: 17),
            BufferStore(68, 8), EndProgram(76));
        var plan = Extract(program, userDataCount: 8);
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12, 0x3000, 32u << 16, 2, 1u << 12];
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x9000 / 4], RequireAlignment = true };
        memory.At(0x1004) = 1u << 16;
        memory.At(0x100C) = 1u << 16;
        for (uint record = 0; record < 2; record++)
        {
            var address = 0x3000ul + record * 32;
            memory.At(address) = 0x5000 + record * 0x1000;
            memory.At(address + 8) = aliasesOtherRecord && record == 0 ? 0x6000u : 0x8000u + record * 0x1000;
            memory.At(address + 12) = 4u << 16;
            memory.At(address + 16) = 4;
            memory.At(address + 20) = 1u << 12;
            ResourceTrackerTests.WriteImage(memory, 0x5000ul + record * 0x1000, ResourceTrackerTests.ImageDescriptor());
        }
        Assert.Equal(expected, RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Images[0].Source,
            Inputs(registers, readCleanMemory: memory.Read), out _));
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void PackedPointerBindingRequiresIdenticalCandidatesAndDisjointWrites(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)),
            Vop1(4, "VMovB32", 5, Operand(0)),
            BufferAccess(8, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(20, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(28, 16, 6),
            Sop2(32, "SMulI32", 17, Operand(16), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(36, 4, 20, 2, dynamicOffsetRegister: 17),
            ScalarLoad(44, 20, 24, 8),
            Image(52, "ImageLoad", 24),
            change == 5 ? GlobalAccess(60, "GlobalStoreDword", 12) : BufferStore(60, 8),
            EndProgram(68));
        var plan = Extract(program, userDataCount: 16);
        Assert.NotNull(plan.DescriptorSources[(int)plan.Info.Images[0].Source].PackedPointer);
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12,
            0x3000, 16u << 16, 2, 1u << 12,
            change == 3 ? 0x6000u : change == 4 ? 0x1000u : 0x8000u, 4u << 16, 4, 1u << 12,
            0x9000, 0, 0, 0];
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x9000 / 4], RequireAlignment = true };
        memory.At(0x1004) = 1u << 16;
        memory.At(0x100C) = 1u << 16;
        memory.At(0x3000) = 0x5000;
        memory.At(0x3004) = 0;
        memory.At(0x3010) = 0x6000;
        memory.At(0x3014) = 0;
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        if (change == 1) second[0] += 256;
        ResourceTrackerTests.WriteImage(memory, 0x5000, first);
        ResourceTrackerTests.WriteImage(memory, 0x6000, second);
        if (change == 2) memory.FailAddress = 0x600C;
        var source = plan.Info.Images[0].Source;
        Assert.Equal(expected, RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source,
            new ResourceRuntimeInputs
            {
                UserData = registers, ReadMemory = memory.Read, ReadCleanMemory = memory.Read,
                OtherStageMayWriteMemory = change == 6,
            }, out var result));
        if (expected) Assert.Equal(first, result.Dwords);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void PackedDataDomainRequiresCleanRecordBoundedRawMemory(int change, bool expected)
    {
        var plan = Extract(Program(BufferAccess(0, "BufferLoadDwordx2", 0, dwords: 2,
            vectorData: 4, indexEnabled: true), EndProgram(8)), userDataCount: 4);
        var handle = plan.Accesses[0]!.Handle!;
        var domain = new IndirectSelectorValues.PackedBufferWordDomain(handle, 1);
        uint[] registers = [0x1000, 8u << 16, 3, 1u << 12];
        if (change == 1) registers[1] |= 0x80000000;
        if (change == 2) registers[3] |= 1u << 23;
        if (change == 3) registers[3] |= 1u << 28;
        if (change == 4) registers[3] |= 1u << 30;
        if (change == 5) registers[3] = 0;
        if (change == 6) registers[2] = 65537;
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x1004) = 0x0001FFFF;
        memory.At(0x100C) = 0x0002AAAA;
        memory.At(0x1014) = 0x00010000;
        if (change == 7) memory.FailAddress = 0x100C;
        Assert.Equal(expected, domain.TryEvaluate(plan, Inputs(registers, readCleanMemory: memory.Read),
            out var values, out var address, out var length));
        if (expected)
        {
            Assert.Equal(new uint[] { 0, 1, 2 }, values);
            Assert.Equal(0x1000ul, address);
            Assert.Equal(24ul, length);
        }
        else Assert.Empty(values);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void PackedDomainOnlyIgnoresInitializationBypassesThatKeepExecutionEmpty(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(126)),
            change == 2 ? Sop1(4, "SMovB64", 126, Gen5Operand.Scalar(22)) : Nop(4),
            Branch(8, change == 1 ? "SCbranchScc0" : "SCbranchExecz", 4),
            Vop1(12, "VMovB32", 5, Operand(0)),
            BufferAccess(16, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            change == 3 ? Sop1(28, "SMovB64", 126, Gen5Operand.Scalar(22)) :
                Sop1(28, "SMovB64", 126, Gen5Operand.Scalar(20)),
            new(32, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(40, 8, 6), EndProgram(44));
        var plan = Extract(program, userDataCount: 4);
        var selector = plan.Graph.FirstLane(plan.Graph.Undefined(ScalarValueType.U32),
            plan.Graph.Undefined(ScalarValueType.Bool), 40);
        Assert.Equal(expected, IndirectSelectorValues.TryGetPackedBufferWordDomain(plan, selector, out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void PackedLaneDomainAllowsNestedLoopsButRequiresANonemptyStableMask(int change, bool expected)
    {
        var program = Program(
            Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(126)),
            Vop1(4, "VMovB32", 5, Operand(0)),
            BufferAccess(8, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            Sop1(16, "SMovB64", 126, Gen5Operand.Scalar(20)),
            change == 1 ? Nop(20) : Branch(20, "SCbranchExecz", 12),
            new(24, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            Sop1(32, "SMovB64", 22, Gen5Operand.Scalar(change == 5 ? 20u : 126u)),
            Sop1(36, "SFF1I32B64", 24, Gen5Operand.Scalar(22)),
            new(40, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(6), Gen5Operand.Scalar(24), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(8)], new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            Branch(48, "SCbranchScc0", 2), Branch(52, "SBranch", -2),
            change == 2 ? Vop1(56, "VMovB32", 6, Gen5Operand.Vector(0)) : Nop(56),
            change == 3 ? Sop1(60, "SMovB64", 22, Gen5Operand.Scalar(20)) :
                Sop2(60, "SAndn2B64", 22, Gen5Operand.Scalar(22), Gen5Operand.Scalar(26)),
            Sop1(64, "SMovB64", 126, Gen5Operand.Scalar(20)),
            Branch(68, change == 4 ? "SBranch" : "SCbranchScc1", -9), EndProgram(72));
        var plan = Extract(program, userDataCount: 4);
        var selector = plan.Graph.FirstLane(plan.Graph.Undefined(ScalarValueType.U32),
            plan.Graph.Undefined(ScalarValueType.Bool), 40);
        Assert.Equal(expected, IndirectSelectorValues.TryGetPackedBufferWordDomain(plan, selector, out _));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void PackedBufferDomainRequiresInitializationAndContainedExecution(int change, bool expected)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(126)),
            change == 1 ? Nop(4) : Vop1(4, "VMovB32", 5, Operand(0)),
            change == 2 ? Sop1(8, "SMovB64", 126, Gen5Operand.Scalar(22)) : Nop(8),
            BufferAccess(12, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true),
            change == 3 ? Vop1(20, "VMovB32", 5, Gen5Operand.Vector(0)) : Nop(20),
            change == 4 ? Sop1(24, "SMovB64", 20, Gen5Operand.Scalar(22)) : Sop1(24, "SMovB64", 126, Gen5Operand.Scalar(20)),
            new(28, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            change == 5 ? Sop1(36, "SMovB64", 126, Gen5Operand.Scalar(20)) : Nop(36),
            ReadFirstLane(40, 8, 6), EndProgram(44),
        };
        var plan = Extract(Program(instructions.ToArray()), userDataCount: 4);
        var selector = plan.Graph.FirstLane(plan.Graph.Undefined(ScalarValueType.U32),
            plan.Graph.Undefined(ScalarValueType.Bool), 40);
        Assert.Equal(expected, IndirectSelectorValues.TryGetPackedBufferWordDomain(plan, selector, out var domain));
        if (expected) Assert.Equal(1u, domain.Component);
    }

    private static Gen5ShaderProgram CreateProgram(bool expandsExecution = false, bool loadBase = false)
    {
        var original = ResourceTrackerTests.IndirectImageProgram(false);
        var prefix = new List<Gen5ShaderInstruction>();
        if (loadBase) prefix.Add(ScalarLoad(0xF00, 40, 9));
        prefix.Add(Sop1(0xF10, "SFF1I32B32", 41, Gen5Operand.Scalar(8)));
        prefix.Add(Sop2(0xF18, "SLshlB32", 41, Gen5Operand.Scalar(41), Operand(5)));
        prefix.Add(Vop1(0xF20, "VFfblB32", 2, Gen5Operand.Vector(0)));
        prefix.Add(Vop3(0x1000, "VAdd3U32", 1, Gen5Operand.Scalar(9), Gen5Operand.Scalar(41), Gen5Operand.Vector(2)));
        if (expandsExecution)
            prefix.Add(Sop1(0x1004, "SMovB64", 126, Gen5Operand.Scalar(42)));
        return Program([.. prefix, .. original.Instructions.Skip(1)]);
    }

    private static IndirectSelectorValues? Selector(ShaderResourcePlan plan) =>
        plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.SelectorValues;

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(115043767u)]
    public void BitScanProofIncludesEveryResultAndWrappedSum(uint baseIndex)
    {
        var plan = Extract(CreateProgram());
        var registers = new uint[64];
        registers[9] = baseIndex;
        var selector = Assert.IsType<IndirectSelectorValues>(Selector(plan));
        Assert.True(selector.TryEvaluate(plan, Inputs(registers), out var values));
        var expected = new HashSet<uint>();
        foreach (var first in Enumerable.Range(0, 32).Select(value => (uint)value).Append(uint.MaxValue))
            foreach (var second in Enumerable.Range(0, 32).Select(value => (uint)value).Append(uint.MaxValue))
                expected.Add(unchecked(baseIndex + (first << 5) + second));
        Assert.Equal(expected.Order(), values.Order());
    }

    [Fact]
    public void ExecutionExpansionDeclinesTheProof()
    {
        Assert.Null(Selector(Extract(CreateProgram(expandsExecution: true))));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(0xFF000003u)]
    [InlineData(0x00FFFFFFu)]
    public void Unsigned24BitMultiplyMasksBothOperandsAndWrapsTheProduct(uint multiplier)
    {
        var original = CreateProgram();
        var program = Program(original.Instructions.Select(instruction => instruction.Pc == 0x1000
            ? Vop3(0x1000, "VMulU32U24", 1, Operand(multiplier), Gen5Operand.Vector(2))
            : instruction).ToArray());
        var plan = Extract(program);
        var selector = Assert.IsType<IndirectSelectorValues>(Selector(plan));
        Assert.True(selector.TryEvaluate(plan, Inputs(new uint[64]), out var values));
        var expected = Enumerable.Range(0, 32).Select(value => (uint)value).Append(uint.MaxValue)
            .Select(value => unchecked((multiplier & 0x00FFFFFF) * (value & 0x00FFFFFF))).Distinct().Order();
        Assert.Equal(expected, values.Order());
    }

    [Fact]
    public void PathWithoutTheVectorDefinitionDeclinesTheProof()
    {
        var original = CreateProgram();
        var program = Program([Branch(0xF00, "SCbranchScc0", 65), .. original.Instructions]);
        Assert.Null(Selector(Extract(program)));
    }

    [Fact]
    public void OverlappingWideWriteDeclinesTheProof()
    {
        var original = CreateProgram();
        var instructions = original.Instructions.ToList();
        instructions.Add(Sop1(0xF28, "SMovB64", 8, Gen5Operand.Scalar(42)));
        Assert.Null(Selector(Extract(Program([.. instructions.OrderBy(instruction => instruction.Pc)]))));
    }

    [Fact]
    public void RuntimeBaseUsesTheCleanReaderAndDeclinesOnFailure()
    {
        var plan = Extract(CreateProgram(loadBase: true));
        var selector = Assert.IsType<IndirectSelectorValues>(Selector(plan));
        var registers = new uint[64];
        registers[40] = 0x3000;
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x3000) = 100;
        Assert.True(selector.TryEvaluate(plan, Inputs(registers, readCleanMemory: memory.Read), out var values));
        Assert.Contains(100u, values);
        memory.FailAddress = 0x3000;
        Assert.False(selector.TryEvaluate(plan, Inputs(registers, readCleanMemory: memory.Read), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticPreservesRuntimeReadsAndEvaluation(bool failRead)
    {
        var plan = Extract(CreateProgram(loadBase: true));
        var selector = Assert.IsType<IndirectSelectorValues>(Selector(plan));
        var registers = new uint[64];
        registers[40] = 0x3000;
        var addresses = new List<ulong>();
        bool ReadWord(ulong address, out uint word)
        {
            addresses.Add(address);
            word = 100;
            return !failRead;
        }
        var inputs = Inputs(registers, readCleanMemory: ReadWord);
        var baseline = selector.TryEvaluate(plan, inputs, out var baselineValues);
        var baselineAddresses = addresses.ToArray();
        addresses.Clear();
        var diagnostic = new IndirectSelectorDiagnostic();
        Assert.Equal(baseline, selector.TryEvaluate(plan, inputs, out var capturedValues, diagnostic));
        Assert.Equal(baselineValues, capturedValues);
        Assert.Equal(baselineAddresses, addresses);
        var read = Assert.Single(diagnostic.MemoryReads);
        Assert.Equal(0x3000ul, read.Address);
        Assert.Equal(!failRead, read.Succeeded);
        Assert.Equal(failRead ? (uint?)null : 100u, read.Value);
        Assert.Equal(failRead ? 0x3000ul : (ulong?)null, diagnostic.FailedReadAddress);
        Assert.Equal(failRead ? "runtime_value_unavailable" : null, diagnostic.EvaluationFailure);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(115043767u, false)]
    public void MaterializationSkipsOnlyProvenUnreachableFields(uint baseIndex, bool expectedSuccess)
    {
        var plan = Extract(CreateProgram());
        uint[] registers = new uint[64];
        new uint[] { 0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0 }.CopyTo(registers, 0);
        registers[9] = baseIndex;
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x1000 + 36) = 1;
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        second[3] = (second[3] & 0x0FFFFFFF) | (10u << 28);
        ResourceTrackerTests.WriteImage(memory, 0x2000, first);
        ResourceTrackerTests.WriteImage(memory, 0x2020, second);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var captures = new List<IndirectImageFailure>();
        Assert.Equal(expectedSuccess, ResourceMaterializer.Materialize(plan,
            Inputs(registers, readCleanMemory: memory.Read), ref snapshot, ref specialization, captures.Add));
        if (expectedSuccess)
        {
            Assert.Empty(captures);
        }
        else
        {
            var diagnostic = Assert.IsType<IndirectSelectorDiagnostic>(Assert.Single(captures).SelectorDiagnostic);
            Assert.Equal("bounded", diagnostic.SelectionMode);
            Assert.Null(diagnostic.EvaluationFailure);
            Assert.Contains(baseIndex, diagnostic.SelectorIndices);
            Assert.Contains(36u, diagnostic.ProvenOffsets);
            Assert.Equal(new SelectorKeyProbe(36, 1), Assert.Single(diagnostic.KeyProbes));
        }
    }

    [Fact]
    public void FailedRuntimeEvaluationCapturesFullDomainFallback()
    {
        var plan = Extract(CreateProgram(loadBase: true));
        var registers = new uint[64];
        new uint[] { 0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0 }.CopyTo(registers, 0);
        registers[40] = 0x3000;
        var memory = ResourceTrackerTests.LinearMemory();
        bool ReadWord(ulong address, out uint word)
        {
            word = 0;
            if (address == 0x3000) return false;
            return memory.Read(address, out word);
        }
        memory.At(0x1000 + 36) = 1;
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        second[3] = (second[3] & 0x0FFFFFFF) | (10u << 28);
        ResourceTrackerTests.WriteImage(memory, 0x2000, first);
        ResourceTrackerTests.WriteImage(memory, 0x2020, second);
        var captures = new List<IndirectImageFailure>();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(registers, readMemory: memory.Read, readCleanMemory: ReadWord),
            ref snapshot, ref specialization, captures.Add));
        var diagnostic = Assert.IsType<IndirectSelectorDiagnostic>(Assert.Single(captures).SelectorDiagnostic);
        Assert.Equal("full_domain_evaluation_failed", diagnostic.SelectionMode);
        Assert.Equal("runtime_value_unavailable", diagnostic.EvaluationFailure);
        Assert.Equal(0x3000ul, diagnostic.FailedReadAddress);
        Assert.Empty(diagnostic.SelectorIndices);
        Assert.Empty(diagnostic.ProvenOffsets);
    }
}
