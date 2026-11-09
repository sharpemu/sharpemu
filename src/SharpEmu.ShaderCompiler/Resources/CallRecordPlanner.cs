// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The callees of a program linked with run-time record arguments (ShaderCallEntryControl.RuntimeArgument): which copy each
// instruction belongs to, and what the host reads through every record's argument besides images and samplers. A callee
// that reaches memory through its argument in a way the host cannot resolve for each record is refused, and its caller is
// linked with a copy per record instead.
public static class CallRecordPlanner
{
    public static bool HasRuntimeEntries(Gen5ShaderProgram program) =>
        program.Instructions.Any(static instruction => instruction.Control is ShaderCallEntryControl { RuntimeArgument: true });

    // The callee slot (its index among Gen5ShaderCallLinker.CallTargets) and the entry pc of each instruction of a copy.
    public static Dictionary<uint, (int Callee, uint Entry)> MapCallees(Gen5ShaderProgram program)
    {
        var targets = Gen5ShaderCallLinker.CallTargets(program);
        var slotOf = new Dictionary<ulong, int>();
        for (var slot = 0; slot < targets.Length; slot++)
        {
            slotOf[targets[slot]] = slot;
        }

        var map = new Dictionary<uint, (int Callee, uint Entry)>();
        (int Callee, uint Entry)? current = null;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is ShaderCallEntryControl { RuntimeArgument: true } entry)
            {
                current = slotOf.TryGetValue(entry.Callee, out var slot) ? (slot, instruction.Pc) : null;
                continue;
            }

            if (instruction.Control is ShaderCallEntryControl or ShaderCallMatchControl or ShaderCallFaultControl)
            {
                current = null;
                continue;
            }

            if (current is { } owner)
            {
                map[instruction.Pc] = owner;
            }
        }

        return map;
    }

    public static IReadOnlyList<CallRecordCalleePlan> Plan(ShaderResourcePlan plan)
    {
        var program = plan.Graph.Program;
        if (!HasRuntimeEntries(program))
        {
            return [];
        }

        var callees = MapCallees(program);
        var targets = Gen5ShaderCallLinker.CallTargets(program);
        var buffers = new List<CallRecordBuffer>[targets.Length];
        var expressions = new List<CallRecordBufferWords>[targets.Length];
        var bytes = new uint[targets.Length];
        for (var slot = 0; slot < targets.Length; slot++)
        {
            buffers[slot] = [];
            expressions[slot] = [];
        }

        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (!callees.TryGetValue(memory.Pc, out var owner) ||
                !plan.CallRecordArguments.TryGetValue(owner.Entry, out var argument) ||
                (plan.ValidateRuntimeValue(argument.Low) && plan.ValidateRuntimeValue(argument.High)))
            {
                // Outside the copies, or an argument the host evaluates like any other value.
                continue;
            }

            var access = plan.Accesses[index];
            if (memory.Kind == MemoryResourceKind.ScalarAddress && access?.Handle is { Kind: ScalarValueKind.AddressHandle } handle &&
                IsArgument(plan, handle, argument))
            {
                if (access.Read is not { } read || !read.Operands[1].IsConstant || read.Operands[1].ConstantU32 != 0 ||
                    unchecked((int)memory.Offset) < 0)
                {
                    throw Failure(plan, memory.Pc, "a callee reads its record argument at a run-time offset");
                }

                bytes[owner.Callee] = Math.Max(bytes[owner.Callee], memory.Offset + Math.Max(memory.DataDwords, 1u) * sizeof(uint));
                continue;
            }

            if (memory.DeviceDescriptor && program.FunctionBufferAccesses.Contains(memory.Pc) &&
                access?.Handle is { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } buffer)
            {
                if (buffer.Operands.All(plan.ValidateRuntimeValue))
                {
                    continue;
                }

                var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
                if (!TryBufferField(plan, buffer, argument, out var immediate))
                {
                    var words = SubstituteArgument(plan, buffer.Operands, argument);
                    if (!words.All(plan.ValidateRuntimeValue))
                    {
                        throw Failure(plan, memory.Pc, "a callee buffer depends on values the host cannot evaluate for its record: " +
                            string.Join(" | ", buffer.Operands.Select(word => Describe(plan, word, 3))));
                    }

                    var computed = expressions[owner.Callee];
                    var same = computed.FindIndex(field => field.Words.Zip(words).All(pair => plan.Graph.Equivalent(pair.First, pair.Second)));
                    if (same < 0)
                    {
                        computed.Add(new CallRecordBufferWords(words, written));
                    }
                    else if (written)
                    {
                        computed[same] = computed[same] with { Written = true };
                    }

                    continue;
                }

                var fields = buffers[owner.Callee];
                var existing = fields.FindIndex(field => field.Immediate == immediate);
                if (existing < 0)
                {
                    fields.Add(new CallRecordBuffer(immediate, written));
                }
                else if (written)
                {
                    fields[existing] = fields[existing] with { Written = true };
                }

                continue;
            }

            if (memory.Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Global or MemoryResourceKind.Flat &&
                access?.Handle is { Kind: ScalarValueKind.AddressHandle } pointer &&
                !pointer.Operands.All(plan.ValidateRuntimeValue) && DependsOn(plan, pointer, argument))
            {
                throw Failure(plan, memory.Pc, "a callee reads memory through a pointer its record holds");
            }
        }

        var result = new CallRecordCalleePlan[targets.Length];
        for (var slot = 0; slot < targets.Length; slot++)
        {
            result[slot] = new CallRecordCalleePlan([.. buffers[slot].OrderBy(static field => field.Immediate)], [.. expressions[slot]], bytes[slot]);
        }

        return result;
    }

    // The ranges of the buffers whose V#s one record's callee computes from its argument (CallRecordBufferWords).
    // False when a buffer the record names describes no buffer (past the 48-bit address space, or larger than 4 GiB): the
    // record's argument is not the callee's.
    public static bool AddRecordRanges(ShaderResourcePlan plan, int callee, ulong argument, ResourceRuntimeInputs inputs,
        uint userDataBase, List<DeviceAddressRange> ranges)
    {
        const ulong MaxRecordBufferBytes = 1UL << 32;
        if ((uint)callee >= (uint)plan.CallRecordCallees.Count || plan.CallRecordCallees[callee].BufferWords.Length == 0)
        {
            return true;
        }

        var evaluator = new RuntimeValueEvaluator(plan, inputs.WithCallArguments(userDataBase, [argument]));
        Span<uint> words = stackalloc uint[4];
        foreach (var buffer in plan.CallRecordCallees[callee].BufferWords)
        {
            var resolved = true;
            for (var dword = 0; dword < 4 && resolved; dword++)
            {
                resolved = evaluator.Evaluate(buffer.Words[dword], out words[dword]);
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
                return false;
            }

            ranges.Add(new DeviceAddressRange(uint.MaxValue, address, size, true, buffer.Written));
        }

        return true;
    }

    // The words with the argument pair replaced by the call argument registers the host fills with each record's argument.
    private static ScalarValue[] SubstituteArgument(ShaderResourcePlan plan, ScalarValue[] words, (ScalarValue Low, ScalarValue High) argument)
    {
        var replacements = new Dictionary<ScalarValue, ScalarValue>
        {
            [argument.Low] = plan.Graph.UserData(ScalarValueGraph.CallArgumentRegisterBase),
            [argument.High] = plan.Graph.UserData(ScalarValueGraph.CallArgumentRegisterBase + 1),
        };
        var memo = new Dictionary<ScalarValue, ScalarValue>();
        return [.. words.Select(word => plan.Graph.Substitute(word, replacements, memo))];
    }

    private static bool IsArgument(ShaderResourcePlan plan, ScalarValue handle, (ScalarValue Low, ScalarValue High) argument) =>
        handle.Operands.Length == 2 && plan.Graph.Equivalent(handle.Operands[0], argument.Low) &&
        plan.Graph.Equivalent(handle.Operands[1], argument.High);

    // Four consecutive dwords read at an immediate offset from the argument.
    private static bool TryBufferField(ShaderResourcePlan plan, ScalarValue buffer, (ScalarValue Low, ScalarValue High) argument,
        out uint immediate)
    {
        immediate = 0;
        for (var dword = 0; dword < 4; dword++)
        {
            var read = plan.Graph.ResolveInvariantPhi(buffer.Operands[dword]) ?? buffer.Operands[dword];
            if (read.Kind != ScalarValueKind.ScalarAddressWord || read.MemoryIndex < 0 || read.MemoryIndex >= plan.Memory.Count ||
                read.Operands[0].Kind != ScalarValueKind.AddressHandle || !IsArgument(plan, read.Operands[0], argument) ||
                !read.Operands[1].IsConstant || read.Operands[1].ConstantU32 != 0)
            {
                return false;
            }

            var memory = plan.Memory[read.MemoryIndex];
            if (memory.Kind != MemoryResourceKind.ScalarAddress || memory.DataBits != 32 || memory.DataDwords != 1 ||
                unchecked((int)memory.Offset) < 0)
            {
                return false;
            }

            if (dword == 0)
            {
                immediate = memory.Offset;
            }
            else if (memory.Offset != immediate + (uint)dword * sizeof(uint))
            {
                return false;
            }
        }

        return true;
    }

    // Whether a value is computed from the argument pair.
    private static bool DependsOn(ShaderResourcePlan plan, ScalarValue value, (ScalarValue Low, ScalarValue High) argument)
    {
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>(ReferenceEqualityComparer.Instance);
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, argument.Low) || ReferenceEquals(current, argument.High) ||
                (current.Kind == argument.Low.Kind && plan.Graph.Equivalent(current, argument.Low)) ||
                (current.Kind == argument.High.Kind && plan.Graph.Equivalent(current, argument.High)))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }

        return false;
    }

    private static string Describe(ShaderResourcePlan plan, ScalarValue value, int depth)
    {
        var text = value.Kind switch
        {
            ScalarValueKind.Constant => $"0x{value.ConstantU32:X}",
            ScalarValueKind.Operation => value.Operation.ToString(),
            ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord when value.MemoryIndex >= 0 && value.MemoryIndex < plan.Memory.Count =>
                $"{value.Kind}[imm=0x{plan.Memory[value.MemoryIndex].Offset:X} pc=0x{plan.Memory[value.MemoryIndex].Pc:X}]",
            _ => value.Kind.ToString(),
        };
        if (ReferenceEquals(value, null) || depth == 0 || value.Operands.Length == 0) return text;
        return text + "(" + string.Join(",", value.Operands.Select(operand => Describe(plan, operand, depth - 1))) + ")";
    }

    private static ResourcePlanException Failure(ShaderResourcePlan plan, uint pc, string reason) =>
        new($"shader call records: hash=0x{plan.Hash:X16} pc=0x{pc:X8} {reason}");
}
