// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Mode = SharpEmu.ShaderCompiler.Resources.CompiledDeviceAddressRanges.Mode;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

[CollectionDefinition("CompiledDeviceAddressRanges", DisableParallelization = true)]
public sealed class CompiledDeviceAddressRangeCollection;

// SHARPEMU_COMPILED_RANGES against the interpreter: the same ranges or exception and the same reads in the same order,
// with the switch off and on. The mode is process-wide, so these tests run alone.
[Collection("CompiledDeviceAddressRanges")]
public sealed partial class CompiledDeviceAddressRangeTests
{
    public enum Path { Off, On }

    // Sets the mode for one test and restores it.
    private sealed class Scope : IDisposable
    {
        private readonly Mode _mode = CompiledDeviceAddressRanges.Configured;

        public Scope(Path path) => CompiledDeviceAddressRanges.Configured = path switch
        {
            Path.On => Mode.On,
            _ => Mode.Off,
        };

        public void Dispose()
        {
            CompiledDeviceAddressRanges.Configured = _mode;
        }
    }

    // Words by address and a log of every read; an address in FailOnce fails its first read.
    private sealed class Memory(Dictionary<ulong, uint> words)
    {
        public readonly List<ulong> Reads = [];
        public readonly HashSet<ulong> FailOnce = [];

        public bool Read(ulong address, out uint word)
        {
            Reads.Add(address);
            word = 0;
            return !FailOnce.Remove(address) && words.TryGetValue(address, out word);
        }
    }

    private sealed record Outcome(DeviceAddressRange[]? Ranges, string? Error, ulong[] Reads);

    private sealed record Scenario(Func<ShaderResourcePlan> Extract, uint[] UserData, Dictionary<ulong, uint> Words,
        ulong[]? FailOnce = null, Action<ShaderResourcePlan>? Prepare = null);

    // A fresh plan built under the path's mode, evaluated once through DeviceAddressRangePlanner.Evaluate.
    private static Outcome Run(Path path, Scenario scenario)
    {
        using var scope = new Scope(path);
        var plan = scenario.Extract();
        scenario.Prepare?.Invoke(plan);
        plan.CompileEvaluatorNow();
        if (path == Path.Off) Assert.Null(plan.CompiledRanges);
        else Assert.Null(Assert.IsType<CompiledDeviceAddressRanges>(plan.CompiledRanges).Refusal);
        var memory = new Memory(scenario.Words);
        foreach (var address in scenario.FailOnce ?? []) memory.FailOnce.Add(address);
        DeviceAddressRange[]? ranges = null;
        string? error = null;
        try
        {
            ranges = DeviceAddressRangePlanner.Evaluate(plan, Inputs(scenario.UserData, memory.Read));
        }
        catch (ResourcePlanException exception)
        {
            error = exception.Message;
        }

        return new(ranges, error, [.. memory.Reads]);
    }

    // Runs both paths; the compiled code must match the interpreter's ranges or exception and its reads.
    private static Outcome RunAll(Scenario scenario)
    {
        var reference = Run(Path.Off, scenario);
        var outcome = Run(Path.On, scenario);
        Assert.Equal(reference.Error, outcome.Error);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Equal(reference.Reads, outcome.Reads);
        return reference;
    }

    private static Dictionary<ulong, uint> Words(params (ulong Address, uint Word)[] words) =>
        words.ToDictionary(static entry => entry.Address, static entry => entry.Word);

    private static ShaderResourcePlan TablePlan() => Extract(Program(
        ScalarLoad(0, 0, 8, 2), ScalarLoad(8, 2, 10, 4),
        Vop1(16, "VMovB32", 0, Operand(0)),
        GlobalAccess(20, "GlobalLoadDword", 8, offset: 8),
        GlobalAccess(28, "GlobalStoreDword", 10, offset: -4),
        GlobalAccess(36, "GlobalLoadDwordx2", 12, offset: 16, dwords: 2),
        EndProgram(44)), userDataCount: 4);

    private static readonly Dictionary<ulong, uint> TableWords =
        Words((0x1000, 0x5000), (0x1004, 0), (0x2000, 0x6000), (0x2004, 0), (0x2008, 0x7000), (0x200C, 0));

    // A global load of 16 bytes at s[0:1], then function buffers through V#1 at 0x1000 (0x3000, stride 16, 4 records) and
    // V#2 at 0x2000 (0x1000, 16 bytes, the global load's range): read, written, read again, V#2.
    private static ShaderResourcePlan FunctionPlan() => Extract(Program(
        ScalarLoad(0, 0, 4, 4), ScalarLoad(8, 2, 8, 4),
        Vop1(16, "VMovB32", 0, Operand(0)),
        GlobalAccess(20, "GlobalLoadDwordx4", 0, dwords: 4),
        BufferLoad(28, 4), BufferStore(36, 4), BufferLoad(44, 4, offset: 4), BufferLoad(52, 8),
        EndProgram(60)) with { FunctionBufferAccesses = new HashSet<uint> { 28, 36, 44, 52 } }, userDataCount: 4);

    private static readonly Dictionary<ulong, uint> FunctionWords = Words(
        (0x1000, 0x3000), (0x1004, 16u << 16), (0x1008, 4), (0x100C, 0),
        (0x2000, 0x1000), (0x2004, 0), (0x2008, 16), (0x200C, 0));

    private static ShaderResourcePlan DeepPlan()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (var index = 0; index < 70; index++, pc += 4)
            instructions.Add(Sop2(pc, index % 2 == 0 ? "SAddU32" : "SXorB32", 0, Gen5Operand.Scalar(0), Operand((uint)index + 1)));
        instructions.Add(Vop1(pc, "VMovB32", 0, Operand(0)));
        instructions.Add(GlobalAccess(pc + 4, "GlobalLoadDword", 0));
        instructions.Add(EndProgram(pc + 12));
        return Extract(Program([.. instructions]), userDataCount: 2);
    }

    private static void WithCallRecordCallee(ShaderResourcePlan plan) =>
        typeof(ShaderResourcePlan).GetProperty(nameof(ShaderResourcePlan.CallRecordCallees))!
            .SetValue(plan, new List<CallRecordCalleePlan> { new([], [], 0) });

    private static void MarkDeviceDescriptor(ShaderResourcePlan plan, uint pc)
    {
        Assert.True(plan.Memory.TryGetIndex(pc, 0, out var index));
        plan.Memory[index].DeviceDescriptor = true;
    }

    private static Scenario Named(string name) => name switch
    {
        "bounded" => new(() => Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalLoadDword", 0, offset: 8),
            GlobalAccess(12, "GlobalLoadDwordx4", 0, offset: 32, dwords: 4),
            EndProgram(20))), [0x1000, 0], []),
        "negative" => new(() => Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalStoreDword", 0, offset: -8),
            GlobalAccess(12, "GlobalLoadDwordx2", 0, offset: 8, dwords: 2),
            EndProgram(20))), [0x2008, 0], []),
        "runtime_offset" => new(() => Extract(Program(GlobalAccess(0, "GlobalLoadDword", 0, offset: 8, vectorAddress: 0), EndProgram(8))),
            [0x1000, 0], []),
        "unplannable" => new(() => Extract(Program(GlobalAccess(0, "FlatStoreDword", 0, vectorAddress: 2), EndProgram(8))), [], []),
        "table" => new(TablePlan, [0x1000, 0, 0x2000, 0], TableWords),
        "table_failures" => new(TablePlan, [0x1000, 0, 0x2000, 0], TableWords, [0x1004, 0x2000]),
        "null_base" => new(TablePlan, [0, 0, 0x2000, 0], TableWords),
        "missing_user_data" => new(TablePlan, [0x1000, 0], TableWords),
        "arithmetic" => new(() => Extract(Program(
            ScalarLoad(0, 0, 8, 2),
            Sop2(8, "SAddU32", 10, Gen5Operand.Scalar(8), Operand(0x100)),
            Sop2(12, "SAddcU32", 11, Gen5Operand.Scalar(9), Operand(0)),
            Vop1(16, "VMovB32", 0, Operand(0)),
            GlobalAccess(20, "GlobalLoadDword", 10),
            EndProgram(28)), userDataCount: 2), [0x1000, 0], Words((0x1000, 0xFFFFFF80), (0x1004, 1))),
        "buffer_word" => new(() => Extract(Program(
            ScalarBufferLoad(0, 0, 8, 2, immediateOffset: 8),
            Vop1(8, "VMovB32", 0, Operand(0)),
            GlobalAccess(12, "GlobalLoadDword", 8),
            EndProgram(20)), userDataCount: 4), [0x3000, 0, 16, 0], Words((0x3008, 0x9000), (0x300C, 0))),
        "buffer_word_past_records" => new(() => Extract(Program(
            ScalarBufferLoad(0, 0, 8, 2, immediateOffset: 8),
            Vop1(8, "VMovB32", 0, Operand(0)),
            GlobalAccess(12, "GlobalLoadDword", 8),
            EndProgram(20)), userDataCount: 4), [0x3000, 0, 12, 0], Words((0x3008, 0x9000), (0x300C, 0))),
        "absolute" => new(() => Extract(Program(
            Sop1(0, "SMovB32", 4, Operand(0x10000)), Sop1(4, "SMovB32", 5, Operand(0)),
            ScalarLoad(8, 4, 8, 2),
            Vop1(16, "VMovB32", 0, Operand(0)),
            GlobalAccess(20, "GlobalLoadDword", 8),
            EndProgram(28)), userDataCount: 2), [], []),
        "first_lane" => new(() => Extract(Program(
            Vop1(0, "VMovB32", 1, Gen5Operand.Scalar(0)),
            ReadFirstLane(4, 8, 1),
            Sop1(8, "SMovB32", 9, Gen5Operand.Scalar(1)),
            Vop1(12, "VMovB32", 0, Operand(0)),
            GlobalAccess(16, "GlobalLoadDword", 8),
            EndProgram(24)), userDataCount: 2), [0x4000, 0], []),
        // The lane select under the active mask takes its first value; the plain select would fail on the compare.
        "first_lane_select" => new(() => Extract(Program(
            Vopc(0, "VCmpxLtU32", Operand(3), 5),
            Vop1(4, "VMovB32", 1, Gen5Operand.Scalar(0)),
            ReadFirstLane(8, 8, 1),
            Sop1(12, "SMovB32", 9, Gen5Operand.Scalar(1)),
            Vop1(16, "VMovB32", 0, Operand(0)),
            GlobalAccess(20, "GlobalLoadDword", 8),
            EndProgram(28)), userDataCount: 2), [0x4000, 0], []),
        // A first-lane value evaluates again in its own evaluator: the table word it wraps is read again.
        "first_lane_read" => new(() => Extract(Program(
            ScalarLoad(0, 0, 8, 2),
            Vop1(8, "VMovB32", 1, Gen5Operand.Scalar(8)),
            ReadFirstLane(12, 10, 1),
            Sop1(16, "SMovB32", 11, Gen5Operand.Scalar(9)),
            Vop1(20, "VMovB32", 0, Operand(0)),
            GlobalAccess(24, "GlobalLoadDword", 8),
            GlobalAccess(32, "GlobalLoadDword", 10),
            EndProgram(40)), userDataCount: 2), [0x1000, 0], Words((0x1000, 0x5000), (0x1004, 0))),
        "function" => new(FunctionPlan, [0x1000, 0, 0x2000, 0], FunctionWords),
        "function_unresolved" => new(() => Extract(Program(
            ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), EndProgram(16)) with { FunctionBufferAccesses = new HashSet<uint> { 8 } },
            userDataCount: 2), [0x1000, 0], Words((0x1000, 0x3000))),
        "function_unresolved_callee" => new(() => Extract(Program(
            ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), BufferLoad(16, 8), EndProgram(24))
            with { FunctionBufferAccesses = new HashSet<uint> { 8, 16 } }),
            [0x1000, 0, 0, 0, 0, 0, 0, 0, 0x5000, 0, 8, 0], Words((0x1000, 0x3000)), Prepare: WithCallRecordCallee),
        "function_missing_descriptor" => new(() => Extract(Program(
            ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4),
            Vop1(16, "VMovB32", 0, Operand(0)),
            GlobalAccess(20, "GlobalLoadDword", 2),
            BufferLoad(28, 4),
            EndProgram(36)) with { FunctionBufferAccesses = new HashSet<uint> { 8, 20, 28 } }, userDataCount: 4),
            [0x1000, 0, 0x2000, 0], FunctionWords, Prepare: plan => MarkDeviceDescriptor(plan, 20)),
        "function_swizzled" => new(() => Extract(Program(BufferLoad(0, 0), EndProgram(8))
            with { FunctionBufferAccesses = new HashSet<uint> { 0 } }), [0x2000, 0x80000000, 10, 0], []),
        "function_swizzled_empty" => new(() => Extract(Program(BufferLoad(0, 0), EndProgram(8))
            with { FunctionBufferAccesses = new HashSet<uint> { 0 } }), [0x2000, 0x80000000, 0, 0], []),
        "function_empty" => new(() => Extract(Program(BufferLoad(0, 0), EndProgram(8))
            with { FunctionBufferAccesses = new HashSet<uint> { 0 } }), [0x2000, 16u << 16, 0, 0], []),
        "function_wraps" => new(() => Extract(Program(BufferLoad(0, 0), EndProgram(8))
            with { FunctionBufferAccesses = new HashSet<uint> { 0 } }), [0xFFFFFFF0, 0xFFFF | (16u << 16), 2, 0], []),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("bounded")]
    [InlineData("negative")]
    [InlineData("runtime_offset")]
    [InlineData("unplannable")]
    [InlineData("table")]
    [InlineData("table_failures")]
    [InlineData("null_base")]
    [InlineData("missing_user_data")]
    [InlineData("arithmetic")]
    [InlineData("buffer_word")]
    [InlineData("buffer_word_past_records")]
    [InlineData("absolute")]
    [InlineData("first_lane")]
    [InlineData("first_lane_select")]
    [InlineData("first_lane_read")]
    [InlineData("function")]
    [InlineData("function_unresolved")]
    [InlineData("function_unresolved_callee")]
    [InlineData("function_missing_descriptor")]
    [InlineData("function_swizzled")]
    [InlineData("function_swizzled_empty")]
    [InlineData("function_empty")]
    [InlineData("function_wraps")]
    public void EveryPathMatchesTheInterpreter(string name) => RunAll(Named(name));

    [Fact]
    public void TableBasesReadOnceInOrder()
    {
        var outcome = RunAll(Named("table"));
        Assert.Null(outcome.Error);
        Assert.Equal(new ulong[] { 0x1000, 0x1004, 0x2000, 0x2004, 0x2008, 0x200C }, outcome.Reads);
        var ranges = outcome.Ranges!;
        Assert.Contains(ranges, range => range.Planned && range.Base == 0x5008 && range.Size == 4 && !range.Written);
        Assert.Contains(ranges, range => range.Planned && range.Base == 0x5FFC && range.Size == 4 && range.Written);
        Assert.Contains(ranges, range => range.Planned && range.Base == 0x7010 && range.Size == 8 && !range.Written);
    }

    // A failed base high leaves its range unplanned, a failed base low also skips its high, and later ranges still plan.
    [Fact]
    public void FailedBasesLeaveOnlyTheirRangesUnplanned()
    {
        var outcome = RunAll(Named("table_failures"));
        Assert.Equal(new ulong[] { 0x1000, 0x1004, 0x2000, 0x2008, 0x200C }, outcome.Reads);
        var ranges = outcome.Ranges!;
        Assert.DoesNotContain(ranges, range => range.Planned && (range.Base == 0x5008 || range.Base == 0x5FFC));
        Assert.Contains(ranges, range => range.Planned && range.Base == 0x7010 && range.Size == 8);
        Assert.Equal(2, ranges.Count(range => !range.Planned && range.Base == 0 && range.Size == 0));
    }

    [Fact]
    public void FirstLaneSelectTakesTheActiveValueAndRereadsInItsOwnEvaluator()
    {
        var select = RunAll(Named("first_lane_select"));
        Assert.Contains(select.Ranges!, range => range.Planned && range.Base == 0x4000);
        var reread = RunAll(Named("first_lane_read"));
        Assert.Equal(new ulong[] { 0x1000, 0x1004, 0x1000 }, reread.Reads);
        Assert.Equal(2, reread.Ranges!.Count(range => range.Planned && range.Base == 0x5000));
    }

    // Function V#s dedupe by (base, size, written) among themselves, in access order; a normal range is not in that set.
    [Fact]
    public void FunctionBuffersDedupeInOrder()
    {
        var outcome = RunAll(Named("function"));
        Assert.Equal(new ulong[] { 0x1000, 0x1004, 0x1008, 0x100C, 0x2000, 0x2004, 0x2008, 0x200C }, outcome.Reads);
        Assert.Equal(new DeviceAddressRange[]
        {
            new(0, 0x1000, 16, true, false),
            new(1, 0x3000, 64, true, false),
            new(2, 0x3000, 64, true, true),
            new(3, 0x1000, 16, true, false),
        }, outcome.Ranges);
    }

    [Theory]
    [InlineData("function_unresolved", DeviceAddressRangePlanner.UnresolvedFunctionBuffer, 2)]
    [InlineData("function_missing_descriptor", DeviceAddressRangePlanner.MissingFunctionDescriptor, 4)]
    [InlineData("function_swizzled", DeviceAddressRangePlanner.SwizzledFunctionBuffer, 0)]
    [InlineData("function_swizzled_empty", DeviceAddressRangePlanner.SwizzledFunctionBuffer, 0)]
    [InlineData("function_wraps", DeviceAddressRangePlanner.WrappingFunctionBuffer, 0)]
    public void FunctionBufferFailuresThrowAfterTheSameReads(string name, string message, int reads)
    {
        var outcome = RunAll(Named(name));
        Assert.Equal(message, outcome.Error);
        Assert.Equal(reads, outcome.Reads.Length);
    }

    // With call-record callees an unreadable V# is skipped and later V#s still resolve.
    [Fact]
    public void CallRecordCalleesSkipAnUnresolvedFunctionBuffer()
    {
        var outcome = RunAll(Named("function_unresolved_callee"));
        Assert.Null(outcome.Error);
        Assert.Equal(new ulong[] { 0x1000, 0x1004 }, outcome.Reads);
        Assert.Equal(new DeviceAddressRange[] { new(0, 0x5000, 8, true, false) }, outcome.Ranges);
    }

    [Fact]
    public void EmptyFunctionBufferIsSkipped()
    {
        var outcome = RunAll(Named("function_empty"));
        Assert.Null(outcome.Error);
        Assert.Empty(outcome.Ranges!);
    }

    // ---- Failure-boundary fixtures, run on every path ----

    private static int[] GlobalRanges(ShaderResourcePlan plan) =>
        plan.DeviceAddressRanges.Select((range, index) => (range, index))
            .Where(entry => entry.range.MemoryIndices.Any(index => plan.Memory[index].Kind == MemoryResourceKind.Global))
            .Select(entry => entry.index).ToArray();

    [Theory]
    [InlineData(Path.Off)]
    [InlineData(Path.On)]
    public void FailedLowSkipsItsHighAndDoesNotAbortLaterIndependentRange(Path path)
    {
        using var scope = new Scope(path);
        var plan = Extract(Program(
            ScalarLoad(0, 0, 8, 2), ScalarLoad(8, 2, 10, 2),
            Vop1(16, "VMovB32", 0, Operand(0)),
            GlobalAccess(20, "GlobalLoadDword", 8),
            GlobalAccess(28, "GlobalLoadDword", 10), EndProgram(36)), userDataCount: 4);
        plan.CompileEvaluatorNow();
        var globals = GlobalRanges(plan);
        Assert.Equal(2, globals.Length);
        var reads = new List<ulong>();
        bool Read(ulong address, out uint word)
        {
            reads.Add(address);
            word = address == 0x2000 ? 0x6000u : 0u;
            Assert.Contains(address, new ulong[] { 0x1000, 0x2000, 0x2004 });
            return address != 0x1000;
        }
        bool Clean(ulong address, out uint word)
        {
            word = 0;
            throw new System.InvalidOperationException("Device-address range evaluation must retain its ordinary reader context.");
        }
        var result = DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0, 0x2000, 0], Read, Clean));
        var failed = result[globals[0]];
        Assert.False(failed.Planned);
        Assert.Equal(0ul, failed.Base);
        Assert.Equal(0ul, failed.Size);
        var later = result[globals[1]];
        Assert.True(later.Planned);
        Assert.Equal(0x6000ul, later.Base);
        Assert.Equal(4ul, later.Size);
        Assert.Equal(new ulong[] { 0x1000, 0x2000, 0x2004 }, reads);
    }

    [Theory]
    [InlineData(true, Path.Off)]
    [InlineData(true, Path.On)]
    [InlineData(false, Path.Off)]
    [InlineData(false, Path.On)]
    public void SharedFailedNodeIsRetriedButSuccessfulDependenciesStayMemoized(bool failLow, Path path)
    {
        using var scope = new Scope(path);
        var plan = Extract(Program(
            ScalarLoad(0, 0, 8, 2), Vop1(8, "VMovB32", 0, Operand(0)),
            GlobalAccess(12, "GlobalLoadDword", 8),
            Sop2(20, "SAddU32", 10, Gen5Operand.Scalar(8), Operand(16)),
            Sop1(24, "SMovB32", 11, Gen5Operand.Scalar(9)),
            GlobalAccess(28, "GlobalLoadDword", 10), EndProgram(36)), userDataCount: 2);
        plan.CompileEvaluatorNow();
        var globals = GlobalRanges(plan);
        Assert.Equal(2, globals.Length);
        var reads = new List<ulong>();
        var failedAddress = failLow ? 0x1000ul : 0x1004ul;
        var didFail = false;
        bool Read(ulong address, out uint word)
        {
            reads.Add(address);
            Assert.Contains(address, new ulong[] { 0x1000, 0x1004 });
            word = address == 0x1000 ? 0x4000u : 0u;
            if (address == failedAddress && !didFail)
            {
                didFail = true;
                return false;
            }
            return true;
        }
        var result = DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0], Read));
        Assert.True(didFail);
        Assert.False(result[globals[0]].Planned);
        Assert.Equal(0ul, result[globals[0]].Base);
        Assert.Equal(0ul, result[globals[0]].Size);
        Assert.True(result[globals[1]].Planned);
        Assert.Equal(0x4010ul, result[globals[1]].Base);
        Assert.Equal(4ul, result[globals[1]].Size);
        Assert.Equal(failLow
            ? new ulong[] { 0x1000, 0x1000, 0x1004 }
            : new ulong[] { 0x1000, 0x1004, 0x1004 }, reads);
    }

    // ---- switch and allocation ----

    [Fact]
    public void SwitchDefaultsOnAndOffBuildsNothing()
    {
        Assert.Equal(Mode.On, CompiledDeviceAddressRanges.Parse(null));
        Assert.Equal(Mode.On, CompiledDeviceAddressRanges.Parse(""));
        Assert.Equal(Mode.Off, CompiledDeviceAddressRanges.Parse("0"));
        Assert.Equal(Mode.Off, CompiledDeviceAddressRanges.Parse("true"));
        Assert.Equal(Mode.On, CompiledDeviceAddressRanges.Parse("1"));
        using var scope = new Scope(Path.Off);
        var plan = TablePlan();
        plan.CompileEvaluatorNow();
        Assert.Null(plan.CompiledRanges);
    }

    // A shape the builder refuses stays on the interpreter.
    [Fact]
    public void RefusedPlanStaysOnTheInterpreter()
    {
        using var scope = new Scope(Path.On);
        var plan = DeepPlan();
        plan.CompileEvaluatorNow();
        Assert.Equal("too_deep", plan.CompiledRanges!.Refusal);
        var result = DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0]));
        Assert.Equal(DeviceAddressRangePlanner.EvaluateInterpreted(plan, Inputs([0x1000, 0])), result);
    }

    [Fact]
    public void CompiledEvaluationAllocatesOnlyItsResult()
    {
        using var scope = new Scope(Path.On);
        var plan = FunctionPlan();
        plan.CompileEvaluatorNow();
        var inputs = Inputs([0x1000, 0, 0x2000, 0], (ulong address, out uint word) => FunctionWords.TryGetValue(address, out word));
        for (var warm = 0; warm < 4; warm++) DeviceAddressRangePlanner.Evaluate(plan, inputs);
        var length = DeviceAddressRangePlanner.EvaluateInterpreted(plan, inputs).Length;
        Assert.Equal(4, length);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var array = new DeviceAddressRange[length];
        var arrayBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        GC.KeepAlive(array);
        long Smallest(Func<DeviceAddressRange[]> evaluate)
        {
            var smallest = long.MaxValue;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = evaluate();
                smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Equal(length, result.Length);
            }

            return smallest;
        }

        Assert.Equal(arrayBytes, Smallest(() => DeviceAddressRangePlanner.Evaluate(plan, inputs)));
        Assert.True(Smallest(() => DeviceAddressRangePlanner.EvaluateInterpreted(plan, inputs)) > arrayBytes);
    }

    [Fact]
    public void CompiledEvaluationIsThreadSafe()
    {
        using var scope = new Scope(Path.On);
        var plan = FunctionPlan();
        plan.CompileEvaluatorNow();
        var inputs = Inputs([0x1000, 0, 0x2000, 0], (ulong address, out uint word) => FunctionWords.TryGetValue(address, out word));
        var expected = DeviceAddressRangePlanner.EvaluateInterpreted(plan, inputs);
        Parallel.For(0, 8, _ =>
        {
            for (var iteration = 0; iteration < 2000; iteration++)
                Assert.Equal(expected, DeviceAddressRangePlanner.Evaluate(plan, inputs));
        });
    }
}
