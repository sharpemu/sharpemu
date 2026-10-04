// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

// The graph values one memory access reads its descriptor from: the handles, the read
// node of a scalar load, and the uniform vector offset of a global access.
// Active is the EXEC mask the access runs under: a lane outside it performs no access,
// so a lane-masked register write under the same mask contributes only its new value.
public sealed record MemoryAccessBinding(ScalarValue? Handle, ScalarValue? SamplerHandle, ScalarValue? Read, ScalarValue? Offset = null,
    ScalarValue? Active = null);

// A V_READLANE whose fixed VGPR lane is defined on every incoming control-flow
// path. The emitter can restore this value through a branch-local shadow instead
// of broadcasting from a host subgroup lane which may be inactive.
public readonly record struct FixedLaneReadBinding(uint Register, uint Lane);

// A V_WRITELANE with a scalar value source and a selector proven constant by
// the value graph. Recording writes as well as reads keeps shadow updates and
// shadow consumers on exactly the same control-flow proof.
public readonly record struct FixedLaneWriteBinding(uint Register, uint Lane);

// The uniform value graph of one program: every value a descriptor can be assembled
// from, symbolic in user data and in the shader base.
public sealed partial class ScalarValueGraph
{
    private readonly Dictionary<int, int> _interned = [];
    private readonly List<InternedValue> _internedValues = [];
    private readonly record struct InternedValue(ScalarValue Value, ulong Payload, ulong Identity, int Next);
    private readonly List<ScalarValue> _values = [];

    private ScalarValueGraph(Gen5ShaderProgram program, IrControlFlowGraph controlFlow, MemoryAccessTable memory, uint userDataBase, uint userDataCount,
        uint waveSize)
    {
        Program = program;
        WaveSize = waveSize;
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
    // A wave32 lane mask fills one SGPR; wave64 fills an aligned pair.
    public uint WaveSize { get; }

    // Index-aligned with Memory.Entries; null when the access has no descriptor value.
    public MemoryAccessBinding?[] Accesses { get; private set; } = [];

    // Guest instruction PC -> fixed VGPR lane proven to reach that read on all
    // paths. Populated by the final value-graph recording pass.
    internal Dictionary<uint, FixedLaneReadBinding> MutableFixedLaneReads { get; } = [];
    public IReadOnlyDictionary<uint, FixedLaneReadBinding> FixedLaneReads =>
        MutableFixedLaneReads;
    internal Dictionary<uint, FixedLaneWriteBinding> MutableFixedLaneWrites { get; } = [];
    public IReadOnlyDictionary<uint, FixedLaneWriteBinding> FixedLaneWrites =>
        MutableFixedLaneWrites;

    public IReadOnlyList<ScalarValue> Values => _values;

    internal Dictionary<uint, ScalarValue> BranchConditions { get; } = [];

    public bool Equivalent(ScalarValue left, ScalarValue right) => ScalarValueEquivalence.Equivalent(Memory, left, right);

    // The graph is complete once built, so a phi's invariant value never changes; the
    // resource evaluator asks for it on every draw.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ScalarValue, ScalarValue?> _invariantPhis = new();

    public ScalarValue? ResolveInvariantPhi(ScalarValue value) =>
        value.Kind != ScalarValueKind.Phi
            ? value
            : _invariantPhis.GetOrAdd(value, static (phi, memory) => ScalarValueEquivalence.ResolveInvariantPhi(memory, phi), Memory);

    public static ScalarValueGraph Build(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads = null, uint waveSize = 64)
    {
        if (waveSize is not 32 and not 64)
        {
            throw new ArgumentOutOfRangeException(nameof(waveSize), waveSize, "Wave size must be 32 or 64.");
        }

        var controlFlow = IrControlFlowGraph.Build(program.Instructions, Gen5IrBranchResolver.Instance);
        var graph = new ScalarValueGraph(program, controlFlow, MemoryAccessTable.Build(program, fixedFunctionVertexLoads), userDataBase, userDataCount,
            waveSize);
        new Builder(graph).Run();
        return graph;
    }

    // Node creation. Equal structure yields the same node, and operations over
    // constants fold, so revisiting a block reproduces its values exactly.
    internal ScalarValue Constant(uint value) => Intern(ScalarValueKind.Constant, ScalarValueType.U32, value);

    internal ScalarValue MemoryAperture(uint operand) => Intern(ScalarValueKind.MemoryAperture, ScalarValueType.U32, operand);

    internal ScalarValue Constant(ulong value) => Intern(ScalarValueKind.Constant, ScalarValueType.U64, value);

    internal ScalarValue Constant(bool value) => Intern(ScalarValueKind.Constant, ScalarValueType.Bool, value ? 1ul : 0ul);

    /// <summary>
    /// The instruction the builder is translating, for diagnostics only.
    /// </summary>
    /// <remarks>
    /// Undefined nodes are interned per type, so a single node stands for every
    /// instruction the builder could not model and carries no origin of its own.
    /// A resource plan that fails on an undefined dword therefore names no
    /// culprit, and the builder has about twenty places that produce one. This
    /// records which instructions actually gave up, so the failure points at an
    /// opcode instead of a list of candidates.
    /// </remarks>
    internal (uint Pc, string Opcode) BuilderInstruction { get; set; }

    private readonly Dictionary<ScalarValue, (uint Pc, string Opcode)> _undefinedOrigins = [];

    /// <summary>
    /// The instruction an undefined value came from, when origins are tracked.
    /// </summary>
    internal bool TryGetUndefinedOrigin(ScalarValue value, out (uint Pc, string Opcode) origin) =>
        _undefinedOrigins.TryGetValue(value, out origin);

    // Undefined nodes are interned per instruction. Revisiting a block reaches
    // the same instruction, so graph values stay stable while resource planning
    // can distinguish a known safe fallback from an unrelated malformed input.
    internal ScalarValue Undefined(ScalarValueType type)
    {
        if (BuilderInstruction.Opcode is null)
        {
            return Intern(ScalarValueKind.Undefined, type);
        }

        var instruction = BuilderInstruction;
        var value = Intern(ScalarValueKind.Undefined, type, identity: (1ul << 32) | instruction.Pc);
        _undefinedOrigins.TryAdd(value, instruction);
        return value;
    }

    internal ScalarValue UserData(uint register) => Intern(ScalarValueKind.UserData, ScalarValueType.U32, register);

    internal ScalarValue ShaderBase() => Intern(ScalarValueKind.ShaderBase, ScalarValueType.U64);

    // Keep the finite result range even when the input cannot be resolved on the host.
    internal ScalarValue FindLowestSetBit(ScalarValue value, uint instructionAddress)
    {
        if (value.IsConstant)
            return Operation(ScalarOperation.FindLowestBit32, ScalarValueType.U32, value);

        // Keep each instruction separate because its input guard can differ from another scan's guard.
        var result = Intern(ScalarValueKind.Operation, ScalarValueType.U32, operation: ScalarOperation.FindLowestBit32,
            operands: [value], identity: (1ul << 32) | instructionAddress);
        _bitScanInstructions[result] = instructionAddress;
        return result;
    }

    internal ScalarValue ResourceTableWord(uint slot) => Intern(ScalarValueKind.ResourceTableWord, ScalarValueType.U32, slot);

    internal ScalarValue Handle(ScalarValueKind kind, params ReadOnlySpan<ScalarValue> dwords) =>
        Intern(kind, ScalarValueType.U32, operands: dwords);

    internal ScalarValue MemoryRead(ScalarValueKind kind, ScalarValue handle, ScalarValue offset, int memoryIndex) =>
        Intern(kind, ScalarValueType.U32, unchecked((ulong)memoryIndex), operands: [handle, offset]);

    internal ScalarValue FirstLane(ScalarValue value, ScalarValue activeMask, uint instructionAddress) =>
        Intern(ScalarValueKind.FirstLane, ScalarValueType.U32, instructionAddress, operands: [value, activeMask]);

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

        return Intern(ScalarValueKind.Select, whenTrue.Type, operands: [condition, whenTrue, whenFalse]);
    }

    internal ScalarValue Operation(ScalarOperation operation, ScalarValueType type, params ReadOnlySpan<ScalarValue> operands)
    {
        // Some ALU identities produce a deterministic value even when the other
        // operand has no tracked provenance. This matters for descriptor setup code
        // that masks an unused or hardware-defined register before using it. Keep
        // these identities ahead of the general undefined propagation below.
        if (TryFoldWithUndefined(operation, type, operands, out var undefinedFolded))
        {
            return undefinedFolded;
        }

        foreach (var operand in operands)
        {
            if (operand.IsUndefined)
            {
                // Keep the operation shape when one input lacks provenance. The
                // runtime validator will still reject the undefined leaf, but
                // preserving the node lets later symbolic identities eliminate
                // only the bits that are provably independent of that input and
                // keeps diagnostics attached to the real instruction graph.
                return Intern(ScalarValueKind.Operation, type, operation: operation, operands: operands);
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

        return Intern(ScalarValueKind.Operation, type, operation: operation, operands: operands);
    }

    private bool TryFoldWithUndefined(ScalarOperation operation, ScalarValueType type, ReadOnlySpan<ScalarValue> operands, out ScalarValue folded)
    {
        folded = null!;

        if (operands.Length != 2)
        {
            return false;
        }

        if (operation == ScalarOperation.And32 &&
            ((operands[0].IsConstant && operands[0].Type == ScalarValueType.U32 && operands[0].ConstantU32 == 0) ||
             (operands[1].IsConstant && operands[1].Type == ScalarValueType.U32 && operands[1].ConstantU32 == 0)))
        {
            folded = Constant(0u);
            return true;
        }

        if (operation == ScalarOperation.And64 &&
            ((operands[0].IsConstant && operands[0].Type == ScalarValueType.U64 && operands[0].ConstantU64 == 0) ||
             (operands[1].IsConstant && operands[1].Type == ScalarValueType.U64 && operands[1].ConstantU64 == 0)))
        {
            folded = Constant(0ul);
            return true;
        }

        if (operation == ScalarOperation.Or32 &&
            ((operands[0].IsConstant && operands[0].Type == ScalarValueType.U32 && operands[0].ConstantU32 == uint.MaxValue) ||
             (operands[1].IsConstant && operands[1].Type == ScalarValueType.U32 && operands[1].ConstantU32 == uint.MaxValue)))
        {
            folded = Constant(uint.MaxValue);
            return true;
        }

        if (operation == ScalarOperation.LogicalAnd &&
            ((operands[0].IsConstant && operands[0].Type == ScalarValueType.Bool && !operands[0].ConstantBool) ||
             (operands[1].IsConstant && operands[1].Type == ScalarValueType.Bool && !operands[1].ConstantBool)))
        {
            folded = Constant(false);
            return true;
        }

        if (operation == ScalarOperation.LogicalOr &&
            ((operands[0].IsConstant && operands[0].Type == ScalarValueType.Bool && operands[0].ConstantBool) ||
             (operands[1].IsConstant && operands[1].Type == ScalarValueType.Bool && operands[1].ConstantBool)))
        {
            folded = Constant(true);
            return true;
        }

        return false;
    }

    private bool TryFold(ScalarOperation operation, ScalarValueType type, ReadOnlySpan<ScalarValue> operands, out ScalarValue folded)
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

    // Hashes select a chain only. Full metadata and operand identity comparisons
    // preserve correctness on collisions and avoid allocating anything on a hit.
    // Keep the original payload in the entry: planning can retarget a memory node.
    private ScalarValue Intern(ScalarValueKind kind, ScalarValueType type, ulong payload = 0,
        ScalarOperation operation = ScalarOperation.None, ReadOnlySpan<ScalarValue> operands = default, ulong identity = 0)
    {
        var key = InternHash(kind, type, payload, operation, operands, identity);
        var head = _interned.TryGetValue(key, out var first) ? first : -1;
        for (var index = head; index >= 0; index = _internedValues[index].Next)
        {
            var entry = _internedValues[index];
            var value = entry.Value;
            if (value.Kind == kind && value.Type == type && value.Operation == operation &&
                entry.Payload == payload && entry.Identity == identity &&
                operands.SequenceEqual(value.Operands))
                return value;
        }

        var created = ScalarValue.CreateInterned(kind, type, operation, payload, operands.ToArray());
        _interned[key] = _internedValues.Count;
        _internedValues.Add(new(created, payload, identity, head));
        _values.Add(created);
        return created;
    }

    private static int InternHash(ScalarValueKind kind, ScalarValueType type, ulong payload,
        ScalarOperation operation, ReadOnlySpan<ScalarValue> operands, ulong identity)
    {
        var hash = new HashCode();
        hash.Add(kind);
        hash.Add(type);
        hash.Add(payload);
        hash.Add(operation);
        hash.Add(identity);
        foreach (var operand in operands) hash.Add(operand.Id);
        return hash.ToHashCode();
    }

    private ScalarValue Track(ScalarValue value)
    {
        _values.Add(value);
        return value;
    }
}
