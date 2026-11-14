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

    private static Gen5ShaderProgram ConditionalBuffers(string branch = "SCbranchScc1", bool shared = false) => Program(
        Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
        Branch(4, branch, 3), BufferLoad(8, 0), Branch(16, "SBranch", 2),
        BufferLoad(20, shared ? 0u : 4u), EndProgram(28));

    private static uint[] UserData(uint condition) => [0x1000, 0, 256, 0, 0x2000, 0, 256, 0, condition];

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

    private static bool CleanRead(ulong address, out uint word) { word = 0; return false; }

    private ResourceSnapshot Materialize(ShaderResourcePlan plan, uint[] userData, GuestWordReader? cleanReader) =>
        Materialize(plan, Inputs(userData, readCleanMemory: cleanReader));

    private ResourceSnapshot Materialize(ShaderResourcePlan plan, ResourceRuntimeInputs inputs) => Materialize(plan, inputs, out _);

    private ResourceSnapshot Materialize(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out ResourceSpecialization specialization)
    {
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
    private static Gen5ShaderProgram CountedSampleThenStore() => Program(
        ScalarBufferLoad(0, 0, 24), Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(24), Operand(0)), Branch(12, "SCbranchScc1", 2),
        Image(16, "ImageSample", 8, 16, dimension: 5), BufferStore(24, 4), EndProgram(32));

    private static readonly uint[] VolumeImage = [.. ResourceTrackerTests.ImageDescriptor()[..3],
        (ResourceTrackerTests.ImageDescriptor()[3] & 0x0FFFFFFFu) | (GuestImageFormat.ImageType3D << 28), 0, 0, 0, 0];

    private static ShaderResourcePlan ExtractWithWrites(Gen5ShaderProgram program, bool allowed)
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
