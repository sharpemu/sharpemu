// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Ir;

public enum IrScalarState
{
    Unknown,
    Constant,
    Merged,
}

public enum IrReachingState
{
    None,
    Single,
    Multiple,
}

public readonly record struct IrReachingDefinition(IrReachingState State, uint DefinitionPc)
{
    public static readonly IrReachingDefinition None = new(IrReachingState.None, 0);

    public static readonly IrReachingDefinition Multiple = new(IrReachingState.Multiple, 0);

    public static IrReachingDefinition At(uint pc) => new(IrReachingState.Single, pc);

    public IrReachingDefinition Join(IrReachingDefinition other)
    {
        if (State == IrReachingState.None)
        {
            return other;
        }

        if (other.State == IrReachingState.None)
        {
            return this;
        }

        if (State == IrReachingState.Single &&
            other.State == IrReachingState.Single &&
            DefinitionPc == other.DefinitionPc)
        {
            return this;
        }

        return Multiple;
    }
}

public readonly record struct IrScalarValue(IrScalarState State, uint Constant)
{
    public static readonly IrScalarValue Unknown = new(IrScalarState.Unknown, 0);

    public static readonly IrScalarValue Merged = new(IrScalarState.Merged, 0);

    public static IrScalarValue FromConstant(uint value) => new(IrScalarState.Constant, value);

    public bool IsResolved => State == IrScalarState.Constant;

    public IrScalarValue Join(IrScalarValue other)
    {
        if (State == IrScalarState.Unknown)
        {
            return other;
        }

        if (other.State == IrScalarState.Unknown)
        {
            return this;
        }

        if (State == IrScalarState.Constant &&
            other.State == IrScalarState.Constant &&
            Constant == other.Constant)
        {
            return this;
        }

        return Merged;
    }
}

public sealed class Gen5ScalarSsa
{
    public const int ScalarRegisterCount = 256;

    private Gen5ScalarSsa(
        IrControlFlowGraph graph,
        IReadOnlyList<IrScalarValue[]> entryState,
        IReadOnlyList<IrScalarValue[]> exitState,
        IReadOnlyList<IrReachingDefinition[]> entryDefinitions,
        IReadOnlyDictionary<uint, int> blockByPc,
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        Graph = graph;
        _entryState = entryState;
        _exitState = exitState;
        _entryDefinitions = entryDefinitions;
        _blockByPc = blockByPc;
        _instructions = instructions;
    }

    private readonly IReadOnlyList<IrReachingDefinition[]> _entryDefinitions;

    public IrControlFlowGraph Graph { get; }

    private readonly IReadOnlyList<IrScalarValue[]> _entryState;
    private readonly IReadOnlyList<IrScalarValue[]> _exitState;
    private readonly IReadOnlyDictionary<uint, int> _blockByPc;
    private readonly IReadOnlyList<Gen5ShaderInstruction> _instructions;

    public static Gen5ScalarSsa Build(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IReadOnlyList<uint> userData,
        IIrBranchResolver? resolver = null)
    {
        resolver ??= Gen5IrBranchResolver.Instance;
        var graph = IrControlFlowGraph.Build(instructions, resolver);
        var blockCount = graph.Blocks.Count;

        var entry = new List<IrScalarValue[]>(blockCount);
        var exit = new List<IrScalarValue[]>(blockCount);
        var defEntry = new List<IrReachingDefinition[]>(blockCount);
        var defExit = new List<IrReachingDefinition[]>(blockCount);
        for (var index = 0; index < blockCount; index++)
        {
            entry.Add(NewState());
            exit.Add(NewState());
            defEntry.Add(NewDefinitions());
            defExit.Add(NewDefinitions());
        }

        if (blockCount > 0)
        {
            var initial = entry[0];
            for (var index = 0; index < userData.Count && index < ScalarRegisterCount; index++)
            {
                initial[index] = IrScalarValue.FromConstant(userData[index]);
            }
        }

        var blockByPc = new Dictionary<uint, int>();
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var range = graph.Blocks[blockIndex];
            foreach (var instruction in instructions)
            {
                if (instruction.Pc >= range.StartPc && instruction.Pc < range.EndPc)
                {
                    blockByPc[instruction.Pc] = blockIndex;
                }
            }
        }

        var worklist = new Queue<int>();
        for (var index = 0; index < blockCount; index++)
        {
            worklist.Enqueue(index);
        }

        var visits = new int[blockCount];
        const int visitLimit = 8;
        while (worklist.Count > 0)
        {
            var blockIndex = worklist.Dequeue();
            if (visits[blockIndex]++ > visitLimit)
            {
                continue;
            }

            var state = (IrScalarValue[])entry[blockIndex].Clone();
            if (graph.Predecessors[blockIndex].Count > 0)
            {
                state = NewState();
                var first = true;
                foreach (var predecessor in graph.Predecessors[blockIndex])
                {
                    var incoming = exit[predecessor];
                    for (var register = 0; register < ScalarRegisterCount; register++)
                    {
                        state[register] = first
                            ? incoming[register]
                            : state[register].Join(incoming[register]);
                    }

                    first = false;
                }

                if (blockIndex == 0)
                {
                    for (var register = 0; register < userData.Count && register < ScalarRegisterCount; register++)
                    {
                        state[register] = state[register].Join(
                            IrScalarValue.FromConstant(userData[register]));
                    }
                }
            }

            entry[blockIndex] = state;

            var definitions = NewDefinitions();
            if (graph.Predecessors[blockIndex].Count == 0)
            {
                for (var register = 0; register < userData.Count && register < ScalarRegisterCount; register++)
                {
                    definitions[register] = IrReachingDefinition.At(uint.MaxValue);
                }
            }
            else
            {
                var first = true;
                foreach (var predecessor in graph.Predecessors[blockIndex])
                {
                    var incoming = defExit[predecessor];
                    for (var register = 0; register < ScalarRegisterCount; register++)
                    {
                        definitions[register] = first
                            ? incoming[register]
                            : definitions[register].Join(incoming[register]);
                    }

                    first = false;
                }
            }

            defEntry[blockIndex] = definitions;

            var computed = Transfer(instructions, graph.Blocks[blockIndex], state);
            var computedDefinitions = TransferDefinitions(
                instructions,
                graph.Blocks[blockIndex],
                definitions);
            var changed = !SameState(exit[blockIndex], computed) ||
                !SameDefinitions(defExit[blockIndex], computedDefinitions);
            exit[blockIndex] = computed;
            defExit[blockIndex] = computedDefinitions;
            if (changed)
            {
                foreach (var successor in graph.Successors[blockIndex])
                {
                    worklist.Enqueue(successor);
                }
            }
        }

        return new Gen5ScalarSsa(graph, entry, exit, defEntry, blockByPc, instructions);
    }

    public IrScalarValue GetScalarAt(uint pc, uint register)
    {
        if (register >= ScalarRegisterCount || !_blockByPc.TryGetValue(pc, out var blockIndex))
        {
            return IrScalarValue.Unknown;
        }

        var state = (IrScalarValue[])_entryState[blockIndex].Clone();
        var range = _graphRange(blockIndex);
        foreach (var instruction in _instructions)
        {
            if (instruction.Pc < range.StartPc || instruction.Pc >= range.EndPc)
            {
                continue;
            }

            if (instruction.Pc >= pc)
            {
                break;
            }

            Apply(instruction, state);
        }

        return state[register];
    }

    public IrReachingDefinition GetReachingDefinitionAt(uint pc, uint register)
    {
        if (register >= ScalarRegisterCount || !_blockByPc.TryGetValue(pc, out var blockIndex))
        {
            return IrReachingDefinition.None;
        }

        var definitions = (IrReachingDefinition[])_entryDefinitions[blockIndex].Clone();
        var range = _graphRange(blockIndex);
        foreach (var instruction in _instructions)
        {
            if (instruction.Pc < range.StartPc || instruction.Pc >= range.EndPc)
            {
                continue;
            }

            if (instruction.Pc >= pc)
            {
                break;
            }

            ApplyDefinitions(instruction, definitions);
        }

        return definitions[register];
    }

    public bool IsInsideDivergentMerge(uint pc) =>
        _blockByPc.TryGetValue(pc, out var blockIndex) &&
        _graphPredecessorCount(blockIndex) > 1;

    private IrBlockRange _graphRange(int blockIndex) => Graph.Blocks[blockIndex];

    private int _graphPredecessorCount(int blockIndex) => Graph.Predecessors[blockIndex].Count;

    private static IrScalarValue[] NewState()
    {
        var state = new IrScalarValue[ScalarRegisterCount];
        for (var index = 0; index < state.Length; index++)
        {
            state[index] = IrScalarValue.Unknown;
        }

        return state;
    }

    private static IrReachingDefinition[] NewDefinitions()
    {
        var definitions = new IrReachingDefinition[ScalarRegisterCount];
        for (var index = 0; index < definitions.Length; index++)
        {
            definitions[index] = IrReachingDefinition.None;
        }

        return definitions;
    }

    private static bool SameDefinitions(IrReachingDefinition[] left, IrReachingDefinition[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static IrReachingDefinition[] TransferDefinitions(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IrBlockRange range,
        IrReachingDefinition[] entry)
    {
        var definitions = (IrReachingDefinition[])entry.Clone();
        foreach (var instruction in instructions)
        {
            if (instruction.Pc < range.StartPc || instruction.Pc >= range.EndPc)
            {
                continue;
            }

            ApplyDefinitions(instruction, definitions);
        }

        return definitions;
    }

    public const uint VccLo = 106;

    public const uint VccHi = 107;

    /// <summary>
    /// VOPC compares and the VOP2 carry forms write VCC without naming it: the ISA
    /// makes the destination implicit in the encoding, so the decoded instruction
    /// carries no destination operand for it. Modelling that here (rather than in
    /// the shared decoder) keeps the linear evaluator's behaviour untouched while
    /// letting the dataflow see that VCC was written.
    /// </summary>
    public static bool WritesVccImplicitly(Gen5ShaderInstruction instruction)
    {
        // An SDWA or VOP3 compare instruction can use an SGPR pair as its
        // destination. For an SGPR destination, write the comparison mask to the
        // specified pair, not to VCC. If all VOPC instructions write to VCC, a later
        // control-flow join can incorrectly identify s106:s107 offsets as vector
        // values.
        if (instruction.Destinations.Any(static destination =>
                destination.Kind == Gen5OperandKind.ScalarRegister))
        {
            return false;
        }

        if (instruction.Encoding == Gen5ShaderEncoding.Vopc)
        {
            return true;
        }

        return instruction.Encoding == Gen5ShaderEncoding.Vop2 &&
            instruction.Opcode is
                "VAddCoCiU32" or
                "VSubCoCiU32" or
                "VSubrevCoCiU32";
    }

    private static void ApplyDefinitions(
        Gen5ShaderInstruction instruction,
        IrReachingDefinition[] definitions)
    {
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.ScalarRegister ||
                destination.Value >= ScalarRegisterCount)
            {
                continue;
            }

            definitions[destination.Value] = IrReachingDefinition.At(instruction.Pc);
        }

        if (WritesVccImplicitly(instruction))
        {
            definitions[VccLo] = IrReachingDefinition.At(instruction.Pc);
            definitions[VccHi] = IrReachingDefinition.At(instruction.Pc);
        }
    }

    private static bool SameState(IrScalarValue[] left, IrScalarValue[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static IrScalarValue[] Transfer(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IrBlockRange range,
        IrScalarValue[] entry)
    {
        var state = (IrScalarValue[])entry.Clone();
        foreach (var instruction in instructions)
        {
            if (instruction.Pc < range.StartPc || instruction.Pc >= range.EndPc)
            {
                continue;
            }

            Apply(instruction, state);
        }

        return state;
    }

    private static void Apply(Gen5ShaderInstruction instruction, IrScalarValue[] state)
    {
        var resolved = ResolveResult(instruction, state);
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.ScalarRegister ||
                destination.Value >= ScalarRegisterCount)
            {
                continue;
            }

            state[destination.Value] = resolved;
        }
    }

    private static IrScalarValue ResolveResult(
        Gen5ShaderInstruction instruction,
        IrScalarValue[] state)
    {
        if (instruction.Destinations.Count != 1)
        {
            return IrScalarValue.Unknown;
        }

        return instruction.Opcode switch
        {
            "SMov" or "SMovB32" => Source(instruction, state, 0),
            _ => IrScalarValue.Unknown,
        };
    }

    private static IrScalarValue Source(
        Gen5ShaderInstruction instruction,
        IrScalarValue[] state,
        int index)
    {
        if (index >= instruction.Sources.Count)
        {
            return IrScalarValue.Unknown;
        }

        var source = instruction.Sources[index];
        return source.Kind switch
        {
            Gen5OperandKind.ScalarRegister when source.Value < ScalarRegisterCount =>
                state[source.Value],
            Gen5OperandKind.LiteralConstant => IrScalarValue.FromConstant(source.Value),
            _ => IrScalarValue.Unknown,
        };
    }
}
