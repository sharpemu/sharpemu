// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// A plan's device-address ranges evaluated by code compiled once per plan on the SRT compiler thread.
// On by default; SHARPEMU_COMPILED_RANGES=0, or any value but 1, turns it off.
// DeviceAddressRangePlanner.EvaluateInterpreted is the reference, one evaluator for every range and then the function
// V#s, and the compiled code keeps its rules:
//   - each node is evaluated at most once per call, its operands in the evaluator's order, and the first failure ends
//     the value (a failed base low skips its base high);
//   - only a successful node keeps its value, so a node that failed is evaluated (and read) again when a later range
//     or V# needs it;
//   - a phi is its invariant value and a table word its table read, in the same evaluator;
//   - a first-lane value runs its own evaluator, empty each time, which takes a select on the value's active mask as the
//     select's first value.
// The range assembly and the function-buffer tail are the reference's code with only the value evaluation replaced.
// The function-buffer accesses are listed once per plan, and a call allocates nothing but its result array.
// A plan with a cycle, a malformed node or more than MaxBlocks nodes stays on the interpreter.
// CallRecordPlanner.AddRecordRanges evaluates each callee's computed V#s (CallRecordBufferWords) the same way, compiled
// once per callee, with the record helper's own rules: one memo per record, shared by its buffers; the user data a record
// reads is what WithCallArguments builds (the draw's, zeros up to the argument registers, then the argument) without
// building it. A record's reads of one handle at consecutive constant offsets take one read of the resident reader, fresh
// each record, as RawReadPrefetch's runs do; a refused read, or no resident reader, leaves them to word reads.
internal sealed class CompiledDeviceAddressRanges
{
    internal enum Mode : byte { Off, On }

    internal static Mode Parse(string? value) => value switch
    {
        null or "" or "1" => Mode.On,
        _ => Mode.Off,
    };

    // Tests change it; plans compile their ranges only while it is on.
    internal static Mode Configured { get; set; } = Parse(Environment.GetEnvironmentVariable("SHARPEMU_COMPILED_RANGES"));

    private const int MaxBlocks = 2048;
    // A large plan gets bounded methods, not an unbounded version of the small method. Slot identity and State stay
    // shared across methods, preserving successful-read memoization and first-lane evaluator contexts across roots.
    // SHARPEMU_COMPILED_LARGE_RANGES=0 leaves large plans on the interpreter.
    internal static readonly bool LargeConfigured = Environment.GetEnvironmentVariable("SHARPEMU_COMPILED_LARGE_RANGES") != "0";
    private const int MaxLargeBlocks = 262144;
    private const int MaxLargeSlots = 262144;
    private const int MaxLargeRoots = 262144;
    private const int MaxDistinctLargeRoots = 65536;
    private const int MaxDepth = 64;
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // A function-buffer access in the order the reference's loop visits it. A null handle is where that loop throws for a
    // missing descriptor, so the list ends there.
    internal readonly record struct FunctionBuffer(ScalarValue? Handle, bool Written);

    private readonly IReadOnlyList<DeviceAddressRangePlan> _ranges;
    private readonly FunctionBuffer[]? _functions;
    private readonly Func<State, int, bool>? _evaluate;
    private readonly int _slots;
    private readonly RecordRanges?[] _records;

    internal string? Refusal { get; }
    internal int Partitions { get; }
    internal int RootCount { get; }
    internal int DistinctRootCount { get; }

    private CompiledDeviceAddressRanges(ShaderResourcePlan plan, FunctionBuffer[]? functions, Func<State, int, bool>? evaluate,
        int slots, string? refusal, RecordRanges?[] records, int partitions = 0, int roots = 0, int distinctRoots = 0)
    {
        _ranges = plan.DeviceAddressRanges;
        _functions = functions;
        _evaluate = evaluate;
        _slots = slots;
        Refusal = refusal;
        _records = records;
        Partitions = partitions;
        RootCount = roots;
        DistinctRootCount = distinctRoots;
    }

    // A callee's compiled V#s; null for a callee without computed V#s.
    internal RecordRanges? Record(int callee) => (uint)callee < (uint)_records.Length ? _records[callee] : null;

    internal static FunctionBuffer[]? FunctionBuffers(ShaderResourcePlan plan)
    {
        var accesses = plan.Graph.Program.FunctionBufferAccesses;
        if (accesses.Count == 0) return null;
        var functions = new List<FunctionBuffer>();
        foreach (var memory in plan.Memory.Entries)
        {
            if (!memory.DeviceDescriptor || !accesses.Contains(memory.Pc)) continue;
            if (!plan.Memory.TryGetIndex(memory.Pc, memory.ComponentIndex, out var memoryIndex) ||
                plan.Accesses[memoryIndex]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle)
            {
                functions.Add(new(null, false));
                break;
            }

            functions.Add(new(handle, memory.Access is MemoryAccess.Write or MemoryAccess.Atomic));
        }

        return [.. functions];
    }

    // ShaderResourcePlan.CompileEvaluatorNow builds it under the plan's compile gate; null while the switch is off.
    internal static CompiledDeviceAddressRanges? TryBuild(ShaderResourcePlan plan)
    {
        if (Configured == Mode.Off) return null;
        CompiledDeviceAddressRanges compiled;
        try
        {
            compiled = Build(plan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            compiled = new(plan, null, null, 0, "error:" + exception.GetType().Name, []);
        }

        return compiled;
    }

    // Roots: 2r is range r's base low and 2r + 1 its base high; 2R + 4f + w is word w of function buffer f.
    internal static CompiledDeviceAddressRanges Build(ShaderResourcePlan plan, bool? compileLarge = null)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") == "0")
            return new(plan, null, null, 0, "no_dynamic_code", []);
        var records = new RecordRanges?[plan.CallRecordCallees.Count];
        for (var callee = 0; callee < records.Length; callee++)
        {
            if (plan.CallRecordCallees[callee].BufferWords is { Length: > 0 } buffers) records[callee] = RecordRanges.Build(plan, buffers);
        }

        var ranges = plan.DeviceAddressRanges;
        var functions = FunctionBuffers(plan);
        var roots = new List<(int Root, ScalarValue Value)>();
        for (var index = 0; index < ranges.Count; index++)
        {
            if (!ranges[index].Plannable) continue;
            roots.Add((2 * index, ranges[index].BaseLow));
            roots.Add((2 * index + 1, ranges[index].BaseHigh));
        }

        for (var function = 0; function < (functions?.Length ?? 0); function++)
        {
            if (functions![function].Handle is not { } handle) continue;
            for (var word = 0; word < 4; word++)
                roots.Add((2 * ranges.Count + 4 * function + word, handle.Operands[word]));
        }

        if (roots.Count == 0) return new(plan, functions, null, 0, null, records);
        var builder = new Builder(plan, records: false);
        try
        {
            var evaluate = builder.Compile(roots);
            return new(plan, functions, evaluate, builder.Slots, null, records);
        }
        catch (RefusedShape refused)
        {
            if (refused.Message == "too_large" && (compileLarge ?? LargeConfigured))
            {
                // Retry compilation only. No guest reads have occurred, and malformed/deep/cyclic roots keep refusing.
                var partitioned = new Builder(plan, records: false);
                try
                {
                    var evaluate = partitioned.CompilePartitioned(roots, out var pieces);
                    return new(plan, functions, evaluate, partitioned.Slots, null, records, pieces, roots.Count, partitioned.DistinctRoots);
                }
                catch (RefusedShape largeRefusal)
                {
                    return new(plan, null, null, 0, largeRefusal.Message, records, roots: roots.Count, distinctRoots: partitioned.DistinctRoots);
                }
            }
            return new(plan, null, null, 0, refused.Message, records);
        }
    }

    // Serves DeviceAddressRangePlanner.Evaluate while the switch is on.
    internal static DeviceAddressRange[] Serve(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        if (Configured == Mode.Off) return DeviceAddressRangePlanner.EvaluateInterpreted(plan, inputs);
        var compiled = plan.CompiledRanges;
        if (compiled is null || compiled.Refusal is not null || !ReferenceEquals(compiled._ranges, plan.DeviceAddressRanges))
            return DeviceAddressRangePlanner.EvaluateInterpreted(plan, inputs);
        return compiled.Evaluate(plan, inputs);
    }

    internal DeviceAddressRange[] Evaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var ranges = _ranges;
        var state = State.Rent(_slots, 0, plan, inputs);
        try
        {
            if (_functions is null)
            {
                if (ranges.Count == 0) return [];
                var result = new DeviceAddressRange[ranges.Count];
                for (var index = 0; index < result.Length; index++)
                    result[index] = Range(state, ranges[index], index);
                return result;
            }

            var functionRanges = state.Ranges;
            for (var index = 0; index < ranges.Count; index++)
                functionRanges.Add(Range(state, ranges[index], index));
            var seen = state.Seen;
            Span<uint> words = stackalloc uint[4];
            for (var function = 0; function < _functions.Length; function++)
            {
                var entry = _functions[function];
                if (entry.Handle is null) throw new ResourcePlanException(DeviceAddressRangePlanner.MissingFunctionDescriptor);
                var root = 2 * ranges.Count + 4 * function;
                var resolved = true;
                for (var component = 0; component < 4 && resolved; component++)
                    resolved = Root(state, root + component, out words[component]);
                if (!resolved)
                {
                    // A V# of a callee linked with run-time arguments: the host resolves it for each record (CallRecordCallee).
                    if (plan.CallRecordCallees.Count != 0) continue;
                    throw new ResourcePlanException(DeviceAddressRangePlanner.UnresolvedFunctionBuffer);
                }
                var address = words[0] | ((ulong)(words[1] & 0xFFFF) << 32);
                if ((words[1] & 0x80000000) != 0)
                    throw new ResourcePlanException(DeviceAddressRangePlanner.SwizzledFunctionBuffer);
                var stride = (words[1] >> 16) & 0x3FFF;
                var size = (ulong)words[2] * Math.Max(stride, 1u);
                if (size == 0) continue;
                if (size > AddressMask - address + 1)
                    throw new ResourcePlanException(DeviceAddressRangePlanner.WrappingFunctionBuffer);
                if (seen.Add((address, size, entry.Written)))
                    functionRanges.Add(new((uint)functionRanges.Count, address, size, true, entry.Written));
            }
            return functionRanges.ToArray();
        }
        finally
        {
            state.Return();
        }
    }

    private DeviceAddressRange Range(State state, DeviceAddressRangePlan range, int index)
    {
        uint low = 0;
        uint high = 0;
        var planned = range.Plannable && Root(state, 2 * index, out low) && Root(state, 2 * index + 1, out high);
        return DeviceAddressRangePlanner.EvaluateRange(range, planned, low, high);
    }

    private bool Root(State state, int root, out uint value) => Root(_evaluate!, state, root, out value);

    private static bool Root(Func<State, int, bool> evaluate, State state, int root, out uint value)
    {
        if (!evaluate(state, root))
        {
            value = 0;
            return false;
        }

        value = (uint)state.Result;
        return true;
    }

    // Serves CallRecordPlanner.AddRecordRanges while the switch is on, for a callee with computed V#s.
    internal static bool ServeRecord(ShaderResourcePlan plan, int callee, ulong argument, ResourceRuntimeInputs inputs,
        uint userDataBase, List<DeviceAddressRange> ranges)
    {
        if (Configured == Mode.Off || plan.CompiledRanges is not { } compiled)
            return CallRecordPlanner.AddRecordRangesInterpreted(plan, callee, argument, inputs, userDataBase, ranges);

        // User data that reaches the argument registers: the interpreter throws WithCallArguments' exception.
        if (compiled.Record(callee) is not { Refusal: null } record || !record.Compiles(plan.CallRecordCallees[callee].BufferWords) ||
            userDataBase > ScalarValueGraph.CallArgumentRegisterBase ||
            inputs.UserData.Count > ScalarValueGraph.CallArgumentRegisterBase - userDataBase)
            return CallRecordPlanner.AddRecordRangesInterpreted(plan, callee, argument, inputs, userDataBase, ranges);
        return record.Add(plan, argument, inputs, userDataBase, ranges);
    }

    // One callee's computed V#s (CallRecordBufferWords), evaluated for each record: root 4b + w is word w of buffer b.
    internal sealed class RecordRanges
    {
        private readonly CallRecordBufferWords[] _buffers;
        private readonly Func<State, int, bool>? _evaluate;
        private readonly int _slots;
        private readonly int _groups;

        internal string? Refusal { get; }

        private RecordRanges(CallRecordBufferWords[] buffers, Func<State, int, bool>? evaluate, int slots, int groups, string? refusal)
        {
            _buffers = buffers;
            _evaluate = evaluate;
            _slots = slots;
            _groups = groups;
            Refusal = refusal;
        }

        internal static RecordRanges Build(ShaderResourcePlan plan, CallRecordBufferWords[] buffers)
        {
            var roots = new List<(int Root, ScalarValue Value)>();
            for (var buffer = 0; buffer < buffers.Length; buffer++)
            {
                if (buffers[buffer].Words.Length != 4) return new(buffers, null, 0, 0, "malformed_buffer");
                for (var word = 0; word < 4; word++) roots.Add((4 * buffer + word, buffers[buffer].Words[word]));
            }

            var builder = new Builder(plan, records: true);
            try
            {
                var evaluate = builder.Compile(roots);
                return new(buffers, evaluate, builder.Slots, builder.Groups, null);
            }
            catch (RefusedShape refused)
            {
                return new(buffers, null, 0, 0, refused.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One callee's failure leaves the plan's ranges and the other callees compiled.
                return new(buffers, null, 0, 0, "error:" + exception.GetType().Name);
            }
        }

        // Compiled from these buffers' words.
        internal bool Compiles(CallRecordBufferWords[] buffers) => ReferenceEquals(buffers, _buffers);

        // CallRecordPlanner.AddRecordRangesInterpreted with only the value evaluation replaced.
        internal bool Add(ShaderResourcePlan plan, ulong argument, ResourceRuntimeInputs inputs, uint userDataBase,
            List<DeviceAddressRange> ranges)
        {
            const ulong MaxRecordBufferBytes = 1UL << 32;
            var state = State.Rent(_slots, _groups, plan, inputs);
            state.RecordFirst = ScalarValueGraph.CallArgumentRegisterBase - userDataBase;
            state.Argument = argument;
            try
            {
                Span<uint> words = stackalloc uint[4];
                for (var index = 0; index < _buffers.Length; index++)
                {
                    var buffer = _buffers[index];
                    var resolved = true;
                    for (var dword = 0; dword < 4 && resolved; dword++)
                    {
                        resolved = Root(_evaluate!, state, 4 * index + dword, out words[dword]);
                    }

                    if (!resolved)
                    {
                        continue;
                    }

                    var address = words[0] | ((ulong)(words[1] & 0xFFFF) << 32);
                    var stride = (words[1] >> 16) & 0x3FFF;
                    var size = (ulong)words[2] * Math.Max(stride, 1u);
                    if (address == 0 || size == 0)
                    {
                        continue;
                    }

                    if (size > MaxRecordBufferBytes || address + size > 1UL << 48)
                    {
                        if (buffer.Written)
                        {
                            return false;
                        }

                        continue;
                    }

                    ranges.Add(new DeviceAddressRange(uint.MaxValue, address, size, true, buffer.Written));
                }

                return true;
            }
            finally
            {
                state.Return();
            }
        }
    }

    // One call's values. Known[i] is set when node i succeeded; a failed node stays unknown and is evaluated again.
    internal sealed class State
    {
        [ThreadStatic]
        private static State? _available;

        public ulong[] Values = [];
        public bool[] Known = [];
        public ulong Result;
        public ulong ShaderBase;
        public GuestWordReader? Reader;
        public bool ReadsClean;
        public IReadOnlyList<uint> UserData = [];
        public uint[]? UserDataArray;
        public ShaderResourcePlan? Plan;
        public readonly List<DeviceAddressRange> Ranges = [];
        public readonly HashSet<(ulong Base, ulong Size, bool Written)> Seen = [];
        // A record's: where its argument registers start in the user data, its argument, the resident reader and the read
        // groups it has tried.
        public uint RecordFirst;
        public ulong Argument;
        public ResidentGuestBytesReader? Resident;
        public bool[] GroupsTried = [];

        internal static State Rent(int slots, int groups, ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
        {
            var state = _available ?? new State();
            _available = null;
            if (state.Values.Length < slots)
            {
                state.Values = new ulong[slots];
                state.Known = new bool[slots];
            }
            else
            {
                Array.Clear(state.Known, 0, slots);
            }

            if (state.GroupsTried.Length < groups) state.GroupsTried = new bool[groups];
            else Array.Clear(state.GroupsTried, 0, groups);
            state.Plan = plan;
            state.ShaderBase = inputs.ShaderBase;
            state.Reader = inputs.ReadMemory;
            state.ReadsClean = inputs.ReadsClean;
            state.UserData = inputs.UserData;
            state.UserDataArray = inputs.UserData as uint[];
            state.Resident = inputs.ReadResidentMemory;
            return state;
        }

        internal void Return()
        {
            Plan = null;
            Reader = null;
            Resident = null;
            UserData = [];
            UserDataArray = null;
            Ranges.Clear();
            if (Seen.Count != 0) Seen.Clear();
            _available = this;
        }
    }

    private static bool ReadUserData(State state, uint index, out ulong value)
    {
        if (state.UserDataArray is { } array)
        {
            if (index < (uint)array.Length)
            {
                value = array[index];
                return true;
            }
        }
        else if (index < (uint)state.UserData.Count)
        {
            value = state.UserData[(int)index];
            return true;
        }

        value = 0;
        return false;
    }

    // The user data ResourceRuntimeInputs.WithCallArguments builds for a record, without building it: the draw's, zeros up
    // to the argument registers, then the argument's two dwords.
    private static bool ReadRecordUserData(State state, uint index, out ulong value)
    {
        if (index < (uint)state.UserData.Count) return ReadUserData(state, index, out value);
        var first = state.RecordFirst;
        value = index == first ? (uint)state.Argument : index == first + 1 ? (uint)(state.Argument >> 32) : 0;
        return index <= first + 1;
    }

    // A record's reads of one handle at consecutive constant offsets, made by one read of the resident reader the first time
    // one of them is evaluated in the record. A refused read, or no resident reader, leaves them to word reads.
    private sealed class Group(int index, long relative, int[] slots, int[] words, ScalarValue[] nodes)
    {
        public readonly int Index = index;
        public readonly long Relative = relative;
        public readonly int Words = words[^1] + 1;
        public readonly int[] Slots = slots;
        public readonly int[] Word = words;
        public readonly ScalarValue[] Nodes = nodes;
    }

    private const int MaxGroupWords = 64;

    private static void ReadGroup(State state, Group group, ulong low, ulong high)
    {
        if (state.GroupsTried[group.Index]) return;
        state.GroupsTried[group.Index] = true;
        var reader = state.Resident;
        var baseAddress = ((high << 32) | (uint)low) & AddressMask;
        // As RawReadPrefetch: the word reader must exist, a null base keeps its own rule, and the words must not wrap.
        if (reader is null || state.Reader is null || (baseAddress & ~3ul) == 0 ||
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, baseAddress, high, 0, group.Relative, 0,
                out var first) != RuntimeValueEvaluator.RawAddress.Read ||
            RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, baseAddress, high, 0,
                group.Relative + 4L * (group.Words - 1), 0, out var last) != RuntimeValueEvaluator.RawAddress.Read ||
            last - first != 4ul * (uint)(group.Words - 1))
            return;
        Span<byte> bytes = stackalloc byte[4 * MaxGroupWords];
        bytes = bytes[..(4 * group.Words)];
        if (!reader(first, bytes, state.ReadsClean)) return;
        var values = state.Values;
        var known = state.Known;
        for (var member = 0; member < group.Slots.Length; member++)
        {
            var slot = group.Slots[member];
            if (known[slot]) continue;
            var word = group.Word[member];
            values[slot] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * word)..]);
            known[slot] = true;
            ResourceMaterializationCache.RecordReadConsumer(state.Plan!, group.Nodes[member], first + 4ul * (uint)word);
        }
    }

    // RuntimeValueEvaluator.ReadRawWord through the call's reader.
    private static bool ReadWord(State state, ScalarValueKind kind, ulong low, ulong high, ulong offset, ulong records,
        long immediate, bool absolute, ScalarValue node, out ulong value)
    {
        value = 0;
        var baseAddress = ((high << 32) | (uint)low) & AddressMask;
        switch (RuntimeValueEvaluator.ResolveRawAddress(kind, baseAddress, high, records, immediate, (uint)offset, out var address))
        {
            case RuntimeValueEvaluator.RawAddress.Failed:
                return false;
            case RuntimeValueEvaluator.RawAddress.Zero:
                return true;
        }

        var reader = state.Reader;
        if (reader is null || !reader(address, out var word))
        {
            // As in the evaluator: a null base, or an absolute address the shader carries that nothing maps, reads zero.
            return (baseAddress & ~3ul) == 0 || (absolute && reader is not null && !state.ReadsClean);
        }

        value = word;
        ResourceMaterializationCache.RecordReadConsumer(state.Plan!, node, address);
        return true;
    }

    private sealed class RefusedShape(string reason) : Exception(reason);

    // Each root is straight-line code: a node's statements run under "if (!known[node])" where the root first needs it,
    // and its later uses in that root read its value (a failure anywhere ends the root, so a later use finds it known).
    // A record's code reads the user data as ReadRecordUserData does and groups its reads (Group).
    private sealed class Builder(ShaderResourcePlan plan, bool records)
    {
        private static readonly MethodInfo EvaluateOperation = typeof(ScalarOperationSemantics)
            .GetMethod(nameof(ScalarOperationSemantics.TryEvaluateFixed), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo ReadWordMethod = typeof(CompiledDeviceAddressRanges)
            .GetMethod(nameof(ReadWord), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo ReadUserDataMethod = typeof(CompiledDeviceAddressRanges)
            .GetMethod(nameof(ReadUserData), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo ReadRecordUserDataMethod = typeof(CompiledDeviceAddressRanges)
            .GetMethod(nameof(ReadRecordUserData), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo ReadGroupMethod = typeof(CompiledDeviceAddressRanges)
            .GetMethod(nameof(ReadGroup), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly ParameterExpression _state = Expression.Parameter(typeof(State), "state");
        private readonly ParameterExpression _root = Expression.Parameter(typeof(int), "root");
        private readonly ParameterExpression _values = Expression.Variable(typeof(ulong[]), "values");
        private readonly ParameterExpression _known = Expression.Variable(typeof(bool[]), "known");
        private readonly ParameterExpression _word = Expression.Variable(typeof(ulong), "word");
        private readonly LabelTarget _return = Expression.Label(typeof(bool), "return");
        private readonly Context _planContext = new(null);
        private readonly Dictionary<(Context, ScalarValue), Context> _contexts = [];
        private readonly Dictionary<(Context, ScalarValue), int> _slots = [];
        private readonly Dictionary<ScalarValue, Group> _groups = new(ReferenceEqualityComparer.Instance);
        private int _groupCount;
        private int _blocks;

        // One evaluator: the plan's, or a first-lane value's, which starts empty each time that value is evaluated and
        // takes a select on its active mask as the select's first value.
        private sealed class Context(ScalarValue? mask)
        {
            public readonly ScalarValue? Mask = mask;
            public readonly List<int> Slots = [];
        }

        // One run of a context in a root: the nodes it has made known so far.
        private sealed class Run(Context context)
        {
            public readonly Context Context = context;
            public readonly HashSet<ScalarValue> Emitted = [];
        }

        internal int Slots => _slots.Count;

        internal int Groups => _groupCount;
        internal int DistinctRoots { get; private set; }

        internal Func<State, int, bool> Compile(List<(int Root, ScalarValue Value)> roots)
        {
            if (records) CollectGroups(roots);
            var cases = new List<SwitchCase>(roots.Count);
            foreach (var (root, value) in roots)
            {
                var statements = new List<Expression>();
                var result = Emit(value, statements, new Run(_planContext), []);
                if (result is not null)
                {
                    statements.Add(Expression.Assign(Expression.Field(_state, nameof(State.Result)), result));
                    statements.Add(Expression.Return(_return, Expression.Constant(true)));
                }
                else
                {
                    statements.Add(Fail());
                }

                cases.Add(Expression.SwitchCase(Expression.Block(typeof(void), statements), Expression.Constant(root)));
            }

            return CompileCases(cases);
        }

        private Func<State, int, bool> CompileCases(List<SwitchCase> cases)
        {
            var body = Expression.Block(typeof(bool), [_values, _known, _word],
                Expression.Assign(_values, Expression.Field(_state, nameof(State.Values))),
                Expression.Assign(_known, Expression.Field(_state, nameof(State.Known))),
                Expression.Switch(typeof(void), _root, Fail(), null, cases),
                Expression.Label(_return, Expression.Constant(false)));
            return Expression.Lambda<Func<State, int, bool>>(body, _state, _root).Compile();
        }

        internal Func<State, int, bool> CompilePartitioned(List<(int Root, ScalarValue Value)> roots, out int partitions)
        {
            if (records || roots.Count == 0 || roots.Count > MaxLargeRoots ||
                roots.Any(root => root.Root < 0 || root.Root >= MaxLargeRoots))
                throw new RefusedShape("large_root_budget");
            // Function-buffer access sites often name the very same scalar nodes thousands of times. Emit each
            // reference-identical root once; the external sequence still invokes it each time, including retries after
            // a failed read. This shares code only, not a cached result or a first-lane execution context.
            var aliases = new int[roots.Max(root => root.Root) + 1];
            Array.Fill(aliases, -1);
            var distinct = new List<ScalarValue>();
            var identities = new Dictionary<ScalarValue, int>(ReferenceEqualityComparer.Instance);
            foreach (var (root, value) in roots)
            {
                if (!identities.TryGetValue(value, out var index))
                {
                    index = distinct.Count;
                    DistinctRoots = index + 1;
                    if (DistinctRoots > MaxDistinctLargeRoots) throw new RefusedShape("large_distinct_root_budget");
                    identities.Add(value, index);
                    distinct.Add(value);
                }
                aliases[root] = index;
            }
            var dispatch = new Func<State, int, bool>?[distinct.Count];
            var cases = new List<SwitchCase>();
            var indices = new List<int>();
            var pieceBlocks = 0;
            var totalBlocks = 0;
            var pieces = 0;
            void FinishPiece()
            {
                if (cases.Count == 0) return;
                var evaluate = CompileCases(cases);
                foreach (var root in indices) dispatch[root] = evaluate;
                cases.Clear();
                indices.Clear();
                pieceBlocks = 0;
                pieces++;
            }

            for (var root = 0; root < distinct.Count; root++)
            {
                // Each root keeps its own depth and block checks; only the cumulative budget across roots is split.
                // The same builder owns every slot/context: creating a new builder here would repeat guest reads.
                _blocks = 0;
                var statements = new List<Expression>();
                var result = Emit(distinct[root], statements, new Run(_planContext), []);
                totalBlocks += _blocks;
                if (totalBlocks > MaxLargeBlocks) throw new RefusedShape("large_total_budget");
                if (Slots > MaxLargeSlots) throw new RefusedShape("large_slot_budget");
                if (pieceBlocks + _blocks > MaxBlocks || cases.Count == 64) FinishPiece();
                if (result is not null)
                {
                    statements.Add(Expression.Assign(Expression.Field(_state, nameof(State.Result)), result));
                    statements.Add(Expression.Return(_return, Expression.Constant(true)));
                }
                else statements.Add(Fail());
                cases.Add(Expression.SwitchCase(Expression.Block(typeof(void), statements), Expression.Constant(root)));
                indices.Add(root);
                pieceBlocks += _blocks;
            }
            FinishPiece();
            partitions = pieces;
            // Only large plans take this extra dispatch over their pieces; small plans keep their direct call.
            return (state, root) => (uint)root < (uint)aliases.Length && aliases[root] is var index && index >= 0 &&
                dispatch[index] is { } evaluate && evaluate(state, index);
        }

        private Expression Fail() => Expression.Return(_return, Expression.Constant(false));

        private IndexExpression Value(int slot) => Expression.ArrayAccess(_values, Expression.Constant(slot));

        private IndexExpression Known(int slot) => Expression.ArrayAccess(_known, Expression.Constant(slot));

        // The node one evaluator computes for a value: a phi's invariant value, a table word's read.
        private ScalarValue? Resolve(ScalarValue value)
        {
            for (var step = 0; step < MaxDepth; step++)
            {
                if (value.Kind == ScalarValueKind.Phi)
                {
                    if (plan.Graph.ResolveInvariantPhi(value) is not { } invariant) return null;
                    value = invariant;
                }
                else if (value.Kind == ScalarValueKind.ResourceTableWord)
                {
                    if (value.Payload >= (ulong)plan.TableReads.Count) return null;
                    value = plan.TableReads[(int)value.Payload].Value;
                }
                else
                {
                    return value;
                }
            }

            throw new RefusedShape("cycle");
        }

        private int Slot(Context context, ScalarValue node)
        {
            if (!_slots.TryGetValue((context, node), out var slot))
            {
                _slots.Add((context, node), slot = _slots.Count);
                context.Slots.Add(slot);
            }

            return slot;
        }

        // The plan evaluator's reads of one address handle at constant offsets, in runs of consecutive words. The walk takes
        // the operands Emit evaluates in that evaluator; a first-lane value's reads are its own evaluator's and stay word reads.
        private void CollectGroups(List<(int Root, ScalarValue Value)> roots)
        {
            var reads = new Dictionary<ScalarValue, List<(long Relative, int Order, ScalarValue Node)>>(ReferenceEqualityComparer.Instance);
            var visited = new HashSet<ScalarValue>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<ScalarValue>();
            for (var index = roots.Count - 1; index >= 0; index--) pending.Push(roots[index].Value);
            while (pending.TryPop(out var value))
            {
                if (ResolveOrNull(value) is not { } node || !visited.Add(node)) continue;
                switch (node.Kind)
                {
                    case ScalarValueKind.Select:
                    case ScalarValueKind.Operation when RuntimeValueValidator.IsUniformOperation(node.Operation):
                        foreach (var operand in node.Operands) pending.Push(operand);
                        break;
                    case ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord when node.Operands.Length >= 2:
                        foreach (var operand in node.Operands[0].Operands) pending.Push(operand);
                        pending.Push(node.Operands[1]);
                        if (node.Kind == ScalarValueKind.ScalarAddressWord && node.Operands[0].Operands.Length >= 2 &&
                            node.MemoryIndex >= 0 && node.MemoryIndex < plan.Memory.Count &&
                            ResolveOrNull(node.Operands[1]) is { Kind: ScalarValueKind.Constant } offset)
                        {
                            if (!reads.TryGetValue(node.Operands[0], out var list)) reads.Add(node.Operands[0], list = []);
                            // RuntimeValueEvaluator.ResolveRawAddress's offset from the base.
                            var relative = ((long)(int)plan.Memory[node.MemoryIndex].Offset & ~3L) + ((uint)offset.Payload & ~3u);
                            list.Add((relative, visited.Count, node));
                        }

                        break;
                }
            }

            foreach (var list in reads.Values)
            {
                list.Sort(static (left, right) => left.Relative != right.Relative
                    ? left.Relative.CompareTo(right.Relative) : left.Order.CompareTo(right.Order));
                var start = 0;
                for (var end = 1; end <= list.Count; end++)
                {
                    if (end < list.Count && list[end].Relative - list[end - 1].Relative <= 4 &&
                        list[end].Relative - list[start].Relative < 4L * MaxGroupWords)
                        continue;
                    if (list[end - 1].Relative != list[start].Relative) AddGroup(list, start, end);
                    start = end;
                }
            }
        }

        private void AddGroup(List<(long Relative, int Order, ScalarValue Node)> list, int start, int end)
        {
            var slots = new int[end - start];
            var words = new int[slots.Length];
            var nodes = new ScalarValue[slots.Length];
            for (var member = 0; member < slots.Length; member++)
            {
                var (relative, _, node) = list[start + member];
                slots[member] = Slot(_planContext, node);
                words[member] = (int)((relative - list[start].Relative) / 4);
                nodes[member] = node;
            }

            var group = new Group(_groupCount++, list[start].Relative, slots, words, nodes);
            foreach (var node in nodes) _groups[node] = group;
        }

        private ScalarValue? ResolveOrNull(ScalarValue value)
        {
            try
            {
                return Resolve(value);
            }
            catch (RefusedShape)
            {
                return null;
            }
        }

        // Adds what makes the value known to statements (a failure returns false) and returns the expression of its
        // value, or null when the statements end in a failure the value cannot avoid.
        private Expression? Emit(ScalarValue value, List<Expression> statements, Run run, List<ScalarValue> path)
        {
            var node = Resolve(value);
            if (node is null)
            {
                statements.Add(Fail());
                return null;
            }

            switch (node.Kind)
            {
                case ScalarValueKind.Constant:
                    return Expression.Constant(node.Payload);
                case ScalarValueKind.MemoryAperture:
                    return Expression.Constant(Gen5InlineConstants.DecodeAperture64((uint)node.Payload) >> 32);
                case ScalarValueKind.ShaderBase:
                    return Expression.Field(_state, nameof(State.ShaderBase));
                case ScalarValueKind.Select when node.Operands.Length == 3 && run.Context.Mask is { } mask &&
                    ReferenceEquals(node.Operands[0], mask):
                {
                    if (path.Contains(node)) throw new RefusedShape("cycle");
                    if (path.Count >= MaxDepth) throw new RefusedShape("too_deep");
                    path.Add(node);
                    var selected = Emit(node.Operands[1], statements, run, path);
                    path.RemoveAt(path.Count - 1);
                    return selected;
                }
                case ScalarValueKind.UserData when node.UserDataRegister >= plan.UserDataBase:
                case ScalarValueKind.Operation when RuntimeValueValidator.IsUniformOperation(node.Operation):
                case ScalarValueKind.Select or ScalarValueKind.FirstLane:
                case ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord:
                    break;
                default:
                    // Undefined, a handle, a register below the user data or a non-uniform operation fails before any
                    // operand is evaluated.
                    statements.Add(Fail());
                    return null;
            }

            if (path.Contains(node)) throw new RefusedShape("cycle");
            var slot = Slot(run.Context, node);
            if (run.Emitted.Contains(node)) return Value(slot);
            if (path.Count >= MaxDepth) throw new RefusedShape("too_deep");
            if (++_blocks > MaxBlocks) throw new RefusedShape("too_large");
            path.Add(node);
            var block = new List<Expression>();
            var complete = EmitNode(node, slot, block, run, path);
            path.RemoveAt(path.Count - 1);
            statements.Add(Expression.IfThen(Expression.Not(Known(slot)), Expression.Block(typeof(void), block)));
            if (!complete) return null;
            run.Emitted.Add(node);
            return Value(slot);
        }

        // RuntimeValueEvaluator.EvaluateNode for the kinds Emit keeps.
        private bool EmitNode(ScalarValue node, int slot, List<Expression> block, Run run, List<ScalarValue> path)
        {
            Expression call;
            switch (node.Kind)
            {
                case ScalarValueKind.UserData:
                    call = Expression.Call(records ? ReadRecordUserDataMethod : ReadUserDataMethod, _state,
                        Expression.Constant(node.UserDataRegister - plan.UserDataBase), _word);
                    break;
                case ScalarValueKind.FirstLane:
                {
                    if (node.Operands.Length < 2) throw new RefusedShape("malformed_first_lane");
                    if (!_contexts.TryGetValue((run.Context, node), out var context))
                        _contexts.Add((run.Context, node), context = new Context(node.Operands[1]));
                    var body = new List<Expression>();
                    var operand = Emit(node.Operands[0], body, new Run(context), path);
                    // Its own evaluator starts empty every time the value is evaluated.
                    foreach (var forgotten in context.Slots) block.Add(Expression.Assign(Known(forgotten), Expression.Constant(false)));
                    block.AddRange(body);
                    if (operand is null) return false;
                    block.Add(Expression.Assign(Value(slot), operand));
                    block.Add(Expression.Assign(Known(slot), Expression.Constant(true)));
                    return true;
                }
                case ScalarValueKind.Select:
                {
                    if (node.Operands.Length != 3) throw new RefusedShape("malformed_select");
                    var operands = new Expression[3];
                    for (var index = 0; index < operands.Length; index++)
                    {
                        if (Emit(node.Operands[index], block, run, path) is not { } operand) return false;
                        operands[index] = operand;
                    }

                    block.Add(Expression.Assign(Value(slot), Expression.Condition(
                        Expression.NotEqual(operands[0], Expression.Constant(0UL)), operands[1], operands[2])));
                    block.Add(Expression.Assign(Known(slot), Expression.Constant(true)));
                    return true;
                }
                case ScalarValueKind.Operation:
                {
                    // Every operand is evaluated; the semantics use the first four.
                    var operands = new Expression[4];
                    for (var index = 0; index < node.Operands.Length; index++)
                    {
                        if (Emit(node.Operands[index], block, run, path) is not { } operand) return false;
                        if (index < operands.Length) operands[index] = operand;
                    }

                    for (var index = node.Operands.Length; index < operands.Length; index++) operands[index] = Expression.Constant(0UL);
                    call = Expression.Call(EvaluateOperation, Expression.Constant(node.Operation),
                        operands[0], operands[1], operands[2], operands[3], _word);
                    break;
                }
                default:
                {
                    // The interpreter and the compiled evaluator disagree on these malformed reads; neither occurs in a built graph.
                    var buffer = node.Kind == ScalarValueKind.ScalarBufferWord;
                    if (node.MemoryIndex < 0 || node.Operands.Length < 2 ||
                        (buffer && node.Operands[0].Operands.Length is >= 2 and not 4))
                        throw new RefusedShape("malformed_read");
                    var handle = node.Operands[0];
                    if (node.MemoryIndex >= plan.Memory.Count || handle.Operands.Length < 2)
                    {
                        block.Add(Fail());
                        return false;
                    }

                    var operands = new Expression[buffer ? 4 : 3];
                    ScalarValue[] sources = buffer
                        ? [handle.Operands[0], handle.Operands[1], node.Operands[1], handle.Operands[2], handle.Operands[3]]
                        : [handle.Operands[0], handle.Operands[1], node.Operands[1]];
                    for (var index = 0; index < sources.Length; index++)
                    {
                        if (Emit(sources[index], block, run, path) is not { } operand) return false;
                        if (index < operands.Length) operands[index] = operand;
                    }

                    call = Expression.Call(ReadWordMethod, _state, Expression.Constant(node.Kind), operands[0], operands[1],
                        operands[2], buffer ? operands[3] : Expression.Constant(0UL),
                        Expression.Constant((long)(int)plan.Memory[node.MemoryIndex].Offset),
                        Expression.Constant(RuntimeValueEvaluator.IsAbsoluteAddress(node.Kind, handle)),
                        Expression.Constant(node, typeof(ScalarValue)), _word);
                    if (run.Context == _planContext && _groups.TryGetValue(node, out var group))
                    {
                        // The group's read may make this word known; otherwise it is read alone.
                        block.Add(Expression.Call(ReadGroupMethod, _state, Expression.Constant(group), operands[0], operands[1]));
                        block.Add(Expression.IfThen(Expression.Not(Known(slot)), Expression.Block(typeof(void),
                            Expression.IfThen(Expression.Not(call), Fail()),
                            Expression.Assign(Value(slot), _word),
                            Expression.Assign(Known(slot), Expression.Constant(true)))));
                        return true;
                    }

                    break;
                }
            }

            block.Add(Expression.IfThen(Expression.Not(call), Fail()));
            block.Add(Expression.Assign(Value(slot), _word));
            block.Add(Expression.Assign(Known(slot), Expression.Constant(true)));
            return true;
        }
    }
}
