// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Mode = SharpEmu.ShaderCompiler.Resources.CompiledDeviceAddressRanges.Mode;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// CallRecordPlanner.AddRecordRanges with SHARPEMU_COMPILED_RANGES against the interpreter, record after record: the same
// results, ranges and reads. Runs in the compiled-range collection, which runs alone.
[Collection("CompiledDeviceAddressRanges")]
public sealed class CompiledCallRecordRangeTests
{
    public enum Path { Off, On }

    public enum Resident { None, Serve, Refuse }

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

    private sealed class Memory(Dictionary<ulong, uint> words, Resident resident = Resident.None)
    {
        public readonly List<ulong> Reads = [];
        public readonly List<(ulong Address, int Bytes)> ResidentReads = [];
        public readonly HashSet<ulong> FailOnce = [];

        public bool Read(ulong address, out uint word)
        {
            Reads.Add(address);
            word = 0;
            return !FailOnce.Remove(address) && words.TryGetValue(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            ResidentReads.Add((address, destination.Length));
            if (resident != Resident.Serve) return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!words.TryGetValue(address + (ulong)offset, out var word)) return false;
                BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }

            return true;
        }

        public ResourceRuntimeInputs Inputs(uint[] userData) => new()
        {
            UserData = userData,
            ReadMemory = Read,
            ReadResidentMemory = resident == Resident.None ? null : (ResidentGuestBytesReader)ReadResident,
        };
    }

    private static void SetBuffers(ShaderResourcePlan plan, params CallRecordBufferWords[] buffers) =>
        typeof(ShaderResourcePlan).GetProperty(nameof(ShaderResourcePlan.CallRecordCallees))!
            .SetValue(plan, new List<CallRecordCalleePlan> { new([], buffers, 64) });

    // A plan whose callee 0 computes the given buffers; the scalar load gives the memory entries (offsets 0, 4, 8, 12) the
    // argument reads use.
    private static ShaderResourcePlan Plan(Func<ShaderResourcePlan, CallRecordBufferWords[]> buffers)
    {
        var plan = Extract(Program(ScalarLoad(0, 0, 4, 4), EndProgram(8)), userDataCount: 4);
        SetBuffers(plan, buffers(plan));
        return plan;
    }

    private static ScalarValue ArgumentRegister(uint index) => ScalarValue.UserData(ScalarValueGraph.CallArgumentRegisterBase + index);

    private static ScalarValue ArgumentHandle() => ScalarValue.Handle(ScalarValueKind.AddressHandle, [ArgumentRegister(0), ArgumentRegister(1)]);

    // The dword a handle points at, plus an immediate of 0, 4, 8 or 12.
    private static ScalarValue ArgumentWord(ShaderResourcePlan plan, ScalarValue handle, uint offset)
    {
        Assert.True(plan.Memory.TryGetIndex(0, offset / 4, out var index));
        return ScalarValue.MemoryRead(ScalarValueKind.ScalarAddressWord, handle, ScalarValue.ConstantOf(0u), index);
    }

    // A: the record's V# at its argument. B (written): A's address + 0x100 and A's stride, the fourth word as records (shares
    // A's first two reads). C: an address from a user-data register in the gap below the argument registers (reads zero).
    // D: a register past the argument pair (fails).
    private static CallRecordBufferWords[] Buffers(ShaderResourcePlan plan)
    {
        var handle = ArgumentHandle();
        var words = new[] { 0u, 4u, 8u, 12u }.Select(offset => ArgumentWord(plan, handle, offset)).ToArray();
        return
        [
            new(words, false),
            new([ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, words[0], ScalarValue.ConstantOf(0x100u)),
                words[1], words[3], ScalarValue.ConstantOf(0u)], true),
            new([ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, ScalarValue.UserData(100), ScalarValue.ConstantOf(0x7000u)),
                ScalarValue.UserData(1), ScalarValue.ConstantOf(8u), ScalarValue.ConstantOf(0u)], false),
            new([ScalarValue.UserData(ScalarValueGraph.CallArgumentRegisterBase + 2), ScalarValue.ConstantOf(0u),
                ScalarValue.ConstantOf(8u), ScalarValue.ConstantOf(0u)], false),
        ];
    }

    private static readonly ulong[] Arguments = [0x1000, 0x2000, 0x3000, 0x4000, 0x5000, 0x1000];

    // 0x1000: both buffers. 0x2000: the stride word fails once, so A is skipped and B reads it again. 0x3000: A fits, then B
    // (written) is larger than 4 GiB: the record returns false after adding A. 0x4000: A is larger than 4 GiB (read-only,
    // skipped), B fits. 0x5000: zero address and zero size, nothing added. 0x1000 again: a new record reads again.
    private static readonly Dictionary<ulong, uint> Words = new()
    {
        [0x1000] = 0x5000, [0x1004] = 16u << 16, [0x1008] = 4, [0x100C] = 16,
        [0x2000] = 0x6000, [0x2004] = 16u << 16, [0x2008] = 2, [0x200C] = 16,
        [0x3000] = 0x8000, [0x3004] = 16u << 16, [0x3008] = 1, [0x300C] = 0x2000_0000,
        [0x4000] = 0x7000, [0x4004] = 16u << 16, [0x4008] = 0x2000_0000, [0x400C] = 16,
        [0x5000] = 0, [0x5004] = 0, [0x5008] = 0, [0x500C] = 0,
    };

    private sealed record Outcome(bool[] Results, DeviceAddressRange[] Ranges, ulong[] Reads, (ulong Address, int Bytes)[] ResidentReads);

    private static Outcome Run(Path path, Func<ShaderResourcePlan, CallRecordBufferWords[]> buffers, ulong[] arguments,
        ulong[]? failOnce = null, Resident resident = Resident.None, Dictionary<ulong, uint>? words = null)
    {
        using var scope = new Scope(path);
        var plan = Plan(buffers);
        plan.CompileEvaluatorNow();
        if (path == Path.Off) Assert.Null(plan.CompiledRanges);
        else Assert.Null(Assert.IsType<CompiledDeviceAddressRanges>(plan.CompiledRanges).Record(0)!.Refusal);
        var memory = new Memory(words ?? Words, resident);
        foreach (var address in failOnce ?? []) memory.FailOnce.Add(address);
        var inputs = memory.Inputs([0, 0, 0, 0]);
        var ranges = new List<DeviceAddressRange>();
        bool[] results = [.. arguments.Select(argument => CallRecordPlanner.AddRecordRanges(plan, 0, argument, inputs, 0, ranges))];
        return new(results, [.. ranges], [.. memory.Reads], [.. memory.ResidentReads]);
    }

    private static Outcome RunAll(Func<ShaderResourcePlan, CallRecordBufferWords[]> buffers, ulong[] arguments, ulong[]? failOnce = null)
    {
        var reference = Run(Path.Off, buffers, arguments, failOnce);
        var outcome = Run(Path.On, buffers, arguments, failOnce);
        Assert.Equal(reference.Results, outcome.Results);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Equal(reference.Reads, outcome.Reads);
        return reference;
    }

    // Both paths: a memo shared by a record's buffers and new per record,
    // a failed read evaluated again by a later buffer, the 4 GiB and 48-bit rules, a written buffer that describes no buffer
    // failing its record, the zero gap below the argument registers.
    [Fact]
    public void RecordsMatchTheInterpreterReadForRead()
    {
        var outcome = RunAll(Buffers, Arguments, [0x2004]);
        Assert.Equal(new[] { true, true, false, true, true, true }, outcome.Results);
        Assert.Equal(new ulong[]
        {
            0x1000, 0x1004, 0x1008, 0x100C,
            0x2000, 0x2004, 0x2004, 0x200C,
            0x3000, 0x3004, 0x3008, 0x300C,
            0x4000, 0x4004, 0x4008, 0x400C,
            0x5000, 0x5004, 0x5008, 0x500C,
            0x1000, 0x1004, 0x1008, 0x100C,
        }, outcome.Reads);
        Assert.Equal(new DeviceAddressRange[]
        {
            new(uint.MaxValue, 0x5000, 64, true, false), new(uint.MaxValue, 0x5100, 256, true, true), new(uint.MaxValue, 0x7000, 8, true, false),
            new(uint.MaxValue, 0x6100, 256, true, true), new(uint.MaxValue, 0x7000, 8, true, false),
            new(uint.MaxValue, 0x8000, 16, true, false),
            new(uint.MaxValue, 0x7100, 256, true, true), new(uint.MaxValue, 0x7000, 8, true, false),
            new(uint.MaxValue, 0x7000, 8, true, false),
            new(uint.MaxValue, 0x5000, 64, true, false), new(uint.MaxValue, 0x5100, 256, true, true), new(uint.MaxValue, 0x7000, 8, true, false),
        }, outcome.Ranges);
    }

    // A record whose user data reaches the argument registers throws as WithCallArguments does, on every path.
    [Theory]
    [InlineData(Path.Off)]
    [InlineData(Path.On)]
    public void UserDataReachingTheArgumentRegistersThrows(Path path)
    {
        using var scope = new Scope(path);
        var plan = Plan(Buffers);
        plan.CompileEvaluatorNow();
        var inputs = Inputs(new uint[ScalarValueGraph.CallArgumentRegisterBase + 1], new Memory(Words).Read);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, []));
        Assert.Equal("userDataBase", error.ParamName);
    }

    // A callee the builder refuses stays on the interpreter.
    [Fact]
    public void RefusedCalleeStaysOnTheInterpreter()
    {
        using var scope = new Scope(Path.On);
        CallRecordBufferWords[] Deep(ShaderResourcePlan plan)
        {
            var value = ArgumentRegister(0);
            for (var index = 0; index < 70; index++)
                value = ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, value, ScalarValue.ConstantOf((uint)index + 1));
            return [new([value, ScalarValue.ConstantOf(0u), ScalarValue.ConstantOf(8u), ScalarValue.ConstantOf(0u)], false)];
        }
        var plan = Plan(Deep);
        plan.CompileEvaluatorNow();
        Assert.Equal("too_deep", plan.CompiledRanges!.Record(0)!.Refusal);
        var ranges = new List<DeviceAddressRange>();
        Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, Inputs([0, 0, 0, 0]), 0, ranges));
        Assert.Equal(new DeviceAddressRange(uint.MaxValue, 0x1000 + 70 * 71 / 2, 8, true, false), Assert.Single(ranges));
    }

    // ---- one read of the resident reader for consecutive words ----

    // The argument's four words are one read per record, fresh each record, with the interpreter's results; a null base
    // reads its words alone, as the interpreter does.
    [Fact]
    public void ConsecutiveWordsTakeOneResidentReadPerRecord()
    {
        ulong[] arguments = [0x1000, 0x2000, 0x3000, 0x4000, 0x5000, 0x1000, 0];
        var reference = Run(Path.Off, Buffers, arguments, resident: Resident.Serve);
        var outcome = Run(Path.On, Buffers, arguments, resident: Resident.Serve);
        Assert.Equal(reference.Results, outcome.Results);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Empty(reference.ResidentReads);
        Assert.Equal(arguments[..^1].Select(argument => (argument, 16)), outcome.ResidentReads);
        Assert.Equal(new ulong[] { 0, 4, 8, 12 }, outcome.Reads);
    }

    // A refused read leaves the record to the word reads in the interpreter's order, and is tried once per record.
    [Fact]
    public void RefusedResidentReadFallsBackToTheWordReads()
    {
        var reference = Run(Path.Off, Buffers, Arguments, [0x2004]);
        var outcome = Run(Path.On, Buffers, Arguments, [0x2004], resident: Resident.Refuse);
        Assert.Equal(reference.Results, outcome.Results);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Equal(reference.Reads, outcome.Reads);
        Assert.Equal(Arguments.Select(argument => (argument, 16)), outcome.ResidentReads);
    }

    // Only consecutive words are read together: the words at 0 and 4 are one read, the one at 12 is read alone.
    [Fact]
    public void ResidentReadsTakeOnlyConsecutiveWords()
    {
        CallRecordBufferWords[] Gapped(ShaderResourcePlan plan)
        {
            var handle = ArgumentHandle();
            return [new([ArgumentWord(plan, handle, 0), ArgumentWord(plan, handle, 4), ScalarValue.ConstantOf(8u), ArgumentWord(plan, handle, 12)], false)];
        }
        var reference = Run(Path.Off, Gapped, [0x1000]);
        var outcome = Run(Path.On, Gapped, [0x1000], resident: Resident.Serve);
        Assert.Equal(new DeviceAddressRange[] { new(uint.MaxValue, 0x5000, 128, true, false) }, reference.Ranges);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Equal(new[] { (0x1000ul, 8) }, outcome.ResidentReads);
        Assert.Equal(new ulong[] { 0x100C }, outcome.Reads);
    }

    // A V# read through a pointer the record holds (a computed V#): the pointer's two words and the V#'s four are one read
    // each.
    [Fact]
    public void PointerAndDescriptorWordsTakeOneResidentReadEach()
    {
        CallRecordBufferWords[] Indirect(ShaderResourcePlan plan)
        {
            var argument = ArgumentHandle();
            var pointer = ScalarValue.Handle(ScalarValueKind.AddressHandle, [ArgumentWord(plan, argument, 8), ArgumentWord(plan, argument, 12)]);
            return [new([.. new[] { 0u, 4u, 8u, 12u }.Select(offset => ArgumentWord(plan, pointer, offset))], false)];
        }
        var words = new Dictionary<ulong, uint>
        {
            [0x1008] = 0x2000, [0x100C] = 0,
            [0x2000] = 0x9000, [0x2004] = 4u << 16, [0x2008] = 8, [0x200C] = 0,
        };
        var reference = Run(Path.Off, Indirect, [0x1000], words: words);
        var outcome = Run(Path.On, Indirect, [0x1000], resident: Resident.Serve, words: words);
        Assert.Equal(new ulong[] { 0x1008, 0x100C, 0x2000, 0x2004, 0x2008, 0x200C }, reference.Reads);
        Assert.Equal(new DeviceAddressRange[] { new(uint.MaxValue, 0x9000, 32, true, false) }, reference.Ranges);
        Assert.Equal(reference.Ranges, outcome.Ranges);
        Assert.Equal(new[] { (0x1008ul, 8), (0x2000ul, 16) }, outcome.ResidentReads);
        Assert.Empty(outcome.Reads);
    }

    [Theory]
    [InlineData(Resident.None)]
    [InlineData(Resident.Serve)]
    public void CompiledRecordAllocatesNothing(Resident resident)
    {
        using var scope = new Scope(Path.On);
        var plan = Plan(Buffers);
        plan.CompileEvaluatorNow();
        var inputs = new ResourceRuntimeInputs
        {
            UserData = new uint[4],
            ReadMemory = static (ulong address, out uint word) => Words.TryGetValue(address, out word),
            ReadResidentMemory = resident == Resident.None ? null : (ResidentGuestBytesReader)(static (ulong address, Span<byte> destination, bool clean) =>
            {
                for (var offset = 0; offset < destination.Length; offset += 4)
                {
                    if (!Words.TryGetValue(address + (ulong)offset, out var word)) return false;
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
                }

                return true;
            }),
        };
        var ranges = new List<DeviceAddressRange>(64);
        for (var warm = 0; warm < 4; warm++)
        {
            ranges.Clear();
            CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, ranges);
        }

        var smallest = long.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ranges.Clear();
            var before = GC.GetAllocatedBytesForCurrentThread();
            CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, ranges);
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, smallest);
        Assert.Equal(3, ranges.Count);
    }

    // Records evaluated on several threads at once give each thread the interpreter's ranges.
    [Fact]
    public void CompiledRecordsAreThreadSafe()
    {
        using var scope = new Scope(Path.On);
        var plan = Plan(Buffers);
        plan.CompileEvaluatorNow();
        var inputs = Inputs([0, 0, 0, 0], static (ulong address, out uint word) => Words.TryGetValue(address, out word));
        var expected = new List<DeviceAddressRange>();
        var kept = Arguments.Select(argument => CallRecordPlanner.AddRecordRangesInterpreted(plan, 0, argument, inputs, 0, expected)).ToArray();
        Parallel.For(0, 8, _ =>
        {
            for (var pass = 0; pass < 200; pass++)
            {
                var ranges = new List<DeviceAddressRange>();
                Assert.Equal(kept, Arguments.Select(argument => CallRecordPlanner.AddRecordRanges(plan, 0, argument, inputs, 0, ranges)).ToArray());
                Assert.Equal(expected, ranges);
            }
        });
    }

    // ---- Record input boundaries, on the interpreter and the compiled code ----

    private static ShaderResourcePlan MemoryPlan(bool written = false)
    {
        var plan = Extract(Program(ScalarLoad(0, 0, 4, 4), BufferLoad(8, 4), EndProgram(16)), userDataCount: 2);
        var graph = plan.Graph;
        var handle = graph.Handle(ScalarValueKind.AddressHandle,
            graph.UserData(ScalarValueGraph.CallArgumentRegisterBase),
            graph.UserData(ScalarValueGraph.CallArgumentRegisterBase + 1));
        var words = new ScalarValue[4];
        for (var word = 0; word < 4; word++)
        {
            Assert.True(plan.Memory.TryGetIndex(0, (uint)word, out var index));
            words[word] = graph.MemoryRead(ScalarValueKind.ScalarAddressWord, handle, graph.Constant(0u), index);
        }
        SetBuffers(plan, new CallRecordBufferWords(words, written));
        plan.CompileEvaluatorNow();
        return plan;
    }

    [Theory]
    [InlineData(Path.Off)]
    [InlineData(Path.On)]
    public void SameRecordPointerReadsChangedPayloadAndDoesNotMutateEarlierOutput(Path path)
    {
        using var scope = new Scope(path);
        var plan = MemoryPlan();
        uint[] words = [0x4000, 0, 16, 0];
        var reads = new List<ulong>();
        bool Read(ulong address, out uint word)
        {
            reads.Add(address);
            Assert.InRange(address, 0x1000ul, 0x100Cul);
            word = words[(int)((address - 0x1000) / 4)];
            return true;
        }
        var inputs = Inputs([], Read);
        var first = new List<DeviceAddressRange>();
        var second = new List<DeviceAddressRange>();
        Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, first));
        var saved = Assert.Single(first);
        words[0] = 0x8000;
        words[2] = 32;
        Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, second));
        Assert.Equal(new DeviceAddressRange(uint.MaxValue, 0x4000, 16, true, false), saved);
        Assert.Equal(saved, Assert.Single(first));
        Assert.Equal(new DeviceAddressRange(uint.MaxValue, 0x8000, 32, true, false), Assert.Single(second));
        Assert.Equal(new ulong[] { 0x1000, 0x1004, 0x1008, 0x100C, 0x1000, 0x1004, 0x1008, 0x100C }, reads);
    }

    [Theory]
    [InlineData(Path.Off)]
    [InlineData(Path.On)]
    public void FailedFirstRecordRetriesInFreshStateForTheNextRecord(Path path)
    {
        using var scope = new Scope(path);
        var plan = MemoryPlan();
        var reads = new List<ulong>();
        var fail = true;
        bool Read(ulong address, out uint word)
        {
            reads.Add(address);
            word = address == 0x1000 ? 0x4000u : address == 0x1008 ? 16u : 0u;
            if (fail) { fail = false; return false; }
            return true;
        }
        var inputs = Inputs([], Read);
        var first = new List<DeviceAddressRange>();
        var second = new List<DeviceAddressRange>();
        Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, first));
        Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, inputs, 0, second));
        Assert.Empty(first); // Unresolved descriptor is skipped; it does not fail the record.
        Assert.Equal(0x4000ul, Assert.Single(second).Base);
        Assert.Equal(new ulong[] { 0x1000, 0x1000, 0x1004, 0x1008, 0x100C }, reads);
    }

    [Theory]
    [InlineData(Path.Off, false, true)]
    [InlineData(Path.Off, true, false)]
    [InlineData(Path.On, false, true)]
    [InlineData(Path.On, true, false)]
    public void OversizedReadOnlyBufferIsSkippedButWrittenBufferRejectsRecord(Path path, bool written, bool success)
    {
        using var scope = new Scope(path);
        var plan = MemoryPlan(written);
        uint[] words = [0x4000, 2u << 16, uint.MaxValue, 0];
        bool Read(ulong address, out uint word)
        {
            word = words[(int)((address - 0x1000) / 4)];
            return true;
        }
        var ranges = new List<DeviceAddressRange>();
        Assert.Equal(success, CallRecordPlanner.AddRecordRanges(plan, 0, 0x1000, Inputs([], Read), 0, ranges));
        Assert.Empty(ranges);
    }

    [Theory]
    [InlineData(Path.Off)]
    [InlineData(Path.On)]
    public void ArgumentOverlayKeepsZeroFilledGapAndRefreshesArgument(Path path)
    {
        using var scope = new Scope(path);
        var plan = Extract(Program(EndProgram(0)), userDataCount: 1);
        var graph = plan.Graph;
        // WithCallArguments currently zero-fills unused ordinary user-data slots.
        var size = graph.Operation(ScalarOperation.IAdd32, ScalarValueType.U32, graph.UserData(8), graph.Constant(16u));
        SetBuffers(plan, new CallRecordBufferWords([
            graph.UserData(ScalarValueGraph.CallArgumentRegisterBase),
            graph.UserData(ScalarValueGraph.CallArgumentRegisterBase + 1), size, graph.Constant(0u)], false));
        plan.CompileEvaluatorNow();
        foreach (var argument in new ulong[] { 0x4000, 0x5000, 0x4000 })
        {
            var ranges = new List<DeviceAddressRange>();
            Assert.True(CallRecordPlanner.AddRecordRanges(plan, 0, argument, Inputs([99]), 0, ranges));
            Assert.Equal(new DeviceAddressRange(uint.MaxValue, argument, 16, true, false), Assert.Single(ranges));
        }
    }
}
