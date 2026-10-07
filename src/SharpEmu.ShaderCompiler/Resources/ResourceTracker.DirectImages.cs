// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    private bool TryMakeWorkgroupImage(ScalarValue handle, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8) return false;
        var reads = handle.Operands.Select(value => value.Kind == ScalarValueKind.Phi
            ? _graph.ResolveInvariantPhi(value) ?? value : value).ToArray();
        var indices = new int[8];
        for (var component = 0; component < reads.Length; component++)
        {
            var read = reads[component];
            if (read.Kind != ScalarValueKind.ScalarBufferWord || read.Operands.Length != 2 ||
                read.MemoryIndex < 0 || read.MemoryIndex >= _plan.Memory.Count ||
                !MemoryIndexBelongsTo(read.MemoryIndex, read)) return false;
            var memory = _plan.Memory[read.MemoryIndex];
            if (memory.Kind != MemoryResourceKind.ScalarBuffer || memory.Access != MemoryAccess.Read ||
                memory.DataBits != 32 || memory.DataDwords != 1) return false;
            indices[component] = read.MemoryIndex;
        }
        var instruction = _graph.Program.Instructions.FirstOrDefault(candidate => candidate.Pc == _plan.Memory[indices[0]].Pc);
        if (instruction?.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: not null } ||
            !IndirectSelectorValues.WorkgroupDescriptor.TryCreate(_plan, handle, reads[0].Operands[1], out var workgroup)) return false;
        // Evaluate every actual word across the bounded dispatch domain. Loads
        // may split the descriptor, but must execute in the same basic block.
        var firstPc = _plan.Memory[indices[0]].Pc;
        var block = _graph.ControlFlow.Blocks.First(candidate => firstPc >= candidate.StartPc && firstPc < candidate.EndPc);
        if (indices.Any(index => _plan.Memory[index].Pc < block.StartPc || _plan.Memory[index].Pc >= block.EndPc)) return false;
        plan = new IndirectImagePlan
        {
            Handle = handle,
            Source = InternSource(new DescriptorSource { Dwords = handle.Operands,
                IndirectImage = new IndirectImageSelector(0, 0, 0, 0, 0) { Workgroup = workgroup } }),
            Key = reads[0], KeyIsAddressOffset = true,
            // Retain the original reads even when only image instructions use them.
            SuppressMemoryReads = false, Memory = indices, Reads = reads,
        };
        return true;
    }

    private bool TryMakeRuntimeSampler(ScalarValue handle, out uint sourceIndex)
    {
        sourceIndex = 0;
        var reads = handle.Operands.Select(word => _graph.ResolveInvariantPhi(word) ?? word).ToArray();
        if (reads.Length != 4 || reads.Any(word => word.Kind is not
                (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord) ||
                word.Kind != reads[0].Kind || word.MemoryIndex < 0 || word.MemoryIndex >= _plan.Memory.Count ||
                !MemoryIndexBelongsTo(word.MemoryIndex, word))) return false;
        var memory = _plan.Memory[reads[0].MemoryIndex];
        var block = _graph.ControlFlow.Blocks.First(candidate => memory.Pc >= candidate.StartPc && memory.Pc < candidate.EndPc);
        if (reads.Any(word => _plan.Memory[word.MemoryIndex].Pc < block.StartPc ||
                _plan.Memory[word.MemoryIndex].Pc >= block.EndPc) ||
            !TryGetBoundedRuntimeSelector(reads[0].Operands[1], memory.Pc, out var selector, out var limit, out var guardPc)) return false;
        var candidates = new List<uint>();
        for (uint index = 0; index < ShaderResourceInfo.MaxImages; index++)
        {
            var replacements = new Dictionary<ScalarValue, ScalarValue> { [selector] = _graph.Constant(index) };
            var memo = new Dictionary<ScalarValue, ScalarValue>();
            var candidate = new DescriptorSource
            {
                Dwords = reads.Select(word => _graph.Substitute(word, replacements, memo)).ToArray(),
            };
            if (!ValidateSource(candidate, out _)) return false;
            candidates.Add(InternSource(candidate));
        }
        var countSource = InternSource(new DescriptorSource { Dwords = [limit] });
        sourceIndex = InternSource(new DescriptorSource
        {
            Dwords = Enumerable.Repeat(_graph.Constant(0u), 4).ToArray(),
            EquivalentSamplerSources = candidates,
            RuntimeSamplerCountSource = countSource,
            RuntimeZeroCountGuardPc = guardPc,
        });
        return true;
    }

    private bool TryMakeFiniteSampler(ScalarValue handle, DescriptorSource original, out uint sourceIndex)
    {
        sourceIndex = 0;
        var reads = handle.Operands;
        if (reads.Length != 4 || reads.Any(read => read.Kind != ScalarValueKind.ScalarBufferWord)) return false;
        var first = ScalarReadMemory(reads[0], out _);
        if (first is null) return false;
        for (var component = 0; component < reads.Length; component++)
        {
            var memory = ScalarReadMemory(reads[component], out _);
            if (memory is null || memory.Offset != first.Offset + (uint)component * sizeof(uint) ||
                !_graph.Equivalent(reads[component].Operands[0], reads[0].Operands[0]) ||
                !_graph.Equivalent(reads[component].Operands[1], reads[0].Operands[1])) return false;
        }
        var pending = new Stack<ScalarValue>();
        pending.Push(reads[0].Operands[1]);
        var visited = new HashSet<ScalarValue>();
        ScalarValue? selector = null;
        uint[] values = [];
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.FirstLane &&
                IndirectSelectorValues.TryGetConstantValues(_plan, value, out var finite))
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                values = finite;
                continue;
            }
            if (!value.IsConstant && value.Kind != ScalarValueKind.Operation) return false;
            foreach (var operand in value.Operands) pending.Push(operand);
        }
        if (selector is null || values.Length == 0) return false;
        var candidates = new List<DescriptorSource>();
        var offsets = new List<uint>();
        foreach (var value in values)
        {
            var replacements = new Dictionary<ScalarValue, ScalarValue> { [selector] = _graph.Constant(value) };
            var memo = new Dictionary<ScalarValue, ScalarValue>();
            var offset = _graph.Substitute(reads[0].Operands[1], replacements, memo);
            if (!offset.IsConstant || offset.Type != ScalarValueType.U32 || offsets.Contains(offset.ConstantU32)) return false;
            var candidate = new DescriptorSource
            {
                Dwords = original.Dwords.Select(word => _graph.Substitute(word, replacements, memo)).ToArray(),
            };
            if (!ValidateSource(candidate, out _)) return false;
            candidates.Add(candidate);
            offsets.Add(offset.ConstantU32);
        }
        var sources = candidates.Select(InternSource).ToArray();
        sourceIndex = InternSource(new DescriptorSource
        {
            Dwords = Enumerable.Repeat(_graph.Constant(0u), 4).ToArray(),
            EquivalentSamplerSources = [sources[0]],
            FiniteSamplerSources = sources.Select((source, index) => new DirectImageCandidate(offsets[index], source)).ToArray(),
            SamplerSelectorMemoryIndex = reads[0].MemoryIndex,
        });
        return true;
    }

    private bool TryMakeDirectImage(ScalarValue handle, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
            return false;

        var reads = handle.Operands.Select(value => _graph.ResolveInvariantPhi(value) ?? value).ToArray();
        var memoryIndices = new int[8];
        var canSuppressMemoryReads = true;
        for (var component = 0; component < reads.Length; component++)
        {
            var read = reads[component];
            if (read.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord) ||
                read.Kind != reads[0].Kind || read.MemoryIndex < 0 ||
                read.MemoryIndex >= _plan.Memory.Count || !MemoryIndexBelongsTo(read.MemoryIndex, read)) return false;
            var memory = _plan.Memory[read.MemoryIndex];
            if (memory.Kind != (read.Kind == ScalarValueKind.ScalarBufferWord
                    ? MemoryResourceKind.ScalarBuffer : MemoryResourceKind.ScalarAddress) ||
                memory.DataBits != 32 || memory.DataDwords != 1)
                return false;
            canSuppressMemoryReads &= ReferenceEquals(handle.Operands[component], read) &&
                UsesOnlyThroughInvariantPhis(read, handle) && HasOnlyImageConsumers(memory, handle);
            memoryIndices[component] = read.MemoryIndex;
        }

        var keyRead = reads[0];
        var keyMemory = _plan.Memory[keyRead.MemoryIndex];
        var keyInstruction = _graph.Program.Instructions.First(instruction => instruction.Pc == keyMemory.Pc);
        if (keyInstruction.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: not null })
            return false;
        // Reconstruct every component for each proven selector, including words
        // that start inside a wide load or continue in a separate scalar load.
        var block = _graph.ControlFlow.Blocks.First(candidate => keyMemory.Pc >= candidate.StartPc && keyMemory.Pc < candidate.EndPc);
        if (memoryIndices.Any(index => _plan.Memory[index].Pc < block.StartPc || _plan.Memory[index].Pc >= block.EndPc))
            return false;

        if (TryGetPostTestedLoopSelector(reads[0].Operands[1], keyMemory.Pc, out var loopKey, out var loopValues))
            return MakeCandidates(loopKey, loopValues, out plan);
        if (TryGetBoundedRuntimeSelector(reads[0].Operands[1], keyMemory.Pc, out var runtimeKey, out var runtimeLimit, out var runtimeGuard))
        {
            var countSource = InternSource(new DescriptorSource { Dwords = [runtimeLimit] });
            return MakeCandidates(runtimeKey,
                Enumerable.Range(0, ShaderResourceInfo.MaxImages).Select(index => (uint)index).ToArray(),
                out plan, countSource, zeroCountGuard: runtimeGuard);
        }
        // Include the all-zero input result unless the scan's incoming edge proves it cannot occur.
        if (TryGetGuardedSelector(reads[0].Operands[1], keyMemory.Pc, out var guarded, out var bound))
        {
            var made = MakeCandidates(guarded, bound, out var guardedPlan);
            plan = guardedPlan;
            return made;
        }

        var pending = new Stack<ScalarValue>(reads.Select(read => read.Operands[1]));
        var visited = new HashSet<ScalarValue>();
        ScalarValue? selector = null;
        uint[]? finiteValues = null;
        IndirectSelectorValues.GatheredByteSelectorProof? byteSelectorProof = null;
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.FindLowestBit32)
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                continue;
            }
            if (value.Kind == ScalarValueKind.FirstLane &&
                IndirectSelectorValues.TryGetConstantValues(_plan, value, out var values))
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                finiteValues = values;
                continue;
            }
            if (value.Kind == ScalarValueKind.FirstLane &&
                IndirectSelectorValues.GatheredByteSelectorProof.TryCreate(_plan, value, out var byteProof))
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                finiteValues = Enumerable.Range(0, byte.MaxValue + 1).Select(index => (uint)index).ToArray();
                byteSelectorProof = byteProof;
                continue;
            }
            if (!value.IsConstant && value.Kind != ScalarValueKind.Operation) return false;
            foreach (var operand in value.Operands) pending.Push(operand);
        }
        if (selector is null) return false;

        var madeCandidates = MakeCandidates(selector, finiteValues, out var selectedPlan, byteSelectorProof: byteSelectorProof);
        plan = selectedPlan;
        return madeCandidates;

        bool MakeCandidates(ScalarValue selected, uint[]? domain, out IndirectImagePlan candidatePlan,
            uint? countSource = null, IndirectSelectorValues.GatheredByteSelectorProof? byteSelectorProof = null,
            uint? zeroCountGuard = null)
        {
            candidatePlan = null!;
            IndirectSelectorValues.PackedTextureDomain.TryCreate(_plan, selected, out var packedDomain);
            if (packedDomain is null && IndirectSelectorValues.GatheredByteSelectorProof.TryCreate(_plan, selected, out var gatheredDomain))
                packedDomain = gatheredDomain.TextureDomain(_plan);
            var candidates = new List<DirectImageCandidate>();
            var sources = new List<DescriptorSource>();
            var keys = new HashSet<uint>();
            IEnumerable<uint> results = domain ?? Enumerable.Range(0, 32).Select(index => (uint)index).ToArray();
            if (domain is null && !_graph.HasNonZeroBitScanInput(selected)) results = results.Append(uint.MaxValue);
            foreach (var result in results)
            {
                var replacements = new Dictionary<ScalarValue, ScalarValue> { [selected] = _graph.Constant(result) };
                var memo = new Dictionary<ScalarValue, ScalarValue>();
                var key = _graph.Substitute(keyRead.Operands[1], replacements, memo);
                if (!key.IsConstant || key.Type != ScalarValueType.U32 || !keys.Add(key.ConstantU32)) return false;
                var source = new DescriptorSource
                {
                    Dwords = reads.Select(read => _graph.Substitute(read, replacements, memo)).ToArray(),
                };
                if (!ValidateSource(source, out _)) return false;
                sources.Add(source);
                candidates.Add(new DirectImageCandidate(key.ConstantU32, 0) { SelectorValue = result });
            }

            for (var index = 0; index < candidates.Count; index++)
                candidates[index] = candidates[index] with { Source = InternSource(sources[index]) };
            var imageSource = new DescriptorSource
            {
                Dwords = sources[0].Dwords,
                RuntimeZeroCountGuardPc = zeroCountGuard,
                IndirectImage = new IndirectImageSelector(0, 0, 0, 0, 0)
                    {
                        DirectCandidates = candidates,
                        CandidateCountSource = countSource,
                        PackedTextureDomain = packedDomain ?? byteSelectorProof?.TextureDomain(_plan),
                        GatheredByteSelectorProof = byteSelectorProof,
                    },
            };
            candidatePlan = new IndirectImagePlan
            {
                Handle = handle,
                Source = InternSource(imageSource),
                Key = keyRead,
                KeyIsAddressOffset = true,
                SuppressMemoryReads = canSuppressMemoryReads,
                Memory = memoryIndices,
                Reads = reads,
            };
            return true;
        }
    }

    private bool TryGetBoundedRuntimeSelector(ScalarValue offset, uint loadPc, out ScalarValue selector, out ScalarValue limit,
        out uint guardPc)
    {
        selector = limit = null!;
        guardPc = 0;
        // A constant record-field offset does not change the guarded counter.
        // Candidate construction still evaluates the complete offset expression.
        while (offset.Kind == ScalarValueKind.Operation && offset.Operation == ScalarOperation.IAdd32 &&
            offset.Operands.Length == 2)
        {
            if (offset.Operands[1].IsConstant) offset = offset.Operands[0];
            else if (offset.Operands[0].IsConstant) offset = offset.Operands[1];
            else return false;
        }
        if (offset.Kind != ScalarValueKind.Operation ||
            offset.Operation is not (ScalarOperation.IMul32 or ScalarOperation.ShiftLeft32) ||
            offset.Operands.Length != 2) return false;
        var key = offset.Operands[1].IsConstant ? offset.Operands[0] :
            offset.Operands[0].IsConstant ? offset.Operands[1] : null;
        if (key is null || key.Type != ScalarValueType.U32) return false;

        var flow = _graph.ControlFlow;
        var loadBlock = Enumerable.Range(0, flow.Blocks.Count).First(index =>
            loadPc >= flow.Blocks[index].StartPc && loadPc < flow.Blocks[index].EndPc);
        foreach (var (pc, condition) in _plan.FlattenedBranchConditions)
        {
            if (condition.Kind != ScalarValueKind.Operation || condition.Operation != ScalarOperation.LogicalNot ||
                condition.Operands.Length != 1) continue;
            var gated = condition.Operands[0];
            var direct = gated.Kind == ScalarValueKind.Operation;
            if (!direct && (gated.Kind != ScalarValueKind.Select || gated.Operands.Length != 3 ||
                !gated.Operands[2].IsConstant || gated.Operands[2].Payload != 0)) continue;
            var comparison = direct ? gated : gated.Operands[0];
            if (comparison.Kind != ScalarValueKind.Operation ||
                comparison.Operation is not (ScalarOperation.ULessThan32 or ScalarOperation.SLessThan32) ||
                comparison.Operands.Length != 2 || !_graph.Equivalent(comparison.Operands[0], key)) continue;
            if (comparison.Operation == ScalarOperation.SLessThan32 &&
                (key.Kind != ScalarValueKind.Phi || key.Operands.Length != 2 ||
                 !key.Operands.Any(value => value.IsConstant && value.Payload == 0) ||
                 !key.Operands.Any(value => value.Kind == ScalarValueKind.Operation &&
                     value.Operation == ScalarOperation.IAdd32 && value.Operands.Length == 2 &&
                     ReferenceEquals(value.Operands[0], key) && value.Operands[1].IsConstant && value.Operands[1].Payload == 1)))
                continue;
            var bound = _graph.ResolveInvariantPhi(comparison.Operands[1]);
            if (bound is null || bound.Type != ScalarValueType.U32 || !_plan.ValidateRuntimeValue(bound, out _)) continue;
            var branch = _graph.Program.Instructions.First(instruction => instruction.Pc == pc);
            if (branch.Opcode != (direct ? "SCbranchScc0" : "SCbranchVccz") ||
                !SharpEmu.ShaderCompiler.Ir.Gen5IrBranchResolver.Instance.TryGetBranchTarget(branch, out var target) ||
                !flow.BlockByStartPc.TryGetValue(target, out var rejected) ||
                !flow.BlockByStartPc.TryGetValue(pc + (uint)branch.Words.Count * sizeof(uint), out var admitted)) continue;
            var guard = Enumerable.Range(0, flow.Blocks.Count).First(index =>
                pc >= flow.Blocks[index].StartPc && pc < flow.Blocks[index].EndPc);
            if (ReachesLoad(0) || ReachesLoad(rejected)) continue;
            selector = key;
            limit = bound;
            guardPc = pc;
            return true;

            bool ReachesLoad(int start)
            {
                var pending = new Stack<int>();
                pending.Push(start);
                var visited = new HashSet<int>();
                while (pending.TryPop(out var current))
                {
                    if (!visited.Add(current)) continue;
                    if (current == loadBlock) return true;
                    foreach (var next in flow.Successors[current])
                        if (current != guard || next != admitted) pending.Push(next);
                }
                return false;
            }
        }
        return false;
    }

    private bool UsesOnlyThroughInvariantPhis(ScalarValue value, ScalarValue handle)
    {
        var pending = new Stack<ScalarValue>(); pending.Push(value);
        var visited = new HashSet<ScalarValue>();
        var foundHandle = false;
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (!_uses.TryGetValue(current, out var users)) return false;
            foreach (var user in users)
            {
                if (ReferenceEquals(user, handle)) { foundHandle = true; continue; }
                if (user.Kind != ScalarValueKind.Phi || !ReferenceEquals(_graph.ResolveInvariantPhi(user), value)) return false;
                pending.Push(user);
            }
        }
        return foundHandle;
    }

    private static bool IsInvariantPhiOf(ScalarValue value, ScalarValue root)
    {
        var pending = new Stack<ScalarValue>(); pending.Push(value);
        var visited = new HashSet<ScalarValue>(); var found = false;
        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, root)) { found = true; continue; }
            if (!visited.Add(current)) continue;
            if (current.Kind != ScalarValueKind.Phi) return false;
            foreach (var operand in current.Operands) pending.Push(operand);
        }
        return found;
    }
    private bool TryGetPostTestedLoopSelector(ScalarValue offset, uint loadPc, out ScalarValue selector, out uint[] values)
    {
        selector = null!; values = [];
        if (offset.Kind != ScalarValueKind.Operation || offset.Operation is not (ScalarOperation.ShiftLeft32 or ScalarOperation.IMul32) ||
            offset.Operands.Length != 2 || !offset.Operands[1].IsConstant) return false;
        var key = offset.Operands[0];
        if (key.Kind != ScalarValueKind.Phi || key.Type != ScalarValueType.U32) return false;
        var alternatives = new HashSet<ScalarValue>();
        var pending = new Stack<ScalarValue>(); pending.Push(key); var visited = new HashSet<ScalarValue>();
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.Phi) foreach (var operand in value.Operands) pending.Push(operand);
            else alternatives.Add(value);
        }
        if (alternatives.Count != 2 || !alternatives.Any(value => value.IsConstant && value.Payload == 0)) return false;
        var increment = alternatives.SingleOrDefault(value => !value.IsConstant);
        if (increment is null || increment.Kind != ScalarValueKind.Operation || increment.Operation != ScalarOperation.IAdd32 || increment.Operands.Length != 2 ||
            !increment.Operands[1].IsConstant || increment.Operands[1].Payload != 1 || !IsInvariantPhiOf(increment.Operands[0], key)) return false;
        var flow = _graph.ControlFlow;
        var header = key.PhiBlock;
        var loadBlock = flow.BlockOf(loadPc);
        foreach (var (pc, condition) in _plan.FlattenedBranchConditions)
        {
            if (condition.Kind != ScalarValueKind.Operation || condition.Operation != ScalarOperation.SLessThan32 || condition.Operands.Length != 2 ||
                !_graph.Equivalent(condition.Operands[0], increment) || !condition.Operands[1].IsConstant ||
                condition.Operands[1].Payload is 0 or > ShaderResourceInfo.MaxImages) continue;
            var branch = _graph.Program.Instructions.First(instruction => instruction.Pc == pc);
            if (branch.Opcode != "SCbranchScc1" || !SharpEmu.ShaderCompiler.Ir.Gen5IrBranchResolver.Instance.TryGetBranchTarget(branch, out var target) ||
                !flow.BlockByStartPc.TryGetValue(target, out var backedgeHeader) || backedgeHeader != header) continue;
            var latch = flow.BlockOf(pc);
            // No path may enter the descriptor load without the counter's defining header.
            var queue = new Stack<int>(); queue.Push(0); var seen = new HashSet<int>(); var bypass = false;
            while (queue.TryPop(out var block))
            {
                if (block == header || !seen.Add(block)) continue;
                if (block == loadBlock) { bypass = true; break; }
                foreach (var next in flow.Successors[block]) queue.Push(next);
            }
            if (bypass || flow.Predecessors[header].Count(parent => parent >= header && parent != latch) != 0) continue;
            selector = key;
            values = Enumerable.Range(0, (int)condition.Operands[1].Payload).Select(index => (uint)index).ToArray();
            return true;
        }
        return false;
    }
    private bool TryGetGuardedSelector(ScalarValue offset, uint loadPc, out ScalarValue selector, out uint[] values)
    {
        selector = null!;
        values = [];
        if (offset.Kind == ScalarValueKind.Operation && offset.Operation == ScalarOperation.IAdd32 &&
            offset.Operands.Length == 2)
        {
            if (offset.Operands[1].IsConstant) offset = offset.Operands[0];
            else if (offset.Operands[0].IsConstant) offset = offset.Operands[1];
            else return false;
        }
        if (offset.Kind != ScalarValueKind.Operation || offset.Operation != ScalarOperation.IMul32 ||
            offset.Operands.Length != 2) return false;
        var key = offset.Operands[1].IsConstant ? offset.Operands[0] :
            offset.Operands[0].IsConstant ? offset.Operands[1] : null;
        if (key is null || key.Type != ScalarValueType.U32) return false;
        var flow = _graph.ControlFlow;
        var loadBlock = Enumerable.Range(0, flow.Blocks.Count).First(index =>
            loadPc >= flow.Blocks[index].StartPc && loadPc < flow.Blocks[index].EndPc);
        foreach (var (pc, condition) in _plan.FlattenedBranchConditions)
        {
            if (condition.Kind != ScalarValueKind.Operation || condition.Operation != ScalarOperation.UGreaterThanEqual32 ||
                condition.Operands.Length != 2 || !_graph.Equivalent(condition.Operands[0], key) ||
                !condition.Operands[1].IsConstant) continue;
            var limit = condition.Operands[1].ConstantU32;
            if (limit == 0 || limit > 4096) continue;
            var branch = _graph.Program.Instructions.First(instruction => instruction.Pc == pc);
            // Only SCC=1 takes the rejected (key >= limit) edge. Reversing
            // the branch would admit precisely the values excluded here.
            if (branch.Opcode != "SCbranchScc1") continue;
            if (!SharpEmu.ShaderCompiler.Ir.Gen5IrBranchResolver.Instance.TryGetBranchTarget(branch, out var target) ||
                !flow.BlockByStartPc.TryGetValue(target, out var rejected) ||
                !flow.BlockByStartPc.TryGetValue(pc + (uint)branch.Words.Count * 4, out var admitted)) continue;
            var guard = Enumerable.Range(0, flow.Blocks.Count).First(index =>
                pc >= flow.Blocks[index].StartPc && pc < flow.Blocks[index].EndPc);
            if (ReachesLoad(0) || ReachesLoad(rejected)) continue;
            selector = key;
            values = Enumerable.Range(0, (int)limit).Select(value => (uint)value).ToArray();
            return true;

            bool ReachesLoad(int start)
            {
                var pending = new Stack<int>(); pending.Push(start);
                var visited = new HashSet<int>();
                while (pending.TryPop(out var block))
                {
                    if (!visited.Add(block)) continue;
                    if (block == loadBlock) return true;
                    foreach (var next in flow.Successors[block])
                        if (block != guard || next != admitted) pending.Push(next);
                }
                return false;
            }
        }
        return false;
    }


    // Resource-graph uses omit ordinary shader arithmetic; check those reads before removing a load.
    private bool HasOnlyImageConsumers(MemoryAccessInfo memory, ScalarValue handle)
    {
        var instructions = _graph.Program.Instructions;
        var load = instructions.First(instruction => instruction.Pc == memory.Pc);
        if (memory.ComponentIndex >= load.Destinations.Count) return false;
        var destination = load.Destinations[(int)memory.ComponentIndex];
        if (destination.Kind != Gen5OperandKind.ScalarRegister || destination.Value >= 106) return false;
        var flow = _graph.ControlFlow;
        var initialBlock = Enumerable.Range(0, flow.Blocks.Count)
            .First(index => load.Pc >= flow.Blocks[index].StartPc && load.Pc < flow.Blocks[index].EndPc);
        var pending = new Queue<(int Block, uint Start)>();
        var visited = new HashSet<(int Block, uint Start)>();
        pending.Enqueue((initialBlock, load.Pc + (uint)load.Words.Count * sizeof(uint)));
        while (pending.TryDequeue(out var position))
        {
            if (!visited.Add(position)) continue;
            var block = flow.Blocks[position.Block];
            var overwritten = false;
            foreach (var instruction in instructions.Where(instruction => instruction.Pc >= position.Start && instruction.Pc < block.EndPc))
            {
                if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal)) return false;
                var allowedImage = instruction.Control is Gen5ImageControl &&
                    _plan.Memory.TryGetIndex(instruction.Pc, 0, out var imageIndex) &&
                    ReferenceEquals(_plan.Accesses[imageIndex]?.Handle, handle);
                if (!allowedImage && (ReadsScalar(instruction, destination.Value) ||
                    (instruction.Encoding == Gen5ShaderEncoding.Sopk && instruction.Opcode is "SAddkI32" or "SMulkI32" &&
                        instruction.Destinations.Contains(destination)))) return false;
                overwritten = instruction.Destinations.Any(target => target.Kind == Gen5OperandKind.ScalarRegister &&
                    (target == destination || (instruction.Opcode.Contains("64", StringComparison.Ordinal) && target.Value + 1 == destination.Value))) ||
                    instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination } &&
                        destination.Value >= scalarDestination && destination.Value - scalarDestination < 2 ||
                    instruction.Control is Gen5SdwaControl { ScalarDestination: { } compareDestination } &&
                        destination.Value >= compareDestination && destination.Value - compareDestination < 2;
                if (overwritten) break;
            }
            if (!overwritten)
                foreach (var successor in flow.Successors[position.Block])
                    pending.Enqueue((successor, flow.Blocks[successor].StartPc));
        }
        return true;
    }

    private static bool ReadsScalar(Gen5ShaderInstruction instruction, uint register)
    {
        bool InRange(uint first, uint width) => register >= first && register - first < width;
        var width = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
        if (instruction.Sources.Any(source => source.Kind == Gen5OperandKind.ScalarRegister && InRange(source.Value, width)))
            return true;
        return instruction.Control switch
        {
            Gen5ImageControl image => InRange(image.ScalarResource, 8) || InRange(image.ScalarSampler, 4),
            Gen5BufferMemoryControl buffer => InRange(buffer.ScalarResource, 4),
            Gen5GlobalMemoryControl global => InRange(global.ScalarAddress, 2),
            Gen5ScalarMemoryControl scalar =>
                instruction.Sources.Count > 0 && InRange(instruction.Sources[0].Value,
                    instruction.Opcode.StartsWith("SBuffer", StringComparison.Ordinal) ? 4u : 2u) ||
                scalar.DynamicOffsetRegister == register,
            _ => false,
        };
    }
}
