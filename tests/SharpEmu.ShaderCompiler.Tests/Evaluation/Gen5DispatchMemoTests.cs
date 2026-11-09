// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Evaluation;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Evaluation.EvaluationTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Evaluation;

public sealed class Gen5DispatchMemoTests
{
    private static readonly uint[] Source = Enumerable.Range(0, 256).Select(index => 0x1000u + (uint)index).ToArray();

    private static Gen5EvaluableProgram Kernel(string name) => Gen5EvaluableProgram.Create(Decode(name switch
    {
        "104" => ModuloKernel104,
        "107" => ModuloKernel107,
        _ => MaskedKernel,
    }));

    private static uint[] Words(Gen5DispatchOutcome outcome)
    {
        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
        return Assert.IsType<Gen5DispatchPrediction>(outcome.Prediction).Words.ToArray();
    }

    [Theory]
    [InlineData("104")]
    [InlineData("107")]
    public void ARepeatedIdenticalDispatch_TakesTheStoredWordsWithoutRunning(string name)
    {
        var program = Kernel(name);
        var memory = CopyMemory(130, 7, Source);
        var request = CopyRequest(CopyUserData(256, 512), 3);
        var memo = new Gen5DispatchMemo();

        var first = memo.Evaluate(program, request, memory);
        var second = memo.Evaluate(program, request, memory);

        Assert.Equal(Gen5MemoLookup.Absent, first.Memo);
        Assert.True(first.LaneOperations > 0);
        Assert.Equal(Gen5MemoLookup.Hit, second.Memo);
        Assert.Equal(0, second.LaneOperations);
        Assert.Equal(0, second.ProbeRuns);
        Assert.Equal(Words(first), Words(second));
        Assert.Equal(Words(Gen5DispatchEvaluator.Evaluate(program, request, memory)), Words(second));
        Assert.Equal(first.Prediction!.Address, second.Prediction!.Address);
        Assert.NotSame(first.Prediction.Buffer, second.Prediction.Buffer);
    }

    [Fact]
    public void AChangedInputWord_MissesAndTheNewResultIsStored()
    {
        var program = Kernel("107");
        var memory = CopyMemory(130, 7, Source);
        var request = CopyRequest(CopyUserData(256, 512), 3);
        var memo = new Gen5DispatchMemo();
        _ = memo.Evaluate(program, request, memory);

        memory.WriteWords(SourceAddress + 3 * sizeof(uint), 0xDEAD_BEEF);
        var changed = memo.Evaluate(program, request, memory);
        var again = memo.Evaluate(program, request, memory);

        Assert.Equal(Gen5MemoLookup.Changed, changed.Memo);
        Assert.Equal(0xDEAD_BEEFu, Words(changed)[3]);
        Assert.Equal(0xDEAD_BEEFu, Words(changed)[10]);
        Assert.Equal(Gen5MemoLookup.Hit, again.Memo);
        Assert.Equal(Words(changed), Words(again));
        Assert.Equal(1, memo.Count);
    }

    [Theory]
    [InlineData(ControlAddress)]
    [InlineData(ControlAddress + sizeof(uint))]
    public void AChangedControlWord_Misses(ulong address)
    {
        var program = Kernel("104");
        var memory = CopyMemory(130, 7, Source);
        var request = CopyRequest(CopyUserData(256, 512), 3);
        var memo = new Gen5DispatchMemo();
        _ = memo.Evaluate(program, request, memory);

        memory.WriteWords(address, 5);
        var outcome = memo.Evaluate(program, request, memory);

        Assert.Equal(Gen5MemoLookup.Changed, outcome.Memo);
        Assert.Equal(Words(Gen5DispatchEvaluator.Evaluate(program, request, memory)), Words(outcome));
    }

    [Fact]
    public void AWordTheKernelNeverRead_LeavesTheResultReusable()
    {
        var program = Kernel("107");
        var memory = CopyMemory(130, 7, Source);
        var request = CopyRequest(CopyUserData(256, 512), 3);
        var memo = new Gen5DispatchMemo();
        _ = memo.Evaluate(program, request, memory);

        memory.WriteWords(SourceAddress + 100 * sizeof(uint), 0xDEAD_BEEF);
        memory.WriteWords(DestinationAddress + 5 * sizeof(uint), 0xDEAD_BEEF);

        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(program, request, memory).Memo);
    }

    [Fact]
    public void OtherUserDataOrDispatchDimensions_AreSeparateEntries()
    {
        var program = Kernel("107");
        var memory = CopyMemory(130, 7, Source);
        var memo = new Gen5DispatchMemo();
        _ = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 3), memory);

        var userData = memo.Evaluate(program, CopyRequest(CopyUserData(255, 512), 3), memory);
        var groups = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 2), memory);
        var waveSize = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 3, waveSize: 32), memory);
        var limit = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 3, threadLimit: 100), memory);

        Assert.Equal(Gen5MemoLookup.Absent, userData.Memo);
        Assert.Equal(Gen5MemoLookup.Absent, groups.Memo);
        Assert.Equal(Gen5MemoLookup.Absent, waveSize.Memo);
        Assert.Equal(Gen5MemoLookup.Absent, limit.Memo);
        Assert.Equal(128, Words(groups).Length);
        Assert.Equal(100, Words(limit).Length);
        Assert.Equal(5, memo.Count);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 2), memory).Memo);
    }

    [Fact]
    public void TheHolesOfASparseResult_AreInputsToo()
    {
        var (program, request, memory) = EveryOtherLane();
        var memo = new Gen5DispatchMemo();
        var first = memo.Evaluate(program, request, memory);
        Assert.False(first.Prediction!.Dense);

        memory.WriteWords(DestinationAddress, 0xAAAA_AAAA);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(program, request, memory).Memo);

        memory.WriteWords(DestinationAddress + sizeof(uint), 0xBBBB_BBBB);
        var hole = memo.Evaluate(program, request, memory);

        Assert.Equal(Gen5MemoLookup.Changed, hole.Memo);
        Assert.Equal(0xBBBB_BBBBu, Words(hole)[1]);
    }

    [Fact]
    public void ARefusedEvaluation_DropsTheStaleEntry()
    {
        var program = Kernel("107");
        var memory = CopyMemory(16, 4, [1, 2, 3, 4]);
        var request = CopyRequest(CopyUserData(4, 64), 1);
        var memo = new Gen5DispatchMemo();
        _ = memo.Evaluate(program, request, memory);

        memory.Owned = (SourceAddress, sizeof(uint));
        var refused = memo.Evaluate(program, request, memory);

        Assert.Equal(Gen5EvaluationRefusal.InputOwned, refused.Refusal);
        Assert.Equal(Gen5MemoLookup.Changed, refused.Memo);
        Assert.Equal(0, memo.Count);
        Assert.Equal(0, memo.Bytes);
    }

    [Fact]
    public void EachProgramKeepsItsMostRecentEntries()
    {
        var program = Kernel("107");
        var memory = CopyMemory(130, 7, Source);
        var memo = new Gen5DispatchMemo(entriesPerProgram: 2);
        _ = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 1), memory);
        _ = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 2), memory);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 1), memory).Memo);

        _ = memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 3), memory);

        Assert.Equal(2, memo.Count);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 1), memory).Memo);
        Assert.Equal(Gen5MemoLookup.Absent, memo.Evaluate(program, CopyRequest(CopyUserData(256, 512), 2), memory).Memo);
    }

    [Fact]
    public void TheByteCapEvictsTheLeastRecentlyUsedEntries()
    {
        var memory = CopyMemory(64, 7, Source);
        var programs = Enumerable.Range(0, 6).Select(index => Kernel(index % 2 == 0 ? "104" : "107")).ToArray();
        var request = CopyRequest(CopyUserData(256, 512), 1);
        var probe = new Gen5DispatchMemo();
        _ = probe.Evaluate(programs[0], request, memory);
        var entryBytes = probe.Bytes;
        var capacity = entryBytes * 4 + entryBytes / 2;
        var memo = new Gen5DispatchMemo(byteCapacity: capacity);

        foreach (var program in programs)
        {
            _ = memo.Evaluate(program, request, memory);
        }

        Assert.True(memo.Bytes <= capacity);
        Assert.Equal(4, memo.Count);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(programs[5], request, memory).Memo);
        Assert.Equal(Gen5MemoLookup.Hit, memo.Evaluate(programs[2], request, memory).Memo);
        Assert.Equal(Gen5MemoLookup.Absent, memo.Evaluate(programs[1], request, memory).Memo);
        Assert.Equal(Gen5MemoLookup.Absent, memo.Evaluate(programs[0], request, memory).Memo);
    }

    [Theory]
    [InlineData("104")]
    [InlineData("107")]
    [InlineData("masked")]
    public void MemoResults_EqualFreshEvaluationsOverRandomInputs(string name)
    {
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x5EED);
        var program = Kernel(name);
        var memo = new Gen5DispatchMemo(entriesPerProgram: 4);
        var source = Enumerable.Range(0, 256).Select(_ => (uint)random.Next()).ToArray();
        var memory = CopyMemory(64, 3, source);
        var hits = 0;
        for (var round = 0; round < 400; round++)
        {
            switch (random.Next(8))
            {
                case 0:
                    memory.WriteWords(SourceAddress + (ulong)random.Next(256) * sizeof(uint), (uint)random.Next());
                    break;
                case 1:
                    memory.WriteWords(ControlAddress, (uint)random.Next(1, 200), (uint)random.Next(0, 300));
                    break;
            }

            var groups = (uint)random.Next(1, 4);
            var request = name == "masked"
                ? CopyRequest(CopyUserData(256, 512, DwordBuffer(ControlAddress, 2)), groups)
                : CopyRequest(CopyUserData(256, 512), groups, random.Next(2) == 0 ? 32u : 64u);
            var fresh = Gen5DispatchEvaluator.Evaluate(program, request, memory);
            var memoized = memo.Evaluate(program, request, memory);

            Assert.Equal(fresh.Refusal, memoized.Refusal);
            if (fresh.Prediction is { } prediction)
            {
                Assert.Equal(prediction.Address, memoized.Prediction!.Address);
                Assert.Equal(prediction.WrittenCount, memoized.Prediction.WrittenCount);
                Assert.Equal(prediction.Words.ToArray(), memoized.Prediction.Words.ToArray());
            }

            hits += memoized.Memo == Gen5MemoLookup.Hit ? 1 : 0;
        }

        Assert.True(hits > 20, FormattableString.Invariant($"hits={hits}"));
    }

    private static (Gen5EvaluableProgram Program, Gen5DispatchRequest Request, EvaluationMemory Memory) EveryOtherLane()
    {
        Gen5ShaderInstruction[] body =
        [
            Vop1(0, "VMovB32", 4, Gen5Operand.Vector(0)),
            Vop2(8, "VAndB32", 6, Operand(1), Gen5Operand.Vector(0)),
            Vopc(16, "VCmpxEqU32", Operand(0), 6),
            Vop2(24, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(32, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(40),
        ];
        var program = Gen5EvaluableProgram.Create(Program(body));
        Assert.True(program.IsEvaluable, program.Refusal);
        var memory = new EvaluationMemory();
        uint[] userData = [.. DwordBuffer(SourceAddress, 64), .. DwordBuffer(DestinationAddress, 64)];
        return (program, new Gen5DispatchRequest { UserData = userData, ThreadsX = 64, WaveSize = 64 }, memory);
    }
}
