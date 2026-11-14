// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// While the newest program of a runtime-record caller compiles, the caller's older ready program serves. With SkipPendingPass a
// dispatch whose resolution selects the permutation still compiling polls that compile and, while it compiles, serves the
// fallback without the newest program's record ranges and stage resources; what each dispatch serves, and the fallback it
// reports, must not change.
[Collection(SchedulingStateCollection.Name)]
public sealed class PendingPassSkipTests : IDisposable
{
    private const ulong Caller = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong Table = PipelineTestGuest.MemoryBase + 0x10000;
    private const ulong Callee = PipelineTestGuest.MemoryBase + 0x20000;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
    private long _lastEarlySkips;
    private long _lastCallDispatchReuses;

    private sealed record Dispatch(ulong Served, LinkedCompileFallback? Fallback);

    private sealed record Poll(int Dispatch, ulong Program, ShaderStageResources Stage);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CurrentDispatchContinuationPreservesFallbackSelectionAndPipelinePolling(bool early, bool middle)
    {
        var (ordinary, ordinaryPolls, _) = Run(skip: true, middle: middle, early: early);
        Assert.Equal(0, _lastCallDispatchReuses);
        var (continued, continuedPolls, _) = Run(skip: true, middle: middle, early: early, reuseDispatch: true);
        Assert.True(_lastCallDispatchReuses > 0);
        Assert.Equal(ordinary.Select(Served), continued.Select(Served));
        Assert.Equal(ordinaryPolls.Select(poll => (poll.Dispatch, poll.Program)),
            continuedPolls.Select(poll => (poll.Dispatch, poll.Program)));
    }

    [Fact]
    public void CurrentDispatchContinuationReadsTheNextDispatchsChangedRecords()
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest(CompileSpirv);
        guest.Host.RuntimeCallRecordsSupported = true;
        // Two fixed-offset call sites: the first stays ready and reads the changed argument, while the second
        // introduces new code whose pipeline stays pending. Merely growing this fixed-offset table adds no call site.
        guest.RegisterProgram(Caller, Header,
            [0xF4280400, 125u << 25, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311, 0xBE900312, 0xBE910313, 0xBE8E210E,
             0xF4280400, (125u << 25) | 16, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311, 0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000]);
        guest.WriteWords(Callee, 0xBF800000, 0xBE80200E);
        const ulong secondCallee = Callee + 0x100;
        guest.WriteWords(secondCallee, 0xBF800001, 0xBE80200E);
        guest.WriteWords(Table, unchecked((uint)Callee), (uint)(Callee >> 32), 0x3000, 0,
            unchecked((uint)Callee), (uint)(Callee >> 32), 0x6000, 0);
        UseIsolatedCallUnion(guest, false, false, [28, 60]);
        guest.Programs.ReuseCallDispatch = true;
        guest.Programs.SkipPendingPass = true;
        guest.Programs.SkipPendingPassEarly = true;
        var ready = new HashSet<ulong>();
        guest.Programs.TryComputePipeline = (program, _, _) => ready.Contains(program.Id);
        (ulong Handle, ShaderStageResources Stage) Resolve(uint count)
        {
            var cursor = 0u;
            var program = guest.Programs.GetOrCompile(guest.Source(Caller, ShaderStage.Compute,
                PipelineTestGuest.BufferDescriptor(Table, 16, count, 0)), PipelineTestGuest.ComputeOptions(32),
                ref cursor, out var stage);
            return (program.Id, stage);
        }
        ready.Add(Resolve(2).Handle);
        Assert.Null(Resolve(2).Stage.CompileFallback);
        guest.WriteWords(Table, unchecked((uint)Callee), (uint)(Callee >> 32), 0x4000, 0,
            unchecked((uint)secondCallee), (uint)(secondCallee >> 32), 0x6000, 0);
        var first = Resolve(2);
        Assert.NotNull(first.Stage.CompileFallback);
        Assert.Contains((Callee, 0x4000ul), first.Stage.CallRecords!);
        guest.WriteWords(Table + 8, 0x5000, 0);
        var second = Resolve(2);
        Assert.NotNull(second.Stage.CompileFallback);
        Assert.Contains((Callee, 0x5000ul), second.Stage.CallRecords!);
        Assert.DoesNotContain((Callee, 0x4000ul), second.Stage.CallRecords!);
        Assert.Contains((Callee, 0x4000ul), first.Stage.CallRecords!);
        Assert.True(guest.Programs.CallDispatchReuses >= 2);
    }

    [Fact]
    public void ThePendingNewestProgramIsPolledWhileTheSameProgramsServe()
    {
        var (resolved, resolvedPolls, resolvedSkips) = Run(skip: false);
        var (polled, polledPolls, polledSkips) = Run(skip: true);

        // 0: the first program compiles; 1: it is ready and serves; 2: new callee code links a newer program whose pipeline
        // compiles, and the first serves as its fallback; 3: the newer one still compiles; 4: it is ready and serves.
        Assert.Equal(5, resolved.Count);
        Assert.Null(resolved[1].Fallback);
        Assert.NotNull(resolved[2].Fallback);
        Assert.Equal(resolved[1].Served, resolved[2].Served);
        Assert.Equal("pipeline", resolved[2].Fallback!.Reason);
        Assert.Equal(Served(resolved[2]), Served(resolved[3]));
        Assert.Null(resolved[4].Fallback);
        Assert.NotEqual(resolved[1].Served, resolved[4].Served);

        // The same programs serve, with the same fallbacks, in both modes.
        Assert.Equal(resolved.Select(Served), polled.Select(Served));
        Assert.Equal(0, resolvedSkips);
        Assert.Equal(1, polledSkips);

        // Dispatch 3 polls the newer program's compile once in both modes. Without the skip, the stage step polls with the
        // stage resources it made; with the skip, the dispatch polls with the ones dispatch 2 made and makes none. With the
        // skip, dispatch 4 polls twice: before its record ranges (the compile is ready, so it resolves on) and in the stage step.
        var newer = resolved[4].Served;
        var resolvedNewer = resolvedPolls.Where(poll => poll.Program == newer).ToArray();
        var polledNewer = polledPolls.Where(poll => poll.Program == newer).ToArray();
        Assert.Equal([2, 3, 4], resolvedNewer.Select(static poll => poll.Dispatch));
        Assert.Equal([2, 3, 4, 4], polledNewer.Select(static poll => poll.Dispatch));
        var resolvedAt3 = Assert.Single(resolvedNewer, static poll => poll.Dispatch == 3);
        var polledAt3 = Assert.Single(polledNewer, static poll => poll.Dispatch == 3);
        // Stage resources are a value; their snapshot is the instance one materialization made.
        Assert.NotSame(Assert.Single(resolvedNewer, static poll => poll.Dispatch == 2).Stage.Resources, resolvedAt3.Stage.Resources);
        Assert.Same(Assert.Single(polledNewer, static poll => poll.Dispatch == 2).Stage.Resources, polledAt3.Stage.Resources);
    }

    // SHARPEMU_PENDING_PASS_EARLY: with one permutation, the one still compiling, the poll runs before the newest program's
    // targets and materialization; the same programs and fallbacks serve, and the skipped dispatch makes no stage resources.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEarlySkipServesTheSameProgramsAndMaterializesNothingForThePendingProgram(bool middle)
    {
        var (resolved, _, _) = Run(skip: false, middle: middle);
        var (early, earlyPolls, earlySkips) = Run(skip: true, middle: middle, early: true);

        Assert.Equal(resolved.Select(Served), early.Select(Served));
        Assert.Equal(1, earlySkips);
        Assert.Equal(1, _lastEarlySkips);
        if (!middle)
        {
            var newer = resolved[4].Served;
            var polled = earlyPolls.Where(poll => poll.Program == newer).ToArray();
            Assert.Equal([2, 3, 4, 4], polled.Select(static poll => poll.Dispatch));
            Assert.Same(Assert.Single(polled, static poll => poll.Dispatch == 2).Stage.Resources,
                Assert.Single(polled, static poll => poll.Dispatch == 3).Stage.Resources);
        }
    }

    // The poll can promote an older compile that finished meanwhile: that program serves in both modes, not the
    // fallback the caller had before the poll.
    [Fact]
    public void AMiddleCompileThatFinishesDuringThePollServesInBothModes()
    {
        var (resolved, _, resolvedSkips) = Run(skip: false, middle: true);
        var (polled, _, polledSkips) = Run(skip: true, middle: true);

        // 0: the first program compiles; 1: it is ready and serves; 2: a second program compiles while the first serves;
        // 3: a third (the newest) compiles too; 4: the second finished, so it serves while the newest still compiles.
        Assert.Equal(5, resolved.Count);
        Assert.Equal(resolved[1].Served, resolved[3].Served);
        Assert.NotEqual(resolved[1].Served, resolved[4].Served);
        Assert.Equal(2, resolved[3].Fallback!.RequestedGeneration - resolved[3].Fallback!.ServedGeneration);
        Assert.Equal(1, resolved[4].Fallback!.RequestedGeneration - resolved[4].Fallback!.ServedGeneration);
        Assert.Equal(resolved.Select(Served), polled.Select(Served));
        Assert.Equal(0, resolvedSkips);
        Assert.Equal(1, polledSkips);
    }

    // The shortcut follows the stage step's own selection: the first permutation whose push-data start and specialization
    // match. Only when that is the pending one does it serve the fallback; a ready alternate, or none, takes the full path.
    [Fact]
    public void OnlyThePermutationTheStageStepWouldSelectCountsAsPending()
    {
        var plain = new ResourceSpecialization();
        var imaged = new ResourceSpecialization();
        imaged.Images.Add(new ImageSpecialization(ImageNumericClass.Float, ImageDimension.Dim2D, 1, 0, 0, uint.MaxValue, 0, 0, false));
        var other = new ResourceSpecialization();
        other.Images.Add(new ImageSpecialization(ImageNumericClass.Uint, ImageDimension.Dim2D, 1, 0, 0, uint.MaxValue, 0, 0, false));
        var first = Permutation(1, plain);
        var second = Permutation(2, imaged);
        List<ProgramPermutation> permutations = [first, second];

        Assert.True(ShaderProgramCache.PendingPermutationServes(permutations, second.Handle, 0, imaged));
        // The current specialization selects the other permutation, which may be ready: the stage step decides.
        Assert.False(ShaderProgramCache.PendingPermutationServes(permutations, second.Handle, 0, plain));
        // No permutation matches: the stage step compiles one.
        Assert.False(ShaderProgramCache.PendingPermutationServes(permutations, second.Handle, 0, other));
        // The first match wins, as in the stage step's loop.
        var duplicate = Permutation(3, imaged);
        Assert.False(ShaderProgramCache.PendingPermutationServes([first, second, duplicate], duplicate.Handle, 0, imaged));
        Assert.True(ShaderProgramCache.Selects(second, 0, imaged));
        Assert.False(ShaderProgramCache.Selects(second, 0, plain));

        // Push data: a permutation whose shader data (four dwords) starts at dword 0 serves only a dispatch whose cursor is 0;
        // one compiled for a cursor of 8 serves only that cursor.
        var atZero = Permutation(4, imaged, new BindingLayout { PushDataStartDword = 0, CallTargetCount = 2 });
        var atEight = Permutation(5, imaged, new BindingLayout { PushDataStartDword = 8, CallTargetCount = 2 });
        Assert.Equal(4u, atZero.Bindings.ShaderDataDwordCount);
        Assert.True(ShaderProgramCache.Selects(atZero, 0, imaged));
        Assert.False(ShaderProgramCache.Selects(atZero, 8, imaged));
        Assert.False(ShaderProgramCache.Selects(atZero, 30, imaged));
        Assert.False(ShaderProgramCache.PendingPermutationServes([atZero], atZero.Handle, 8, imaged));
        Assert.True(ShaderProgramCache.PendingPermutationServes([atZero, atEight], atEight.Handle, 8, imaged));
        Assert.False(ShaderProgramCache.PendingPermutationServes([atZero, atEight], atEight.Handle, 0, imaged));
    }

    private static ProgramPermutation Permutation(ulong id, ResourceSpecialization specialization, BindingLayout? bindings = null) => new()
    {
        Specialization = specialization,
        Program = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute, Hash = id, Images = [], Bindings = bindings ?? new BindingLayout(),
        },
        Handle = new ShaderProgram(id),
    };

    // Generations count across caches, so a fallback is compared by its libraries, its reason and how many generations it lags.
    private static (ulong, string?, string?, string?, long?) Served(Dispatch dispatch) =>
        (dispatch.Served, dispatch.Fallback?.RequestedLibrary, dispatch.Fallback?.ServedLibrary, dispatch.Fallback?.Reason,
            dispatch.Fallback?.RequestedGeneration - dispatch.Fallback?.ServedGeneration);

    // Without middle: five dispatches; the second program becomes ready before the last. With middle: a third program links at
    // dispatch 3, and the second (not the newest) becomes ready before dispatch 4.
    private (List<Dispatch> Dispatches, List<Poll> Polls, long Skips) Run(bool skip, bool middle = false, bool early = false,
        bool reuseDispatch = false)
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest(CompileSpirv);
        guest.Host.RuntimeCallRecordsSupported = true;
        guest.RegisterProgram(Caller, Header,
            [0xF4280400, 125u << 25, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311, 0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000]);
        guest.WriteWords(Callee, 0xBF800000, 0xBE80200E);
        const ulong argument = 0x3000;
        guest.WriteWords(Table, unchecked((uint)Callee), (uint)(Callee >> 32), unchecked((uint)argument), (uint)(argument >> 32));
        UseIsolatedCallUnion(guest, skip, middle);
        guest.Programs.SkipPendingPass = skip;
        guest.Programs.SkipPendingPassEarly = early;
        guest.Programs.ReuseCallDispatch = reuseDispatch;

        var dispatches = new List<Dispatch>();
        var polls = new List<Poll>();
        var ready = new HashSet<ulong>();
        guest.Programs.TryComputePipeline = (program, stage, _) =>
        {
            polls.Add(new Poll(dispatches.Count, program.Id, stage));
            return ready.Contains(program.Id);
        };

        dispatches.Add(Next(guest));
        ready.Add(dispatches[0].Served);
        dispatches.Add(Next(guest));
        guest.WriteWords(Callee, 0xBF800001, 0xBE80200E);
        dispatches.Add(Next(guest));
        var second = polls.First(poll => poll.Dispatch == 2 && poll.Program != dispatches[0].Served).Program;
        if (middle)
        {
            guest.WriteWords(Callee, 0xBF800002, 0xBE80200E);
        }

        dispatches.Add(Next(guest));
        ready.Add(second);
        dispatches.Add(Next(guest));
        _lastEarlySkips = guest.Programs.PendingPassEarlySkips;
        _lastCallDispatchReuses = guest.Programs.CallDispatchReuses;
        return (dispatches, polls, guest.Programs.PendingPassSkips);
    }

    private static Dispatch Next(PipelineTestGuest guest)
    {
        var cursor = 0u;
        var program = guest.Programs.GetOrCompile(guest.Source(Caller, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(Table, 16, 1, 0)),
            PipelineTestGuest.ComputeOptions(32), ref cursor, out var stage);
        return new Dispatch(program.Id, stage.CompileFallback);
    }

    // Runtime linking loads the callees kept for the caller before its first link; each run keeps them under this test's
    // directory, so no run reads or writes the user's shader cache or another run's union.
    private void UseIsolatedCallUnion(PipelineTestGuest guest, bool skip, bool middle, uint[]? callSites = null)
    {
        var path = Path.Combine(_directory, $"callees-{skip}-{middle}.bin");
        var decoded = ShaderFunctionReader.Read(Callee, guest.Host.TryReadGuestWord);
        Assert.True(decoded.Complete, decoded.Error);
        var source = guest.Source(Caller, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(Table, 16, 1, 0));
        var callee = new UnitedCallee(new ShaderCallTarget(Callee, 0, decoded.Program),
            System.Security.Cryptography.SHA256.HashData(decoded.Bytes), decoded.Runs);
        // The single-site caller's s_swappc is word 7 (pc 28); the record freshness test uses two sites.
        new ShaderCallUnionStore(path).Save(ShaderStage.Compute, source.Hash,
            (callSites ?? [28]).ToDictionary(static pc => pc, _ => new[] { callee }));
        var field = typeof(ShaderProgramCache).GetField("_callUnionStore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(guest.Programs, new ShaderCallUnionStore(path));
    }

    private static byte[] CompileSpirv(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
