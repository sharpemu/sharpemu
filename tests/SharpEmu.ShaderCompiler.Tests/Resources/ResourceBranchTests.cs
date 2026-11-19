// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public class ResourceBranchTests
{
    // Null: the configured path (the materialization program, else the fallback in tests). The derived class runs every
    // case through the compiled branch transaction.
    internal virtual RuntimeValueEvaluator.CompiledBranchMode? BranchMode => null;

    // True compares the materialization program's compiled branch selection with the evaluator's walk before every case's
    // materialization (the derived class).
    internal virtual bool CompareSelection => false;

    private protected static Gen5ShaderProgram ConditionalBuffers(string branch = "SCbranchScc1", bool shared = false) => Program(
        Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
        Branch(4, branch, 3), BufferLoad(8, 0), Branch(16, "SBranch", 2),
        BufferLoad(20, shared ? 0u : 4u), EndProgram(28));

    private protected static uint[] UserData(uint condition) => [0x1000, 0, 256, 0, 0x2000, 0, 256, 0, condition];

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void EmptySourceEvaluationPreservesBranchActivity(uint condition)
    {
        var plan = Extract(ConditionalBuffers());
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTableTransaction);
        var inputs = Inputs(UserData(condition), readCleanMemory: CleanRead);
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [], inputs, plan.CleanFlatSlots,
            true, out _, out var table, out var activity));
        Assert.NotEmpty(activity);
        Assert.Contains(false, activity);
        Assert.Contains(true, activity);
        Assert.True(ResourceMaterializer.TryEvaluateTable(plan, inputs, out var refreshed));
        Assert.Equal(table, refreshed);
    }

    private protected static bool CleanRead(ulong address, out uint word) { word = 0; return false; }

    private ResourceSnapshot Materialize(ShaderResourcePlan plan, uint[] userData, GuestWordReader? cleanReader) =>
        Materialize(plan, Inputs(userData, readCleanMemory: cleanReader));

    private protected ResourceSnapshot Materialize(ShaderResourcePlan plan, ResourceRuntimeInputs inputs) => Materialize(plan, inputs, out _);

    private ResourceSnapshot Materialize(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out ResourceSpecialization specialization)
    {
        if (CompareSelection && plan.ResourceBranches.Count != 0)
        {
            AssertSelectionMatchesEvaluator(plan, inputs);
        }

        var snapshot = new ResourceSnapshot();
        specialization = new ResourceSpecialization();
        var mode = BranchMode;
        if (mode is not null)
        {
            plan.CompileEvaluatorNow();
            inputs = WithTablePhase(inputs);
        }

        var served = RuntimeValueEvaluator.BranchesServedOnThread;
        RuntimeValueEvaluator.BranchModeOverride = mode;
        try
        {
            Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        }
        finally
        {
            RuntimeValueEvaluator.BranchModeOverride = null;
        }

        if (plan.ResourceBranches.Count != 0 && mode == RuntimeValueEvaluator.CompiledBranchMode.On)
            Assert.True(RuntimeValueEvaluator.BranchesServedOnThread > served, "the compiled branch transaction did not serve");
        return snapshot;
    }

    // The materialization cache reads with a table phase, which leaves the materialization program out, so the
    // selected sources are evaluated by the compiled branch transaction.
    private static ResourceRuntimeInputs WithTablePhase(ResourceRuntimeInputs inputs) => new()
    {
        UserData = inputs.UserData,
        ShaderBase = inputs.ShaderBase,
        ReadMemory = inputs.ReadMemory,
        ReadCleanMemory = inputs.ReadCleanMemory,
        ComputeState = inputs.ComputeState,
        IsRangeMapped = inputs.IsRangeMapped,
        TraceImageCandidate = inputs.TraceImageCandidate,
        TablePhase = inputs.TablePhase ?? (static _ => { }),
        ReadResidentMemory = inputs.ReadResidentMemory,
        ReadsClean = inputs.ReadsClean,
    };

    // The compiled branch selection against the evaluator's walk on one draw: the same active sources, and the same words
    // read through each reader in the same order with the same results. Returns the evaluator's active sources.
    private protected static bool[] AssertSelectionMatchesEvaluator(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, string draw = "")
    {
        var selection = plan.BranchSelection;
        Assert.NotNull(selection);
        var expectedReads = new List<string>();
        var expected = RuntimeValueEvaluator.EvaluateActiveSources(plan, Traced(inputs, expectedReads));
        var reads = new List<string>();
        var active = new bool[selection.SourceCount];
        if (!selection.TrySelect(Traced(inputs, reads), active))
        {
            Assert.True(inputs.ReadCleanMemory is null, $"The compiled selection refused a draw with a clean reader. {draw}");
            Assert.Empty(reads);
            Assert.Empty(expectedReads);
            return expected;
        }

        Assert.True(expected.AsSpan().SequenceEqual(active),
            $"The active sources differ. {draw} evaluator=[{string.Join(",", expected)}] compiled=[{string.Join(",", active)}]");
        Assert.True(expectedReads.SequenceEqual(reads),
            $"The reads differ. {draw} evaluator=[{string.Join(" ", expectedReads)}] compiled=[{string.Join(" ", reads)}]");
        return expected;
    }

    private static ResourceRuntimeInputs Traced(ResourceRuntimeInputs inputs, List<string> trace)
    {
        GuestWordReader? Trace(GuestWordReader? reader, string name) => reader is null ? null : (ulong address, out uint word) =>
        {
            var read = reader(address, out word);
            trace.Add($"{name}:{address:X}:{read}:{word:X}");
            return read;
        };

        return new ResourceRuntimeInputs
        {
            UserData = inputs.UserData,
            ShaderBase = inputs.ShaderBase,
            ReadMemory = Trace(inputs.ReadMemory, "general"),
            ReadCleanMemory = Trace(inputs.ReadCleanMemory, "clean"),
            ComputeState = inputs.ComputeState,
            IsRangeMapped = inputs.IsRangeMapped,
            ReadsClean = inputs.ReadsClean,
        };
    }

    [Theory]
    [InlineData("SCbranchScc1", 0u, 0)]
    [InlineData("SCbranchScc1", 1u, 1)]
    [InlineData("SCbranchScc0", 0u, 1)]
    [InlineData("SCbranchScc0", 1u, 0)]
    public void OnlyTakenUniformBranchMaterializesItsBuffer(string branch, uint condition, int activeBuffer)
    {
        var plan = Extract(ConditionalBuffers(branch));
        Assert.NotEmpty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(condition), CleanRead);
        Assert.Equal(activeBuffer == 0 ? 0x1000u : 0x2000u, snapshot.Buffers[activeBuffer][0]);
        Assert.All(snapshot.Buffers[1 - activeBuffer], word => Assert.Equal(0u, word));
    }

    [Fact]
    public void ChangingTheConditionDoesNotReusePreviousDrawActivity()
    {
        var plan = Extract(ConditionalBuffers());
        var first = Materialize(plan, UserData(0), CleanRead);
        var second = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, first.Buffers[0][0]);
        Assert.Equal(0u, first.Buffers[1][0]);
        Assert.Equal(0u, second.Buffers[0][0]);
        Assert.Equal(0x2000u, second.Buffers[1][0]);
    }

    [Fact]
    public void MissingCleanReaderKeepsBothPaths()
    {
        var snapshot = Materialize(Extract(ConditionalBuffers()), UserData(1), null);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void ResourceSharedByBothBranchesStaysActive(uint condition)
    {
        var snapshot = Materialize(Extract(ConditionalBuffers(shared: true)), UserData(condition), CleanRead);
        Assert.Equal(0x1000u, Assert.Single(snapshot.Buffers)[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShaderWritesAndAtomicsDisablePruning(bool atomic)
    {
        var instructions = ConditionalBuffers().Instructions.ToArray();
        instructions[2] = atomic ? BufferAtomicAdd(8, 0) : BufferStore(8, 0);
        var plan = ExtractWithWrites(Program(instructions), false);
        Assert.Empty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    // A count read from the buffer at s[0:3] guards a 2D-array sample of the T# at s[8:15]; the shader then stores
    // through the buffer at s[4:7]. The count is the first word of the guest memory at 0x1000.
    private protected static Gen5ShaderProgram CountedSampleThenStore() => Program(
        ScalarBufferLoad(0, 0, 24), Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(24), Operand(0)), Branch(12, "SCbranchScc1", 2),
        Image(16, "ImageSample", 8, 16, dimension: 5), BufferStore(24, 4), EndProgram(32));

    private protected static readonly uint[] VolumeImage = [.. ResourceTrackerTests.ImageDescriptor()[..3],
        (ResourceTrackerTests.ImageDescriptor()[3] & 0x0FFFFFFFu) | (GuestImageFormat.ImageType3D << 28), 0, 0, 0, 0];

    private protected static ShaderResourcePlan ExtractWithWrites(Gen5ShaderProgram program, bool allowed)
    {
        ResourceBranchBlock.WritesOverride = allowed;
        try
        {
            return Extract(program);
        }
        finally
        {
            ResourceBranchBlock.WritesOverride = null;
        }
    }

    private static ResourceRuntimeInputs CountedInputs(uint count, uint writtenBase, uint writtenWord1)
    {
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x1000) = count;
        uint[] userData = [0x1000, 0, 16, 0, writtenBase, writtenWord1, 64, 0, .. VolumeImage, 0, 0, 0, 0];
        return Inputs(userData, memory.Read, memory.Read);
    }

    private ResourceSnapshot MaterializeCounted(uint count, uint writtenBase, uint writtenWord1, out ResourceSpecialization specialization,
        out ShaderResourcePlan plan)
    {
        plan = ExtractWithWrites(CountedSampleThenStore(), true);
        return Materialize(plan, CountedInputs(count, writtenBase, writtenWord1), out specialization);
    }

    [Fact]
    public void WritingShaderPrunesWhenNoWriteCanReachItsPredicate()
    {
        var snapshot = MaterializeCounted(0, 0x1800, 0, out var specialization, out var plan);
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.Equal(0x1800u, snapshot.Buffers[(int)Assert.Single(plan.ResourceBranchWrites)][0]);
        Assert.All(Assert.Single(snapshot.Images), word => Assert.Equal(0u, word));
        // The sample the count skips does not specialize the module: a null image keeps the 2D-array view it samples.
        Assert.Equal(ImageDimension.Dim2DArray, Assert.Single(specialization.Images).Dimension);
    }

    [Theory]
    [InlineData(1u, 0x1800u, 0u)]
    [InlineData(0u, 0x1000u, 0u)]
    [InlineData(0u, 0x0F00u, 0u)]
    [InlineData(0u, 0x1800u, 0x8000_0000u)]
    public void WritingShaderKeepsTheSampleItMayReach(uint count, uint writtenBase, uint writtenWord1)
    {
        // A taken branch, a store whose buffer (or its margin) covers the count, or a swizzled store keeps every source.
        var snapshot = MaterializeCounted(count, writtenBase, writtenWord1, out var specialization, out _);
        Assert.Equal(VolumeImage, Assert.Single(snapshot.Images));
        Assert.Equal(ImageDimension.Dim3D, Assert.Single(specialization.Images).Dimension);
    }

    [Fact]
    public void WritesOutsideStaticBuffersKeepTheOldRule()
    {
        var instructions = CountedSampleThenStore().Instructions.ToArray();
        instructions[4] = GlobalAccess(24, "GlobalStoreDword", 4);
        Assert.Empty(ExtractWithWrites(Program(instructions), true).ResourceBranches);
        Assert.NotEmpty(ExtractWithWrites(CountedSampleThenStore(), true).ResourceBranches);
        Assert.Empty(ExtractWithWrites(CountedSampleThenStore(), false).ResourceBranches);
    }

    // ConditionalBuffers, then a write to LDS or scratch where both paths join. It writes through no buffer.
    private static Gen5ShaderProgram ConditionalBuffersThenPrivateWrite(bool scratch) => Program([
        .. ConditionalBuffers().Instructions.SkipLast(1),
        scratch ? GlobalMemory(28, "ScratchStoreDword", NullOperand, 0, 0, 8) : DataShareWrite(28, gds: false),
        EndProgram(36)]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateWritesKeepTheBlocks(bool scratch)
    {
        var original = ExtractWithWrites(ConditionalBuffersThenPrivateWrite(scratch), false);
        Assert.Empty(original.ResourceBranches);
        var plan = ExtractWithWrites(ConditionalBuffersThenPrivateWrite(scratch), true);
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.True(plan.ResourceBranchesWithWrites);
        Assert.Empty(plan.ResourceBranchWrites);

        // With the switch, the path the condition skips materializes its buffer as null.
        var selected = Materialize(plan, Inputs(UserData(1), readCleanMemory: CleanRead));
        Assert.All(selected.Buffers[0], word => Assert.Equal(0u, word));
        Assert.Equal(0x2000u, selected.Buffers[1][0]);
    }

    [Fact]
    public void UnknownBranchConditionKeepsBothPaths()
    {
        var plan = Extract(ConditionalBuffers("SCbranchCdbguser"));
        Assert.Empty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void UnreadablePredicateDoesNotFallBackToGeneralMemory()
    {
        var plan = Extract(Program(ScalarLoad(0, 12, 8),
            Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)), Branch(12, "SCbranchScc1", 3),
            BufferLoad(16, 0), Branch(24, "SBranch", 2), BufferLoad(28, 4), EndProgram(36)));
        uint[] userData = [.. UserData(1), 0, 0, 0, 0x3000, 0];
        var generalReads = 0;
        bool GeneralRead(ulong address, out uint word) { generalReads++; word = 1; return true; }
        ResourceSnapshot snapshot;
        using (MaterializationProgramCheck.Suspend())
        {
            snapshot = Materialize(plan, Inputs(userData, GeneralRead, CleanRead));
        }

        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
        // Flattened reads still use their original reader after the predicate check declines.
        Assert.Equal(plan.TableReads.Count, generalReads);
    }

    [Fact]
    public void InactiveIndirectImageDoesNotReadItsTable()
    {
        var imageInstructions = ResourceTrackerTests.IndirectImageProgram(false).Instructions;
        var endAddress = imageInstructions[^1].Pc;
        var plan = Extract(Program([
            Sopc(0xFF8, "SCmpEqU32", Gen5Operand.Scalar(30), Operand(1)),
            Branch(0xFFC, "SCbranchScc1", checked((short)((endAddress - 0x1000) / 4))),
            .. imageInstructions]));
        Assert.NotEmpty(plan.ResourceBranches);
        var userData = new uint[31];
        uint[] descriptors = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        descriptors.CopyTo(userData, 0);
        userData[30] = 1;
        var reads = 0;
        bool Unreadable(ulong address, out uint word) { reads++; word = 0; return false; }
        var inactive = Materialize(plan, userData, Unreadable);
        Assert.All(Assert.Single(inactive.Images), word => Assert.Equal(0u, word));
        Assert.Equal(0, reads);

        userData[30] = 0;
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        ResourceTrackerTests.WriteImage(memory, 0x2000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x2020, descriptor);
        var active = Materialize(plan, userData, memory.Read);
        Assert.Equal(descriptor, Assert.Single(active.Images));
    }

    [Fact]
    public void LoopWithChangingPredicateKeepsAllReachableResources()
    {
        var plan = Extract(Program(MoveScalar(0, 8, 0),
            Sopc(4, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(8, "SCbranchScc1", 4), BufferLoad(12, 0),
            MoveScalar(20, 8, 1), Branch(24, "SBranch", -6),
            BufferLoad(28, 4), EndProgram(36)));
        var snapshot = Materialize(plan, UserData(0), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void IndirectControlFlowDisablesPruning()
    {
        var instructions = ConditionalBuffers().Instructions.ToArray();
        instructions[3] = Sop1(16, "SSetpcB64", 12, Gen5Operand.Scalar(12));
        Assert.Empty(Extract(Program(instructions)).ResourceBranches);
    }

    [Fact]
    public void UnknownInnerBranchKeepsBothSuccessors()
    {
        var plan = Extract(Program(
            Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(4, "SCbranchScc1", 6), Branch(8, "SCbranchCdbguser", 3),
            BufferLoad(12, 0), Branch(20, "SBranch", 2), BufferLoad(24, 4), EndProgram(32)));
        Assert.NotEmpty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(0), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }
}

public sealed class CompiledResourceBranchTests : ResourceBranchTests
{
    internal override RuntimeValueEvaluator.CompiledBranchMode? BranchMode => RuntimeValueEvaluator.CompiledBranchMode.On;
}

// Every case above with the materialization program's compiled branch selection compared against the evaluator's walk, then
// draws with varied register and memory states over the value shapes a selection evaluates.
public sealed class CompiledBranchSelectionTests : ResourceBranchTests
{
    internal override bool CompareSelection => true;

    // Guest words by address; a missing or refused word fails its read.
    private sealed class WordMemory
    {
        public Dictionary<ulong, uint> Words { get; } = [];
        public HashSet<ulong> Refused { get; } = [];

        public bool Read(ulong address, out uint word)
        {
            word = 0;
            return !Refused.Contains(address) && Words.TryGetValue(address, out word);
        }
    }

    // A selection never reads through the general reader: it answers words no clean read returns.
    private static bool GeneralRead(ulong address, out uint word)
    {
        word = 0xDEAD_0000u | (uint)(address & 0xFFFF);
        return true;
    }

    private static uint Pick(System.Random random, params uint[] values) =>
        random.Next(5) == 0 ? (uint)random.NextInt64(0, 1L << 32) : values[random.Next(values.Length)];

    // Most draws have a clean reader; a few have none, which keeps every reachable path.
    private static ResourceRuntimeInputs Draw(System.Random random, uint[] userData, WordMemory memory) => new()
    {
        UserData = userData,
        ReadMemory = GeneralRead,
        ReadCleanMemory = random.Next(16) == 0 ? null : memory.Read,
    };

    private static uint[] FourBuffers(params uint[] tail) =>
        [0x1000, 0, 256, 0, 0x2000, 0, 256, 0, 0x3000, 0, 256, 0, 0x4000, 0, 256, 0, .. tail];

    // The predicate is the dword at s[12:13].
    private static Gen5ShaderProgram LoadedPredicate() => Program(ScalarLoad(0, 12, 8),
        Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)), Branch(12, "SCbranchScc1", 3),
        BufferLoad(16, 0), Branch(24, "SBranch", 2), BufferLoad(28, 4), EndProgram(36));

    // Two dwords read through s[18:19]: the first decides two branches and is read once for both; the second, against user
    // data s16, decides a third.
    private static Gen5ShaderProgram NestedLoads() => Program(
        ScalarLoad(0, 18, 20, count: 2),
        Sopc(8, "SCmpLtU32", Gen5Operand.Scalar(20), Operand(4)), Branch(12, "SCbranchScc1", 8),
        Sopc(16, "SCmpEqU32", Gen5Operand.Scalar(20), Operand(9)), Branch(20, "SCbranchScc0", 3),
        BufferLoad(24, 0), Branch(32, "SBranch", 10),
        BufferLoad(36, 4), Branch(44, "SBranch", 7),
        Sopc(48, "SCmpLtU32", Gen5Operand.Scalar(21), Gen5Operand.Scalar(16)), Branch(52, "SCbranchScc1", 3),
        BufferLoad(56, 8), Branch(64, "SBranch", 2),
        BufferLoad(68, 12), EndProgram(76));

    // s_cselect picks one of two loaded dwords by user data s16; both are read, whichever it picks.
    private static Gen5ShaderProgram SelectedLoad() => Program(
        ScalarLoad(0, 18, 20, count: 2),
        Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(16), Operand(0)),
        Sop2(12, "SCselectB32", 22, Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)),
        Sopc(16, "SCmpLtU32", Gen5Operand.Scalar(22), Operand(5)), Branch(20, "SCbranchScc1", 3),
        BufferLoad(24, 0), Branch(32, "SBranch", 2),
        BufferLoad(36, 4), EndProgram(44));

    // s_brev_b64 of user data s[16:17] compares its halves.
    private static Gen5ShaderProgram ReversedPair() => Program(
        Sop1(0, "SBrevB64", 20, Gen5Operand.Scalar(16)),
        Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)), Branch(8, "SCbranchScc1", 3),
        BufferLoad(12, 0), Branch(20, "SBranch", 2),
        BufferLoad(24, 4), EndProgram(32));

    // The predicate is record s16 of the buffer at s[0:3], read with a dynamic offset and checked against its records.
    private static Gen5ShaderProgram IndexedRecord() => Program(
        Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(16), Operand(2)),
        ScalarBufferLoad(4, 0, 24, dynamicOffsetRegister: 20),
        Sopc(12, "SCmpLtU32", Gen5Operand.Scalar(24), Operand(5)), Branch(16, "SCbranchScc1", 3),
        BufferLoad(20, 4), Branch(28, "SBranch", 2),
        BufferLoad(32, 8), EndProgram(40));

    // The predicate is read through a V# and an offset that are themselves read from s[18:19], so every operand of its read
    // reads memory.
    private static Gen5ShaderProgram ChasedDescriptor() => Program(
        ScalarLoad(0, 18, 20, count: 4),
        ScalarLoad(8, 18, 26, immediateOffset: 16),
        ScalarBufferLoad(16, 20, 24, dynamicOffsetRegister: 26),
        Sopc(24, "SCmpLtU32", Gen5Operand.Scalar(24), Operand(5)), Branch(28, "SCbranchScc1", 3),
        BufferLoad(32, 0), Branch(40, "SBranch", 2),
        BufferLoad(44, 4), EndProgram(52));

    // User data s5 chooses between a buffer and an image whose T# halves are scalar buffer reads 80 bytes apart in record s4
    // of the table at s[16:19].
    private static Gen5ShaderProgram RecordImageOrBuffer() => Program(
        Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(5), Operand(0)), Branch(4, "SCbranchScc1", 8),
        Sop2(8, "SLshlB32", 106, Gen5Operand.Scalar(4), Operand(7)),
        ScalarBufferLoad(12, 16, destination: 20, count: 4, immediateOffset: 16, dynamicOffsetRegister: 106),
        ScalarBufferLoad(20, 16, destination: 24, count: 4, immediateOffset: 96, dynamicOffsetRegister: 106),
        Image(28, "ImageLoad", 20), Branch(36, "SBranch", 2),
        BufferLoad(40, 8), EndProgram(48));

    private static uint[] RecordUserData(uint record, uint condition) =>
        [0, 0, 0, 0, record, condition, 0, 0, 0x2000, 0, 256, 0, 0, 0, 0, 0, 0x1000, 0, 0x200, 0];

    private delegate ResourceRuntimeInputs DrawState(System.Random random);

    private sealed record Shape(string Name, ShaderResourcePlan Plan, DrawState Draw);

    private static Shape[] Shapes() =>
    [
        new("uniform scc1", Extract(ConditionalBuffers()), random =>
            Draw(random, UserData(Pick(random, 0, 1, 2, 0x8000_0000, uint.MaxValue)), new WordMemory())),
        new("uniform scc0", Extract(ConditionalBuffers("SCbranchScc0")), random =>
            Draw(random, UserData(Pick(random, 0, 1, 2, 0x8000_0000, uint.MaxValue)), new WordMemory())),
        new("loaded predicate", Extract(LoadedPredicate()), random =>
        {
            var memory = new WordMemory();
            memory.Words[0x3000] = Pick(random, 0, 1, 2);
            memory.Words[0x3004] = Pick(random, 0, 1);
            if (random.Next(4) == 0) memory.Refused.Add(0x3000);
            return Draw(random, [.. UserData(0), 0, 0, 0, Pick(random, 0x3000, 0x3004, 0, 2, 0x9000), Pick(random, 0, 0, 0, 1)], memory);
        }),
        new("write guard", ExtractWithWrites(CountedSampleThenStore(), true), random =>
        {
            // The stored V# lies around the count's word at 0x1000, within and past the guard's margin.
            var memory = new WordMemory();
            memory.Words[0x1000] = Pick(random, 0, 0, 1);
            if (random.Next(6) == 0) memory.Refused.Add(0x1000);
            uint[] userData =
            [
                Pick(random, 0x1000, 0x1000, 0x1000, 0, 0x2000), Pick(random, 0, 0, 0, 4 << 16), Pick(random, 16, 16, 16, 0, 3, 4), 0,
                Pick(random, 0x1800, 0x1000, 0x0F00, 0x0EFC, 0x1104, 0x1103, 0),
                Pick(random, 0, 0, 0, 0x8000_0000, 16 << 16, 1),
                Pick(random, 64, 64, 0, 1, uint.MaxValue),
                Pick(random, 0, 0, 0, 0, 1u << 23, 1u << 30, 3u << 30),
                .. VolumeImage, 0, 0, 0, 0,
            ];
            return Draw(random, userData, memory);
        }),
        new("nested loads", Extract(NestedLoads()), random => NestedDraw(random)),
        new("selected load", Extract(SelectedLoad()), random => NestedDraw(random)),
        new("reversed pair", Extract(ReversedPair()), random =>
            Draw(random, FourBuffers(Pick(random, 0, 1, 0x8000_0000, 0x0F0F_0F0F), Pick(random, 0, 1, 0x8000_0000, 2)), new WordMemory())),
        new("indexed record", Extract(IndexedRecord()), random =>
        {
            var memory = new WordMemory();
            for (var record = 0u; record < 16; record++)
            {
                memory.Words[0x6000 + record * 4] = Pick(random, 0, 4, 5, 6);
            }

            if (random.Next(5) == 0) memory.Refused.Add(0x6000 + (ulong)random.Next(16) * 4);
            uint[] userData =
            [
                Pick(random, 0x6000, 0x6000, 0, 0x6002), Pick(random, 0, 0, 4 << 16, 16 << 16), Pick(random, 16, 64, 0, 1, 4, 1000), 0,
                0x2000, 0, 256, 0, 0x3000, 0, 256, 0, 0, 0, 0, 0, Pick(random, 0, 1, 2, 3, 15, 0x4000_0000),
            ];
            return Draw(random, userData, memory);
        }),
        new("chased descriptor", Extract(ChasedDescriptor()), random =>
        {
            var memory = new WordMemory();
            uint[] words = [Pick(random, 0x8000, 0x8000, 0, 0x8002), Pick(random, 0, 0, 4 << 16, 1), Pick(random, 64, 64, 0, 1, 2),
                Pick(random, 0, 0, 1u << 30), Pick(random, 0, 4, 8, 60, 64)];
            for (var word = 0; word < words.Length; word++)
            {
                memory.Words[0x7000 + (ulong)word * 4] = words[word];
            }

            for (var word = 0u; word < 16; word++)
            {
                memory.Words[0x8000 + word * 4] = Pick(random, 0, 4, 5, 6);
            }

            if (random.Next(4) == 0)
            {
                memory.Refused.Add(random.Next(2) == 0 ? 0x7000 + (ulong)random.Next(5) * 4 : 0x8000 + (ulong)random.Next(16) * 4);
            }

            return Draw(random, FourBuffers(0, 0, Pick(random, 0x7000, 0x7000, 0x7004, 0), 0), memory);
        }),
        new("record image", Extract(RecordImageOrBuffer(), stage: ShaderStage.Pixel), random =>
            Draw(random, RecordUserData(Pick(random, 0, 1, 2), Pick(random, 0, 1, 7)), new WordMemory())),
    ];

    private static ResourceRuntimeInputs NestedDraw(System.Random random)
    {
        var memory = new WordMemory();
        memory.Words[0x5000] = Pick(random, 0, 3, 4, 9, 10);
        memory.Words[0x5004] = Pick(random, 0, 1, 5, uint.MaxValue);
        if (random.Next(5) == 0) memory.Refused.Add(random.Next(2) == 0 ? 0x5000ul : 0x5004ul);
        return Draw(random, FourBuffers(Pick(random, 0, 2, 6, uint.MaxValue), 0, Pick(random, 0x5000, 0x5000, 0, 0x5002, 0x9000), 0), memory);
    }

    // The shapes take turns on one thread, so each selection starts from the scratch another plan left. The evaluator walks
    // with the plan's compiled evaluator, or with its interpreter until the background compiler builds one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledSelectionChoosesAndReadsWhatTheEvaluatorDoes(bool compiledEvaluator)
    {
        var shapes = Shapes();
        if (compiledEvaluator)
        {
            foreach (var shape in shapes)
            {
                shape.Plan.CompileEvaluatorNow();
            }
        }

        var outcomes = shapes.Select(_ => new HashSet<string>()).ToArray();
        var random = new System.Random(compiledEvaluator ? 0x5E1EC7 : 0x7A11);
        for (var draw = 0; draw < 1024; draw++)
        {
            for (var index = 0; index < shapes.Length; index++)
            {
                var shape = shapes[index];
                Assert.NotEmpty(shape.Plan.ResourceBranches);
                var active = AssertSelectionMatchesEvaluator(shape.Plan, shape.Draw(random), $"shape={shape.Name} draw={draw}");
                outcomes[index].Add(string.Concat(active.Select(source => source ? '1' : '0')));
            }
        }

        // Every shape took more than one selection; the write guard both pruned and kept every source.
        for (var index = 0; index < shapes.Length; index++)
        {
            Assert.True(outcomes[index].Count > 1, $"shape={shapes[index].Name} took one selection");
        }

        var guard = outcomes[Array.FindIndex(shapes, shape => shape.Name == "write guard")];
        Assert.Contains(guard, outcome => outcome.Contains('0'));
        Assert.Contains(guard, outcome => !outcome.Contains('0'));
    }

    // A condition the registers do not cover, here a first-lane read, leaves the program's selection to the evaluator.
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void AConditionThatDoesNotLowerIsSelectedByTheEvaluator(uint condition)
    {
        var plan = Extract(Program(
            MoveVectorFromScalar(0, 1, 8), ReadFirstLane(4, 9, 1),
            Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(9), Operand(1)), Branch(12, "SCbranchScc1", 3),
            BufferLoad(16, 0), Branch(24, "SBranch", 2), BufferLoad(28, 4), EndProgram(36)));
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.NotNull(plan.MaterializationProgram);
        Assert.Null(plan.BranchSelection);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(UserData(condition), readCleanMemory: CleanRead), ref snapshot,
            ref specialization));
        Assert.Equal(condition == 0 ? 0x1000u : 0u, snapshot.Buffers[0][0]);
        Assert.Equal(condition == 1 ? 0x2000u : 0u, snapshot.Buffers[1][0]);
    }

    // The selection reads through the clean reader only; a draw without one is left to the evaluator.
    [Fact]
    public void ADrawWithoutACleanReaderIsLeftToTheEvaluator()
    {
        var plan = Extract(LoadedPredicate());
        var selection = Assert.IsType<MaterializationProgram.BranchSelection>(plan.BranchSelection);
        var active = new bool[selection.SourceCount];
        Assert.False(selection.TrySelect(Inputs([.. UserData(0), 0, 0, 0, 0x3000, 0], GeneralRead), active));
        Assert.All(active, Assert.False);
    }

    // A guest read can select another draw's sources on the same thread: the outer selection keeps its values, its pending
    // blocks and the reads its write guard checks.
    [Fact]
    public void ASelectionInsideAGuestReadLeavesTheOuterSelectionIntact()
    {
        var inner = Extract(NestedLoads());
        var innerMemory = new WordMemory();
        innerMemory.Words[0x5000] = 2;
        innerMemory.Words[0x5004] = 1;
        var innerInputs = Inputs(FourBuffers(6, 0, 0x5000, 0), GeneralRead, innerMemory.Read);
        var innerExpected = RuntimeValueEvaluator.EvaluateActiveSources(inner, innerInputs);
        var nested = 0;
        var memory = new WordMemory();
        memory.Words[0x1000] = 0;
        bool Reentering(ulong address, out uint word)
        {
            nested++;
            var active = new bool[inner.BranchSelection!.SourceCount];
            Assert.True(inner.BranchSelection.TrySelect(innerInputs, active));
            Assert.Equal(innerExpected, active);
            return memory.Read(address, out word);
        }

        // The stored buffer covers the count, so the guard keeps every source; one that lost the count's read would prune.
        var outer = ExtractWithWrites(CountedSampleThenStore(), true);
        uint[] userData = [0x1000, 0, 16, 0, 0x1000, 0, 64, 0, .. VolumeImage, 0, 0, 0, 0];
        var expected = RuntimeValueEvaluator.EvaluateActiveSources(outer, Inputs(userData, GeneralRead, memory.Read));
        Assert.All(expected, Assert.True);
        var selected = new bool[outer.BranchSelection!.SourceCount];
        Assert.True(outer.BranchSelection.TrySelect(Inputs(userData, GeneralRead, Reentering), selected));
        Assert.Equal(expected, selected);
        Assert.Equal(1, nested);
    }

    // A T# whose halves are scalar buffer reads at a uniform record offset is evaluated word by word; the arm that reads it
    // is pruned like any other, through the program's full and fast paths.
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void AnImageReadWordByWordIsPrunedWithItsArm(uint condition)
    {
        var plan = Extract(RecordImageOrBuffer(), stage: ShaderStage.Pixel);
        Assert.NotNull(plan.MaterializationProgram);
        var image = Assert.Single(plan.Info.Images);
        Assert.All(plan.DescriptorSources[(int)image.Source].Dwords, word => Assert.Equal(ScalarValueKind.ScalarBufferWord, word.Kind));
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        // Record 2 starts at byte 256 of the table.
        for (var word = 0; word < 4; word++)
        {
            memory.At(0x1000 + 256 + 16 + (ulong)word * 4) = descriptor[word];
            memory.At(0x1000 + 256 + 96 + (ulong)word * 4) = descriptor[4 + word];
        }

        var snapshot = Materialize(plan, Inputs(RecordUserData(2, condition), memory.Read, memory.Read));
        // The table the T# is read from is a buffer of the image's arm.
        uint[] table = [0x1000, 0, 0x200, 0];
        uint[] buffer = [0x2000, 0, 256, 0];
        if (condition == 0)
        {
            Assert.Equal([new uint[4], buffer], snapshot.Buffers);
            Assert.All(Assert.Single(snapshot.Images), word => Assert.Equal(0u, word));
        }
        else
        {
            Assert.Equal([table, new uint[4]], snapshot.Buffers);
            Assert.Equal(descriptor, Assert.Single(snapshot.Images));
        }
    }
}
