// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class CompiledResourceTransactionTests
{
    private static ShaderResourcePlan CreatePlan(int count, bool nested)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        if (nested)
        {
            instructions.Add(ScalarLoad(pc, 0, 10, 2));
            pc += 8;
        }
        instructions.Add(ScalarLoad(pc, nested ? 10u : 0u, 4, 4, 2048));
        pc += 8;
        for (var index = 0; index < count; index++)
        {
            instructions.Add(ScalarLoad(pc, nested ? 10u : 0u, 16, immediateOffset: 64 + index * 4));
            pc += 8;
            instructions.Add(MoveVectorFromScalar(pc, 4, 16));
            pc += 4;
            instructions.Add(BufferStore(pc, 4, index * 4));
            pc += 8;
        }
        instructions.Add(EndProgram(pc));
        return Extract(Program([.. instructions]), userDataCount: 2);
    }

    private static bool Reference(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        out List<DescriptorWords> descriptors, out uint[] table) =>
        RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources.ToArray(), inputs,
            plan.CleanFlatSlots, true, out descriptors, out table);

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(128, false)]
    [InlineData(128, true)]
    public void GroupedReadsPreserveOutputsOrderFailureAndLifetime(int count, bool nested)
    {
        var plan = CreatePlan(count, nested);
        plan.CompileEvaluatorNow();
        var transaction = Assert.IsType<CompiledResourceTransaction>(plan.CompiledTransaction);
        var memory = new Memory();
        Assert.True(Reference(plan, memory.Inputs(false), out var expectedDescriptors, out var expectedTable));
        var expectedTrace = memory.Trace.ToArray();
        memory.Trace.Clear();
        Assert.True(transaction.Evaluate(memory.Inputs(true), 3, out var descriptors, out var table));
        Assert.Equal(expectedDescriptors.SelectMany(words => words.Dwords), descriptors.SelectMany(words => words.Dwords));
        Assert.Equal(expectedTable, table[..^3]);
        Assert.Equal(new uint[3], table[^3..]);
        Assert.Equal(expectedTrace, memory.Trace);
        Assert.True(memory.SpanCalls > 0);
        Assert.InRange(memory.MaximumSpanWords, 2, 16);
        var savedTable = table.ToArray();
        var savedDescriptors = descriptors.SelectMany(words => words.Dwords).ToArray();
        memory.Salt++;
        Assert.True(transaction.Evaluate(memory.Inputs(true), 0, out _, out var changed));
        Assert.NotEqual(savedTable[^4], changed[^1]);
        Assert.Equal(savedTable, table);
        Assert.Equal(savedDescriptors, descriptors.SelectMany(words => words.Dwords));

        foreach (var address in expectedTrace.Where(item => item.StartsWith("R:")).Select(item => ulong.Parse(item.Split(':')[1])).Distinct())
        {
            memory.FailureAddress = address;
            memory.Trace.Clear();
            Assert.False(Reference(plan, memory.Inputs(false), out _, out _));
            var failureTrace = memory.Trace.ToArray();
            memory.Trace.Clear();
            memory.RefuseSpan = true;
            Assert.False(transaction.Evaluate(memory.Inputs(true), 0, out var failedDescriptors, out var failedTable));
            Assert.Empty(failedDescriptors);
            Assert.Empty(failedTable);
            Assert.Equal(failureTrace, memory.Trace);
            Assert.False(memory.InTable);
        }
    }

    [Fact]
    public void CleanReadsRemainSeparateAndCannotFallBackToGeneralReads()
    {
        var plan = CreatePlan(4, true);
        Array.Fill((byte[])plan.CleanFlatSlots, (byte)1);
        var transaction = Assert.IsType<CompiledResourceTransaction>(CompiledResourceTransaction.Build(plan));
        var memory = new Memory();
        Assert.True(Reference(plan, memory.Inputs(false), out _, out var expected));
        var trace = memory.Trace.ToArray();
        memory.Trace.Clear();
        Assert.True(transaction.Evaluate(memory.Inputs(true), 0, out _, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(trace, memory.Trace);
        memory.RefuseClean = true;
        memory.RefuseSpan = true;
        memory.Trace.Clear();
        Assert.False(Reference(plan, memory.Inputs(false), out _, out _));
        trace = memory.Trace.ToArray();
        memory.Trace.Clear();
        Assert.False(transaction.Evaluate(memory.Inputs(true), 0, out var descriptors, out var table));
        Assert.Empty(descriptors);
        Assert.Empty(table);
        Assert.Equal(trace, memory.Trace);
        Assert.False(transaction.Evaluate(new ResourceRuntimeInputs { UserData = [0x1000, 0], ReadMemory = memory.Read },
            0, out _, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TableOnlyCompilationPreservesReadPolicyFailureAndOutputs(bool nested, bool clean)
    {
        var plan = CreatePlan(32, nested);
        if (clean) Array.Fill((byte[])plan.CleanFlatSlots, (byte)1);
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTableTransaction);
        var memory = new Memory();
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [], memory.Inputs(false), plan.CleanFlatSlots.ToArray(),
            true, out _, out var expected));
        var trace = memory.Trace.ToArray();
        memory.Trace.Clear();
        Assert.True(ResourceMaterializer.TryEvaluateTable(plan, memory.Inputs(true), out var actual));
        Assert.Equal(expected, actual[..expected.Length]);
        Assert.All(actual[expected.Length..], word => Assert.Equal(0u, word));
        Assert.Equal(trace, memory.Trace);
        Assert.All(memory.Trace.Where(item => item.StartsWith("R:")), item => Assert.EndsWith(":True", item));
        var saved = actual.ToArray();
        foreach (var address in trace.Where(item => item.StartsWith("R:")).Select(item => ulong.Parse(item.Split(':')[1])).Distinct())
        {
            memory.FailureAddress = address;
            memory.RefuseSpan = true;
            memory.Trace.Clear();
            Assert.False(RuntimeValueEvaluator.EvaluateSources(plan, [], memory.Inputs(false), plan.CleanFlatSlots.ToArray(),
                true, out _, out _));
            var failedTrace = memory.Trace.ToArray();
            memory.Trace.Clear();
            Assert.False(ResourceMaterializer.TryEvaluateTable(plan, memory.Inputs(true), out var failed));
            Assert.Empty(failed);
            Assert.Equal(failedTrace, memory.Trace);
            Assert.False(memory.InTable);
        }
        Assert.Equal(saved, actual);
        memory.FailureAddress = ulong.MaxValue;
        memory.ThrowAddress = nested ? 0x2040UL : 0x1040UL;
        Assert.Throws<InvalidOperationException>(() => ResourceMaterializer.TryEvaluateTable(plan, memory.Inputs(true), out _));
        Assert.False(memory.InTable);
    }

    [Fact]
    public void CompiledRefreshKeepsValidatedWrittenRangesAndOldSnapshots()
    {
        var plan = CreatePlan(4, false);
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTableTransaction);
        var cache = new ResourceMaterializationCache();
        var memory = new Memory();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        var previous = snapshot;
        var oldTable = previous.FlattenedResourceTable.ToArray();
        memory.Salt++;
        Assert.True(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        Assert.Equal(1, cache.TableRefreshes);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(oldTable, previous.FlattenedResourceTable);
        foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
            Assert.Equal(oldTable.AsSpan((int)slot, 3).ToArray(), snapshot.FlattenedResourceTable.AsSpan((int)slot, 3).ToArray());
    }

    [Fact]
    public void GuardReusesShapeButNotPayloadsOrCallerMutableSpecialization()
    {
        var plan = CreatePlan(4, false);
        var memory = new Memory();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, memory.Inputs(false), ref snapshot, ref specialization));
        var guard = Assert.IsType<ResourceMaterializer.SpecializationGuard>(plan.SpecializationGuard);
        var expected = specialization.Clone();
        var previous = snapshot;
        specialization.Buffers.Clear();
        memory.DescriptorAddress += 0x1000;
        Assert.True(ResourceMaterializer.Materialize(plan, memory.Inputs(false), ref snapshot, ref specialization));
        Assert.Same(guard, plan.SpecializationGuard);
        Assert.Equal(expected, specialization);
        Assert.Equal(memory.DescriptorAddress, snapshot.Buffers[0][0]);
        Assert.NotEqual(previous.Buffers[0][0], snapshot.Buffers[0][0]);
        memory.DescriptorStride = 16u << 16;
        Assert.True(ResourceMaterializer.Materialize(plan, memory.Inputs(false), ref snapshot, ref specialization));
        Assert.NotSame(guard, plan.SpecializationGuard);
        Assert.NotEqual(expected, specialization);
        var saved = snapshot;
        var savedSpecialization = specialization;
        var savedGuard = plan.SpecializationGuard;
        memory.FailureAddress = 0x1040;
        Assert.False(ResourceMaterializer.Materialize(plan, memory.Inputs(false), ref snapshot, ref specialization));
        Assert.Same(saved, snapshot);
        Assert.Same(savedSpecialization, specialization);
        Assert.Same(savedGuard, plan.SpecializationGuard);
    }

    [Fact]
    public void FailureDoesNotReplayTheReferenceEvaluatorAndPreservesMaterializerOutputs()
    {
        var plan = CreatePlan(4, false);
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTransaction);
        var memory = new Memory { FailureAddress = 0x1040, RefuseSpan = true };
        Assert.False(RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources, memory.Inputs(true),
            plan.CleanFlatSlots, true, out _, out _));
        Assert.Single(memory.Trace, item => item.StartsWith("R:4160:"));
        memory.Trace.Clear();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var savedSnapshot = snapshot;
        var savedSpecialization = specialization;
        Assert.False(ResourceMaterializer.Materialize(plan, memory.Inputs(true), ref snapshot, ref specialization));
        Assert.Same(savedSnapshot, snapshot);
        Assert.Same(savedSpecialization, specialization);
        Assert.False(memory.InTable);
    }

    [Fact]
    public void CacheKeepsFreshDependenciesAndPriorSnapshots()
    {
        var plan = CreatePlan(4, false);
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTransaction);
        var cache = new ResourceMaterializationCache();
        var memory = new Memory();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        var previous = snapshot;
        var previousSpecialization = specialization;
        var previousWords = previous.Buffers[0].ToArray();
        memory.DescriptorAddress += 0x1000;
        Assert.True(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        Assert.Equal(memory.DescriptorAddress, snapshot.Buffers[0][0]);
        Assert.Equal(previousWords, previous.Buffers[0]);
        Assert.NotSame(previous, snapshot);
        Assert.Equal(2, cache.Misses);
        previous = snapshot;
        previousSpecialization = specialization;
        memory.FailureAddress = 0x1040;
        memory.RefuseSpan = true;
        Assert.False(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        Assert.Same(previous, snapshot);
        Assert.Same(previousSpecialization, specialization);
        memory.FailureAddress = ulong.MaxValue;
        memory.RefuseSpan = false;
        Assert.True(cache.Materialize(plan, memory.Inputs(true), memory.ReadSpan, ref snapshot, ref specialization, out _));
        Assert.Same(previous, snapshot);
    }

    // A descriptor the shader loads from an absolute address of its own that nothing maps reads zero, as the
    // evaluator reads it; the transaction used to fail the snapshot there.
    [Fact]
    public void UnmappedAbsoluteAddressReadsZeroAsTheReferenceDoes()
    {
        var plan = Extract(Program(
            MoveScalar(0, 4, 0xE0040000),
            MoveScalar(8, 5, 0xF),
            ScalarLoad(16, 4, 8, 4),
            BufferLoad(24, 8),
            EndProgram(32)));
        var transaction = Assert.IsType<CompiledResourceTransaction>(CompiledResourceTransaction.Build(plan));
        var reads = 0;
        var inputs = Inputs([], (ulong address, out uint word) => { reads++; word = 0xDEADBEEF; return false; });
        Assert.True(Reference(plan, inputs, out var expectedDescriptors, out var expectedTable));
        Assert.True(transaction.Evaluate(inputs, 0, out var descriptors, out var table));
        Assert.True(reads > 0);
        Assert.Equal(expectedTable, table);
        Assert.Equal(expectedDescriptors.SelectMany(words => words.Dwords), descriptors.SelectMany(words => words.Dwords));
        Assert.NotEmpty(descriptors);
        Assert.All(descriptors.SelectMany(words => words.Dwords), word => Assert.Equal(0u, word));
    }

    [Fact]
    public void ReaderExceptionsResetTheTablePhase()
    {
        var plan = CreatePlan(4, false);
        var transaction = Assert.IsType<CompiledResourceTransaction>(CompiledResourceTransaction.Build(plan));
        var memory = new Memory { ThrowAddress = 0x1040 };
        Assert.Throws<InvalidOperationException>(() => transaction.Evaluate(memory.Inputs(true), 0, out _, out _));
        Assert.False(memory.InTable);
    }

    [Fact]
    public void ParallelCallsOwnTheirOutputs()
    {
        var plan = CreatePlan(32, true);
        var transaction = Assert.IsType<CompiledResourceTransaction>(CompiledResourceTransaction.Build(plan));
        Parallel.For(0, 64, index =>
        {
            var memory = new Memory { Salt = (uint)index };
            Assert.True(transaction.Evaluate(memory.Inputs(true), 0, out var descriptors, out var table));
            Assert.True(Reference(plan, memory.Inputs(false), out var expectedDescriptors, out var expectedTable));
            Assert.Equal(expectedTable, table);
            Assert.Equal(expectedDescriptors.SelectMany(words => words.Dwords), descriptors.SelectMany(words => words.Dwords));
        });
    }

    [Fact]
    public void StructuredPlanCompilesOnlyTheTableTransaction()
    {
        var plan = Extract(Program(ScalarLoad(0, 0, 16), MoveVectorFromScalar(8, 4, 16),
            Sopc(12, "SCmpEqU32", Gen5Operand.Scalar(16), Operand(1)), Branch(16, "SCbranchScc1", 3),
            BufferLoad(20, 0), Branch(28, "SBranch", 2), BufferLoad(32, 4), EndProgram(40)), userDataCount: 9);
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.Null(CompiledResourceTransaction.Build(plan));
        Assert.NotNull(CompiledResourceTransaction.Build(plan, tableOnly: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArithmeticTransactionMatchesReferenceAndReadFailure(bool failRead)
    {
        var plan = Extract(Program(
            Sop2(0, "SAddU32", 0, Gen5Operand.Scalar(0), Operand(256)),
            Sop2(4, "SAddcU32", 1, Gen5Operand.Scalar(1), Operand(0)),
            ScalarLoad(8, 0, 4, 4, 2048),
            Sop2(16, "SAndB32", 6, Gen5Operand.Scalar(6), Operand(65535)),
            ScalarLoad(20, 0, 16, immediateOffset: 64),
            MoveVectorFromScalar(28, 4, 16), BufferStore(32, 4), EndProgram(40)), userDataCount: 2);
        var transaction = Assert.IsType<CompiledResourceTransaction>(CompiledResourceTransaction.Build(plan));
        var memory = new Memory { FailureAddress = failRead ? 0x1140UL : ulong.MaxValue };
        var expected = Reference(plan, memory.Inputs(false), out var expectedDescriptors, out var expectedTable);
        var actual = transaction.Evaluate(memory.Inputs(false), 0, out var descriptors, out var table);
        Assert.Equal(expected, actual);
        Assert.Equal(!failRead, actual);
        Assert.Equal(expectedTable, table);
        Assert.Equal(expectedDescriptors.SelectMany(words => words.Dwords), descriptors.SelectMany(words => words.Dwords));
    }

    [Theory]
    [InlineData("select")]
    [InlineData("aperture")]
    [InlineData("invariant_phi")]
    [InlineData("variant_phi")]
    [InlineData("failed_select")]
    public void AdditionalNodeKindsKeepScalarSemantics(string kind)
    {
        var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)));
        var value = ScalarValue.UserData(0);
        if (kind.Contains("phi"))
        {
            var phi = ScalarValue.Phi(0, ScalarValueType.U32);
            phi.SetPhiOperands([0, 1], [value, kind == "invariant_phi" ? value : ScalarValue.UserData(1)]);
            value = phi;
        }
        else if (kind == "aperture") value = ScalarValue.MemoryAperture(235);
        else value = ScalarValue.Select(ScalarValue.ConstantOf(true), value,
            kind == "failed_select" ? ScalarValue.UserData(100) : ScalarValue.UserData(1));
        plan.DescriptorSources[0].Dwords[0] = value;
        var transaction = CompiledResourceTransaction.Build(plan);
        if (kind == "variant_phi")
        {
            Assert.Null(transaction);
            return;
        }
        Assert.NotNull(transaction);
        var inputs = Inputs([0x1000, 0, 256, 0]);
        var succeeded = new RuntimeValueEvaluator(plan, inputs).Evaluate(value, out var expected);
        Assert.Equal(succeeded, transaction.Evaluate(inputs, 0, out var descriptors, out var table));
        Assert.Equal(kind != "failed_select", succeeded);
        if (succeeded) Assert.Equal(expected, Assert.Single(descriptors).Dwords[0]);
        else
        {
            Assert.Empty(descriptors);
            Assert.Empty(table);
        }
    }

    private sealed class Memory
    {
        public uint Salt;
        public uint DescriptorAddress = 0x8000;
        public uint DescriptorStride;
        public ulong FailureAddress = ulong.MaxValue;
        public ulong ThrowAddress = ulong.MaxValue;
        public bool InTable;
        public bool RefuseSpan;
        public bool RefuseClean;
        public int SpanCalls;
        public int MaximumSpanWords;
        public List<string> Trace { get; } = [];
        public ResourceRuntimeInputs Inputs(bool resident) => new()
        {
            UserData = [0x1000, 0], ReadMemory = Read, ReadCleanMemory = ReadClean,
            ReadResidentMemory = resident ? ReadSpan : null,
            TablePhase = table => { InTable = table; Trace.Add("P:" + table); },
        };
        public bool Read(ulong address, out uint word) => ReadCore(address, false, out word);
        private bool ReadClean(ulong address, out uint word) => ReadCore(address, true, out word);
        private bool ReadCore(ulong address, bool clean, out uint word)
        {
            Trace.Add($"R:{address}:{clean}:{InTable}");
            if (address == ThrowAddress) throw new InvalidOperationException();
            word = address switch
            {
                0x1000 => 0x2000, 0x1004 => 0,
                0x1800 or 0x2800 => DescriptorAddress, 0x1804 or 0x2804 => DescriptorStride,
                0x1808 or 0x2808 => 4096, 0x180C or 0x280C => 0,
                _ => (uint)address + Salt,
            };
            return address != FailureAddress && !(clean && RefuseClean);
        }
        public bool ReadSpan(ulong address, Span<byte> bytes, bool clean)
        {
            SpanCalls++;
            MaximumSpanWords = Math.Max(MaximumSpanWords, bytes.Length / 4);
            if (RefuseSpan)
            {
                bytes.Fill(0xFF);
                return false;
            }
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!ReadCore(address + (ulong)offset, clean, out var word)) return false;
                BitConverter.TryWriteBytes(bytes[offset..], word);
            }
            return true;
        }
    }
}
