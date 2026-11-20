// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The branch selection of a plan with resource branches (RuntimeValueEvaluator.EvaluateActiveSources) on registers lowered once
// per plan, so a draw builds no evaluators for it. Clean reads have effects (a resolve worker logs each word it reads, and a
// read can throw to resolve the draw in stream order), so the selection reads exactly what the evaluator reads, in its order:
// the conditions of the blocks it visits, evaluated on demand operand by operand (a failed operand ends its value, a success
// is kept for the call), then, for a plan that writes memory, the written buffers' descriptor words, buffer by buffer, which
// ResourceBranchWriteGuard tests against the words the conditions read.
internal sealed partial class MaterializationProgram
{
    private enum BranchNodeKind : byte
    {
        Constant,
        Step,
        Read,
    }

    private readonly record struct BranchNode(BranchNodeKind Kind, ulong Constant, Step Step, ReadStep Read);

    [ThreadStatic]
    private static BranchScratch? _branchScratch;

    private sealed class BranchScratch
    {
        public ulong[] Values = new ulong[128];
        public uint[] Stamps = new uint[128];
        public uint Generation;
        // Whether a successful clean read is kept for the write guard.
        public bool Recording;
        public ulong[] Reads = new ulong[16];
        public int ReadCount;
        public bool[] Visited = new bool[16];
        public int[] Pending = new int[32];

        public void Begin(int registers, int blocks, int pending, bool recording)
        {
            if (Values.Length < registers)
            {
                var size = Math.Max(registers, Values.Length * 2);
                Values = new ulong[size];
                Stamps = new uint[size];
            }

            if (Visited.Length < blocks)
            {
                Visited = new bool[Math.Max(blocks, Visited.Length * 2)];
            }
            else
            {
                Array.Clear(Visited, 0, blocks);
            }

            if (Pending.Length < pending)
            {
                Pending = new int[Math.Max(pending, Pending.Length * 2)];
            }

            if (++Generation == 0)
            {
                Array.Clear(Stamps);
                Generation = 1;
            }

            Recording = recording;
            ReadCount = 0;
        }

        public void Record(ulong address)
        {
            if (ReadCount == Reads.Length)
            {
                Array.Resize(ref Reads, ReadCount * 2);
            }

            Reads[ReadCount++] = address;
        }
    }

    internal sealed class BranchSelection
    {
        private readonly BranchNode[] _nodes;
        private readonly int[] _conditions;
        private readonly int[][] _successors;
        private readonly uint[][] _sources;
        private readonly bool[] _initialActive;
        private readonly bool _guarded;
        // Four registers per written buffer, in ResourceBranchWrites order, up to the first whose descriptor is not four words.
        private readonly int[] _writtenWords;
        // The guard fails once it reaches a written descriptor that is not four words.
        private readonly bool _guardFails;
        private readonly int _pendingCapacity;

        private BranchSelection(BranchNode[] nodes, int[] conditions, int[][] successors, uint[][] sources, bool[] initialActive, bool guarded,
            int[] writtenWords, bool guardFails, int pendingCapacity)
        {
            _nodes = nodes;
            _conditions = conditions;
            _successors = successors;
            _sources = sources;
            _initialActive = initialActive;
            _guarded = guarded;
            _writtenWords = writtenWords;
            _guardFails = guardFails;
            _pendingCapacity = pendingCapacity;
        }

        internal int SourceCount => _initialActive.Length;

        // Null when a condition or a written descriptor word does not lower: the plan selects with the evaluator.
        internal static BranchSelection? Build(ShaderResourcePlan plan)
        {
            var blocks = plan.ResourceBranches;
            if (blocks.Count == 0)
            {
                return null;
            }

            var builder = new Builder(plan);
            var conditions = new int[blocks.Count];
            for (var index = 0; index < blocks.Count; index++)
            {
                conditions[index] = -1;
                if (blocks[index].Condition is { } condition && !builder.Register(condition, out conditions[index]))
                {
                    return null;
                }
            }

            var writtenWords = new List<int>();
            var guardFails = false;
            if (plan.ResourceBranchesWithWrites)
            {
                foreach (var buffer in plan.ResourceBranchWrites)
                {
                    var source = plan.DescriptorSources[(int)plan.Info.Buffers[(int)buffer].Source];
                    if (source.DwordCount != 4)
                    {
                        guardFails = true;
                        break;
                    }

                    foreach (var word in source.Dwords)
                    {
                        if (!builder.Register(word, out var register))
                        {
                            return null;
                        }

                        writtenWords.Add(register);
                    }
                }
            }

            var successors = new int[blocks.Count][];
            var sources = new uint[blocks.Count][];
            var initialActive = new bool[plan.DescriptorSources.Count];
            Array.Fill(initialActive, true);
            // A walk visits each block once and pushes each of its successors at most once.
            var pendingCapacity = 1;
            for (var index = 0; index < blocks.Count; index++)
            {
                successors[index] = blocks[index].Successors;
                sources[index] = blocks[index].Sources;
                pendingCapacity += successors[index].Length;
                foreach (var source in sources[index])
                {
                    initialActive[source] = false;
                }
            }

            return new BranchSelection(builder.Nodes(), conditions, successors, sources, initialActive, plan.ResourceBranchesWithWrites,
                [.. writtenWords], guardFails, pendingCapacity);
        }

        // Fills `active` (SourceCount long) as EvaluateActiveSources does; false without a clean reader, which the evaluator
        // handles.
        internal bool TrySelect(ResourceRuntimeInputs inputs, bool[] active)
        {
            var clean = inputs.ReadCleanMemory;
            if (clean is null)
            {
                return false;
            }

            // Taken out of its slot while in use: a guest read can select another draw's sources on this thread.
            var scratch = _branchScratch ?? new BranchScratch();
            _branchScratch = null;
            try
            {
                Select(inputs, clean, scratch, active);
            }
            finally
            {
                _branchScratch = scratch;
            }

            ResourceMaterializationProfile.RecordActivity(active);
            return true;
        }

        private void Select(ResourceRuntimeInputs inputs, GuestWordReader clean, BranchScratch scratch, bool[] active)
        {
            scratch.Begin(_nodes.Length, _conditions.Length, _pendingCapacity, _guarded);
            Array.Copy(_initialActive, active, _initialActive.Length);
            var visited = scratch.Visited;
            var pending = scratch.Pending;
            var count = 0;
            pending[count++] = 0;
            while (count != 0)
            {
                var block = pending[--count];
                if (visited[block])
                {
                    continue;
                }

                visited[block] = true;
                foreach (var source in _sources[block])
                {
                    active[source] = true;
                }

                // An unreadable predicate keeps both paths.
                var condition = _conditions[block];
                var successors = _successors[block];
                if (condition >= 0 && Evaluate(condition, inputs, clean, scratch))
                {
                    pending[count++] = successors[(uint)scratch.Values[condition] != 0 ? 0 : 1];
                    continue;
                }

                foreach (var successor in successors)
                {
                    pending[count++] = successor;
                }
            }

            if (_guarded && !GuardHolds(inputs, clean, scratch))
            {
                Array.Fill(active, true);
            }
        }

        // ResourceBranchWriteGuard.Holds: each written descriptor, read after every predicate, against the words the predicates
        // read.
        private bool GuardHolds(ResourceRuntimeInputs inputs, GuestWordReader clean, BranchScratch scratch)
        {
            var predicateReads = scratch.Reads.AsSpan(0, scratch.ReadCount);
            scratch.Recording = false;
            for (var at = 0; at < _writtenWords.Length; at += 4)
            {
                for (var word = at; word < at + 4; word++)
                {
                    if (!Evaluate(_writtenWords[word], inputs, clean, scratch))
                    {
                        return false;
                    }
                }

                var values = scratch.Values;
                if (ResourceBranchWriteGuard.Reaches((uint)values[_writtenWords[at]], (uint)values[_writtenWords[at + 1]],
                    (uint)values[_writtenWords[at + 2]], (uint)values[_writtenWords[at + 3]], predicateReads))
                {
                    return false;
                }
            }

            return !_guardFails;
        }

        // The evaluator's value of a register: its operands in the evaluator's order, where the first failure ends it and a
        // success is kept for the rest of the call.
        private bool Evaluate(int register, ResourceRuntimeInputs inputs, GuestWordReader clean, BranchScratch scratch)
        {
            if (scratch.Stamps[register] == scratch.Generation)
            {
                return true;
            }

            ref readonly var node = ref _nodes[register];
            ulong value;
            switch (node.Kind)
            {
                case BranchNodeKind.Constant:
                    value = node.Constant;
                    break;
                case BranchNodeKind.Step:
                    if (!EvaluateStep(node.Step, inputs, clean, scratch, out value))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!ReadClean(node.Read, inputs, clean, scratch, out value))
                    {
                        return false;
                    }

                    break;
            }

            scratch.Values[register] = value;
            scratch.Stamps[register] = scratch.Generation;
            return true;
        }

        private bool EvaluateStep(in Step step, ResourceRuntimeInputs inputs, GuestWordReader clean, BranchScratch scratch, out ulong value)
        {
            value = 0;
            switch (step.Kind)
            {
                case StepKind.UserData:
                    if ((uint)step.First >= (uint)inputs.UserData.Count)
                    {
                        return false;
                    }

                    value = inputs.UserData[step.First];
                    return true;
                case StepKind.ShaderBase:
                    value = inputs.ShaderBase;
                    return true;
                case StepKind.Select:
                {
                    // A select reads both arms, as the evaluator's does.
                    if (!Evaluate(step.First, inputs, clean, scratch) || !Evaluate(step.Second, inputs, clean, scratch) ||
                        !Evaluate(step.Third, inputs, clean, scratch))
                    {
                        return false;
                    }

                    var values = scratch.Values;
                    value = values[step.First] != 0 ? values[step.Second] : values[step.Third];
                    return true;
                }
                default:
                {
                    var registers = step.Operands!;
                    foreach (var operand in registers)
                    {
                        if (!Evaluate(operand, inputs, clean, scratch))
                        {
                            return false;
                        }
                    }

                    Span<ulong> operands = registers.Length <= 8 ? stackalloc ulong[8] : new ulong[registers.Length];
                    operands = operands[..registers.Length];
                    for (var index = 0; index < registers.Length; index++)
                    {
                        operands[index] = scratch.Values[registers[index]];
                    }

                    return ScalarOperationSemantics.TryEvaluate(step.Operation, operands, out value);
                }
            }
        }

        // RuntimeValueEvaluator.EvaluateRawRead and ReadRawWord: the handle's low and high words, the offset, then a buffer's
        // records and word 3, and one aligned dword. Every predicate reader is clean, so a refused word reads as zero only
        // through a null base.
        private bool ReadClean(in ReadStep read, ResourceRuntimeInputs inputs, GuestWordReader clean, BranchScratch scratch, out ulong value)
        {
            value = 0;
            var buffer = read.Kind == ScalarValueKind.ScalarBufferWord;
            if (!Evaluate(read.Low, inputs, clean, scratch) || !Evaluate(read.High, inputs, clean, scratch) ||
                (read.Offset >= 0 && !Evaluate(read.Offset, inputs, clean, scratch)) ||
                (buffer && (!Evaluate(read.Records, inputs, clean, scratch) || !Evaluate(read.Word3, inputs, clean, scratch))))
            {
                return false;
            }

            var values = scratch.Values;
            var high = values[read.High];
            var records = buffer ? values[read.Records] : 0;
            var offset = read.Offset >= 0 ? (uint)values[read.Offset] : read.ConstantOffset;
            var baseAddress = ((high << 32) | (uint)values[read.Low]) & AddressMask;
            switch (RuntimeValueEvaluator.ResolveRawAddress(read.Kind, baseAddress, high, records, read.Immediate, offset, out var address))
            {
                case RuntimeValueEvaluator.RawAddress.Failed:
                    return false;
                case RuntimeValueEvaluator.RawAddress.Zero:
                    return true;
            }

            if (!clean(address, out var word))
            {
                return (baseAddress & ~3ul) == 0;
            }

            if (scratch.Recording)
            {
                scratch.Record(address);
            }

            value = word;
            return true;
        }
    }

    // The active sources of TryRun and TryRunInto: the plan's compiled selection when it lowers and the draw has a clean reader,
    // else EvaluateActiveSources. With `reuse`, the array is that scratch's.
    private static bool[] SelectActiveSources(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, Scratch? reuse)
    {
        if (plan.BranchSelection is { } selection)
        {
            var active = reuse?.ActiveSources(selection.SourceCount) ?? new bool[selection.SourceCount];
            if (selection.TrySelect(inputs, active))
            {
                return active;
            }
        }

        return RuntimeValueEvaluator.EvaluateActiveSources(plan, inputs);
    }
}
