// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// A finite overestimate of a selector. Unsupported paths retain the full-domain scan.
public sealed class IndirectSelectorValues
{
    private const int MaximumValues = 4096;
    private const int MaximumCombinations = 65536;
    private sealed record Expression(uint[]? Values = null, ScalarValue? RuntimeValue = null,
        ScalarOperation Operation = ScalarOperation.None, Expression[]? Inputs = null);
    private readonly Expression _root;

    private IndirectSelectorValues(Expression root) => _root = root;

    internal static IndirectSelectorValues? Create(ShaderResourcePlan plan, ScalarValue selector)
    {
        if (selector.Kind != ScalarValueKind.FirstLane) return null;
        var instruction = plan.Graph.Program.Instructions.FirstOrDefault(candidate => candidate.Pc == selector.Payload);
        if (instruction is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1 }) return null;
        var builder = new Builder(plan);
        var root = builder.Read(instruction.Sources[0], instruction.Pc);
        return root is null || !builder.HasBitScan ? null : new IndirectSelectorValues(root);
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

    private sealed class Builder(ShaderResourcePlan plan)
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
                        if (operand.Kind == Gen5OperandKind.VectorRegister && MayExpandExecution(instruction)) return null;
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
            if (!instruction.Destinations.Contains(destination)) return null;
            if (instruction.Control is Gen5Vop3Control { AbsoluteMask: not 0 } or Gen5Vop3Control { NegateMask: not 0 } or
                Gen5Vop3Control { Clamp: true } or Gen5Vop3Control { OutputModifier: not 0 } or Gen5Vop3Control { OperandSelect: not 0 } or
                Gen5SdwaControl or Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl) return null;
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

        private static bool MayExpandExecution(Gen5ShaderInstruction instruction)
        {
            if (instruction.Opcode is "SAndSaveexecB64" or "SAndSaveexecB32") return false;
            return instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                WritesRegister(instruction, Gen5Operand.Scalar(126)) || WritesRegister(instruction, Gen5Operand.Scalar(127));
        }

        private static bool WritesRegister(Gen5ShaderInstruction instruction, Gen5Operand register)
        {
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
