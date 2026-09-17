// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class RuntimeEvaluationScratchTests
{
    private static ShaderResourcePlan BufferPlan() => Extract(Program(
        ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), EndProgram(16)));

    private static ResourceSnapshot Materialize(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        return snapshot;
    }

    [Fact]
    public void ReturnedStorageIsClearedAndReused()
    {
        var scratch = RuntimeEvaluationScratch.Rent();
        var value = ScalarValue.UserData(0);
        scratch.Values.Add(value, 42);
        scratch.Visiting.Add(value);
        var capacity = scratch.Values.EnsureCapacity(0);
        scratch.Dispose();
        using var reused = RuntimeEvaluationScratch.Rent();
        Assert.Same(scratch, reused);
        Assert.Empty(reused.Values);
        Assert.Empty(reused.Visiting);
        Assert.Equal(capacity, reused.Values.EnsureCapacity(0));
        using var nested = RuntimeEvaluationScratch.Rent();
        Assert.NotSame(reused, nested);
    }

    [Fact]
    public void ChangedInputsAndMemoryDoNotAlterAnEarlierSnapshot()
    {
        var plan = BufferPlan();
        var memory = new TestWordMemory { Words = [0x2000, 0, 256, 0] };
        uint[] userData = [0x1000, 0];
        var first = Materialize(plan, Inputs(userData, memory.Read));
        memory.Words[0] = 0x3000;
        var second = Materialize(plan, Inputs(userData, memory.Read));
        Assert.Equal(0x2000u, first.Buffers[0][0]);
        Assert.Equal(0x3000u, second.Buffers[0][0]);
        Assert.NotSame(first.Buffers[0], second.Buffers[0]);
        Assert.NotSame(first.FlattenedResourceTable, second.FlattenedResourceTable);
        Assert.Equal(0x2000u, first.FlattenedResourceTable[0]);
        Assert.Equal(0x3000u, second.FlattenedResourceTable[0]);
        second.FlattenedResourceTable[0] = 0x9000;
        second.Buffers[0][0] = 0x9000;
        Assert.Equal(0x2000u, first.FlattenedResourceTable[0]);
        Assert.Equal(0x2000u, first.Buffers[0][0]);
        userData[0] = 0x4000;
        var otherMemory = new TestWordMemory { Base = 0x4000, Words = [0x5000, 0, 512, 0] };
        var third = Materialize(plan, Inputs(userData, otherMemory.Read));
        Assert.Equal(0x5000u, third.Buffers[0][0]);
        Assert.Equal(0x1000u, first.UserData[0]);
        Assert.Equal(0x4000u, third.UserData[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrThrowingReaderDoesNotPoisonTheNextCall(bool throws)
    {
        var plan = BufferPlan();
        var memory = new TestWordMemory { Words = [0x2000, 0, 256, 0] };
        var snapshot = Materialize(plan, Inputs([0x1000, 0], memory.Read));
        var original = snapshot;
        var specialization = new ResourceSpecialization();
        var originalSpecialization = specialization;
        bool FailedRead(ulong address, out uint word)
        {
            if (address == 0x1008)
            {
                if (throws) throw new InvalidOperationException("Test reader failure.");
                word = 0;
                return false;
            }
            return memory.Read(address, out word);
        }
        if (throws)
            Assert.Throws<InvalidOperationException>(() => ResourceMaterializer.Materialize(plan,
                Inputs([0x1000, 0], FailedRead), ref snapshot, ref specialization));
        else
            Assert.False(ResourceMaterializer.Materialize(plan,
                Inputs([0x1000, 0], FailedRead), ref snapshot, ref specialization));
        Assert.Same(original, snapshot);
        Assert.Same(originalSpecialization, specialization);
        memory.Words[0] = 0x6000;
        Assert.Equal(0x6000u, Materialize(plan, Inputs([0x1000, 0], memory.Read)).Buffers[0][0]);
    }

    [Fact]
    public void ReaderCanMaterializeAnotherDrawWithoutChangingTheOuterValues()
    {
        var plan = BufferPlan();
        var outerMemory = new TestWordMemory { Words = [0x2000, 0, 256, 0] };
        var innerMemory = new TestWordMemory { Words = [0x7000, 0, 512, 0] };
        var nestedCalls = 0;
        ResourceSnapshot? innerSnapshot = null;
        bool Read(ulong address, out uint word)
        {
            innerSnapshot = Materialize(plan, Inputs([0x1000, 0], innerMemory.Read));
            Assert.Equal(0x7000u, innerSnapshot.Buffers[0][0]);
            nestedCalls++;
            return outerMemory.Read(address, out word);
        }
        var outerSnapshot = Materialize(plan, Inputs([0x1000, 0], Read));
        Assert.Equal(0x2000u, outerSnapshot.Buffers[0][0]);
        Assert.True(nestedCalls > 0);
        Assert.NotNull(innerSnapshot);
        innerSnapshot.FlattenedResourceTable[0] = 0x9000;
        Assert.Equal(0x2000u, outerSnapshot.FlattenedResourceTable[0]);
    }

    [Fact]
    public void CleanAndOrdinaryReadersNeverShareComputedValues()
    {
        var plan = BufferPlan();
        var ordinary = new TestWordMemory { Words = [0x2000, 0, 256, 0] };
        var clean = new TestWordMemory { Words = [0x8000, 0, 512, 0] };
        var inputs = Inputs([0x1000, 0], ordinary.Read, clean.Read);
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources,
            inputs, [1, 1, 1, 1], true, out var cleanResults, out _));
        Assert.Equal(0x8000u, cleanResults[0].Dwords[0]);
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources,
            inputs, [], true, out var ordinaryResults, out _));
        Assert.Equal(0x2000u, ordinaryResults[0].Dwords[0]);
        clean.FailAddress = 0x1000;
        Assert.False(RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources,
            inputs, [1, 1, 1, 1], true, out _, out _));
    }

    [Fact]
    public void FirstLaneEvaluationKeepsItsActiveMaskSeparate()
    {
        var plan = BufferPlan();
        var mask = ScalarValue.UserData(0);
        var selected = ScalarValue.Select(mask, ScalarValue.ConstantOf(11u), ScalarValue.ConstantOf(22u));
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, Inputs([0u]));
        Assert.True(evaluator.Evaluate(selected, out var ordinary));
        Assert.Equal(22u, ordinary);
        Assert.True(evaluator.Evaluate(ScalarValue.FirstLane(selected, mask), out var active));
        Assert.Equal(11u, active);
        Assert.True(evaluator.Evaluate(selected, out ordinary));
        Assert.Equal(22u, ordinary);
    }

    [Fact]
    public void ConcurrentCallsOnOnePlanKeepTheirOwnInputs()
    {
        var plan = BufferPlan();
        Parallel.For(0, 128, iteration =>
        {
            var expected = (uint)(0x2000 + iteration * 256);
            var memory = new TestWordMemory { Words = [expected, 0, 256, 0] };
            for (var draw = 0; draw < 8; draw++)
                Assert.Equal(expected, Materialize(plan, Inputs([0x1000, 0], memory.Read)).Buffers[0][0]);
        });
    }
}
