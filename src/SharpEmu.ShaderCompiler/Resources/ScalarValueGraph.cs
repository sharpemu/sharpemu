// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

// The graph values one memory access reads its descriptor from: the handles, the read
// node of a scalar load, and the uniform vector offset of a global access.
public sealed record MemoryAccessBinding(ScalarValue? Handle, ScalarValue? SamplerHandle, ScalarValue? Read, ScalarValue? Offset = null);

// The uniform value graph of one program: every value a descriptor can be assembled
// from, symbolic in user data and in the shader base.
public sealed partial class ScalarValueGraph
{
    private readonly Dictionary<string, ScalarValue> _interned = [];
    private readonly List<ScalarValue> _values = [];

    private ScalarValueGraph(Gen5ShaderProgram program, IrControlFlowGraph controlFlow, MemoryAccessTable memory, uint userDataBase, uint userDataCount)
    {
        Program = program;
        ControlFlow = controlFlow;
        Memory = memory;
        UserDataBase = userDataBase;
        UserDataCount = userDataCount;
    }

    public Gen5ShaderProgram Program { get; }
    public IrControlFlowGraph ControlFlow { get; }
    public MemoryAccessTable Memory { get; }
    public uint UserDataBase { get; }
    public uint UserDataCount { get; }

    // Index-aligned with Memory.Entries; null when the access has no descriptor value.
    public MemoryAccessBinding?[] Accesses { get; private set; } = [];

    public IReadOnlyList<ScalarValue> Values => _values;

    internal Dictionary<uint, ScalarValue> BranchConditions { get; } = [];

    public bool Equivalent(ScalarValue left, ScalarValue right) => ScalarValueEquivalence.Equivalent(Memory, left, right);

    public ScalarValue? ResolveInvariantPhi(ScalarValue value) => ScalarValueEquivalence.ResolveInvariantPhi(Memory, value);

    public static ScalarValueGraph Build(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads = null)
    {
        var controlFlow = IrControlFlowGraph.Build(program.Instructions, Gen5IrBranchResolver.Instance);
        var graph = new ScalarValueGraph(program, controlFlow, MemoryAccessTable.Build(program, fixedFunctionVertexLoads), userDataBase, userDataCount);
        new Builder(graph).Run();
        return graph;
    }

    // Node creation. Equal structure yields the same node, and operations over
    // constants fold, so revisiting a block reproduces its values exactly.
    internal ScalarValue Constant(uint value) => Intern($"c32:{value}", () => ScalarValue.ConstantOf(value));

    internal ScalarValue Constant(ulong value) => Intern($"c64:{value}", () => ScalarValue.ConstantOf(value));

    internal ScalarValue Constant(bool value) => Intern($"c1:{value}", () => ScalarValue.ConstantOf(value));

    // One undefined node per type keeps every value built over it stable across visits.
    internal ScalarValue Undefined(ScalarValueType type) => Intern($"undef:{type}", () => ScalarValue.Undefined(type));

    internal ScalarValue UserData(uint register) => Intern($"ud:{register}", () => ScalarValue.UserData(register));

    internal ScalarValue ShaderBase() => Intern("base", ScalarValue.ShaderBase);

    // Keep the finite result range even when the input cannot be resolved on the host.
    internal ScalarValue FindLowestSetBit(ScalarValue value, uint instructionAddress)
    {
        if (value.IsConstant)
            return Operation(ScalarOperation.FindLowestBit32, ScalarValueType.U32, value);

        // Keep each instruction separate because its input guard can differ from another scan's guard.
        var result = Intern($"lowest-bit:{instructionAddress}:{value.Id}", () =>
            ScalarValue.MakeOperation(ScalarOperation.FindLowestBit32, ScalarValueType.U32, value));
        _bitScanInstructions[result] = instructionAddress;
        return result;
    }

    internal ScalarValue ResourceTableWord(uint slot) => Intern($"table:{slot}", () => ScalarValue.ResourceTableWord(slot));

    internal ScalarValue Handle(ScalarValueKind kind, params ScalarValue[] dwords) =>
        Intern($"{kind}:{Ids(dwords)}", () => ScalarValue.Handle(kind, dwords));

    internal ScalarValue MemoryRead(ScalarValueKind kind, ScalarValue handle, ScalarValue offset, int memoryIndex) =>
        Intern($"{kind}:{memoryIndex}:{handle.Id}:{offset.Id}", () => ScalarValue.MemoryRead(kind, handle, offset, memoryIndex));

    internal ScalarValue FirstLane(ScalarValue value, ScalarValue activeMask, uint instructionAddress) =>
        Intern($"first:{instructionAddress}:{value.Id}:{activeMask.Id}", () => ScalarValue.FirstLane(value, activeMask, instructionAddress));

    internal ScalarValue Phi(int block, ScalarValueType type) => Track(ScalarValue.Phi(block, type));

    internal ScalarValue Select(ScalarValue condition, ScalarValue whenTrue, ScalarValue whenFalse)
    {
        if (condition.IsConstant)
        {
            return condition.ConstantBool ? whenTrue : whenFalse;
        }

        if (ReferenceEquals(whenTrue, whenFalse))
        {
            return whenTrue;
        }

        return Intern($"sel:{condition.Id}:{whenTrue.Id}:{whenFalse.Id}", () => ScalarValue.Select(condition, whenTrue, whenFalse));
    }

    internal ScalarValue Operation(ScalarOperation operation, ScalarValueType type, params ScalarValue[] operands)
    {
        foreach (var operand in operands)
        {
            if (operand.IsUndefined)
            {
                return Undefined(type);
            }
        }

        // An extracted half of a constructed pair is that half; the low half of a
        // carrying add is the plain add.
        if (operation == ScalarOperation.Extract64 && operands[1].IsConstant && operands[0].Kind == ScalarValueKind.Operation)
        {
            if (operands[0].Operation == ScalarOperation.Construct64)
            {
                return operands[0].Operands[Math.Min(operands[1].ConstantU32, 1)];
            }

            if (operands[0].Operation == ScalarOperation.AddCarry32 && operands[1].ConstantU32 == 0)
            {
                return Operation(ScalarOperation.IAdd32, ScalarValueType.U32, operands[0].Operands[0], operands[0].Operands[1]);
            }
        }

        if (TryFold(operation, type, operands, out var folded))
        {
            return folded;
        }

        return Intern($"op:{operation}:{type}:{Ids(operands)}", () => ScalarValue.MakeOperation(operation, type, operands));
    }

    private bool TryFold(ScalarOperation operation, ScalarValueType type, ScalarValue[] operands, out ScalarValue folded)
    {
        folded = null!;
        Span<ulong> values = stackalloc ulong[operands.Length];
        for (var index = 0; index < operands.Length; index++)
        {
            if (!operands[index].IsConstant)
            {
                return false;
            }

            values[index] = operands[index].Payload;
        }

        if (!ScalarOperationSemantics.TryEvaluate(operation, values, out var result))
        {
            return false;
        }

        folded = type switch
        {
            ScalarValueType.Bool => Constant(result != 0),
            ScalarValueType.U64 => Constant(result),
            _ => Constant((uint)result),
        };
        return true;
    }

    // Rebuilds a value with every replaced node swapped for its replacement. Phis are
    // recreated first so a cycle through them terminates.
    internal ScalarValue Substitute(ScalarValue value, IReadOnlyDictionary<ScalarValue, ScalarValue> replacements, Dictionary<ScalarValue, ScalarValue> memo)
    {
        if (replacements.TryGetValue(value, out var replacement))
        {
            return replacement;
        }

        if (memo.TryGetValue(value, out var rebuilt))
        {
            return rebuilt;
        }

        if (value.Operands.Length == 0)
        {
            memo[value] = value;
            return value;
        }

        if (value.Kind == ScalarValueKind.Phi)
        {
            var phi = Phi(value.PhiBlock, value.Type);
            memo[value] = phi;
            var phiOperands = new ScalarValue[value.Operands.Length];
            for (var index = 0; index < phiOperands.Length; index++)
            {
                phiOperands[index] = ReferenceEquals(value.Operands[index], value) ? phi : Substitute(value.Operands[index], replacements, memo);
            }

            phi.SetPhiOperands(value.PhiPredecessors, phiOperands);
            return phi;
        }

        var operands = new ScalarValue[value.Operands.Length];
        var changed = false;
        for (var index = 0; index < operands.Length; index++)
        {
            operands[index] = Substitute(value.Operands[index], replacements, memo);
            changed |= !ReferenceEquals(operands[index], value.Operands[index]);
        }

        if (!changed)
        {
            memo[value] = value;
            return value;
        }

        rebuilt = value.Kind switch
        {
            ScalarValueKind.Operation when _bitScanInstructions.TryGetValue(value, out var instructionAddress) =>
                FindLowestSetBit(operands[0], instructionAddress),
            ScalarValueKind.Operation => Operation(value.Operation, value.Type, operands),
            ScalarValueKind.Select => Select(operands[0], operands[1], operands[2]),
            ScalarValueKind.FirstLane => FirstLane(operands[0], operands[1], (uint)value.Payload),
            ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord => MemoryRead(value.Kind, operands[0], operands[1], value.MemoryIndex),
            _ => Handle(value.Kind, operands),
        };
        memo[value] = rebuilt;
        return rebuilt;
    }

    // Every value reachable from the recorded accesses, each with the values that use it.
    internal Dictionary<ScalarValue, List<ScalarValue>> CollectUses(IEnumerable<ScalarValue> roots)
    {
        var uses = new Dictionary<ScalarValue, List<ScalarValue>>();
        var pending = new Stack<ScalarValue>(roots);
        var visited = new HashSet<ScalarValue>();
        while (pending.Count != 0)
        {
            var value = pending.Pop();
            if (!visited.Add(value))
            {
                continue;
            }

            foreach (var operand in value.Operands)
            {
                if (!uses.TryGetValue(operand, out var list))
                {
                    list = [];
                    uses[operand] = list;
                }

                list.Add(value);
                pending.Push(operand);
            }
        }

        return uses;
    }

    private static string Ids(ScalarValue[] operands)
    {
        var text = new System.Text.StringBuilder();
        foreach (var operand in operands)
        {
            text.Append(operand.Id).Append(',');
        }

        return text.ToString();
    }

    private ScalarValue Intern(string key, Func<ScalarValue> create)
    {
        if (_interned.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var value = create();
        _interned[key] = value;
        _values.Add(value);
        return value;
    }

    private ScalarValue Track(ScalarValue value)
    {
        _values.Add(value);
        return value;
    }
}
