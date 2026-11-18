// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Proves an inclusive unsigned upper bound for a material-table selector from the
// clamps and masks the shader itself applies (min, med3, and-mask, shifts, selects over
// bounded values). Anything not proven stays unbounded; there is no guess.
internal static class IndirectSelectorBound
{
    internal static uint? TryCreate(ShaderResourcePlan plan, ScalarValue selector)
    {
        if (selector.Kind != ScalarValueKind.FirstLane) return null;
        var instruction = plan.Graph.Program.Instructions.FirstOrDefault(candidate => candidate.Pc == selector.Payload);
        if (instruction is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1 }) return null;
        var bound = new Analyzer(plan).Read(instruction.Sources[0], instruction.Pc);
        return bound is { } value && value <= uint.MaxValue ? (uint)value : null;
    }

    private sealed class Analyzer(ShaderResourcePlan plan)
    {
        private readonly HashSet<(Gen5Operand Operand, uint Address)> _active = [];
        private readonly Dictionary<(Gen5Operand Operand, uint Address), ulong?> _cache = [];
        private int _requests;

        public ulong? Read(Gen5Operand operand, uint before)
        {
            var key = (operand, before);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (++_requests > 512 || !_active.Add(key)) return null;
            try { return _cache[key] = ReadCore(operand, before); }
            finally { _active.Remove(key); }
        }

        private ulong? ReadCore(Gen5Operand operand, uint before)
        {
            if (operand.Kind == Gen5OperandKind.LiteralConstant) return operand.Value;
            if (operand.Kind == Gen5OperandKind.EncodedConstant)
                return Gen5InlineConstants.TryDecode(operand.Value, out var constant) ? constant : null;
            if (operand.Kind is not (Gen5OperandKind.ScalarRegister or Gen5OperandKind.VectorRegister)) return null;
            var flow = plan.Graph.ControlFlow;
            var initial = Enumerable.Range(0, flow.Blocks.Count).FirstOrDefault(
                index => before >= flow.Blocks[index].StartPc && before < flow.Blocks[index].EndPc, -1);
            if (initial < 0) return null;
            var pending = new Queue<(int Block, uint Before)>();
            var visited = new HashSet<(int Block, uint Before)>();
            ulong result = 0;
            var any = false;
            pending.Enqueue((initial, before));
            while (pending.TryDequeue(out var position))
            {
                if (!visited.Add(position)) continue;
                var block = flow.Blocks[position.Block];
                var found = false;
                foreach (var instruction in plan.Graph.Program.Instructions.Reverse())
                {
                    if (instruction.Pc < block.StartPc || instruction.Pc >= position.Before) continue;
                    if (instruction.Opcode.Contains("Movreld", StringComparison.Ordinal) ||
                        instruction.Opcode.Contains("Movrelsd", StringComparison.Ordinal) ||
                        instruction.Opcode.Contains("Swaprel", StringComparison.Ordinal) ||
                        instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal)) return null;
                    if (IndirectSelectorValues.WritesRegister(instruction, operand))
                    {
                        if (Define(instruction, operand) is not { } definition) return null;
                        result = any ? Math.Max(result, definition) : definition;
                        any = true;
                        found = true;
                        break;
                    }
                    if (operand.Kind == Gen5OperandKind.VectorRegister && IndirectSelectorValues.MayExpandExecution(instruction)) return null;
                }
                if (found) continue;
                // Entry state (user data or an undefined register) is not bounded by the shader.
                if (position.Block == 0 || flow.Predecessors[position.Block].Count == 0) return null;
                foreach (var predecessor in flow.Predecessors[position.Block])
                    pending.Enqueue((predecessor, flow.Blocks[predecessor].EndPc));
            }
            return any ? result : null;
        }

        private ulong? Define(Gen5ShaderInstruction instruction, Gen5Operand destination)
        {
            if (!instruction.Destinations.Contains(destination)) return null;
            if (instruction.Control is Gen5Vop3Control { AbsoluteMask: not 0 } or Gen5Vop3Control { NegateMask: not 0 } or
                Gen5Vop3Control { Clamp: true } or Gen5Vop3Control { OutputModifier: not 0 } or Gen5Vop3Control { OperandSelect: not 0 } or
                Gen5SdwaControl or Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl) return null;
            var sources = instruction.Sources;
            ulong? Source(int index) => index < sources.Count ? Read(sources[index], instruction.Pc) : null;
            bool Constant(int index, out uint value)
            {
                value = 0;
                if (index >= sources.Count) return false;
                if (sources[index].Kind == Gen5OperandKind.LiteralConstant) { value = sources[index].Value; return true; }
                return sources[index].Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(sources[index].Value, out value);
            }
            // A bit field of the given width: at most the mask, and at most the shifted source.
            static ulong Mask(uint width) => width >= 32 ? uint.MaxValue : (1ul << (int)width) - 1;
            switch (instruction.Opcode)
            {
                case "SMovB32" or "VMovB32":
                    return Source(0);
                case "VMinU32" or "SMinU32":
                {
                    // min(a, b) <= either operand, so one proven bound suffices.
                    var (a, b) = (Source(0), Source(1));
                    return a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
                }
                case "VMin3U32":
                {
                    var bounds = new[] { Source(0), Source(1), Source(2) }.Where(bound => bound is not null).Select(bound => bound!.Value).ToArray();
                    return bounds.Length == 0 ? null : bounds.Min();
                }
                case "VMinI32" or "SMinI32":
                {
                    // A signed minimum is an unsigned bound only when no operand can be negative.
                    var (a, b) = (Source(0), Source(1));
                    return a is <= 0x7FFFFFFF && b is <= 0x7FFFFFFF ? Math.Min(a.Value, b.Value) : null;
                }
                case "VMed3U32":
                {
                    // The median is at most the second largest of the operand bounds, an unbounded operand counting as the maximum.
                    var bounds = new[] { Source(0), Source(1), Source(2) }.Select(bound => bound ?? uint.MaxValue).ToArray();
                    Array.Sort(bounds);
                    return bounds[1] == uint.MaxValue ? null : bounds[1];
                }
                case "VMed3I32":
                {
                    var bounds = new[] { Source(0), Source(1), Source(2) };
                    if (bounds.Any(bound => bound is null or > 0x7FFFFFFF)) return null;
                    var sorted = bounds.Select(bound => bound!.Value).Order().ToArray();
                    return sorted[1];
                }
                case "VAndB32" or "SAndB32":
                {
                    var (a, b) = (Source(0), Source(1));
                    return a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
                }
                case "VCndmaskB32" or "SCselectB32":
                {
                    var (a, b) = (Source(0), Source(1));
                    return a is null || b is null ? null : Math.Max(a.Value, b.Value);
                }
                case "VLshrrevB32" when Constant(0, out var shift):
                    return Source(1) is { } reversed ? reversed >> (int)(shift & 31) : null;
                case "SLshrB32" when Constant(1, out var shift):
                    return Source(0) is { } scalar ? scalar >> (int)(shift & 31) : null;
                case "VBfeU32" when Constant(2, out var width) && Constant(1, out var offset):
                {
                    var mask = Mask(width);
                    return Source(0) is { } field ? Math.Min(mask, field >> (int)(offset & 31)) : mask;
                }
                case "SBfeU32" when Constant(1, out var packed):
                {
                    var mask = Mask((packed >> 16) & 0x7F);
                    return Source(0) is { } field ? Math.Min(mask, field >> (int)(packed & 31)) : mask;
                }
                case "SLshlB32" when Constant(1, out var shift):
                    return Source(0) is { } left && left << (int)(shift & 31) <= uint.MaxValue && (left << (int)(shift & 31)) >> (int)(shift & 31) == left ?
                        left << (int)(shift & 31) : null;
                case "VLshlrevB32" when Constant(0, out var shift):
                    return Source(1) is { } right && right << (int)(shift & 31) <= uint.MaxValue && (right << (int)(shift & 31)) >> (int)(shift & 31) == right ?
                        right << (int)(shift & 31) : null;
                case "SAddU32" or "VAddU32" or "SAddI32" or "VAddI32":
                {
                    var (a, b) = (Source(0), Source(1));
                    return a is not null && b is not null && a.Value + b.Value <= uint.MaxValue ? a.Value + b.Value : null;
                }
                case "VMulLoU32" or "SMulI32":
                {
                    var (a, b) = (Source(0), Source(1));
                    return a is not null && b is not null && (a.Value == 0 || b.Value <= uint.MaxValue / a.Value) ? a.Value * b.Value : null;
                }
                default:
                    return null;
            }
        }
    }
}
