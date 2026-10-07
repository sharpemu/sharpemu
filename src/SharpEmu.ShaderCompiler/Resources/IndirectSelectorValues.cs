// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

// A finite overestimate of a selector. Unsupported paths retain the full-domain scan.
public sealed class IndirectSelectorValues
{
    internal static bool IsLoopCounterAlias(ScalarValue value, ScalarValue counter)
    {
        if (ReferenceEquals(value, counter)) return true;
        if (value.Kind != ScalarValueKind.Phi) return false;
        var pending = new Stack<ScalarValue>(); pending.Push(value);
        var seen = new HashSet<ScalarValue>(); var found = false;
        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, counter)) { found = true; continue; }
            if (!seen.Add(current)) continue;
            if (current.Kind != ScalarValueKind.Phi) return false;
            foreach (var operand in current.Operands) pending.Push(operand);
        }
        return found;
    }

    // A sampled origin is conditional on the runtime source being point-sampled
    // RGBA8_UINT. The host reader must establish that condition before use.
    internal readonly record struct PackedByteOrigin(int MemoryIndex, uint Channel, uint Constant)
    {
        internal bool IsConstant => MemoryIndex < 0;
        internal ScalarValue? InitializedMask { get; init; }
        internal uint[] InitializationPcs { get; init; } = [];
        public bool Equals(PackedByteOrigin other) => MemoryIndex == other.MemoryIndex && Channel == other.Channel &&
            Constant == other.Constant && ReferenceEquals(InitializedMask, other.InitializedMask) &&
            InitializationPcs.AsSpan().SequenceEqual(other.InitializationPcs);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MemoryIndex); hash.Add(Channel); hash.Add(Constant); hash.Add(InitializedMask);
            foreach (var pc in InitializationPcs ?? []) hash.Add(pc);
            return hash.ToHashCode();
        }
    }

    internal sealed record PackedTextureDomain(PackedByteOrigin[] Origins)
    {
        internal static bool TryCreate(ShaderResourcePlan plan, ScalarValue selector, out PackedTextureDomain domain)
        {
            domain = null!;
            var reads = new List<uint>();
            bool Visit(ScalarValue value)
            {
                if (value.Kind == ScalarValueKind.FirstLane) { reads.Add((uint)value.Payload); return true; }
                return value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.UMin32 &&
                    value.Operands.Length == 2 && value.Operands.All(Visit);
            }
            if (!Visit(selector) || reads.Count == 0 || reads.Count > 2) return false;
            // Unknown shader writes cannot be assumed disjoint from sampled data.
            if (Enumerable.Range(0, plan.Memory.Count).Any(index => plan.Memory[index].Kind != MemoryResourceKind.LocalDataShare &&
                plan.Memory[index].Access is MemoryAccess.Write or MemoryAccess.Atomic)) return false;
            PackedByteOrigin[]? common = null;
            foreach (var read in reads)
            {
                PackedByteOrigin[]? found = null;
                foreach (var edge in plan.Graph.Program.Instructions)
                {
                    if (!Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var start) || start >= read || edge.Pc <= read) continue;
                    var end = edge.Pc + (uint)edge.Words.Count * 4;
                    if (!TryGetPackedReductionOrigins(plan, read, start, end, out var origins)) continue;
                    if (found is not null && !found.ToHashSet().SetEquals(origins)) return false;
                    found = origins;
                }
                if (found is null || common is not null && !common.ToHashSet().SetEquals(found))
                {
                    return false;
                }
                common = found;
            }
            if (common is null) return false;
            foreach (var origin in common.Where(origin => !origin.IsConstant))
            {
                if (origin.MemoryIndex >= plan.Graph.Accesses.Length ||
                    plan.Graph.Accesses[origin.MemoryIndex] is not { Handle: { } image, SamplerHandle: { } sampler } ||
                    image.Operands.Length != 8 || sampler.Operands.Length != 4 ||
                    image.Operands.Concat(sampler.Operands).Any(word => !plan.ValidateRuntimeValue(word)))
                {
                    return false;
                }
            }
            domain = new(common);
            return true;
        }

        internal bool TryFilterCandidates(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
            IReadOnlyList<DirectImageCandidate> candidates, out IReadOnlyList<DirectImageCandidate> selected)
        {
            selected = candidates;
            if (candidates.Any(candidate => !candidate.SelectorValue.HasValue) ||
                !TryEvaluate(plan, inputs, out var values)) return false;
            var allowed = values.ToHashSet();
            selected = candidates.Where(candidate => allowed.Contains(candidate.SelectorValue!.Value)).ToArray();
            return true;
        }

        internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] values)
        {
            values = [];
            if (inputs.OtherStageMayWriteMemory || inputs.ReadCleanMemory is null || inputs.ReadPointSampledByteDomain is null) return false;
            if (plan.Memory.Entries.Any(memory => memory.Kind is not (MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) &&
                memory.Access is MemoryAccess.Write or MemoryAccess.Atomic)) return false;
            var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(inputs.ReadCleanMemory));
            var found = Origins.Where(origin => origin.IsConstant).Select(origin => origin.Constant).ToHashSet();
            var sampledDomains = new Dictionary<string, uint[]>();
            foreach (var group in Origins.Where(origin => !origin.IsConstant).GroupBy(origin => origin.MemoryIndex))
            {
                var access = plan.Graph.Accesses[group.Key]!;
                var image = new uint[8]; var sampler = new uint[4];
                for (var index = 0; index < image.Length; index++)
                    if (!evaluator.Evaluate(access.Handle!.Operands[index], out image[index])) return false;
                for (var index = 0; index < sampler.Length; index++)
                    if (!evaluator.Evaluate(access.SamplerHandle!.Operands[index], out sampler[index])) return false;
                var channels = group.Aggregate(0u, (mask, origin) => mask | (1u << (int)origin.Channel));
                var gathered = plan.Memory[group.Key].Opcode == "ImageGather4Lz";
                // Identical descriptors and channel masks share one scan within
                // this evaluation only; ownership is checked again on later draws.
                var key = string.Join(",", image.Concat(sampler).Append(channels).Append(gathered ? 1u : 0u));
                if (!sampledDomains.TryGetValue(key, out var sampled))
                {
                    if (!inputs.ReadPointSampledByteDomain(image, sampler, channels, gathered, inputs.ReadCleanMemory, out sampled)) return false;
                    sampledDomains.Add(key, sampled);
                }
                found.UnionWith(sampled);
            }
            values = found.Order().ToArray();
            return true;
        }
    }

    internal static bool TryGetPackedReductionOrigins(ShaderResourcePlan plan, uint readPc, uint loopStart, uint loopEnd,
        out PackedByteOrigin[] origins)
    {
        origins = [];
        if (!TryGetInitializedReductionInput(plan, readPc, out var input, out var before)) return false;
        var registers = new HashSet<uint>();
        var requests = 0;
        bool ReadMinimum(Gen5Operand vector, uint pc)
        {
            if (++requests > 32 || vector.Kind != Gen5OperandKind.VectorRegister) return false;
            foreach (var instruction in plan.Graph.Program.Instructions.Reverse())
            {
                if (instruction.Pc >= pc) continue;
                if (instruction.Pc < loopStart || Builder.MayExpandExecution(instruction) ||
                    Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _)) return false;
                if (!Builder.WritesRegister(instruction, vector)) continue;
                var sources = instruction.Sources;
                if (instruction.Opcode == "VAndB32" && instruction.Control is null && sources.Count == 2 &&
                    sources[0].Kind == Gen5OperandKind.LiteralConstant && sources[0].Value == 255 &&
                    sources[1].Kind == Gen5OperandKind.VectorRegister)
                {
                    registers.Add(sources[1].Value); return true;
                }
                if (instruction.Opcode == "VMinU32" && sources.Count == 2 &&
                    instruction.Control is Gen5SdwaControl { DestinationSelect: 6, Source0Select: 0, Source1Select: 0,
                        Source0SignExtend: false, Source1SignExtend: false, AbsoluteMask: 0, NegateMask: 0,
                        OutputModifier: 0, Clamp: false, ScalarDestination: null } &&
                    sources.All(source => source.Kind == Gen5OperandKind.VectorRegister))
                {
                    foreach (var source in sources) registers.Add(source.Value);
                    return true;
                }
                if (instruction.Opcode == "VMin3U32" && sources.Count == 3 &&
                    instruction.Control is Gen5Vop3Control { AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false, OperandSelect: 0 })
                    return sources.All(source => ReadMinimum(source, instruction.Pc));
                return false;
            }
            return false;
        }
        if (!ReadMinimum(input, before) || !TryGetPackedLoopByteOrigins(plan, registers.ToArray(), loopStart, loopEnd, out origins)) return false;
        var initialize = plan.Graph.Program.Instructions.First(instruction => instruction.Pc == before + 4);
        if (!plan.Graph.LaneSelectionMasks.TryGetValue(initialize.Pc, out var selectedMask)) return false;
        var capture = plan.Graph.Program.Instructions.Last(instruction => instruction.Pc < before &&
            Builder.WritesSavedMask(instruction, initialize.Sources[2]));
        var laneProofs = new Dictionary<uint, IReadOnlySet<uint>>();
        foreach (var origin in origins)
            if (origin.InitializedMask is { } initializedMask && !MaskSubset(selectedMask, initializedMask))
            {
                if (origin.InitializationPcs.Length == 0) return false;
                foreach (var pc in origin.InitializationPcs)
                {
                    if (!laneProofs.TryGetValue(pc, out var lanes))
                        laneProofs.Add(pc, lanes = Gen5ExecFullAnalysis.AnalyzeInitializedLanes(plan.Graph.Program, pc, true));
                    if (!lanes.Contains(capture.Pc)) return false;
                }
            }
        origins = origins.Append(new PackedByteOrigin(-1, 0, uint.MaxValue)).Distinct().ToArray();
        return true;
    }

    private static bool MaskSubset(ScalarValue selected, ScalarValue initialized)
    {
        if (initialized.Kind == ScalarValueKind.Operation && initialized.Operation == ScalarOperation.LogicalAnd)
            return initialized.Operands.All(operand => MaskSubset(selected, operand));
        var active = new HashSet<ScalarValue>();
        var requests = 0;
        (bool Proven, bool Anchored) Visit(ScalarValue value)
        {
            if (++requests > 4096) return (false, false);
            if (ReferenceEquals(value, initialized) || value.IsConstant && value.ConstantU32 == 0 ||
                initialized.IsConstant && initialized.ConstantU32 != 0) return (true, true);
            if (!active.Add(value)) return (true, false);
            try
            {
                if (value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.LogicalAnd)
                {
                    foreach (var operand in value.Operands)
                    {
                        var result = Visit(operand);
                        if (result.Proven && result.Anchored) return result;
                    }
                    return (false, false);
                }
                if (value.Kind == ScalarValueKind.Phi || value.Kind == ScalarValueKind.Select ||
                    value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.LogicalOr)
                {
                    var operands = value.Kind == ScalarValueKind.Select ? value.Operands.Skip(1) : value.Operands;
                    var anchored = false;
                    var any = false;
                    foreach (var operand in operands)
                    {
                        any = true;
                        var result = Visit(operand);
                        if (!result.Proven) return (false, false);
                        anchored |= result.Anchored;
                    }
                    return (any, anchored);
                }
                return (false, false);
            }
            finally { active.Remove(value); }
        }
        var proof = Visit(selected);
        return proof.Proven && proof.Anchored;
    }

    // Establish a full-wave initialization followed only by value-preserving
    // lane moves and unsigned minima. The returned input still needs its own
    // byte-domain proof; the neutral sentinel and zero must also be included.
    internal static bool TryGetInitializedReductionInput(ShaderResourcePlan plan, uint readPc,
        out Gen5Operand input, out uint before)
    {
        input = default; before = 0;
        var instructions = plan.Graph.Program.Instructions;
        var readIndex = instructions.ToList().FindIndex(instruction => instruction.Pc == readPc);
        if (readIndex < 2) return false;
        var read = instructions[readIndex];
        if (read.Opcode != "VReadlaneB32" || read.Sources.Count < 2 ||
            !Constant(read.Sources[1], out _) || plan.Graph.WaveSize is not (32 or 64)) return false;
        var restoreIndex = readIndex - 1;
        while (restoreIndex > 0 && instructions[restoreIndex].Opcode == "VReadlaneB32") restoreIndex--;
        var reductionEnd = restoreIndex;
        if (instructions[restoreIndex].Opcode != "SMovB64")
        {
            reductionEnd = readIndex;
            while (reductionEnd > 0 && instructions[reductionEnd - 1].Opcode == "VReadlaneB32") reductionEnd--;
            restoreIndex = readIndex + 1;
            while (restoreIndex < instructions.Count && instructions[restoreIndex].Opcode == "VReadlaneB32") restoreIndex++;
            if (restoreIndex >= instructions.Count) return false;
        }
        var restore = instructions[restoreIndex];
        if (restore.Opcode != "SMovB64" || restore.Sources.Count != 1 ||
            !restore.Destinations.Contains(Gen5Operand.Scalar(126))) return false;
        var saved = restore.Sources[0];
        var expandIndex = reductionEnd - 1;
        while (expandIndex >= 0 && instructions[expandIndex].Opcode != "SOrn2SaveexecB64") expandIndex--;
        if (expandIndex < 0 || expandIndex + 1 >= reductionEnd) return false;
        var expand = instructions[expandIndex];
        if (expand.Sources.Count != 1 || expand.Sources[0] != Gen5Operand.Scalar(126) ||
            !expand.Destinations.Contains(saved)) return false;
        var initialize = instructions[expandIndex + 1];
        if (initialize.Opcode != "VCndmaskB32" || initialize.Sources.Count != 3 || initialize.Destinations.Count != 1 ||
            !Constant(initialize.Sources[0], out var neutral) || neutral is not (255 or uint.MaxValue) ||
            initialize.Control is not Gen5Vop3Control { AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false, OperandSelect: 0 } ||
            initialize.Sources[1].Kind != Gen5OperandKind.VectorRegister) return false;
        var mask = initialize.Sources[2];
        var maskIndex = expandIndex - 1;
        while (maskIndex >= 0 && !Builder.WritesSavedMask(instructions[maskIndex], mask)) maskIndex--;
        if (maskIndex < 0) return false;
        var capture = instructions[maskIndex];
        if (capture.Opcode != "SAndB64" || capture.Sources.Count != 2 ||
            !capture.Sources.Contains(Gen5Operand.Scalar(126))) return false;
        for (var index = maskIndex + 1; index < expandIndex; index++)
            if (Builder.MayExpandExecution(instructions[index]) ||
                Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[index], out _)) return false;
        var known = new HashSet<Gen5Operand> { initialize.Destinations[0] };
        for (var index = expandIndex + 2; index < reductionEnd; index++)
        {
            var instruction = instructions[index];
            if (instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister) return false;
            if (instruction.Opcode == "VMinU32" && instruction.Sources.Count == 2 && instruction.Sources.All(known.Contains))
            {
                if (instruction.Control is not null && instruction.Control is not Gen5DppControl
                    { AbsoluteMask: 0, NegateMask: 0, BankMask: 15, RowMask: 15, FetchInactive: false }) return false;
                if (instruction.Control is Gen5DppControl && !known.Contains(instruction.Destinations[0])) return false;
                if (instruction.Control is Gen5DppControl dpp && dpp.Control is not (273 or 274 or 276 or 280)) return false;
            }
            else if (instruction.Opcode == "VPermlanex16B32" && instruction.Sources.Count == 3 && known.Contains(instruction.Sources[0]) &&
                Constant(instruction.Sources[1], out _) && Constant(instruction.Sources[2], out _) &&
                instruction.Control is Gen5Vop3Control { AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false, OperandSelect: 2 }) { }
            else return false;
            known.Add(instruction.Destinations[0]);
        }
        if (!known.Contains(read.Sources[0])) return false;
        if (instructions.Any(instruction => Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
            target > capture.Pc && target <= read.Pc)) return false;
        input = initialize.Sources[1]; before = expand.Pc;
        return true;

        static bool Constant(Gen5Operand operand, out uint value)
        {
            value = operand.Value;
            return operand.Kind == Gen5OperandKind.LiteralConstant ||
                operand.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(operand.Value, out value);
        }
    }

    // This proves the packed register domain is closed under the loop's writes.
    // It does not prove which lanes a later reduction reads.
    internal static bool TryGetPackedLoopByteOrigins(ShaderResourcePlan plan, IReadOnlyList<uint> registers,
        uint loopStart, uint loopEnd, out PackedByteOrigin[] origins)
    {
        origins = [];
        if (registers.Count == 0 || registers.Distinct().Count() != registers.Count || loopStart >= loopEnd) return false;
        var found = new HashSet<PackedByteOrigin>();
        foreach (var register in registers)
            for (var component = 0; component < 4; component++)
            {
                if (!TryReadPackedByteOrigin(plan, Gen5Operand.Vector(register), component, loopStart, out var origin)) return false;
                found.Add(origin);
            }
        var instructions = plan.Graph.Program.Instructions;
        var backedge = false;
        foreach (var instruction in instructions)
        {
            var inside = instruction.Pc >= loopStart && instruction.Pc < loopEnd;
            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target))
            {
                if (!inside && target >= loopStart && target < loopEnd) return false;
                if (inside && target == loopStart) backedge = true;
                if (inside && target < loopStart) return false;
            }
            if (!inside) continue;
            // Reject vector memory tuple writes until their reaching definitions
            // are tracked per component by this proof.
            if (instruction.Control is Gen5BufferMemoryControl or Gen5GlobalMemoryControl) return false;
            if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal)) return false;
            foreach (var register in registers)
            {
                var vector = Gen5Operand.Vector(register);
                if (instruction.Control is Gen5ImageControl image && register >= image.VectorData && register - image.VectorData < (instruction.Opcode.StartsWith("ImageGather", StringComparison.Ordinal) ? 4 : System.Numerics.BitOperations.PopCount(image.Dmask)))
                    return false;
                if (!Builder.WritesRegister(instruction, vector)) continue;
                if (instruction.Opcode != "VLshrrevB32" || instruction.Sources.Count != 2 ||
                    instruction.Sources[1] != vector || instruction.Control is not null ||
                    !Decode(instruction.Sources[0], out var amount) || amount != 8) return false;
                found.Add(new(-1, 0, 0));
            }
        }
        if (!backedge) return false;
        origins = found.ToArray();
        return true;

        static bool Decode(Gen5Operand operand, out uint value)
        {
            value = operand.Value;
            return operand.Kind == Gen5OperandKind.LiteralConstant ||
                operand.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(operand.Value, out value);
        }
    }

    internal static bool TryReadPackedByteOrigin(ShaderResourcePlan plan, Gen5Operand operand,
        int byteIndex, uint before, out PackedByteOrigin origin)
    {
        origin = default;
        if (byteIndex is < 0 or > 3) return false;
        var active = new HashSet<(Gen5Operand, int, uint)>();
        var requests = 0;
        static bool ShiftAmount(Gen5Operand value, out uint amount)
        {
            amount = value.Value;
            return (value.Kind == Gen5OperandKind.LiteralConstant ||
                value.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(value.Value, out amount)) &&
                amount <= 24 && amount % 8 == 0;
        }
        bool Read(Gen5Operand value, int component, uint pc, out PackedByteOrigin result)
        {
            result = default;
            uint? writePc = null;
            if (++requests > 256 || !active.Add((value, component, pc))) return false;
            try
            {
                uint constant;
                if (value.Kind == Gen5OperandKind.LiteralConstant) constant = value.Value;
                else if (value.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(value.Value, out constant)) { }
                else
                {
                    if (value.Kind != Gen5OperandKind.VectorRegister) return false;
                    foreach (var instruction in plan.Graph.Program.Instructions.Reverse())
                    {
                        if (instruction.Pc >= pc) continue;
                        // This local proof does not infer reaching definitions across
                        // branches, relative register addressing or mask expansion.
                        if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                            instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal) ||
                            Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _) ||
                            Builder.MayExpandExecution(instruction)) return false;
                        if (instruction.Control is Gen5ImageControl image &&
                            value.Value >= image.VectorData && value.Value - image.VectorData < (instruction.Opcode.StartsWith("ImageGather", StringComparison.Ordinal) ? 4 : System.Numerics.BitOperations.PopCount(image.Dmask)))
                        {
                            writePc = instruction.Pc;
                            if (instruction.Opcode is not ("ImageSampleA" or "ImageSampleAO") || image.Dmask != 15 || image.D16 ||
                                !plan.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex)) return false;
                            result = component == 0 ? new(memoryIndex, value.Value - image.VectorData, 0) : new(-1, 0, 0);
                            return true;
                        }
                        if (instruction.Control is Gen5BufferMemoryControl or Gen5GlobalMemoryControl) return false;
                        if (!Builder.WritesRegister(instruction, value)) continue;
                        writePc = instruction.Pc;
                        if (instruction.Control is Gen5SdwaControl or Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl ||
                            instruction.Control is Gen5Vop3Control { AbsoluteMask: not 0 } or
                                Gen5Vop3Control { NegateMask: not 0 } or Gen5Vop3Control { OperandSelect: not 0 } or
                                Gen5Vop3Control { Clamp: true } or Gen5Vop3Control { OutputModifier: not 0 }) return false;
                        var sources = instruction.Sources;
                        if (instruction.Opcode == "VMovB32" && sources.Count == 1)
                            return Read(sources[0], component, instruction.Pc, out result);
                        if (instruction.Opcode is "VOrB32" or "VOr3U32")
                        {
                            result = new(-1, 0, 0);
                            foreach (var source in sources)
                            {
                                if (!Read(source, component, instruction.Pc, out var next)) return false;
                                if (next.IsConstant && next.Constant == 0) continue;
                                if (!result.IsConstant || result.Constant != 0) return false;
                                result = next;
                            }
                            return true;
                        }
                        if (instruction.Opcode == "VLshlOrU32" && sources.Count == 3 && ShiftAmount(sources[1], out var packedShift))
                        {
                            var inputByte = component - (int)(packedShift / 8);
                            var shifted = new PackedByteOrigin(-1, 0, 0);
                            if (inputByte >= 0 && !Read(sources[0], inputByte, instruction.Pc, out shifted)) return false;
                            if (!Read(sources[2], component, instruction.Pc, out var other)) return false;
                            if (shifted.IsConstant && shifted.Constant == 0) { result = other; return true; }
                            if (other.IsConstant && other.Constant == 0) { result = shifted; return true; }
                            return false;
                        }
                        if (instruction.Opcode is "VLshlrevB32" or "VLshrrevB32" && sources.Count == 2 &&
                            ShiftAmount(sources[0], out var shift))
                        {
                            var inputByte = component + (instruction.Opcode == "VLshrrevB32" ? 1 : -1) * (int)(shift / 8);
                            if (inputByte is < 0 or > 3) { result = new(-1, 0, 0); return true; }
                            return Read(sources[1], inputByte, instruction.Pc, out result);
                        }
                        return false;
                    }
                    return false;
                }
                result = new(-1, 0, (constant >> (component * 8)) & 255);
                return true;
            }
            finally
            {
                if (writePc is { } definition && plan.Graph.InstructionExecutionMasks.TryGetValue(definition, out var mask))
                    result = result with { InitializedMask = result.InitializedMask is { } prior
                        ? plan.Graph.Operation(ScalarOperation.LogicalAnd, ScalarValueType.Bool, prior, mask) : mask,
                        InitializationPcs = [.. result.InitializationPcs ?? [], definition] };
                active.Remove((value, component, pc));
            }
        }
        return Read(operand, byteIndex, before, out origin);
    }


    private const int MaximumValues = 4096;
    private const int MaximumCombinations = 65536;
    internal static bool WritesMask(Gen5ShaderInstruction instruction, Gen5Operand mask) => Builder.WritesSavedMask(instruction, mask);
    private sealed record Expression(uint[]? Values = null, ScalarValue? RuntimeValue = null,
        ScalarOperation Operation = ScalarOperation.None, Expression[]? Inputs = null, int? GatherMemoryIndex = null);
    private readonly Expression _root;
    private readonly WaveMaskSelectorBounds? _waveBounds;

    internal sealed record GatheredByteSelectorProof(int[] ImageMemoryIndices)
    {
        internal uint[] Constants { get; init; } = [];
        internal PackedTextureDomain TextureDomain(ShaderResourcePlan plan) => new(
            ImageMemoryIndices.Select(index => new PackedByteOrigin(index,
                (uint)System.Numerics.BitOperations.TrailingZeroCount(
                    ((Gen5ImageControl)plan.Graph.Program.Instructions.First(instruction => instruction.Pc == plan.Memory[index].Pc).Control!).Dmask), 0))
            .Concat(Constants.Select(value => new PackedByteOrigin(-1, 0, value))).ToArray());
        internal static bool TryCreate(ShaderResourcePlan plan, ScalarValue selector,
            out GatheredByteSelectorProof proof)
        {
            proof = null!;
            if (plan.Stage != ShaderStage.Pixel || selector.Kind != ScalarValueKind.FirstLane) return false;
            var instructions = plan.Graph.Program.Instructions;
            var readIndex = instructions.ToList().FindIndex(instruction => instruction.Pc == selector.Payload);
            if (readIndex < 0 || !ResourceTracker.TryGetStableLaneReadStart(instructions, readIndex, out var start)) return false;
            var read = instructions[readIndex];
            var captureIndex = instructions.ToList().FindIndex(instruction => instruction.Pc == start);
            if (captureIndex < 1 || instructions[captureIndex] is not
                { Opcode: "SMovB64", Sources.Count: 1, Destinations.Count: 1 } capture ||
                capture.Sources[0] != Gen5Operand.Scalar(126)) return false;
            var remaining = capture.Destinations[0];
            var guardIndex = captureIndex - 1;
            while (guardIndex >= 0 && !Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[guardIndex], out _))
            {
                if (Builder.MayExpandExecution(instructions[guardIndex])) return false;
                guardIndex--;
            }
            if (guardIndex < 0 || instructions[guardIndex].Opcode != "SCbranchExecz" ||
                !Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[guardIndex], out var emptyTarget) ||
                emptyTarget <= read.Pc) return false;

            var flow = plan.Graph.ControlFlow;
            int Block(uint pc) => Enumerable.Range(0, flow.Blocks.Count).FirstOrDefault(index =>
                pc >= flow.Blocks[index].StartPc && pc < flow.Blocks[index].EndPc, -1);
            var guardBlock = Block(instructions[guardIndex].Pc);
            var readBlock = Block(read.Pc);
            var emptyBlock = Block(emptyTarget);
            var admittedBlock = Block(instructions[guardIndex].Pc + (uint)instructions[guardIndex].Words.Count * sizeof(uint));
            if (guardBlock < 0 || readBlock < 0 || emptyBlock < 0 || admittedBlock < 0) return false;
            bool ReachesRead(int entry, bool removeAdmitted)
            {
                var pending = new Queue<int>();
                var visited = new HashSet<int>();
                pending.Enqueue(entry);
                while (pending.TryDequeue(out var block))
                {
                    if (!visited.Add(block)) continue;
                    if (block == readBlock) return true;
                    foreach (var next in flow.Successors[block])
                        if (!removeAdmitted || block != guardBlock || next != admittedBlock) pending.Enqueue(next);
                }
                return false;
            }
            if (ReachesRead(0, true) || ReachesRead(emptyBlock, false)) return false;
            if (instructions[readIndex - 1] is not { Opcode: "SFF1I32B64", Sources.Count: 1 } scan ||
                scan.Sources[0] != remaining) return false;
            var repeatIndex = -1;
            for (var index = readIndex + 1; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (!Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) || target != scan.Pc) continue;
                if (instruction.Opcode != "SCbranchScc1") return false;
                repeatIndex = index;
                break;
            }
            if (repeatIndex < 0 || emptyTarget <= instructions[repeatIndex].Pc) return false;
            var setter = repeatIndex - 1;
            while (setter > readIndex && instructions[setter].Opcode == "SMovB64") setter--;
            if (setter <= readIndex || instructions[setter] is not { Opcode: "SAndn2B64", Sources.Count: 2 } reduction ||
                reduction.Sources[0] != remaining || !reduction.Destinations.Contains(remaining)) return false;
            for (var index = captureIndex + 1; index < repeatIndex; index++)
                if (Builder.WritesSavedMask(instructions[index], remaining) && index != setter) return false;

            var root = new Builder(plan, allowGather: true).Read(read.Sources[0], start);
            if (root is null) return false;
            var origins = new HashSet<int>();
            var constants = new HashSet<uint>();
            bool Collect(Expression expression)
            {
                if (expression.GatherMemoryIndex is { } memory)
                {
                    if ((uint)memory >= plan.Graph.Accesses.Length ||
                        plan.Graph.Accesses[memory] is not { Handle: { } image, SamplerHandle: { } sampler } ||
                        image.Operands.Length != 8 || sampler.Operands.Length != 4 ||
                        image.Operands.Concat(sampler.Operands).Any(word => !plan.ValidateRuntimeValue(word))) return false;
                    origins.Add(memory);
                    return true;
                }
                if (expression.Values is { } values)
                {
                    if (values.Any(value => value > byte.MaxValue)) return false;
                    constants.UnionWith(values);
                    return true;
                }
                return expression.RuntimeValue is null && expression.Operation == ScalarOperation.None &&
                    expression.Inputs is { } operands && operands.All(Collect);
            }
            if (!Collect(root) || origins.Count == 0) return false;
            proof = new(origins.Order().ToArray()) { Constants = constants.Order().ToArray() };
            return true;
        }

        internal bool HasByteRange(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
        {
            if (inputs.OtherStageMayWriteMemory || inputs.ReadCleanMemory is null ||
                Enumerable.Range(0, plan.Memory.Count).Any(index => plan.Memory[index].Kind != MemoryResourceKind.LocalDataShare &&
                    plan.Memory[index].Access is MemoryAccess.Write or MemoryAccess.Atomic)) return false;

            var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(inputs.ReadCleanMemory));
            foreach (var memoryIndex in ImageMemoryIndices)
            {
                if ((uint)memoryIndex >= plan.Accesses.Length ||
                    plan.Accesses[memoryIndex] is not { Handle: { } image, SamplerHandle: { } sampler } ||
                    image.Operands.Length != 8 || sampler.Operands.Length != 4) return false;
                var imageWords = new uint[8];
                var samplerWords = new uint[4];
                for (var index = 0; index < imageWords.Length; index++)
                    if (!evaluator.Evaluate(image.Operands[index], out imageWords[index])) return false;
                for (var index = 0; index < samplerWords.Length; index++)
                    if (!evaluator.Evaluate(sampler.Operands[index], out samplerWords[index])) return false;

                var format = GuestImageFormat.FormatOf(imageWords);
                if (format is not (GuestImageFormat.Format8Uint or GuestImageFormat.Format8x4Uint) ||
                    GuestImageFormat.ImageTypeOf(imageWords) != GuestImageFormat.ImageType2D ||
                    (imageWords[1] >> 8 & 0xFFF) != 0 || (imageWords[3] >> 12 & 0xFF) != 0 ||
                    (imageWords[4] & 0x1FFF) != 0 || (imageWords[4] >> 16 & 0x1FFF) != 0 ||
                    (imageWords[5] >> 4 & 0xF) != 0 || (imageWords[6] & (3u << 20)) != 0) return false;
                for (var channel = 0; channel < 4; channel++)
                    if (((imageWords[3] >> (channel * 3)) & 7) is not (0 or 1 or 4 or 5 or 6 or 7)) return false;

                // Gather returns individual texels; filter modes do not blend their values.
                if ((samplerWords[0] & (7u << 12)) != 0 ||
                    (samplerWords[0] & (1u << 15)) != 0 ||
                    (samplerWords[3] >> 30) == 3) return false;
            }
            return true;
        }
    }

    // Enumerate the actual dispatch domain, never a guessed workgroup ID. The
    // original scalar loads and their run-time image selector remain in the shader.
    internal sealed record WorkgroupDescriptor(ScalarValue Handle, ScalarValue Input, ScalarValue? Key,
        uint? FlatAttribute = null, uint FlatChannel = 0, IndexedBufferDomain? BufferDomain = null,
        ScalarValue? LoopCounter = null, ScalarValue? LoopInitial = null, ScalarValue? LoopLimit = null)
    {
        internal static bool TryCreate(ShaderResourcePlan plan, ScalarValue handle, ScalarValue? key,
            out WorkgroupDescriptor result)
        {
            result = null!;
            if (plan.Stage is not (ShaderStage.Compute or ShaderStage.Pixel) ||
                handle.Kind is not (ScalarValueKind.ImageHandle or ScalarValueKind.SamplerHandle) ||
                handle.Operands.Length != (handle.Kind == ScalarValueKind.ImageHandle ? 8 : 4)) return false;
            ScalarValue? input = null;
            var pending = new Stack<ScalarValue>(handle.Operands);
            if (key is not null) pending.Push(key);
            var visited = new HashSet<ScalarValue>();
            while (pending.TryPop(out var value))
            {
                if (!visited.Add(value)) continue;
                if (value.Kind == ScalarValueKind.WorkgroupId)
                {
                    if (value.Payload > 2 || input is not null && !ReferenceEquals(input, value)) return false;
                    input = value;
                }
                // No lane-dependent proof or branch-dependent descriptor is implied.
                else if (value.Kind == ScalarValueKind.FirstLane)
                {
                    if (plan.Stage != ShaderStage.Pixel || input is not null && !ReferenceEquals(input, value)) return false;
                    input = value;
                    continue;
                }
                foreach (var operand in value.Operands) pending.Push(operand);
            }
            if (input is null) return false;
            var replacements = new Dictionary<ScalarValue, ScalarValue> { [input] = plan.Graph.Constant(0u) };
            ScalarValue? counter = null, initial = null, limit = null;
            var loops = visited.Where(value => value.Kind == ScalarValueKind.Phi &&
                plan.Graph.ResolveInvariantPhi(value) is null).ToArray();
            if (loops.Length != 0)
            {
                if (plan.Stage != ShaderStage.Compute) return false;
                foreach (var candidate in loops)
                    if (TryGetLoop(plan, candidate, visited, out var candidateInitial, out var candidateLimit))
                    {
                        if (counter is not null) return false;
                        counter = candidate; initial = candidateInitial; limit = candidateLimit;
                    }
                if (counter is null || initial is null || limit is null ||
                    loops.Any(value => !IsLoopCounterAlias(value, counter))) return false;
                var boundsMemo = new Dictionary<ScalarValue, ScalarValue>();
                if (!plan.ValidateRuntimeValue(plan.Graph.Substitute(initial, replacements, boundsMemo)) ||
                    !plan.ValidateRuntimeValue(plan.Graph.Substitute(limit, replacements, boundsMemo))) return false;
                replacements[counter] = plan.Graph.Constant(0u);
                foreach (var alias in loops) replacements[alias] = plan.Graph.Constant(0u);
                if (handle.Kind == ScalarValueKind.SamplerHandle && key is null)
                {
                    var first = plan.Graph.ResolveInvariantPhi(handle.Operands[0]) ?? handle.Operands[0];
                    if (first.Kind != ScalarValueKind.ScalarBufferWord || first.Operands.Length != 2 ||
                        first.MemoryIndex < 0 || first.MemoryIndex >= plan.Memory.Count) return false;
                    key = first.Operands[1];
                }
            }
            var memo = new Dictionary<ScalarValue, ScalarValue>();
            foreach (var word in handle.Operands)
                if (!plan.ValidateRuntimeValue(plan.Graph.Substitute(word, replacements, memo))) return false;
            if (key is not null && (key.Type != ScalarValueType.U32 ||
                !plan.ValidateRuntimeValue(plan.Graph.Substitute(key, replacements, memo)))) return false;
            uint? attribute = null; uint channel = 0;
            if (input.Kind == ScalarValueKind.FirstLane)
            {
                var instructions = plan.Graph.Program.Instructions;
                var lane = instructions.FirstOrDefault(instruction => instruction.Pc == input.Payload);
                if (lane is not { Opcode: "VReadlaneB32", Sources.Count: >= 1 } ||
                    lane.Sources[0].Kind != Gen5OperandKind.VectorRegister) return false;
                var definition = instructions.Where(instruction => instruction.Pc < lane.Pc &&
                    Builder.WritesRegister(instruction, lane.Sources[0])).LastOrDefault();
                if (TryGetIndexedBufferDomain(plan, lane, definition, out var bufferDomain))
                {
                    result = new(handle, input, key, BufferDomain: bufferDomain);
                    return true;
                }
                // P0 is one vertex's parameter, not a barycentric difference.
                if (definition is not { Opcode: "VInterpMovF32", Control: Gen5InterpolationControl interpolation } ||
                    (definition.Words[0] & 255) != 2 || interpolation.Channel >= 4 ||
                    instructions.Any(instruction => instruction.Pc > definition.Pc && instruction.Pc < lane.Pc &&
                        Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _))) return false;
                if (lane.Sources.Count < 2 || lane.Sources[1].Kind != Gen5OperandKind.ScalarRegister) return false;
                var scan = instructions.Where(instruction => instruction.Pc < lane.Pc &&
                    Builder.WritesRegister(instruction, lane.Sources[1])).LastOrDefault();
                if (scan is not { Opcode: "SFF1I32B64", Sources.Count: 1 } ||
                    scan.Sources[0].Kind != Gen5OperandKind.ScalarRegister) return false;
                var capture = instructions.Where(instruction => instruction.Pc < definition.Pc &&
                    Builder.WritesSavedMask(instruction, scan.Sources[0])).LastOrDefault();
                if (capture is not { Opcode: "SMovB64", Sources.Count: 1 } ||
                    capture.Sources[0] != Gen5Operand.Scalar(126) ||
                    instructions.Any(instruction => instruction.Pc > capture.Pc && instruction.Pc < lane.Pc &&
                        (Builder.WritesSavedMask(instruction, scan.Sources[0]) || Builder.MayExpandExecution(instruction)))) return false;
                if (!Gen5ExecFullAnalysis.AnalyzeMatchingExecutionLanes(plan.Graph.Program, capture.Pc, true).Contains(definition.Pc) ||
                    !Gen5ExecFullAnalysis.AnalyzeInitializedLanes(plan.Graph.Program, definition.Pc, true).Contains(lane.Pc)) return false;
                var loopEnd = lane.Pc;
                foreach (var edge in instructions.Where(instruction => instruction.Pc > lane.Pc))
                    if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var start) && start > definition.Pc && start <= lane.Pc)
                        loopEnd = Math.Max(loopEnd, edge.Pc);
                if (instructions.Any(instruction => instruction.Pc > definition.Pc && instruction.Pc <= loopEnd &&
                    Builder.WritesRegister(instruction, lane.Sources[0]))) return false;
                var flow = plan.Graph.ControlFlow;
                var definitionBlock = Enumerable.Range(0, flow.Blocks.Count).First(index =>
                    definition.Pc >= flow.Blocks[index].StartPc && definition.Pc < flow.Blocks[index].EndPc);
                var readBlock = Enumerable.Range(0, flow.Blocks.Count).First(index =>
                    lane.Pc >= flow.Blocks[index].StartPc && lane.Pc < flow.Blocks[index].EndPc);
                var captureBlock = flow.BlockOf(capture.Pc);
                var capturePending = new Stack<int>(); capturePending.Push(0);
                var captureSeen = new HashSet<int>();
                while (capturePending.TryPop(out var block))
                {
                    if (block == captureBlock || !captureSeen.Add(block)) continue;
                    if (block == definitionBlock) return false;
                    foreach (var next in flow.Successors[block]) capturePending.Push(next);
                }
                var pendingBlocks = new Stack<int>(); pendingBlocks.Push(0);
                var seenBlocks = new HashSet<int>();
                while (pendingBlocks.TryPop(out var block))
                {
                    if (block == definitionBlock || !seenBlocks.Add(block)) continue;
                    if (block == readBlock) return false;
                    foreach (var next in flow.Successors[block]) pendingBlocks.Push(next);
                }
                attribute = interpolation.Attribute; channel = interpolation.Channel;
            }
            result = new(handle, input, key, attribute, channel, LoopCounter: counter, LoopInitial: initial, LoopLimit: limit);
            return true;
        }

        private static bool TryGetLoop(ShaderResourcePlan plan, ScalarValue counter,
            HashSet<ScalarValue> dependencies, out ScalarValue initial, out ScalarValue limit)
        {
            // A signed, unit-step scalar loop is finite only after its actual
            // nonnegative bounds are checked. Every dependent load must pass
            // through the loop's admission edge; a matching compare alone is insufficient.
            initial = limit = null!;
            if (counter.Type != ScalarValueType.U32 || counter.Operands.Length != 2 || counter.PhiPredecessors.Length != 2)
                return false;
            var increment = Array.FindIndex(counter.Operands, value => value.Kind == ScalarValueKind.Operation &&
                value.Operation == ScalarOperation.IAdd32 && value.Operands.Length == 2 &&
                IsLoopCounterAlias(value.Operands[0], counter) && value.Operands[1].IsConstant && value.Operands[1].Payload == 1);
            if (increment < 0) return false;
            initial = counter.Operands[1 - increment];
            var flow = plan.Graph.ControlFlow;
            if (flow.Predecessors[counter.PhiBlock].Count != 2 ||
                !flow.Successors[counter.PhiPredecessors[increment]].Contains(counter.PhiBlock)) return false;
            foreach (var (pc, condition) in plan.FlattenedBranchConditions)
            {
                if (flow.BlockOf(pc) != counter.PhiBlock || condition.Kind != ScalarValueKind.Operation ||
                    condition.Operation != ScalarOperation.LogicalNot || condition.Operands.Length != 1) continue;
                var comparison = condition.Operands[0];
                if (comparison.Kind != ScalarValueKind.Operation || comparison.Operation != ScalarOperation.SLessThan32 ||
                    comparison.Operands.Length != 2 || !ReferenceEquals(comparison.Operands[0], counter)) continue;
                var branch = plan.Graph.Program.Instructions.First(instruction => instruction.Pc == pc);
                if (branch.Opcode != "SCbranchScc0" ||
                    !Gen5IrBranchResolver.Instance.TryGetBranchTarget(branch, out var target) ||
                    !flow.BlockByStartPc.TryGetValue(target, out var rejected) ||
                    !flow.BlockByStartPc.TryGetValue(pc + (uint)branch.Words.Count * 4, out var admitted)) continue;
                var loads = dependencies.Where(value => value.Kind is ScalarValueKind.ScalarBufferWord or ScalarValueKind.ScalarAddressWord &&
                    DependsOn(value, counter)).ToArray();
                if (loads.Length == 0 || loads.Any(value => value.MemoryIndex < 0 || value.MemoryIndex >= plan.Memory.Count)) continue;
                var blocks = loads.Select(value => flow.BlockOf(plan.Memory[value.MemoryIndex].Pc)).ToHashSet();
                // A counter-dependent output after the loop observes the exit
                // value, not one of the admitted iterations. Do not substitute
                // an iteration value for that output's descriptor.
                for (var index = 0; index < plan.Memory.Count; index++)
                    if (plan.Memory[index].Access is MemoryAccess.Write or MemoryAccess.Atomic &&
                        plan.Accesses[index]?.Handle is { } output && DependsOn(output, counter))
                        blocks.Add(flow.BlockOf(plan.Memory[index].Pc));
                if (ReachesLoads(0) || ReachesLoads(rejected)) continue;
                limit = comparison.Operands[1];
                return true;

                bool ReachesLoads(int start)
                {
                    var pending = new Stack<int>(); pending.Push(start); var seen = new HashSet<int>();
                    while (pending.TryPop(out var block))
                    {
                        if (!seen.Add(block)) continue;
                        if (blocks.Contains(block)) return true;
                        foreach (var next in flow.Successors[block])
                            if (block != counter.PhiBlock || next != admitted) pending.Push(next);
                    }
                    return false;
                }
            }
            return false;

            static bool DependsOn(ScalarValue root, ScalarValue target)
            {
                var pending = new Stack<ScalarValue>(); pending.Push(root); var seen = new HashSet<ScalarValue>();
                while (pending.TryPop(out var value))
                {
                    if (ReferenceEquals(value, target)) return true;
                    if (!seen.Add(value)) continue;
                    foreach (var operand in value.Operands) pending.Push(operand);
                }
                return false;
            }
        }

        internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
            out uint[] keys, out uint[][] descriptors)
        {
            keys = []; descriptors = [];
            if (inputs.OtherStageMayWriteMemory || inputs.ReadCleanMemory is null) return false;
            uint[] inputValues;
            if (BufferDomain is { } bufferDomain)
            {
                if (plan.Memory.Entries.Any(memory => memory.Kind is not (MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) &&
                        memory.Access is MemoryAccess.Write or MemoryAccess.Atomic) ||
                    !bufferDomain.TryEvaluate(plan, inputs, out inputValues)) return false;
            }
            else if (FlatAttribute is { } attribute)
            {
                if (plan.Memory.Entries.Any(memory => memory.Kind is not (MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) && memory.Access is MemoryAccess.Write or MemoryAccess.Atomic) ||
                    inputs.ReadFlatParameterDomain is null ||
                    !inputs.ReadFlatParameterDomain(attribute, FlatChannel, inputs.ReadCleanMemory, out inputValues) ||
                    inputValues.Length == 0 || inputValues.Length > MaximumCombinations) return false;
                inputValues = inputValues.Distinct().ToArray();
            }
            else
            {
                if (inputs.ComputeState is not { } dispatch) return false;
                var count = Input.Payload switch
                {
                    0 => dispatch.DispatchGroupsX, 1 => dispatch.DispatchGroupsY, 2 => dispatch.DispatchGroupsZ, _ => 0u,
                };
                if (count == 0 || count > MaximumCombinations || dispatch.DispatchGroupsX == 0 ||
                    dispatch.DispatchGroupsY == 0 || dispatch.DispatchGroupsZ == 0) return false;
                inputValues = Enumerable.Range(0, (int)count).Select(value => (uint)value).ToArray();
            }
            var captured = new Dictionary<ulong, uint>();
            bool Read(ulong address, out uint word)
            {
                if (captured.TryGetValue(address, out word)) return true;
                if (address > (1ul << 48) - 4 || !inputs.ReadCleanMemory(address, out word)) return false;
                captured.Add(address, word);
                return true;
            }
            var clean = new ResourceRuntimeInputs { UserData = inputs.UserData, ShaderBase = inputs.ShaderBase,
                ReadMemory = Read, ReadCleanMemory = Read, ReadsClean = true, ComputeState = inputs.ComputeState };
            var candidates = new Dictionary<uint, uint[]>();
            var writes = new HashSet<(ulong Base, ulong Size)>();
            var evaluations = 0;
            // Every possible shader write must be bounded. Unknown address spaces
            // decline this proof instead of assuming they cannot touch the table.
            var writeHandles = new Dictionary<ScalarValue, ulong>();
            var imageWrites = new HashSet<ScalarValue>();
            for (var index = 0; index < plan.Memory.Count; index++)
            {
                var memory = plan.Memory[index];
                if (memory.Access is not (MemoryAccess.Write or MemoryAccess.Atomic)) continue;
                // GDS has its own backing allocation, like LDS and scratch. Its
                // offsets are not guest virtual addresses into descriptor memory.
                if (memory.Kind is MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) continue;
                if (memory.Kind == MemoryResourceKind.Image)
                {
                    if (inputs.ReadImageWriteRange is null ||
                        plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.ImageHandle, Operands.Length: 8 } image)
                        return false;
                    imageWrites.Add(image);
                    continue;
                }
                if (memory.Kind is not (MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer) ||
                    plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } output)
                    return false;
                var extra = (ulong)memory.Offset + Math.Max(16ul, (ulong)memory.DataBits * memory.DataDwords / 8);
                writeHandles[output] = Math.Max(writeHandles.GetValueOrDefault(output), extra);
            }
            var inputFrontier = new HashSet<ScalarValue>();
            if (LoopCounter is not null)
            {
                var roots = new List<ScalarValue>(Handle.Operands) { LoopInitial!, LoopLimit! };
                if (Key is not null) roots.Add(Key);
                roots.AddRange(writeHandles.Keys.SelectMany(handle => handle.Operands));
                roots.AddRange(imageWrites.SelectMany(handle => handle.Operands));
                var pending = new Stack<ScalarValue>(roots); var visited = new HashSet<ScalarValue>();
                while (pending.TryPop(out var value))
                {
                    if (!visited.Add(value)) continue;
                    if (value.Kind == ScalarValueKind.ResourceTableWord)
                    {
                        var slot = (int)value.Payload;
                        if (slot >= 0 && slot < plan.TableReads.Count) pending.Push(plan.TableReads[slot].Value);
                        continue;
                    }
                    if (ReferenceEquals(value, Input)) inputFrontier.Add(Input);
                    foreach (var operand in value.Operands)
                    {
                        if (ReferenceEquals(operand, Input)) inputFrontier.Add(value);
                        else pending.Push(operand);
                    }
                }
            }
            var repeatedInputs = new HashSet<string>();
            var loopAliases = new Dictionary<ScalarValue, bool>();
            foreach (var group in inputValues)
            {
                if (inputFrontier.Count != 0)
                {
                    // Equal values at every immediate use of the workgroup input
                    // imply equal descriptor, bound and output expressions. Include
                    // output dependencies so a later group cannot hide an alias.
                    var frontierEvaluator = new RuntimeValueEvaluator(plan, clean, Input, group);
                    var signature = new List<ulong>(inputFrontier.Count);
                    foreach (var value in inputFrontier)
                    {
                        if (!frontierEvaluator.EvaluateWide(value, out var word)) { signature.Clear(); break; }
                        signature.Add(word);
                    }
                    if (signature.Count == inputFrontier.Count && !repeatedInputs.Add(string.Join(',', signature))) continue;
                }
                uint first = 0, end = 1;
                if (LoopCounter is not null)
                {
                    var bounds = new RuntimeValueEvaluator(plan, clean, Input, group);
                    // Empty groups need a separate proof for writes outside the
                    // loop. Decline them here rather than omit those writes.
                    if (!bounds.Evaluate(LoopInitial!, out first) || !bounds.Evaluate(LoopLimit!, out end) ||
                        first > int.MaxValue || end > int.MaxValue || first >= end || end - first > MaximumValues)
                        return false;
                }
                for (var iteration = first; iteration < end; iteration++)
                {
                    if (++evaluations > MaximumCombinations) return false;
                    var evaluator = new RuntimeValueEvaluator(plan, clean, Input, group, LoopCounter, iteration, loopAliases);
                    var key = group;
                    if (Key is not null && !evaluator.Evaluate(Key, out key)) return false;
                    var words = new uint[Handle.Operands.Length];
                    for (var component = 0; component < words.Length; component++)
                        if (!evaluator.Evaluate(Handle.Operands[component], out words[component])) return false;
                    if (candidates.TryGetValue(key, out var existing))
                    {
                        if (!existing.AsSpan().SequenceEqual(words)) return false;
                    }
                    else if (candidates.Count >= MaximumValues) return false;
                    else candidates.Add(key, words);
                    foreach (var (output, extra) in writeHandles)
                    {
                        if (!PackedPointerDescriptor.EvaluateHandle(output, evaluator, out var outputWords) ||
                            !PackedPointerDescriptor.Range(outputWords, out var address, out var length) ||
                            address + length + extra > 1ul << 48) return false;
                        writes.Add((address, length + extra));
                    }
                    foreach (var output in imageWrites)
                    {
                        var outputWords = new uint[8];
                        for (var component = 0; component < outputWords.Length; component++)
                            if (!evaluator.Evaluate(output.Operands[component], out outputWords[component])) return false;
                        if (!inputs.ReadImageWriteRange!(outputWords, out var address, out var size) || size == 0 ||
                            address >= 1ul << 48 || size > (1ul << 48) - address) return false;
                        writes.Add((address, size));
                    }
                }
            }
            // Compare after evaluating *all* groups: a later group's output may
            // alias an earlier group's descriptor or one of its pointer dependencies.
            var reads = captured.Keys.Order().ToArray();
            var orderedWrites = writes.OrderBy(range => range.Base).ToArray();
            var readIndex = 0; var writeIndex = 0;
            while (readIndex < reads.Length && writeIndex < orderedWrites.Length)
            {
                var write = orderedWrites[writeIndex];
                if (write.Base + write.Size <= reads[readIndex]) writeIndex++;
                else if (reads[readIndex] + 4 <= write.Base) readIndex++;
                else return false;
            }
            keys = candidates.Keys.ToArray();
            descriptors = candidates.Values.ToArray();
            return descriptors.Length != 0;
        }
    }

    private static bool TryGetIndexedBufferDomain(ShaderResourcePlan plan, Gen5ShaderInstruction lane,
        Gen5ShaderInstruction? definition, out IndexedBufferDomain domain)
    {
        domain = null!;
        if (definition?.Control is not Gen5BufferMemoryControl
                { IndexEnabled: true, OffsetEnabled: false, Typed: false } control ||
            definition.Opcode is not ("BufferLoadDword" or "BufferLoadDwordx2" or "BufferLoadDwordx3" or "BufferLoadDwordx4") ||
            control.OffsetBytes < 0 ||
            definition.Sources.Count < 3 || !(definition.Sources[2] is { Kind: Gen5OperandKind.EncodedConstant, Value: 128 } or { Kind: Gen5OperandKind.LiteralConstant, Value: 0 }) ||
            lane.Sources.Count < 2 || lane.Sources[1].Kind != Gen5OperandKind.ScalarRegister ||
            !plan.Memory.TryGetIndex(definition.Pc, 0, out var memoryIndex) ||
            plan.Accesses[memoryIndex]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle ||
            !plan.ValidateRuntimeValue(handle)) return false;
        var component = lane.Sources[0].Value - control.VectorData;
        if (component >= control.DwordCount) return false;
        var instructions = plan.Graph.Program.Instructions;
        var scan = instructions.Where(instruction => instruction.Pc < lane.Pc &&
            Builder.WritesRegister(instruction, lane.Sources[1])).LastOrDefault();
        if (scan is not { Opcode: "SFF1I32B64", Sources.Count: 1 } ||
            scan.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister, Value: < 126 } remaining || (remaining.Value & 1) != 0) return false;
        var capture = instructions.Where(instruction => instruction.Pc < scan.Pc &&
            Builder.WritesSavedMask(instruction, remaining)).LastOrDefault();
        if (capture is not { Sources.Count: 1 } ||
            !(capture.Opcode == "SMovB64" && capture.Sources[0] == Gen5Operand.Scalar(126) || capture.Opcode == "SAndSaveexecB64") ||
            !Gen5ExecFullAnalysis.AnalyzeInitializedLanes(plan.Graph.Program, definition.Pc, plan.Graph.WaveSize == 32).Contains(capture.Pc)) return false;
        var flow = plan.Graph.ControlFlow;
        bool Dominates(uint first, uint last)
        {
            var firstBlock = flow.BlockOf(first); var lastBlock = flow.BlockOf(last);
            if (firstBlock == lastBlock) return first <= last;
            var pending = new Stack<int>(); pending.Push(0); var seen = new HashSet<int>();
            while (pending.TryPop(out var block))
            {
                if (block == firstBlock || !seen.Add(block)) continue;
                if (block == lastBlock) return false;
                foreach (var next in flow.Successors[block]) pending.Push(next);
            }
            return true;
        }
        if (!Dominates(definition.Pc, capture.Pc) || !Dominates(capture.Pc, scan.Pc) || !Dominates(scan.Pc, lane.Pc)) return false;
        var end = lane.Pc;
        foreach (var edge in instructions.Where(instruction => instruction.Pc > lane.Pc))
            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var target) && target >= capture.Pc && target <= lane.Pc)
                end = Math.Max(end, edge.Pc);
        foreach (var instruction in instructions.Where(instruction => instruction.Pc > capture.Pc && instruction.Pc <= end))
            if (Builder.WritesSavedMask(instruction, remaining) &&
                (instruction is not { Opcode: "SAndn2B64", Sources.Count: 2 } || instruction.Sources[0] != remaining)) return false;
        if (instructions.Any(instruction => instruction.Pc > definition.Pc && instruction.Pc <= end &&
            Builder.WritesRegister(instruction, lane.Sources[0]))) return false;
        domain = new(handle, checked((uint)control.OffsetBytes + component * 4), checked((uint)control.OffsetBytes + control.DwordCount * 4));
        return true;
    }

    internal sealed record IndexedBufferDomain(ScalarValue Handle, uint Offset, uint RequiredBytes)
    {
        internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] values)
        {
            values = [];
            var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(inputs.ReadCleanMemory));
            if (inputs.ReadCleanMemory is null || !PackedPointerDescriptor.EvaluateHandle(Handle, evaluator, out var words)) return false;
            var stride = (words[1] >> 16) & 0x3FFF;
            var address = ((ulong)(words[1] & 0xFFFF) << 32) | words[0];
            var size = (ulong)words[2] * stride;
            if (stride == 0 || RequiredBytes > stride || (Offset & 3) != 0 || (address & 3) != 0 ||
                (words[1] & 0x80000000) != 0 || (words[3] & 0xF0800000) != 0 ||
                words[2] > MaximumCombinations || size > 16 * 1024 * 1024 || address + size > 1ul << 48) return false;
            var found = new HashSet<uint> { 0 }; // Raw vector loads return zero for out-of-range records.
            for (uint record = 0; record < words[2]; record++)
            {
                if (!inputs.ReadCleanMemory(address + (ulong)record * stride + Offset, out var word) ||
                    found.Add(word) && found.Count > MaximumValues) return false;
            }
            values = found.Order().ToArray();
            return true;
        }
    }

    // An indexed raw buffer word supplies a finite data domain even when its
    // lane index is unknown. Runtime enumeration must validate the V# layout,
    // include the out-of-bounds value, and establish write disjointness.
    internal sealed record PackedBufferWordDomain(ScalarValue Handle, uint Component)
    {
        // This is a data-domain proof, not permission to bind a descriptor.
        // The caller must separately exclude writes to every dependency range.
        internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
            out uint[] values, out ulong baseAddress, out ulong byteLength)
        {
            values = [];
            baseAddress = byteLength = 0;
            if (inputs.ReadCleanMemory is null || Handle.Operands.Length != 4 || Component >= 2) return false;
            var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(inputs.ReadCleanMemory));
            var descriptor = new uint[4];
            for (var index = 0; index < descriptor.Length; index++)
                if (!evaluator.Evaluate(Handle.Operands[index], out descriptor[index])) return false;
            var stride = (descriptor[1] >> 16) & 0x3FFF;
            // Only the unswizzled, record-bounded raw layout is implemented.
            // Other OOB modes must not be interpreted as a record count.
            if (stride != 8 || (descriptor[1] & 0x80000000) != 0 ||
                (descriptor[3] & 0xF0800000) != 0 ||
                ((descriptor[3] >> 12) & 0x7F) == 0 || descriptor[2] > MaximumCombinations) return false;
            baseAddress = ((ulong)(descriptor[1] & 0xFFFF) << 32) | descriptor[0];
            byteLength = (ulong)descriptor[2] * stride;
            if (baseAddress + byteLength > 1ul << 48) return false;
            var found = new HashSet<uint> { 0 }; // Initial and out-of-bounds reads.
            for (uint record = 0; record < descriptor[2]; record++)
            {
                var address = baseAddress + (ulong)record * stride + Component * sizeof(uint);
                if (!inputs.ReadCleanMemory(address, out var word)) return false;
                if (found.Add(word >> 16) && found.Count > MaximumValues) return false;
            }
            values = found.Order().ToArray();
            return true;
        }
    }

    internal sealed record PackedPointerDescriptor(PackedBufferWordDomain Domain,
        ScalarValue MaterialHandle, uint Stride, uint Offset, uint PointerImmediate,
        uint DescriptorImmediate, uint Width, uint SelectorPc, bool BufferFieldsOnly = false)
    {
        internal static bool TryCreateBufferFields(ShaderResourcePlan plan, ScalarValue handle,
            out PackedPointerDescriptor result)
        {
            result = null!;
            if (handle.Kind != ScalarValueKind.BufferHandle || handle.Operands.Length != 4 ||
                !TryGetSingleBufferRead(handle.Operands[0], out var first) ||
                !TryGetOffset(first.Operands[1], out var selector, out var stride, out var offset) ||
                !TryGetPackedBufferWordDomain(plan, selector, out var domain) ||
                !plan.ValidateRuntimeValue(first.Operands[0])) return false;
            var firstMemory = plan.Memory[first.MemoryIndex];
            if ((firstMemory.Offset & 3) != 0) return false;
            for (var component = 0; component < 4; component++)
            {
                if (!TryGetSingleBufferRead(handle.Operands[component], out var read) ||
                    !plan.Graph.Equivalent(first.Operands[0], read.Operands[0]) ||
                    !plan.Graph.Equivalent(first.Operands[1], read.Operands[1])) return false;
                var memory = plan.Memory[read.MemoryIndex];
                if (memory.Kind != MemoryResourceKind.ScalarBuffer || memory.Access != MemoryAccess.Read ||
                    memory.DataBits != 32 || memory.DataDwords != 1 || memory.Pc != firstMemory.Pc ||
                    (ulong)memory.Offset != (ulong)firstMemory.Offset + (uint)component * 4) return false;
            }
            result = new(domain, first.Operands[0], stride, offset, firstMemory.Offset,
                0, 4, (uint)selector.Payload, BufferFieldsOnly: true);
            return true;
        }

        internal static bool TryCreate(ShaderResourcePlan plan, ScalarValue handle,
            out PackedPointerDescriptor result)
        {
            result = null!;
            if (handle.Kind is not (ScalarValueKind.ImageHandle or ScalarValueKind.SamplerHandle) ||
                handle.Operands.Length is not (4 or 8)) return false;
            ScalarValue? address = null;
            uint immediate = 0;
            for (var component = 0; component < handle.Operands.Length; component++)
            {
                var read = handle.Operands[component];
                if (read.Kind != ScalarValueKind.ScalarAddressWord || read.Operands.Length != 2 ||
                    !read.Operands[1].IsConstant || read.Operands[1].ConstantU32 != 0 ||
                    read.MemoryIndex < 0 || read.MemoryIndex >= plan.Memory.Count) return false;
                var memory = plan.Memory[read.MemoryIndex];
                if (memory.Access != MemoryAccess.Read || memory.Kind != MemoryResourceKind.ScalarAddress ||
                    memory.DataBits != 32 || memory.DataDwords != 1 || memory.Offset < component * 4u) return false;
                var current = memory.Offset - (uint)component * 4;
                if (component == 0) { address = read.Operands[0]; immediate = current; }
                else if (current != immediate || !plan.Graph.Equivalent(address!, read.Operands[0])) return false;
            }
            if (address is not { Kind: ScalarValueKind.AddressHandle, Operands.Length: 2 } ||
                !TryGetSingleBufferRead(address.Operands[0], out var low) ||
                !TryGetSingleBufferRead(address.Operands[1], out var high)) return false;
            var lowMemory = plan.Memory[low.MemoryIndex];
            var highMemory = plan.Memory[high.MemoryIndex];
            if ((immediate & 3) != 0 || lowMemory.Offset > uint.MaxValue - 4 ||
                lowMemory.Kind != MemoryResourceKind.ScalarBuffer || highMemory.Kind != MemoryResourceKind.ScalarBuffer ||
                lowMemory.Access != MemoryAccess.Read || highMemory.Access != MemoryAccess.Read ||
                lowMemory.DataBits != 32 || highMemory.DataBits != 32 ||
                lowMemory.DataDwords != 1 || highMemory.DataDwords != 1 ||
                lowMemory.Pc != highMemory.Pc || highMemory.Offset != lowMemory.Offset + 4 ||
                highMemory.ComponentIndex != lowMemory.ComponentIndex + 1 ||
                !plan.Graph.Equivalent(low.Operands[0], high.Operands[0]) ||
                !plan.Graph.Equivalent(low.Operands[1], high.Operands[1]) ||
                !TryGetOffset(low.Operands[1], out var selector, out var stride, out var offset) ||
                !TryGetPackedBufferWordDomain(plan, selector, out var domain) ||
                !plan.ValidateRuntimeValue(low.Operands[0])) return false;
            result = new(domain, low.Operands[0], stride, offset, lowMemory.Offset,
                immediate, (uint)handle.Operands.Length, (uint)selector.Payload);
            return true;
        }

        private static bool TryGetSingleBufferRead(ScalarValue value, out ScalarValue read)
        {
            read = null!;
            var pending = new Stack<ScalarValue>(); pending.Push(value);
            var visited = new HashSet<ScalarValue>();
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current)) continue;
                if (current.Kind == ScalarValueKind.ScalarBufferWord && current.Operands.Length == 2)
                {
                    if (read is not null && !ReferenceEquals(read, current)) return false;
                    read = current;
                }
                else if (current.Kind == ScalarValueKind.Phi && current.Operands.Length != 0)
                    foreach (var operand in current.Operands) pending.Push(operand);
                else return false;
            }
            return read is not null;
        }

        private static bool TryGetOffset(ScalarValue value, out ScalarValue selector, out uint stride, out uint offset)
        {
            selector = null!; stride = offset = 0;
            if (value.Kind == ScalarValueKind.Phi)
            {
                var alternatives = new HashSet<ScalarValue>();
                var pending = new Stack<ScalarValue>(); pending.Push(value);
                var visited = new HashSet<ScalarValue>();
                while (pending.TryPop(out var current))
                {
                    if (!visited.Add(current)) continue;
                    if (current.Kind == ScalarValueKind.Phi)
                        foreach (var operand in current.Operands) pending.Push(operand);
                    else alternatives.Add(current);
                }
                if (alternatives.Count != 1) return false;
                value = alternatives.Single();
            }
            if (value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.IAdd32)
            {
                if (value.Operands[0].IsConstant) { offset = value.Operands[0].ConstantU32; value = value.Operands[1]; }
                else if (value.Operands[1].IsConstant) { offset = value.Operands[1].ConstantU32; value = value.Operands[0]; }
                else return false;
            }
            if (value.Kind != ScalarValueKind.Operation || value.Operation != ScalarOperation.IMul32) return false;
            if (value.Operands[0].IsConstant) { stride = value.Operands[0].ConstantU32; selector = value.Operands[1]; }
            else if (value.Operands[1].IsConstant) { stride = value.Operands[1].ConstantU32; selector = value.Operands[0]; }
            else return false;
            return stride != 0 && selector.Kind == ScalarValueKind.FirstLane;
        }

        internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] words)
        {
            words = [];
            if (inputs.OtherStageMayWriteMemory || inputs.ReadCleanMemory is null) return false;
            var dependencies = new List<(ulong Base, ulong Size)>();
            var capturedWords = new Dictionary<ulong, uint>();
            bool Read(ulong address, out uint word)
            {
                if (capturedWords.TryGetValue(address, out word)) return true;
                if (!inputs.ReadCleanMemory(address, out word)) return false;
                dependencies.Add((address, 4));
                capturedWords.Add(address, word);
                return true;
            }
            var clean = new ResourceRuntimeInputs { UserData = inputs.UserData, ShaderBase = inputs.ShaderBase,
                ReadMemory = Read, ReadCleanMemory = Read, ReadsClean = true, ComputeState = inputs.ComputeState };
            if (!Domain.TryEvaluate(plan, clean, out var selectors, out var searchBase, out var searchLength)) return false;
            dependencies.Add((searchBase, searchLength));
            var evaluator = new RuntimeValueEvaluator(plan, clean);
            if (!EvaluateHandle(MaterialHandle, evaluator, out var material) ||
                ((material[1] >> 16) & 0x3FFF) != Stride ||
                !Range(material, out var materialBase, out var materialLength)) return false;
            dependencies.Add((materialBase, materialLength));
            uint[]? candidate = null;
            var writes = new List<(ulong Base, ulong Size)>();
            foreach (var selector in selectors)
            {
                var dynamicOffset = unchecked(selector * Stride + Offset);
                var current = new uint[Width];
                if (BufferFieldsOnly)
                {
                    for (uint component = 0; component < Width; component++)
                        if (!ReadMaterial(dynamicOffset, (ulong)PointerImmediate + component * 4, out current[component])) return false;
                    if (!Range(current, out _, out _)) return false;
                    // Addresses and record counts stay in the executing SGPRs.
                    // Only require agreement on addressing/conversion fields.
                    current = [0, current[1] & 0xFFFF0000, 0, current[3]];
                }
                else
                {
                    if (!ReadMaterial(dynamicOffset, PointerImmediate, out var low) ||
                        !ReadMaterial(dynamicOffset, (ulong)PointerImmediate + 4, out var high)) return false;
                    var pointer = ((ulong)high << 32) | low;
                    if (pointer == 0 || pointer > 0xFFFFFFFFFFFFul ||
                        pointer + DescriptorImmediate + Width * 4ul > 1ul << 48) return false;
                    for (uint component = 0; component < Width; component++)
                        if (!Read((pointer & ~3ul) + DescriptorImmediate + component * 4, out current[component])) return false;
                }
                if (candidate is not null && !candidate.AsSpan().SequenceEqual(current)) return false;
                candidate = current;

                for (var index = 0; index < plan.Memory.Count; index++)
                {
                    var memory = plan.Memory[index];
                    if (memory.Access is not (MemoryAccess.Write or MemoryAccess.Atomic)) continue;
                    if (memory.Kind is MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch) continue;
                    if (memory.Kind is not (MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer) ||
                        plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle) return false;
                    uint[] output;
                    if (plan.ValidateRuntimeValue(handle))
                    {
                        if (!EvaluateHandle(handle, evaluator, out output)) return false;
                    }
                    else
                    {
                        output = new uint[4];
                        for (var component = 0; component < 4; component++)
                        {
                            if (!TryGetSingleBufferRead(handle.Operands[component], out var read) ||
                                !plan.Graph.Equivalent(read.Operands[0], MaterialHandle) ||
                                !TryGetOffset(read.Operands[1], out var key, out var stride, out var offset) ||
                                key.Payload != SelectorPc || stride != Stride || offset != Offset ||
                                !ReadMaterial(dynamicOffset, plan.Memory[read.MemoryIndex].Offset, out output[component])) return false;
                        }
                    }
                    if (!Range(output, out var writeBase, out var writeLength) ||
                        writeBase + writeLength + 16 > 1ul << 48) return false;
                    // Include an entire possible formatted element past the last
                    // record, rather than assume stride equals the format width.
                    writes.Add((writeBase, writeLength + 16));
                }
            }
            var protectedRanges = dependencies.Where(range => range.Size != 0).Distinct().OrderBy(range => range.Base).ToArray();
            var writtenRanges = writes.Distinct().OrderBy(range => range.Base).ToArray();
            var protectedIndex = 0;
            var writtenIndex = 0;
            while (protectedIndex < protectedRanges.Length && writtenIndex < writtenRanges.Length)
            {
                var dependency = protectedRanges[protectedIndex];
                var write = writtenRanges[writtenIndex];
                if (write.Base + write.Size <= dependency.Base) writtenIndex++;
                else if (dependency.Base + dependency.Size <= write.Base) protectedIndex++;
                else return false;
            }
            words = candidate ?? [];
            return words.Length == Width;

            bool ReadMaterial(uint dynamicOffset, ulong immediate, out uint word)
            {
                word = 0;
                var relative = (ulong)(dynamicOffset & ~3u) + (immediate & ~3ul);
                if (relative + 4 > materialLength) return false;
                return Read((materialBase & ~3ul) + relative, out word);
            }
        }

        internal static bool EvaluateHandle(ScalarValue handle, RuntimeValueEvaluator evaluator, out uint[] words)
        {
            words = new uint[4];
            if (handle.Operands.Length != 4) return false;
            for (var index = 0; index < 4; index++)
                if (!evaluator.Evaluate(handle.Operands[index], out words[index])) return false;
            return true;
        }

        internal static bool Range(uint[] words, out ulong address, out ulong length)
        {
            address = ((ulong)(words[1] & 0xFFFF) << 32) | words[0];
            var stride = (words[1] >> 16) & 0x3FFF;
            length = (ulong)words[2] * Math.Max(stride, 1u);
            return (words[1] & 0x80000000) == 0 && (words[3] & 0xF0800000) == 0 &&
                address + length <= 1ul << 48;
        }
    }

    internal static bool TryGetPackedBufferWordDomain(ShaderResourcePlan plan, ScalarValue selector,
        out PackedBufferWordDomain domain)
    {
        domain = null!;
        if (selector.Kind != ScalarValueKind.FirstLane) return false;
        var instructions = plan.Graph.Program.Instructions;
        var laneIndex = instructions.ToList().FindIndex(instruction => instruction.Pc == selector.Payload);
        if (laneIndex < 0) return false;
        var laneRead = instructions[laneIndex];
        if (laneRead.Opcode is not ("VReadlaneB32" or "VReadfirstlaneB32")) return false;
        if (laneRead.Sources.Count == 0 || laneRead.Sources[0].Kind != Gen5OperandKind.VectorRegister) return false;
        var packedIndex = -1;
        for (var index = laneIndex - 1; index >= 0; index--)
            if (Builder.WritesRegister(instructions[index], laneRead.Sources[0])) { packedIndex = index; break; }
        if (packedIndex < 0) return false;
        var packed = instructions[packedIndex];
        if (packed is not { Opcode: "VMovB32", Sources.Count: 1,
            Control: Gen5SdwaControl { DestinationSelect: 6, Source0Select: 5,
                Source0SignExtend: false, AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false } } ||
            packed.Sources[0].Kind != Gen5OperandKind.VectorRegister) return false;
        if (!Dominates(packed.Pc, laneRead.Pc)) return false;
        if (laneRead.Opcode == "VReadlaneB32" && !ReadsInitializedLane()) return false;
        // READFIRSTLANE with empty EXEC has no initialized lane. Such a value
        // cannot become an active resource key if later execution never expands.
        if (laneRead.Opcode == "VReadfirstlaneB32" && instructions.Skip(laneIndex + 1)
                .Any(Builder.MayExpandExecution)) return false;
        for (var index = packedIndex + 1; index < laneIndex; index++)
            if (Builder.MayExpandExecution(instructions[index]) &&
                !instructions[index].Opcode.StartsWith("VCmpx", StringComparison.Ordinal)) return false;
        var vector = packed.Sources[0];
        var initialIndex = -1;
        for (var index = 0; index < packedIndex; index++)
            if (instructions[index] is { Opcode: "VMovB32", Sources.Count: 1, Control: null } initial &&
                initial.Destinations.Contains(vector) && Constant(initial.Sources[0], out var zero) && zero == 0)
            { initialIndex = index; break; }
        if (initialIndex < 0) return false;
        var captureIndex = -1;
        for (var index = initialIndex - 1; index >= 0; index--)
            if (instructions[index] is { Opcode: "SMovB64", Sources.Count: 1, Destinations.Count: 1 } capture &&
                capture.Sources[0] == Gen5Operand.Scalar(126) && capture.Destinations[0] is
                    { Kind: Gen5OperandKind.ScalarRegister, Value: < 126 } saved && (saved.Value & 1) == 0)
            { captureIndex = index; break; }
        if (captureIndex < 0 || !Dominates(instructions[captureIndex].Pc, instructions[initialIndex].Pc)) return false;
        var mask = instructions[captureIndex].Destinations[0];
        ScalarValue? handle = null;
        uint component = 0;
        for (var index = captureIndex + 1; index < packedIndex; index++)
        {
            var instruction = instructions[index];
            if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal) || Builder.WritesSavedMask(instruction, mask)) return false;
            if (Builder.MayExpandExecution(instruction))
            {
                var restrictsCurrent = instruction.Opcode is "SAndn2B64" && instruction.Sources.Count == 2 &&
                    instruction.Sources[0] == Gen5Operand.Scalar(126) || instruction.Opcode == "SAndB64" &&
                    instruction.Sources.Contains(Gen5Operand.Scalar(126));
                if (index < initialIndex || !restrictsCurrent && !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) &&
                    (instruction is not { Opcode: "SMovB64", Sources.Count: 1 } ||
                    instruction.Sources[0] != mask || !instruction.Destinations.Contains(Gen5Operand.Scalar(126)))) return false;
            }
            if (index <= initialIndex || !Builder.WritesRegister(instruction, vector)) continue;
            if (instruction is not { Opcode: "BufferLoadDwordx2", Control: Gen5BufferMemoryControl
                { DwordCount: 2, IndexEnabled: true, OffsetEnabled: false, OffsetBytes: 0, Typed: false } buffer } ||
                instruction.Sources.Count < 3 || !Constant(instruction.Sources[2], out var scalarOffset) || scalarOffset != 0 ||
                vector.Value < buffer.VectorData || vector.Value - buffer.VectorData >= 2 ||
                !Dominates(instructions[initialIndex].Pc, instruction.Pc, captureIndex) ||
                !plan.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex) ||
                plan.Accesses[memoryIndex]?.Handle is not { Kind: ScalarValueKind.BufferHandle } current ||
                !plan.ValidateRuntimeValue(current)) return false;
            var currentComponent = vector.Value - buffer.VectorData;
            if (handle is not null && (!plan.Graph.Equivalent(handle, current) || component != currentComponent)) return false;
            handle = current; component = currentComponent;
        }
        if (handle is null) return false;
        if (!Dominates(instructions[initialIndex].Pc, packed.Pc, captureIndex)) return false;
        domain = new(handle, component);
        return true;

        static bool Constant(Gen5Operand operand, out uint value)
        {
            value = operand.Value;
            return operand.Kind == Gen5OperandKind.LiteralConstant ||
                operand.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(operand.Value, out value);
        }

        bool ReadsInitializedLane()
        {
            if (laneRead.Sources.Count < 2 || laneRead.Sources[1].Kind != Gen5OperandKind.ScalarRegister) return false;
            var scanIndex = laneIndex - 1;
            while (scanIndex > packedIndex && !Builder.WritesRegister(instructions[scanIndex], laneRead.Sources[1])) scanIndex--;
            if (scanIndex <= packedIndex || instructions[scanIndex] is not
                { Opcode: "SFF1I32B64", Sources.Count: 1 } scan || scan.Sources[0] is not
                { Kind: Gen5OperandKind.ScalarRegister, Value: < 126 } remaining || (remaining.Value & 1) != 0) return false;
            if (!Dominates(scan.Pc, laneRead.Pc)) return false;
            var capture = scanIndex - 1;
            while (capture > packedIndex && !Builder.WritesSavedMask(instructions[capture], remaining)) capture--;
            if (capture <= packedIndex || instructions[capture] is not { Opcode: "SMovB64", Sources.Count: 1 } copy ||
                copy.Sources[0] != Gen5Operand.Scalar(126) || !copy.Destinations.Contains(remaining) ||
                !Dominates(copy.Pc, laneRead.Pc)) return false;
            for (var index = packedIndex + 1; index < capture; index++)
                if (Builder.MayExpandExecution(instructions[index])) return false;
            // The first scan must be non-empty. A fallthrough EXECZ guard in the
            // preceding block establishes this without inferring it from data.
            var guarded = false;
            for (var index = packedIndex - 1; index >= 0; index--)
            {
                var instruction = instructions[index];
                if (instruction.Opcode == "SCbranchExecz" && Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
                    instructions.Any(exit => exit.Pc == target && exit.Opcode == "SEndpgm") &&
                    Dominates(instruction.Pc, packed.Pc)) { guarded = true; break; }
                if (Builder.MayExpandExecution(instruction) || Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _)) break;
            }
            if (!guarded) return false;
            for (var index = capture + 1; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal) ||
                    Builder.WritesRegister(instruction, laneRead.Sources[0])) return false;
                if (Builder.WritesSavedMask(instruction, remaining) &&
                    (instruction is not { Opcode: "SAndn2B64", Sources.Count: 2 } || instruction.Sources[0] != remaining ||
                        !instruction.Destinations.Contains(remaining))) return false;
                if (!Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) || target > instruction.Pc) continue;
                if (target <= copy.Pc) return false;
                if (target > instructions[scanIndex].Pc) continue;
                // Each repeat is conditional on the remaining mask's SCC result.
                if (instruction.Opcode != "SCbranchScc1" || target != scan.Pc) return false;
                var setter = index - 1;
                while (setter > capture && instructions[setter].Opcode == "SMovB64") setter--;
                if (setter <= capture || instructions[setter] is not { Opcode: "SAndn2B64", Sources.Count: 2 } reduction ||
                    reduction.Sources[0] != remaining || !reduction.Destinations.Contains(remaining)) return false;
            }
            return true;
        }

        bool Dominates(uint definition, uint use, int zeroCapture = -1)
        {
            var flow = plan.Graph.ControlFlow;
            var definitionBlock = Enumerable.Range(0, flow.Blocks.Count).First(index => definition >= flow.Blocks[index].StartPc && definition < flow.Blocks[index].EndPc);
            var useBlock = Enumerable.Range(0, flow.Blocks.Count).First(index => use >= flow.Blocks[index].StartPc && use < flow.Blocks[index].EndPc);
            if (definitionBlock == useBlock) return definition <= use;
            var pending = new Queue<int>(); pending.Enqueue(0);
            var visited = new HashSet<int>();
            while (pending.TryDequeue(out var block))
            {
                if (block == definitionBlock || !visited.Add(block)) continue;
                if (block == useBlock) return false;
                uint? zeroTarget = null;
                if (zeroCapture >= 0)
                {
                    var last = instructions.LastOrDefault(instruction => instruction.Pc >= flow.Blocks[block].StartPc && instruction.Pc < flow.Blocks[block].EndPc);
                    if (last is { Opcode: "SCbranchExecz" } && last.Pc > instructions[zeroCapture].Pc && last.Pc < definition &&
                        instructions.Skip(zeroCapture + 1).TakeWhile(instruction => instruction.Pc < last.Pc)
                            .All(instruction => !Builder.MayExpandExecution(instruction)) &&
                        Gen5IrBranchResolver.Instance.TryGetBranchTarget(last, out var target) &&
                        target != last.Pc + (uint)last.Words.Count * 4) zeroTarget = target;
                }
                foreach (var next in flow.Successors[block])
                    if (zeroTarget is null || flow.Blocks[next].StartPc != zeroTarget) pending.Enqueue(next);
            }
            return true;
        }
    }

    private IndirectSelectorValues(Expression root, WaveMaskSelectorBounds? waveBounds)
    {
        _root = root;
        _waveBounds = waveBounds;
    }

    internal static IndirectSelectorValues? Create(ShaderResourcePlan plan, ScalarValue selector)
    {
        if (selector.Kind != ScalarValueKind.FirstLane) return null;
        var instruction = plan.Graph.Program.Instructions.FirstOrDefault(candidate => candidate.Pc == selector.Payload);
        if (instruction is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1 }) return null;
        var builder = new Builder(plan);
        var root = builder.Read(instruction.Sources[0], instruction.Pc);
        return root is null || !builder.HasBitScan ? null : new IndirectSelectorValues(root,
            WaveMaskSelectorBounds.TryCreate(plan, instruction));
    }

    // Only constant reaching definitions qualify here. Runtime memory/user data,
    // unknown writes and execution-mask expansion must not manufacture a bound.
    internal static bool TryGetConstantValues(ShaderResourcePlan plan, ScalarValue selector, out uint[] values)
    {
        values = [];
        if (selector.Kind != ScalarValueKind.FirstLane) return false;
        var instruction = plan.Graph.Program.Instructions.FirstOrDefault(candidate => candidate.Pc == selector.Payload);
        if (instruction is null) return false;
        var before = instruction.Pc;
        if (instruction.Opcode == "VReadlaneB32")
        {
            var index = plan.Graph.Program.Instructions.ToList().IndexOf(instruction);
            if (!ResourceTracker.TryGetStableLaneReadStart(plan.Graph.Program.Instructions, index, out before)) return false;
        }
        else if (instruction is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1 }) return false;
        var root = new Builder(plan).Read(instruction.Sources[0], before);
        if (root is null) return false;
        var pending = new Stack<Expression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            if (expression.RuntimeValue is not null) return false;
            if (expression.Inputs is { } operands)
                foreach (var operand in operands) pending.Push(operand);
        }
        return new IndirectSelectorValues(root, null).TryEvaluate(plan, new ResourceRuntimeInputs(), out values);
    }

    internal bool TryEvaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] values,
        IndirectSelectorDiagnostic? diagnostic = null)
    {
        bool ReadCapturedWord(ulong address, out uint word)
        {
            var succeeded = inputs.ReadCleanMemory!(address, out word);
            if (!succeeded) diagnostic!.FailedReadAddress ??= address;
            if (diagnostic!.MemoryReads.Count < 256)
                diagnostic.MemoryReads.Add(new(address, succeeded, succeeded ? word : null));
            else diagnostic.OmittedMemoryReads++;
            return succeeded;
        }
        var reader = inputs.ReadCleanMemory;
        if (diagnostic is not null && reader is not null) reader = ReadCapturedWord;
        var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(reader));
        if (_waveBounds is not null && _waveBounds.TryEvaluate(evaluator, inputs.ComputeState, out values))
            return true;
        var cache = new Dictionary<Expression, uint[]>();
        uint[]? Decline(string reason)
        {
            if (diagnostic is not null) diagnostic.EvaluationFailure ??= reason;
            return null;
        }
        uint[]? Evaluate(Expression expression)
        {
            if (cache.TryGetValue(expression, out var previous)) return previous;
            if (expression.Values is { } constants) return constants;
            if (expression.RuntimeValue is { } runtime)
                return evaluator.Evaluate(runtime, out var value) ? [value] : Decline("runtime_value_unavailable");
            var operands = expression.Inputs!.Select(Evaluate).ToArray();
            if (operands.Any(operand => operand is null)) return null;
            var results = new HashSet<uint>();
            if (expression.Operation == ScalarOperation.None)
            {
                foreach (var operand in operands)
                    foreach (var value in operand!)
                        if (results.Add(value) && results.Count > MaximumValues) return Decline("value_limit");
            }
            else
            {
                if ((long)operands[0]!.Length * operands[1]!.Length > MaximumCombinations) return Decline("combination_limit");
                Span<ulong> pair = stackalloc ulong[2];
                foreach (var left in operands[0]!)
                    foreach (var right in operands[1]!)
                    {
                        pair[0] = left;
                        pair[1] = right;
                        if (!ScalarOperationSemantics.TryEvaluate(expression.Operation, pair, out var result)) return Decline("unsupported_operation");
                        if (results.Add((uint)result) && results.Count > MaximumValues) return Decline("value_limit");
                    }
            }
            return cache[expression] = results.ToArray();
        }
        values = Evaluate(_root) ?? [];
        return values.Length != 0;
    }

    private sealed class Builder(ShaderResourcePlan plan, bool allowGather = false)
    {
        private readonly HashSet<(Gen5Operand Operand, uint Address)> _active = [];
        private int _requests;
        public bool HasBitScan { get; private set; }

        public Expression? Read(Gen5Operand operand, uint before)
        {
            if (++_requests > 512 || !_active.Add((operand, before))) return null;
            try
            {
                if (operand.Kind == Gen5OperandKind.LiteralConstant) return new(Values: [operand.Value]);
                if (operand.Kind == Gen5OperandKind.EncodedConstant)
                    return Gen5InlineConstants.TryDecode(operand.Value, out var constant) ? new(Values: [constant]) : null;
                var flow = plan.Graph.ControlFlow;
                var initial = Enumerable.Range(0, flow.Blocks.Count).FirstOrDefault(
                    index => before >= flow.Blocks[index].StartPc && before < flow.Blocks[index].EndPc, -1);
                if (initial < 0) return null;
                var pending = new Queue<(int Block, uint Before)>();
                var visited = new HashSet<(int Block, uint Before)>();
                var definitions = new List<Expression>();
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
                        if (WritesRegister(instruction, operand))
                        {
                            var definition = Define(instruction, operand);
                            if (definition is null) return null;
                            definitions.Add(definition);
                            found = true;
                            break;
                        }
                        // A later active lane must also have been active at the defining write.
                        if (operand.Kind == Gen5OperandKind.VectorRegister && MayExpandExecution(instruction) &&
                            !(allowGather && instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal)))
                        {
                            var restored = ReadBeforeSavedExecRestore(operand, instruction);
                            if (restored is null) return null;
                            definitions.Add(restored);
                            found = true;
                            break;
                        }
                    }
                    if (found) continue;
                    if (position.Block == 0)
                    {
                        if (operand.Kind != Gen5OperandKind.ScalarRegister || operand.Value < plan.UserDataBase ||
                            operand.Value - plan.UserDataBase >= plan.UserDataCount) return null;
                        definitions.Add(new(RuntimeValue: plan.Graph.UserData(operand.Value)));
                    }
                    if (flow.Predecessors[position.Block].Count == 0 && position.Block != 0) return null;
                    foreach (var predecessor in flow.Predecessors[position.Block])
                        pending.Enqueue((predecessor, flow.Blocks[predecessor].EndPc));
                }
                return definitions.Count == 0 ? null : new(Inputs: definitions.ToArray());
            }
            finally { _active.Remove((operand, before)); }
        }

        private Expression? Define(Gen5ShaderInstruction instruction, Gen5Operand destination)
        {
            if (allowGather && instruction.Control is Gen5ImageControl gather &&
                instruction.Opcode == "ImageGather4Lz" && !gather.D16 && gather.Dmask is 1 or 2 or 4 or 8 &&
                destination.Kind == Gen5OperandKind.VectorRegister && destination.Value >= gather.VectorData &&
                destination.Value - gather.VectorData < 4 && plan.Memory.TryGetIndex(instruction.Pc, 0, out var gatherIndex))
                return new(GatherMemoryIndex: gatherIndex);
            if (!instruction.Destinations.Contains(destination)) return null;
            // Packed descriptor selectors may extract the upper word with SDWA.
            // A full-dword destination discards the previous value; partial
            // destinations and source modifiers require a separate proof.
            if (instruction is { Opcode: "VMovB32", Sources.Count: 1,
                Control: Gen5SdwaControl { DestinationSelect: 6, Source0Select: 5,
                    Source0SignExtend: false, AbsoluteMask: 0, NegateMask: 0,
                    OutputModifier: 0, Clamp: false } })
            {
                var packed = Read(instruction.Sources[0], instruction.Pc);
                return packed is null ? null : new(Operation: ScalarOperation.And32, Inputs:
                    [new(Operation: ScalarOperation.ShiftRightLogical32, Inputs:
                        [packed, new(Values: [16])]), new(Values: [0xFFFF])]);
            }
            if (instruction.Control is Gen5Vop3Control { AbsoluteMask: not 0 } or Gen5Vop3Control { NegateMask: not 0 } or
                Gen5Vop3Control { Clamp: true } or Gen5Vop3Control { OutputModifier: not 0 } or Gen5Vop3Control { OperandSelect: not 0 } or
                Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl) return null;
            if (instruction.Control is Gen5SdwaControl sdwa &&
                (instruction.Opcode != "VCndmaskB32" || sdwa.DestinationSelect != 6 ||
                 sdwa.Source0Select != 6 || sdwa.Source1Select != 6 ||
                 sdwa.Source0SignExtend || sdwa.Source1SignExtend ||
                 sdwa.AbsoluteMask != 0 || sdwa.NegateMask != 0 || sdwa.OutputModifier != 0 || sdwa.Clamp)) return null;
            if (instruction.Opcode is "SFF1I32B32" or "VFfblB32")
            {
                HasBitScan = true;
                return new(Values: Enumerable.Range(0, 32).Select(value => (uint)value).Append(uint.MaxValue).ToArray());
            }
            if (instruction.Encoding == Gen5ShaderEncoding.Smem)
            {
                var component = instruction.Destinations.ToList().IndexOf(destination);
                if (component < 0 || !plan.Memory.TryGetIndex(instruction.Pc, (uint)component, out var memoryIndex)) return null;
                var read = plan.Graph.Accesses[memoryIndex]?.Read;
                return read is not null && plan.ValidateRuntimeValue(read) ? new(RuntimeValue: read) : null;
            }
            if (instruction.Opcode is "SMovB32" or "VMovB32") return Read(instruction.Sources[0], instruction.Pc);
            if (instruction.Opcode == "VCndmaskB32" && instruction.Sources.Count >= 2)
            {
                var falseArm = Read(instruction.Sources[0], instruction.Pc);
                var trueArm = Read(instruction.Sources[1], instruction.Pc);
                return falseArm is null || trueArm is null ? null : new(Inputs: [falseArm, trueArm]);
            }
            if (instruction.Opcode is "VMadU32U24" or "VMulU32U24" &&
                instruction.Sources.Count == (instruction.Opcode == "VMadU32U24" ? 3 : 2))
            {
                var operands = instruction.Sources.Select(source => Read(source, instruction.Pc)).ToArray();
                if (operands.Any(operand => operand is null)) return null;
                var mask = new Expression(Values: [0x00FF_FFFF]);
                var multiplicand = new Expression(Operation: ScalarOperation.And32, Inputs: [operands[0]!, mask]);
                var multiplier = new Expression(Operation: ScalarOperation.And32, Inputs: [operands[1]!, mask]);
                var product = new Expression(Operation: ScalarOperation.IMul32, Inputs: [multiplicand, multiplier]);
                if (instruction.Opcode == "VMulU32U24") return product;
                return new(Operation: ScalarOperation.IAdd32, Inputs: [product, operands[2]!]);
            }
            var operation = instruction.Opcode switch
            {
                "SAddU32" or "SAddI32" or "VAddU32" or "VAddI32" or "VAdd3U32" => ScalarOperation.IAdd32,
                "SLshlB32" => ScalarOperation.ShiftLeft32,
                _ => ScalarOperation.None,
            };
            if (operation == ScalarOperation.None || instruction.Sources.Count < 2) return null;
            var left = Read(instruction.Sources[0], instruction.Pc);
            var right = Read(instruction.Sources[1], instruction.Pc);
            if (left is null || right is null) return null;
            var result = new Expression(Operation: operation, Inputs: [left, right]);
            if (instruction.Opcode != "VAdd3U32") return result;
            var third = Read(instruction.Sources[2], instruction.Pc);
            return third is null ? null : new(Operation: ScalarOperation.IAdd32, Inputs: [result, third]);
        }

        // Restoring a saved mask exposes both unchanged lanes and lanes written
        // while restricted. Retain every reaching value until a full-mask overwrite.
        private Expression? ReadBeforeSavedExecRestore(Gen5Operand vector, Gen5ShaderInstruction restore)
        {
            if (restore is not { Opcode: "SMovB64", Sources.Count: 1 } ||
                !restore.Destinations.Contains(Gen5Operand.Scalar(126)) ||
                restore.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } saved ||
                saved.Value >= 126 || (saved.Value & 1) != 0) return null;
            var instructions = plan.Graph.Program.Instructions;
            var restoreIndex = instructions.ToList().IndexOf(restore);
            // A later full-mask definition can kill every value from an earlier
            // waterfall. Start there only when its saved-mask restoration cannot
            // be bypassed and the defining write dominates this restoration.
            if (TryFindFullSavedMaskOverwrite(vector, saved, restoreIndex, out var overwrite))
                return ReadAfterFullSavedMaskOverwrite(vector, saved, overwrite, restoreIndex);
            var saveIndex = -1;
            for (var index = restoreIndex - 1; index >= 0; index--)
            {
                var instruction = instructions[index];
                if (!WritesSavedMask(instruction, saved)) continue;
                if (instruction is not { Sources.Count: 1 } || instruction.Opcode is not ("SMovB64" or "SAndSaveexecB64" or "SAndn1SaveexecB64") ||
                    !instruction.Destinations.Contains(saved)) return null;
                if (instruction.Opcode == "SMovB64" && instruction.Sources[0] != Gen5Operand.Scalar(126))
                {
                    var alias = instruction.Sources[0];
                    if (alias is not { Kind: Gen5OperandKind.ScalarRegister } || alias.Value >= 126 || (alias.Value & 1) != 0)
                        return null;
                    var restoredAlias = false;
                    for (var previous = index - 1; previous >= 0; previous--)
                    {
                        var prior = instructions[previous];
                        if (WritesSavedMask(prior, alias) || Gen5IrBranchResolver.Instance.TryGetBranchTarget(prior, out _)) return null;
                        if (!MayExpandExecution(prior) && prior.Opcode is not ("SAndSaveexecB64" or "SAndSaveexecB32")) continue;
                        restoredAlias = prior is { Opcode: "SMovB64", Sources.Count: 1 } &&
                            prior.Destinations.Contains(Gen5Operand.Scalar(126)) && prior.Sources[0] == alias;
                        if (instructions.Any(edge => Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var target) &&
                            target > prior.Pc && target <= instruction.Pc)) return null;
                        break;
                    }
                    if (!restoredAlias) return null;
                }
                saveIndex = index;
                break;
            }
            if (saveIndex < 0) return null;
            // A waterfall can recapture the same mask on every iteration. If this
            // register is invariant, read it before the loop rather than following
            // a cyclic reaching definition through the loop's EXEC restoration.
            if (instructions[saveIndex].Opcode == "SAndSaveexecB64")
            {
                for (var readIndex = saveIndex - 1; readIndex >= 0; readIndex--)
                {
                    var laneRead = instructions[readIndex];
                    if (laneRead.Opcode != "VReadlaneB32") continue;
                    if (!ResourceTracker.TryGetStableLaneReadStart(instructions, readIndex, vector, out var start) ||
                        start >= instructions[saveIndex].Pc) break;
                    var region = instructions.Where(candidate => candidate.Pc >= start && candidate.Pc < restore.Pc).ToArray();
                    var unchanged = region.All(candidate => !WritesRegister(candidate, vector) &&
                        !candidate.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Opcode.Contains("GprIdx", StringComparison.Ordinal) &&
                        (candidate == instructions[saveIndex] || !WritesSavedMask(candidate, saved)) &&
                        (candidate.Pc >= instructions[saveIndex].Pc || !MayExpandExecution(candidate) &&
                            candidate.Opcode is not ("SAndSaveexecB64" or "SAndSaveexecB32")));
                    var bypass = instructions.Any(edge => (edge.Pc < start || edge.Pc >= restore.Pc) &&
                        Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var target) &&
                        target > start && target <= restore.Pc);
                    if (unchanged && !bypass) return Read(vector, start);
                    break;
                }
            }
            // An incoming edge must not bypass the saved mask or the defining write.
            foreach (var instruction in instructions)
                if ((instruction.Pc < instructions[saveIndex].Pc || instruction.Pc >= restore.Pc) &&
                    Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
                    target > instructions[saveIndex].Pc && target <= restore.Pc) return null;
            var fullMask = instructions[saveIndex].Opcode == "SMovB64";
            Expression? values = Read(vector, instructions[saveIndex].Pc);
            for (var index = saveIndex + 1; index < restoreIndex; index++)
            {
                var instruction = instructions[index];
                if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal) || WritesSavedMask(instruction, saved)) return null;
                if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target))
                {
                    if (target <= instruction.Pc) return null;
                    // An edge leaving this region cannot reach this restoration.
                    if (target <= restore.Pc)
                    {
                        if (fullMask) return null;
                        var nextRestore = instructions.Skip(index + 1).FirstOrDefault(candidate =>
                            candidate.Pc <= restore.Pc && candidate is { Opcode: "SMovB64", Sources.Count: 1 } &&
                            candidate.Destinations.Contains(Gen5Operand.Scalar(126)) && candidate.Sources[0] == saved);
                        if (nextRestore is null || target > nextRestore.Pc) return null;
                    }
                }
                var writesVector = WritesRegister(instruction, vector);
                if (MayExpandExecution(instruction) || instruction.Opcode is "SAndSaveexecB64" or "SAndSaveexecB32")
                {
                    if (instruction is { Opcode: "SMovB64", Sources.Count: 1 } &&
                        instruction.Destinations.Contains(Gen5Operand.Scalar(126)) && instruction.Sources[0] == saved)
                        fullMask = true;
                    else fullMask = false;
                }
                if (writesVector)
                {
                    var written = Define(instruction, vector);
                    if (fullMask) values = written;
                    else if (values is null || written is null) values = null;
                    else values = new(Inputs: [values, written]);
                }
            }
            return values;
        }

        private bool TryFindFullSavedMaskOverwrite(Gen5Operand vector, Gen5Operand saved,
            int restoreIndex, out int overwrite)
        {
            overwrite = -1;
            var instructions = plan.Graph.Program.Instructions;
            var end = instructions[restoreIndex].Pc;
            for (var start = restoreIndex - 1; start >= 0; start--)
            {
                var restored = instructions[start];
                if (restored is not { Opcode: "SMovB64", Sources.Count: 1 } ||
                    !restored.Destinations.Contains(Gen5Operand.Scalar(126)) || restored.Sources[0] != saved) continue;
                if (instructions.Skip(start + 1).Take(restoreIndex - start - 1).Any(instruction =>
                    WritesSavedMask(instruction, saved) || instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal))) continue;
                var candidate = -1;
                for (var index = start + 1; index < restoreIndex; index++)
                {
                    var instruction = instructions[index];
                    if (MayExpandExecution(instruction) || instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal)) break;
                    // A retry before initialization must cross this restoration
                    // again. Incoming edges that bypass it are rejected below.
                    if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var retry) &&
                        retry > restored.Pc) break;
                    if (WritesRegister(instruction, vector)) candidate = index;
                }
                if (candidate < 0 || Define(instructions[candidate], vector) is null) continue;
                var definition = instructions[candidate].Pc;
                var bypass = instructions.Any(edge => Gen5IrBranchResolver.Instance.TryGetBranchTarget(edge, out var target) &&
                    ((target > restored.Pc && target <= definition && (edge.Pc < restored.Pc || edge.Pc >= end)) ||
                     (target > definition && target <= end && (edge.Pc < definition || edge.Pc >= end)) ||
                     (edge.Pc > definition && edge.Pc < end && target <= edge.Pc && target > restored.Pc)));
                if (bypass) continue;
                overwrite = candidate;
                return true;
            }
            return false;
        }

        private Expression? ReadAfterFullSavedMaskOverwrite(Gen5Operand vector, Gen5Operand saved,
            int overwrite, int restoreIndex)
        {
            var instructions = plan.Graph.Program.Instructions;
            var values = Define(instructions[overwrite], vector);
            for (var index = overwrite + 1; index < restoreIndex; index++)
            {
                var instruction = instructions[index];
                if (MayExpandExecution(instruction))
                {
                    var restoresSavedMask = instruction is { Opcode: "SMovB64", Sources.Count: 1 } &&
                        instruction.Destinations.Contains(Gen5Operand.Scalar(126)) && instruction.Sources[0] == saved;
                    if (!restoresSavedMask && !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal)) return null;
                }
                if (!WritesRegister(instruction, vector)) continue;
                var written = Define(instruction, vector);
                // Later branches may bypass a restore. Keep all possible earlier values
                // rather than assuming a subsequent write replaces every saved lane.
                if (values is null || written is null) values = null;
                else values = new(Inputs: [values, written]);
            }
            return values;
        }

        internal static bool WritesSavedMask(Gen5ShaderInstruction instruction, Gen5Operand saved)
        {
            // RDNA2 CMPX updates EXEC only, leaving the explicit condition SGPRs intact.
            if (instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) && saved.Value < 126) return false;
            var pairEnd = saved.Value + 1;
            var width = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
            if (instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister &&
                destination.Value <= pairEnd && destination.Value + width > saved.Value)) return true;
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } vop } && vop <= pairEnd && vop + 1 >= saved.Value ||
                instruction.Control is Gen5SdwaControl { ScalarDestination: { } sdwa } && sdwa <= pairEnd && sdwa + 1 >= saved.Value)
                return !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal);
            return saved.Value is 106 or 107 && instruction.Opcode.StartsWith('V') &&
                (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) && !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                 instruction.Opcode.Contains("Co", StringComparison.Ordinal) || instruction.Opcode.Contains("Vcc", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool MayExpandExecution(Gen5ShaderInstruction instruction)
        {
            if (instruction.Opcode is "SAndSaveexecB64" or "SAndSaveexecB32") return false;
            return instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                WritesRegister(instruction, Gen5Operand.Scalar(126)) || WritesRegister(instruction, Gen5Operand.Scalar(127));
        }

        internal static bool WritesRegister(Gen5ShaderInstruction instruction, Gen5Operand register)
        {
            if (instruction.Control is Gen5ImageControl gather && instruction.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal) &&
                register.Kind == Gen5OperandKind.VectorRegister && register.Value >= gather.VectorData &&
                register.Value - gather.VectorData < (gather.D16 ? 2u : 4u)) return true;
            if (register.Kind == Gen5OperandKind.ScalarRegister && register.Value is 106 or 107 &&
                instruction.Opcode.StartsWith('V')) return true;
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination } &&
                register.Kind == Gen5OperandKind.ScalarRegister && register.Value >= scalarDestination && register.Value - scalarDestination < 2) return true;
            return instruction.Destinations.Any(destination => destination == register ||
                (destination.Kind == register.Kind && instruction.Opcode.Contains("64", StringComparison.Ordinal) &&
                    register.Value > destination.Value && register.Value - destination.Value == 1));
        }
    }
}
