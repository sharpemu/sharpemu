// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    private bool TryMakeDirectImage(ScalarValue handle, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
            return false;

        var reads = handle.Operands;
        var memoryIndices = new int[8];
        var canSuppressMemoryReads = true;
        for (var component = 0; component < reads.Length; component++)
        {
            var read = reads[component];
            if (read.Kind != ScalarValueKind.ScalarAddressWord || read.MemoryIndex < 0 ||
                read.MemoryIndex >= _plan.Memory.Count || !MemoryIndexBelongsTo(read.MemoryIndex, read) ||
                !UsesOnly(read, [handle])) return false;
            var memory = _plan.Memory[read.MemoryIndex];
            if (memory.Kind != MemoryResourceKind.ScalarAddress || memory.DataBits != 32 || memory.DataDwords != 1)
                return false;
            canSuppressMemoryReads &= HasOnlyImageConsumers(memory, handle);
            memoryIndices[component] = read.MemoryIndex;
        }

        var keyRead = reads[0];
        var keyMemory = _plan.Memory[keyRead.MemoryIndex];
        var keyInstruction = _graph.Program.Instructions.First(instruction => instruction.Pc == keyMemory.Pc);
        if (keyMemory.ComponentIndex != 0 || keyInstruction.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: not null })
            return false;
        var block = _graph.ControlFlow.Blocks.First(candidate => keyMemory.Pc >= candidate.StartPc && keyMemory.Pc < candidate.EndPc);
        if (memoryIndices.Any(index => _plan.Memory[index].Pc < block.StartPc || _plan.Memory[index].Pc >= block.EndPc))
            return false;

        // Include the all-zero input result unless the scan's incoming edge proves it cannot occur.
        var pending = new Stack<ScalarValue>(reads.Select(read => read.Operands[1]));
        var visited = new HashSet<ScalarValue>();
        ScalarValue? selector = null;
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.FindLowestBit32)
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                continue;
            }
            if (!value.IsConstant && value.Kind != ScalarValueKind.Operation) return false;
            foreach (var operand in value.Operands) pending.Push(operand);
        }
        if (selector is null) return false;

        var candidates = new List<DirectImageCandidate>();
        var sources = new List<DescriptorSource>();
        var keys = new HashSet<uint>();
        var results = Enumerable.Range(0, 32).Select(index => (uint)index);
        if (!_graph.HasNonZeroBitScanInput(selector)) results = results.Append(uint.MaxValue);
        foreach (var result in results)
        {
            var replacements = new Dictionary<ScalarValue, ScalarValue> { [selector] = _graph.Constant(result) };
            var memo = new Dictionary<ScalarValue, ScalarValue>();
            var key = _graph.Substitute(keyRead.Operands[1], replacements, memo);
            if (!key.IsConstant || key.Type != ScalarValueType.U32 || !keys.Add(key.ConstantU32)) return false;
            var source = new DescriptorSource
            {
                Dwords = reads.Select(read => _graph.Substitute(read, replacements, memo)).ToArray(),
            };
            if (!ValidateSource(source, out _)) return false;
            sources.Add(source);
            candidates.Add(new DirectImageCandidate(key.ConstantU32, 0));
        }

        for (var index = 0; index < candidates.Count; index++)
            candidates[index] = candidates[index] with { Source = InternSource(sources[index]) };
        var imageSource = new DescriptorSource
        {
            Dwords = sources[0].Dwords,
            IndirectImage = new IndirectImageSelector(0, 0, 0, 0, 0) { DirectCandidates = candidates },
        };
        plan = new IndirectImagePlan
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

    // A walk that reaches this many instructions before the register is written again keeps the load.
    private const int MaxConsumerWalkInstructions = 4096;

    private Dictionary<uint, int>? _instructionIndexByPc;

    // The index of the first instruction at or after pc, in the program's ascending pc order.
    private int FirstInstructionAt(uint pc)
    {
        var instructions = _graph.Program.Instructions;
        if (_instructionIndexByPc is null)
        {
            _instructionIndexByPc = new Dictionary<uint, int>(instructions.Count);
            for (var index = 0; index < instructions.Count; index++)
            {
                _instructionIndexByPc.TryAdd(instructions[index].Pc, index);
            }
        }

        if (_instructionIndexByPc.TryGetValue(pc, out var found))
        {
            return found;
        }

        var low = 0;
        var high = instructions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (instructions[middle].Pc < pc) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    // Resource-graph uses omit ordinary shader arithmetic; check those reads before removing a load.
    private bool HasOnlyImageConsumers(MemoryAccessInfo memory, ScalarValue handle)
    {
        var instructions = _graph.Program.Instructions;
        var loadIndex = FirstInstructionAt(memory.Pc);
        if (loadIndex >= instructions.Count || instructions[loadIndex].Pc != memory.Pc) return false;
        var load = instructions[loadIndex];
        if (memory.ComponentIndex >= load.Destinations.Count) return false;
        var destination = load.Destinations[(int)memory.ComponentIndex];
        if (destination.Kind != Gen5OperandKind.ScalarRegister || destination.Value >= 106) return false;
        var flow = _graph.ControlFlow;
        var initialBlock = flow.BlockOf(load.Pc);
        if (initialBlock < 0) return false;
        var pending = new Queue<(int Block, uint Start)>();
        var visited = new HashSet<(int Block, uint Start)>();
        var walked = 0;
        pending.Enqueue((initialBlock, load.Pc + (uint)load.Words.Count * sizeof(uint)));
        while (pending.TryDequeue(out var position))
        {
            if (!visited.Add(position)) continue;
            var block = flow.Blocks[position.Block];
            var overwritten = false;
            for (var index = FirstInstructionAt(position.Start); index < instructions.Count && instructions[index].Pc < block.EndPc; index++)
            {
                var instruction = instructions[index];
                if (++walked > MaxConsumerWalkInstructions) return false;
                if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal)) return false;
                var allowedImage = instruction.Control is Gen5ImageControl &&
                    _plan.Memory.TryGetIndex(instruction.Pc, 0, out var imageIndex) &&
                    ReferenceEquals(_plan.Accesses[imageIndex]?.Handle, handle);
                if (!allowedImage && (ReadsScalar(instruction, destination.Value) ||
                    (instruction.Encoding == Gen5ShaderEncoding.Sopk && instruction.Opcode is "SAddkI32" or "SMulkI32" &&
                        instruction.Destinations.Contains(destination)))) return false;
                overwritten = instruction.Destinations.Any(target => target.Kind == Gen5OperandKind.ScalarRegister &&
                    (target == destination || (instruction.DestinationWidth > 1 && target.Value + 1 == destination.Value))) ||
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
