// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Evaluation;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Evaluation.EvaluationTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Evaluation;

public sealed class Gen5DispatchEvaluatorTests
{
    private const uint Vcc = 106;
    private const uint Exec = 126;

    public static TheoryData<string> Kernels => ["104", "107", "masked"];

    private static uint[] Kernel(string name) => name switch
    {
        "104" => ModuloKernel104,
        "107" => ModuloKernel107,
        _ => MaskedKernel,
    };

    [Theory]
    [MemberData(nameof(Kernels))]
    public void TheShippedCopyKernels_AreEvaluable(string name)
    {
        var program = Gen5EvaluableProgram.Create(Decode(Kernel(name)));

        Assert.True(program.IsEvaluable, program.Refusal);
    }

    [Theory]
    [InlineData("104", 10u, 3u, 2u, null, 64u)]
    [InlineData("107", 10u, 3u, 2u, null, 64u)]
    [InlineData("104", 130u, 7u, 3u, null, 64u)]
    [InlineData("107", 130u, 7u, 3u, null, 32u)]
    [InlineData("104", 64u, 0u, 1u, null, 64u)]
    [InlineData("107", 64u, 0u, 1u, null, 64u)]
    [InlineData("107", 100u, 0u, 2u, null, 32u)]
    [InlineData("104", 5u, 1u, 1u, null, 64u)]
    [InlineData("107", 5u, 1u, 1u, null, 64u)]
    [InlineData("107", 40u, 100u, 1u, null, 64u)]
    [InlineData("104", 40u, 100u, 1u, null, 64u)]
    [InlineData("107", 500u, 9u, 2u, null, 64u)]
    [InlineData("107", 100u, 12u, 2u, 70u, 64u)]
    [InlineData("104", 100u, 12u, 2u, 70u, 32u)]
    [InlineData("107", 20u, 5u, 1u, 64u, 64u)]
    [InlineData("107", 200u, 3u, 3u, 150u, 32u)]
    [InlineData("107", 129u, 65537u, 3u, null, 64u)]
    public void TheModuloCopyKernels_PredictTheSourcePattern(string name, uint count, uint period, uint groups, uint? threadLimit, uint waveSize)
    {
        var source = Enumerable.Range(0, 256).Select(index => 0x1000u + (uint)index).ToArray();
        var memory = CopyMemory(count, period, source);
        var program = Gen5EvaluableProgram.Create(Decode(Kernel(name)));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(256, 512), groups, waveSize, threadLimit), memory);

        var threads = Math.Min(groups * 64, threadLimit ?? uint.MaxValue);
        var written = (int)Math.Min(count, threads);
        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
        var prediction = Assert.IsType<Gen5DispatchPrediction>(outcome.Prediction);
        Assert.Equal(DestinationAddress, prediction.Address);
        Assert.Equal(written, prediction.Count);
        for (var index = 0u; index < written; index++)
        {
            Assert.Equal(source[ModuloIndex(index, period)], prediction.Words[(int)index]);
        }
    }

    [Theory]
    [InlineData(10u, 3u)]
    [InlineData(64u, 0u)]
    [InlineData(100u, 0xFFFFFFFFu)]
    [InlineData(70u, 5u)]
    public void TheMaskedCopyKernel_PredictsTheMaskedIndex(uint count, uint mask)
    {
        var source = Enumerable.Range(0, 128).Select(index => 0x2000u + (uint)index).ToArray();
        var memory = CopyMemory(count, mask, source);
        var program = Gen5EvaluableProgram.Create(Decode(MaskedKernel));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(128, 512, DwordBuffer(ControlAddress, 2)), 2), memory);

        var prediction = Assert.IsType<Gen5DispatchPrediction>(outcome.Prediction);
        Assert.Equal((int)count, prediction.Count);
        for (var index = 0u; index < count; index++)
        {
            Assert.Equal(source[index & mask], prediction.Words[(int)index]);
        }

        Assert.Equal(0, outcome.ProbeRuns);
    }

    [Fact]
    public void TheModuloKernel_DividesByAReciprocalThatThePrecisionProbeAccepts()
    {
        var memory = CopyMemory(64, 3, Enumerable.Range(0, 8).Select(index => (uint)index).ToArray());
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel107));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(8, 64), 1), memory);

        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
        Assert.Equal(2, outcome.ProbeRuns);
    }

    [Theory]
    [InlineData(SourceAddress, Gen5EvaluationRefusal.InputOwned)]
    [InlineData(ControlAddress, Gen5EvaluationRefusal.InputOwned)]
    public void AKernelWhoseInputTheGpuOwns_IsRefused(ulong owned, Gen5EvaluationRefusal expected)
    {
        var memory = CopyMemory(16, 4, [1, 2, 3, 4]);
        memory.Owned = (owned, sizeof(uint));
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel107));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(4, 64), 1), memory);

        Assert.Equal(expected, outcome.Refusal);
        Assert.Null(outcome.Prediction);
    }

    [Fact]
    public void AStorePastTheDestinationRecords_IsRefused()
    {
        var memory = CopyMemory(50, 4, [1, 2, 3, 4]);
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel104));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(4, 20), 1), memory);

        Assert.Equal(Gen5EvaluationRefusal.OutOfBounds, outcome.Refusal);
    }

    [Fact]
    public void ADispatchPastTheLaneBudget_IsRefusedBeforeAnyLaneRuns()
    {
        var memory = CopyMemory(16, 4, [1, 2, 3, 4]);
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel107));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, new Gen5DispatchRequest
        {
            UserData = CopyUserData(4, 64),
            ThreadsX = 64,
            GroupsX = 10000,
            LaneOperationBudget = 4_000_000,
        }, memory);

        Assert.Equal(Gen5EvaluationRefusal.LaneLimit, outcome.Refusal);
        Assert.Equal(0, outcome.LaneOperations);
        Assert.Equal(0, memory.Reads);
    }

    [Theory]
    [InlineData(64u, 4096L, 1_000_000L, Gen5EvaluationRefusal.None)]
    [InlineData(65u, 4096L, 1_000_000L, Gen5EvaluationRefusal.ThreadLimit)]
    [InlineData(64u, 1_000_000L, 4096L, Gen5EvaluationRefusal.LaneLimit)]
    [InlineData(1u, 1_000_000L, 63L, Gen5EvaluationRefusal.LaneLimit)]
    public void ThePrecheck_RefusesFromTheDispatchShapeAlone(uint groups, long threadBudget, long laneBudget, Gen5EvaluationRefusal expected)
    {
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel104));
        var request = new Gen5DispatchRequest
        {
            UserData = CopyUserData(4, 64),
            ThreadsX = 64,
            GroupsX = groups,
            ThreadBudget = threadBudget,
            LaneOperationBudget = laneBudget,
        };

        var refusal = Gen5DispatchEvaluator.Precheck(program, request, out var threads);

        Assert.Equal(expected, refusal);
        Assert.Equal(groups * 64ul, threads);
    }

    [Fact]
    public void AResultPastTheOutputBudget_IsRefused()
    {
        var memory = CopyMemory(128, 4, [1, 2, 3, 4]);
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel104));
        var request = CopyRequest(CopyUserData(4, 256), 2);

        var refused = Gen5DispatchEvaluator.Evaluate(program, new Gen5DispatchRequest
        {
            UserData = request.UserData,
            SystemRegisters = request.SystemRegisters,
            ThreadsX = 64,
            GroupsX = 2,
            OutputDwordBudget = 100,
        }, memory);
        var predicted = Gen5DispatchEvaluator.Evaluate(program, request, memory);

        Assert.Equal(Gen5EvaluationRefusal.OutputTooLarge, refused.Refusal);
        Assert.Equal(128, predicted.Prediction!.Count);
    }

    [Fact]
    public void AnInputCoveredFromItsOwnAddressOn_IsReadInOnePiece()
    {
        var memory = new SplitMemory(CopyMemory(16, 0, Enumerable.Range(0, 16).Select(index => 0x100u + (uint)index).ToArray()), SourceAddress + 8);
        var program = Gen5EvaluableProgram.Create(Decode(ModuloKernel104));

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(16, 64), 1), memory);

        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
        Assert.Equal(Enumerable.Range(0, 16).Select(index => 0x100u + (uint)index), outcome.Prediction!.Words.ToArray());
        Assert.True(memory.SplitReads <= 4, $"reads across the split: {memory.SplitReads}");
    }

    // A source whose bytes before the split cannot be read together with the bytes after it.
    private sealed class SplitMemory(EvaluationMemory memory, ulong split) : IGen5EvaluationMemory
    {
        public int SplitReads { get; private set; }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < split && address + (ulong)destination.Length > split)
            {
                SplitReads++;
                return false;
            }

            return memory.TryRead(address, destination);
        }
    }

    [Fact]
    public void ADifferentKernelEncoding_IsStillEvaluated()
    {
        var changed = (uint[])ModuloKernel104.Clone();
        changed[^6] = 0xE0002000 ^ 0x1000;
        var program = Gen5EvaluableProgram.Create(Decode(changed));
        var memory = CopyMemory(16, 4, [1, 2, 3, 4]);

        var outcome = Gen5DispatchEvaluator.Evaluate(program, CopyRequest(CopyUserData(4, 64), 1), memory);

        Assert.True(program.IsEvaluable, program.Refusal);
        Assert.NotEqual(Gen5EvaluationRefusal.NotEvaluable, outcome.Refusal);
    }

    private static Gen5DispatchOutcome RunLanes(uint waveSize, params Gen5ShaderInstruction[] body)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        foreach (var instruction in body)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        instructions.Add(Vop2(pc, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)));
        instructions.Add(BufferAccess(pc + 8, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        instructions.Add(EndProgram(pc + 16));
        var program = Gen5EvaluableProgram.Create(Program([.. instructions]));
        Assert.True(program.IsEvaluable, program.Refusal);
        var memory = new EvaluationMemory();
        memory.WriteWords(SourceAddress, Enumerable.Range(0, 64).Select(index => 0x100u + (uint)index).ToArray());
        uint[] userData = [.. DwordBuffer(SourceAddress, 64), .. DwordBuffer(DestinationAddress, 64)];
        return Gen5DispatchEvaluator.Evaluate(program, new Gen5DispatchRequest { UserData = userData, ThreadsX = 64, WaveSize = waveSize }, memory);
    }

    private static uint[] Lanes(Gen5DispatchOutcome outcome)
    {
        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
        return Assert.IsType<Gen5DispatchPrediction>(outcome.Prediction).Words.ToArray();
    }

    private static Gen5Operand Literal(float value) => new(Gen5OperandKind.LiteralConstant, BitConverter.SingleToUInt32Bits(value));

    [Theory]
    [InlineData(3e9f, 3_000_000_000u)]
    [InlineData(5e9f, uint.MaxValue)]
    [InlineData(float.PositiveInfinity, uint.MaxValue)]
    [InlineData(-1f, 0u)]
    [InlineData(-0f, 0u)]
    [InlineData(0.75f, 0u)]
    [InlineData(17.9f, 17u)]
    public void ConvertToUnsigned_Saturates(float value, uint expected)
    {
        var lanes = Lanes(RunLanes(64, Vop1(0, "VCvtU32F32", 4, Literal(value))));

        Assert.All(lanes, lane => Assert.Equal(expected, lane));
    }

    [Theory]
    [InlineData(32u)]
    [InlineData(64u)]
    public void ACompareThatNarrowsExec_SkipsTheBranchedOverStoreOfInactiveLanes(uint waveSize)
    {
        var outcome = RunLanes(waveSize,
            Vop1(0, "VMovB32", 4, Gen5Operand.Vector(0)),
            Vopc(0, "VCmpxGtU32", Operand(10), 0),
            Branch(0, "SCbranchExecz", 5));

        Assert.Equal(Enumerable.Range(0, 10).Select(index => (uint)index), Lanes(outcome));
    }

    [Fact]
    public void StoresOfEveryOtherLane_LeaveHolesSoThePredictionIsNotDense()
    {
        var outcome = RunLanes(64,
            Vop1(0, "VMovB32", 4, Gen5Operand.Vector(0)),
            Vop2(0, "VAndB32", 6, Operand(1), Gen5Operand.Vector(0)),
            Vopc(0, "VCmpxEqU32", Operand(0), 6));

        var prediction = Assert.IsType<Gen5DispatchPrediction>(outcome.Prediction);
        Assert.Equal(63, prediction.Count);
        Assert.Equal(32, prediction.WrittenCount);
        Assert.False(prediction.Dense);
        Assert.True(Assert.IsType<Gen5DispatchPrediction>(RunLanes(64, Vop1(0, "VMovB32", 4, Gen5Operand.Vector(0))).Prediction).Dense);
    }

    [Fact]
    public void ASelectByTheCompareMask_PicksPerLane()
    {
        var lanes = Lanes(RunLanes(64,
            Vopc(0, "VCmpGtU32", Operand(40), 0),
            Vop2(0, "VCndmaskB32", 4, Operand(7), Operand(9))));

        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane < 40 ? 9u : 7u, lanes[lane]);
        }
    }

    [Theory]
    [InlineData(32u, 3u)]
    [InlineData(64u, 40u)]
    [InlineData(64u, 3u)]
    public void ReadFirstLane_TakesTheFirstActiveLaneOfItsWave(uint waveSize, uint first)
    {
        var outcome = RunLanes(waveSize,
            Vopc(0, "VCmpxLeU32", Operand(first), 0),
            ReadFirstLane(0, 20, 0),
            Sop1(0, "SMovB64", Exec, Gen5Operand.Source(193)),
            Vop1(0, "VMovB32", 4, Gen5Operand.Scalar(20)));

        var lanes = Lanes(outcome);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(waveSize == 32 && lane >= 32 ? 32u : first, lanes[lane]);
        }
    }

    [Fact]
    public void ReadFirstLane_WithoutActiveLanes_IsRefused()
    {
        var outcome = RunLanes(64,
            Sop1(0, "SMovB64", Exec, Operand(0)),
            ReadFirstLane(0, 20, 0),
            Sop1(0, "SMovB64", Exec, Gen5Operand.Source(193)),
            Vop1(0, "VMovB32", 4, Gen5Operand.Scalar(20)));

        Assert.Equal(Gen5EvaluationRefusal.NoActiveLane, outcome.Refusal);
    }

    [Fact]
    public void ABufferAddress_AddsOffsetScalarOffsetVectorOffsetAndIndexTimesStride()
    {
        var load = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mubuf, "BufferLoadDword", [0u, 0u],
            [Gen5Operand.Vector(8), Gen5Operand.Scalar(0), Gen5Operand.Scalar(20)], [Gen5Operand.Vector(4)],
            new Gen5BufferMemoryControl(1, 8, 4, 0, 8, IndexEnabled: true, OffsetEnabled: true, Glc: false, Slc: false));

        var lanes = Lanes(RunLanes(64,
            Sop1(0, "SMovB32", 20, Operand(16)),
            Vop2(0, "VAndB32", 8, Operand(7), Gen5Operand.Vector(0)),
            MoveVector(0, 9, 4),
            load));

        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(0x100u + (uint)(lane & 7) + 7, lanes[lane]);
        }
    }

    [Fact]
    public void AnUndefinedRegister_IsRefused()
    {
        Assert.Equal(Gen5EvaluationRefusal.UndefinedRegister, RunLanes(64, Vop1(0, "VMovB32", 4, Gen5Operand.Vector(7))).Refusal);
    }

    [Fact]
    public void ALoadOfADwordTheDispatchWrites_IsRefused()
    {
        var outcome = RunLanes(64,
            MoveVector(0, 4, 1),
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            BufferAccess(0, "BufferLoadDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));

        Assert.Equal(Gen5EvaluationRefusal.ReadAfterWrite, outcome.Refusal);
    }

    [Fact]
    public void LanesStoringDifferentValuesToOneDword_AreRefused()
    {
        var outcome = RunLanes(64,
            Vop1(0, "VMovB32", 4, Gen5Operand.Vector(0)),
            MoveVector(0, 5, 0),
            BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));

        Assert.Equal(Gen5EvaluationRefusal.WriteConflict, outcome.Refusal);
    }

    [Fact]
    public void ABitfieldMaskPastBit31_IsUndefinedAndRefused()
    {
        var outcome = RunLanes(64, Sop2(0, "SBfmB32", 20, Operand(20), Operand(16)));

        Assert.Equal(Gen5EvaluationRefusal.UndefinedBehaviour, outcome.Refusal);
    }

    [Fact]
    public void ScalarArithmetic_FollowsTheShiftAndMaskRules()
    {
        var lanes = Lanes(RunLanes(64,
            Sop1(0, "SMovB32", 20, Operand(0xFFFF_FFF0u)),
            Sop2(0, "SAddI32", 21, Gen5Operand.Scalar(20), Operand(32)),
            Sop2(0, "SLshlB32", 22, Gen5Operand.Scalar(21), Operand(33)),
            Sop2(0, "SLshl2AddU32", 23, Gen5Operand.Scalar(22), Operand(4)),
            Sop2(0, "SBfmB32", 24, Operand(8), Operand(4)),
            Sop2(0, "SMulHiU32", 25, Operand(0x8000_0000u), Operand(4)),
            Sop2(0, "SSubU32", 26, Gen5Operand.Scalar(23), Gen5Operand.Scalar(25)),
            Sop2(0, "SAddI32", 26, Gen5Operand.Scalar(26), Gen5Operand.Scalar(24)),
            Vop1(0, "VMovB32", 4, Gen5Operand.Scalar(26))));

        Assert.All(lanes, lane => Assert.Equal(0x84u - 2u + 0xFF0u, lane));
    }

    [Fact]
    public void AnIndexedCopyLoop_WithABackwardBranch_RunsEachLaneItsCount()
    {
        var lanes = Lanes(RunLanes(64,
            Vop2(0, "VAndB32", 3, Operand(7), Gen5Operand.Vector(0)),
            MoveVector(0, 4, 0),
            Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)),
            Vopc(0, "VCmpxGtU32", Gen5Operand.Vector(3), 4),
            Branch(0, "SCbranchExecz", 5),
            Vop2(0, "VAddI32", 4, Operand(1), Gen5Operand.Vector(4)),
            Branch(0, "SBranch", -7),
            Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20))));

        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal((uint)(lane & 7), lanes[lane]);
        }
    }
}
