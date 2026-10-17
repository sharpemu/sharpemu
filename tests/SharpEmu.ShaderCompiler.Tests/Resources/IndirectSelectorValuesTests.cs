// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class IndirectSelectorValuesTests
{
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
