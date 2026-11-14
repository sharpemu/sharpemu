// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Mode = SharpEmu.ShaderCompiler.Resources.RuntimeValueEvaluator.CompiledBranchMode;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class CompiledBranchTransactionTests
{
    // Two buffers, one in each arm of a uniform branch on user data s8. Both V#s are read through one pointer the shader
    // loads first, so whenever s8 selects the second arm the pointer's first use is in the inactive first arm.
    private static ShaderResourcePlan SharedPointerBranches(bool cleanSlots = false)
    {
        var plan = Extract(Program(
            ScalarLoad(0, 0, 10, 2),
            Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(12, "SCbranchScc1", 5),
            ScalarLoad(16, 10, 16, 4),
            BufferLoad(24, 16),
            Branch(32, "SBranch", 4),
            ScalarLoad(36, 10, 20, 4, 16),
            BufferLoad(44, 20),
            EndProgram(52)), userDataCount: 9);
        if (cleanSlots) Array.Fill((byte[])plan.CleanFlatSlots, (byte)1);
        plan.CompileEvaluatorNow();
        return plan;
    }

    private static TracedMemory Memory()
    {
        var memory = new TracedMemory();
        memory.Words[0x1000] = 0x2000;
        memory.Words[0x1004] = 0;
        uint[] first = [0x5000, 0, 256, 0];
        uint[] second = [0x6000, 0, 512, 0];
        for (var index = 0; index < 4; index++)
        {
            memory.Words[0x2000 + (ulong)index * 4] = first[index];
            memory.Words[0x2010 + (ulong)index * 4] = second[index];
        }

        return memory;
    }

    private static uint[] Words(ResourceSnapshot snapshot) =>
        [.. snapshot.Buffers.SelectMany(words => words), .. snapshot.FlattenedResourceTable];

    private readonly record struct Outcome(bool Succeeded, ResourceSnapshot Snapshot, string[] Trace, int Served);

    private static Outcome Run(ShaderResourcePlan plan, TracedMemory memory, uint condition, Mode mode, bool cleanReader = true)
    {
        memory.Trace.Clear();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var served = RuntimeValueEvaluator.BranchesServedOnThread;
        RuntimeValueEvaluator.BranchModeOverride = mode;
        bool succeeded;
        try
        {
            using (MaterializationProgramCheck.Suspend())
            {
                succeeded = ResourceMaterializer.Materialize(plan, memory.Inputs(condition, cleanReader), ref snapshot, ref specialization);
            }
        }
        finally
        {
            RuntimeValueEvaluator.BranchModeOverride = null;
        }

        return new Outcome(succeeded, snapshot, [.. memory.Trace], RuntimeValueEvaluator.BranchesServedOnThread - served);
    }

    [Theory]
    [InlineData(0u, true, false)]
    [InlineData(1u, true, false)]
    [InlineData(0u, false, false)]
    [InlineData(1u, false, false)]
    [InlineData(0u, true, true)]
    [InlineData(1u, true, true)]
    public void CompiledBranchesReadWhatTheFallbackReadsInItsOrder(uint condition, bool cleanReader, bool cleanSlots)
    {
        var plan = SharedPointerBranches(cleanSlots);
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.NotNull(plan.CompiledBranches);
        var memory = Memory();
        var reference = Run(plan, memory, condition, Mode.Off, cleanReader);
        Assert.True(reference.Succeeded);
        var compiled = Run(plan, memory, condition, Mode.On, cleanReader);
        Assert.Equal(1, compiled.Served);
        Assert.True(compiled.Succeeded);
        Assert.Equal(reference.Trace, compiled.Trace);
        Assert.Equal(Words(reference.Snapshot), Words(compiled.Snapshot));
    }

    // The first arm's V# words are flattened slots too: the inactive arm must leave them to the table, which reads them in
    // its own phase, while the shared pointer is read once, by the active arm, before its V#.
    [Fact]
    public void AValueFirstNeededByAnInactiveSourceIsReadOnceWhereTheActiveSourceNeedsIt()
    {
        var plan = SharedPointerBranches();
        var memory = Memory();
        var reference = Run(plan, memory, 1, Mode.Off);
        var compiled = Run(plan, memory, 1, Mode.On);
        Assert.Equal(1, compiled.Served);
        Assert.True(compiled.Succeeded);
        Assert.Equal(reference.Trace, compiled.Trace);
        Assert.Single(compiled.Trace, item => item.StartsWith("R:1000:", StringComparison.Ordinal));
        foreach (var address in new[] { 0x2000, 0x2004, 0x2008, 0x200C })
        {
            var reads = compiled.Trace.Where(item => item.StartsWith($"R:{address:X}:", StringComparison.Ordinal)).ToArray();
            Assert.Single(reads);
            Assert.EndsWith(":True", reads[0]);
        }

        foreach (var address in new[] { 0x2010, 0x2014, 0x2018, 0x201C })
            Assert.EndsWith(":False", Assert.Single(compiled.Trace, item => item.StartsWith($"R:{address:X}:", StringComparison.Ordinal)));
        var pointer = Array.FindIndex(compiled.Trace, item => item.StartsWith("R:1000:", StringComparison.Ordinal));
        var descriptor = Array.FindIndex(compiled.Trace, item => item.StartsWith("R:2010:", StringComparison.Ordinal));
        var table = Array.IndexOf(compiled.Trace, "P:True");
        Assert.True(pointer < descriptor && descriptor < table);
        Assert.Equal(0x6000u, compiled.Snapshot.Buffers.Single(words => words[0] != 0)[0]);
    }

    // A failed read fails the call at the same read in every mode, whether an active arm, the table (which reads an inactive
    // arm's flattened V#) or the shared pointer meets it first.
    [Theory]
    [InlineData(0u, 0x2004UL)]
    [InlineData(1u, 0x2004UL)]
    [InlineData(1u, 0x1000UL)]
    [InlineData(0u, 0x2018UL)]
    [InlineData(1u, 0x2018UL)]
    public void FailuresAndTheirReadsMatchTheFallback(uint condition, ulong failure)
    {
        var plan = SharedPointerBranches();
        var memory = Memory();
        memory.FailureAddress = failure;
        var reference = Run(plan, memory, condition, Mode.Off);
        var compiled = Run(plan, memory, condition, Mode.On);
        Assert.False(reference.Succeeded);
        Assert.False(compiled.Succeeded);
        Assert.Equal(1, compiled.Served);
        Assert.Equal(reference.Trace, compiled.Trace);
        Assert.Equal(failure == 0x1000UL || (condition == 0) == (failure < 0x2010UL), !reference.Trace.Contains("P:True"));
    }

    [Fact]
    public void CallsOwnTheirOutputsAndAFailureKeepsTheCallersSnapshot()
    {
        var plan = SharedPointerBranches();
        var memory = Memory();
        var first = Run(plan, memory, 0, Mode.On);
        var saved = Words(first.Snapshot);
        memory.Words[0x2000] = 0x7000;
        var second = Run(plan, memory, 0, Mode.On);
        Assert.Equal(saved, Words(first.Snapshot));
        Assert.Equal(0x7000u, second.Snapshot.Buffers.Single(words => words[0] != 0)[0]);

        memory.FailureAddress = 0x2004;
        var snapshot = second.Snapshot;
        var specialization = new ResourceSpecialization();
        var savedSpecialization = specialization;
        RuntimeValueEvaluator.BranchModeOverride = Mode.On;
        try
        {
            Assert.False(ResourceMaterializer.Materialize(plan, memory.Inputs(0, true), ref snapshot, ref specialization));
        }
        finally
        {
            RuntimeValueEvaluator.BranchModeOverride = null;
        }

        Assert.Same(second.Snapshot, snapshot);
        Assert.Same(savedSpecialization, specialization);
    }

    [Fact]
    public void ParallelCallsOwnTheirOutputs()
    {
        var plan = SharedPointerBranches();
        Parallel.For(0, 32, index =>
        {
            var memory = Memory();
            memory.Words[0x2010] = 0x6000 + (uint)index * 0x100;
            var reference = Run(plan, memory, 1, Mode.Off);
            var compiled = Run(plan, memory, 1, Mode.On);
            Assert.Equal(1, compiled.Served);
            Assert.Equal(Words(reference.Snapshot), Words(compiled.Snapshot));
        });
    }

    [Fact]
    public void CompiledBranchesAreOnUnlessConfigured()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_COMPILED_BRANCHES") is not null) return;
        Assert.Equal(Mode.On, RuntimeValueEvaluator.BranchMode);
    }

    private sealed class TracedMemory
    {
        public Dictionary<ulong, uint> Words { get; } = new();
        public List<string> Trace { get; } = [];
        public ulong FailureAddress { get; set; } = ulong.MaxValue;
        private bool _inTable;

        public ResourceRuntimeInputs Inputs(uint condition, bool cleanReader) => new()
        {
            UserData = [0x1000, 0, 0, 0, 0, 0, 0, 0, condition],
            ReadMemory = Read,
            ReadCleanMemory = cleanReader ? ReadClean : null,
            TablePhase = table =>
            {
                _inTable = table;
                Trace.Add("P:" + table);
            },
        };

        private bool Read(ulong address, out uint word) => ReadCore(address, false, out word);

        private bool ReadClean(ulong address, out uint word) => ReadCore(address, true, out word);

        private bool ReadCore(ulong address, bool clean, out uint word)
        {
            Trace.Add($"R:{address:X}:{clean}:{_inTable}");
            word = Words.TryGetValue(address, out var value) ? value : (uint)address;
            return address != FailureAddress;
        }
    }
}
