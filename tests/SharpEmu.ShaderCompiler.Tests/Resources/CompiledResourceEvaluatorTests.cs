// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class CompiledResourceEvaluatorTests
{
    [Fact]
    public void UniformOperationsMatchTheInterpreterWithFullWidthOperands()
    {
        ulong[] cases = [0, 1, 31, 32, 63, 64, uint.MaxValue, 0x80000000, 0x7FC00000, 0xFFFFFFFFFFFFFFFF, 0x8000000000000000];
        foreach (var operation in Enum.GetValues<ScalarOperation>().Where(RuntimeValueValidator.IsUniformOperation))
        {
            var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)), userDataCount: 8);
            var operands = Enumerable.Range(0, 4).Select(index => ScalarValue.MakeOperation(ScalarOperation.Construct64, ScalarValueType.U64,
                ScalarValue.UserData((uint)(index * 2)), ScalarValue.UserData((uint)(index * 2 + 1)))).ToArray();
            var value = ScalarValue.MakeOperation(operation, ScalarValueType.U64, operands);
            plan.DescriptorSources[0].Dwords[0] = value;
            Assert.NotNull(plan.CompileEvaluatorNow());
            Assert.True(plan.CompiledEvaluator.TryGetIndex(value, out _));
            for (var iteration = 0; iteration < cases.Length; iteration++)
            {
                var input = Enumerable.Range(0, 4).Select(index => cases[(iteration + index * 3) % cases.Length]).ToArray();
                uint[] registers = input.SelectMany(word => new[] { (uint)word, (uint)(word >> 32) }).ToArray();
                using var compiledScratch = RuntimeEvaluationScratch.Rent();
                using var interpretedScratch = RuntimeEvaluationScratch.Rent();
                var compiled = new RuntimeValueEvaluator(compiledScratch, plan, Inputs(registers));
                var interpreted = new RuntimeValueEvaluator(interpretedScratch, plan, Inputs(registers), useCompiled: false);
                var expectedOk = interpreted.EvaluateWide(value, out var expected);
                var actualOk = compiled.EvaluateWide(value, out var actual);
                Assert.Equal(expectedOk, actualOk);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void ActiveLaneSkipsAnUndefinedArmWithoutChangingTheOrdinaryCache()
    {
        var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)));
        var mask = ScalarValue.UserData(0);
        var select = ScalarValue.Select(mask, ScalarValue.UserData(1), ScalarValue.Undefined(ScalarValueType.U32));
        var lane = ScalarValue.FirstLane(select, mask);
        plan.DescriptorSources[0].Dwords[0] = lane;
        Assert.NotNull(plan.CompileEvaluatorNow());
        Assert.True(plan.CompiledEvaluator.TryGetIndex(lane, out _));
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, Inputs([0, 42]));
        Assert.False(evaluator.Evaluate(select, out _));
        Assert.True(evaluator.Evaluate(lane, out var result));
        Assert.Equal(42u, result);
        Assert.False(evaluator.Evaluate(select, out _));
    }

    [Fact]
    public void CompiledAndInterpretedTableReadsHaveIdenticalOrderAndFailure()
    {
        var plan = Extract(Program(ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), EndProgram(16)));
        Assert.NotNull(plan.CompileEvaluatorNow());
        foreach (var failAt in new ulong[] { 0, 0x1000, 0x1008 })
        {
            var reads = new List<ulong>();
            bool Read(ulong address, out uint word)
            {
                reads.Add(address);
                word = (uint)address;
                return address != failAt;
            }
            using var compiledScratch = RuntimeEvaluationScratch.Rent();
            using var interpretedScratch = RuntimeEvaluationScratch.Rent();
            var compiled = new RuntimeValueEvaluator(compiledScratch, plan, Inputs([0x1000, 0], Read));
            var interpreted = new RuntimeValueEvaluator(interpretedScratch, plan, Inputs([0x1000, 0], Read), useCompiled: false);
            var actual = plan.DescriptorSources[0].Dwords.Select(value => (compiled.EvaluateWide(value, out var word), word)).ToArray();
            var actualReads = reads.ToArray();
            reads.Clear();
            var expected = plan.DescriptorSources[0].Dwords.Select(value => (interpreted.EvaluateWide(value, out var word), word)).ToArray();
            Assert.Equal(expected, actual);
            Assert.Equal(reads, actualReads);
        }
    }
}
