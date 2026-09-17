// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// The width of a graph value: a lane mask bit, one dword, or a dword pair.
public enum ScalarValueType : byte
{
    Bool,
    U32,
    U64,
}

// The node kinds of the uniform value graph. Leaves have no operands.
public enum ScalarValueKind : byte
{
    Constant,
    Undefined,
    UserData,
    ShaderBase,
    Phi,
    Operation,
    Select,
    FirstLane,
    BufferHandle,
    AddressHandle,
    ImageHandle,
    SamplerHandle,
    ScalarAddressWord,
    ScalarBufferWord,
    ResourceTableWord,
}

// Operations a value node can apply to its operands. The validator accepts only the
// uniform subset; every other operation still builds so a rejection names it.
public enum ScalarOperation : byte
{
    None,
    ConvertU32F32,
    ConvertF32U32,
    Construct64,
    Extract64,
    BitFieldInsert,
    BitFieldUExtract,
    BitFieldSExtract,
    IAdd32,
    IAdd64,
    AddCarry32,
    ISub32,
    ISub64,
    IMul32,
    IMul64,
    UMin32,
    ShiftLeft32,
    ShiftLeft64,
    ShiftRightLogical32,
    ShiftRightLogical64,
    ShiftRightArithmetic32,
    ShiftRightArithmetic64,
    And32,
    And64,
    Or32,
    Xor32,
    Not32,
    ULessThan32,
    IEqual32,
    UGreaterThan32,
    INotEqual32,
    LogicalOr,
    LogicalAnd,
    LogicalXor,
    LogicalNot,
    FLessThanEqual,
    FGreaterThanEqual,
    FIsNan,
    FMul,
    FTrunc,
    UMax32,
    SMin32,
    SMax32,
    IAbs32,
    UMulHi32,
    SMulHi32,
    BitCount32,
    BitReverse32,
    FindLowestBit32,
    FindHighestBit32,
    ULessThanEqual32,
    UGreaterThanEqual32,
    SLessThan32,
    SLessThanEqual32,
    SGreaterThan32,
    SGreaterThanEqual32,
    IEqual64,
    INotEqual64,
    QuadMask32,
    FAdd,
    FSub,
    FMin,
    FMax,
    FLessThan,
    FGreaterThan,
    FEqual,
    FNotEqual,
    ConvertS32F32,
    ConvertF32S32,
}

// One node of the uniform value graph. Nodes are immutable after the graph is built;
// a phi's operands are filled in during construction and sealed afterwards.
public sealed class ScalarValue
{
    private static int _nextId;

    private ScalarValue(ScalarValueKind kind, ScalarValueType type, ScalarOperation operation, ScalarValue[] operands)
    {
        Id = System.Threading.Interlocked.Increment(ref _nextId);
        Kind = kind;
        Type = type;
        Operation = operation;
        Operands = operands;
    }

    public int Id { get; }
    public ScalarValueKind Kind { get; }
    public ScalarValueType Type { get; }
    public ScalarOperation Operation { get; }
    public ScalarValue[] Operands { get; private set; }

    // Constant bits, user-data register, phi block, instruction address, or memory-table index.
    public ulong Payload { get; private set; }

    // The block that owns a phi and the predecessor blocks its operands come from.
    public int PhiBlock => (int)Payload;
    public int[] PhiPredecessors { get; private set; } = [];

    public bool IsConstant => Kind == ScalarValueKind.Constant;
    public bool IsUndefined => Kind == ScalarValueKind.Undefined;
    public uint ConstantU32 => (uint)Payload;
    public ulong ConstantU64 => Payload;
    public bool ConstantBool => Payload != 0;
    public uint UserDataRegister => (uint)Payload;
    public int MemoryIndex => (int)Payload;

    public static ScalarValue ConstantOf(uint value) =>
        new(ScalarValueKind.Constant, ScalarValueType.U32, ScalarOperation.None, []) { Payload = value };

    public static ScalarValue ConstantOf(ulong value) =>
        new(ScalarValueKind.Constant, ScalarValueType.U64, ScalarOperation.None, []) { Payload = value };

    public static ScalarValue ConstantOf(bool value) =>
        new(ScalarValueKind.Constant, ScalarValueType.Bool, ScalarOperation.None, []) { Payload = value ? 1u : 0u };

    public static ScalarValue Undefined(ScalarValueType type) =>
        new(ScalarValueKind.Undefined, type, ScalarOperation.None, []);

    public static ScalarValue UserData(uint register) =>
        new(ScalarValueKind.UserData, ScalarValueType.U32, ScalarOperation.None, []) { Payload = register };

    public static ScalarValue ShaderBase() =>
        new(ScalarValueKind.ShaderBase, ScalarValueType.U64, ScalarOperation.None, []);

    public static ScalarValue MakeOperation(ScalarOperation operation, ScalarValueType type, params ScalarValue[] operands) =>
        new(ScalarValueKind.Operation, type, operation, operands);

    public static ScalarValue Select(ScalarValue condition, ScalarValue whenTrue, ScalarValue whenFalse) =>
        new(ScalarValueKind.Select, whenTrue.Type, ScalarOperation.None, [condition, whenTrue, whenFalse]);

    public static ScalarValue FirstLane(ScalarValue value, ScalarValue activeMask, uint instructionAddress = 0) =>
        new(ScalarValueKind.FirstLane, ScalarValueType.U32, ScalarOperation.None, [value, activeMask]) { Payload = instructionAddress };

    public static ScalarValue Handle(ScalarValueKind kind, ScalarValue[] dwords) =>
        new(kind, ScalarValueType.U32, ScalarOperation.None, dwords);

    // A dword read through an address or buffer handle; the payload names the memory entry.
    public static ScalarValue MemoryRead(ScalarValueKind kind, ScalarValue handle, ScalarValue offset, int memoryIndex) =>
        new(kind, ScalarValueType.U32, ScalarOperation.None, [handle, offset]) { Payload = (ulong)memoryIndex };

    public static ScalarValue ResourceTableWord(uint slot) =>
        new(ScalarValueKind.ResourceTableWord, ScalarValueType.U32, ScalarOperation.None, []) { Payload = slot };

    public static ScalarValue Phi(int block, ScalarValueType type) =>
        new(ScalarValueKind.Phi, type, ScalarOperation.None, []) { Payload = (ulong)block };

    internal void SetPhiOperands(int[] predecessors, ScalarValue[] operands)
    {
        PhiPredecessors = predecessors;
        Operands = operands;
    }

    internal void RetargetMemoryIndex(int memoryIndex) => Payload = (ulong)memoryIndex;

    public override string ToString() => Kind switch
    {
        ScalarValueKind.Constant => Type == ScalarValueType.U64 ? $"0x{Payload:X}ul" : Type == ScalarValueType.Bool ? (Payload != 0 ? "true" : "false") : $"0x{(uint)Payload:X}",
        ScalarValueKind.UserData => $"UserData(s{Payload})",
        ScalarValueKind.Operation => $"{Operation}({string.Join(", ", Operands.Select(operand => operand.ToString()))})",
        ScalarValueKind.Phi => $"Phi#{Id}(block {PhiBlock})",
        _ => $"{Kind}#{Id}",
    };
}

public static class ScalarValueEquivalence
{
    // Structural equality: same kind, type, operation, payload and operands; phis must
    // also draw each operand from the same predecessor block.
    public static bool Equivalent(MemoryAccessTable memory, ScalarValue left, ScalarValue right)
    {
        var visited = new List<(ScalarValue, ScalarValue)>();
        return Equivalent(memory, left, right, visited);
    }

    private static bool Equivalent(MemoryAccessTable memory, ScalarValue left, ScalarValue right, List<(ScalarValue, ScalarValue)> visited)
    {
        // An undefined value is never equivalent, not even to itself.
        if (left.IsUndefined || right.IsUndefined)
        {
            return false;
        }

        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Kind != right.Kind || left.Type != right.Type || left.Operation != right.Operation ||
            left.Operands.Length != right.Operands.Length)
        {
            return false;
        }

        if (left.Kind is ScalarValueKind.Constant or ScalarValueKind.UserData or ScalarValueKind.ResourceTableWord)
        {
            return left.Payload == right.Payload;
        }

        if (left.Kind is ScalarValueKind.ShaderBase)
        {
            return true;
        }

        if (visited.Any(pair => ReferenceEquals(pair.Item1, left) && ReferenceEquals(pair.Item2, right)))
        {
            return true;
        }

        visited.Add((left, right));
        if (left.Kind == ScalarValueKind.FirstLane && left.Payload != right.Payload)
        {
            return false;
        }

        if (left.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
        {
            // Memory reads compare their access records, not their table index.
            if (left.MemoryIndex >= memory.Count || right.MemoryIndex >= memory.Count ||
                !memory[left.MemoryIndex].SameAccess(memory[right.MemoryIndex]))
            {
                return false;
            }
        }
        else if (left.Kind == ScalarValueKind.Phi)
        {
            if (left.PhiBlock != right.PhiBlock || !left.PhiPredecessors.SequenceEqual(right.PhiPredecessors))
            {
                return false;
            }
        }

        for (var index = 0; index < left.Operands.Length; index++)
        {
            if (!Equivalent(memory, left.Operands[index], right.Operands[index], visited))
            {
                return false;
            }
        }

        return true;
    }

    // A phi whose non-phi operands are all equivalent resolves to that value; a phi that
    // merges different values resolves to null.
    public static ScalarValue? ResolveInvariantPhi(MemoryAccessTable memory, ScalarValue value)
    {
        if (value.Kind != ScalarValueKind.Phi)
        {
            return value;
        }

        ScalarValue? invariant = null;
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        pending.Push(value);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (current.Kind == ScalarValueKind.Phi)
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                foreach (var operand in current.Operands)
                {
                    pending.Push(operand);
                }

                continue;
            }

            if (invariant is null)
            {
                invariant = current;
            }
            else if (!Equivalent(memory, invariant, current))
            {
                return null;
            }
        }

        return invariant;
    }
}
