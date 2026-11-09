// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public sealed partial class SpirvModuleBuilder
{
    private const int MaxWordSplitDepth = 6;
    private const uint GlslModf = 35;
    private const uint GlslFrexp = 51;
    private const uint SubgroupScope = 3;
    private static readonly uint[] GlslMinMax = [37, 38, 39, 40, 41, 42, 79, 80];

    private readonly Dictionary<uint, uint> _resultTypes = [];
    private readonly Dictionary<uint, (uint Width, bool Signed)> _integerTypeInfo = [];
    private readonly Dictionary<uint, ulong> _wideConstantValues = [];
    private readonly Dictionary<uint, uint> _floatConstantBits = [];
    private readonly Dictionary<uint, Definition> _definitions = [];
    private readonly Dictionary<InstructionKey, uint> _blockValues = [];
    private readonly HashSet<uint> _localVariables = [];
    private readonly HashSet<uint> _escapedVariables = [];
    private readonly Dictionary<uint, uint> _elementBases = [];
    private readonly HashSet<uint> _scalarPointerTypes = [];
    private readonly HashSet<uint> _inputVariables = [];
    private readonly HashSet<uint> _changingInputs = [];
    private readonly Dictionary<uint, uint> _entryValues = [];
    private readonly Dictionary<InstructionKey, uint> _entryOperations = [];
    private readonly HashSet<uint> _entryIds = [];
    private readonly HashSet<uint> _laneIdInputs = [];
    private readonly HashSet<uint> _laneIdValues = [];
    private readonly Dictionary<uint, int> _predecessorCounts = [];
    private readonly HashSet<uint> _emittedLabels = [];
    private readonly Dictionary<uint, List<(uint Block, BlockState State)>> _incomingStates = [];
    private readonly Dictionary<uint, int> _inheritedLabels = [];
    private readonly HashSet<uint> _loopHeaders = [];
    private readonly HashSet<uint> _loopHeaderBlocks = [];
    private readonly HashSet<uint> _inheritedPointers = [];
    private uint _currentBlock;
    private bool _inheritedValueUsed;
    private bool _loopHeaderReadStaleValue;
    private readonly Dictionary<int, List<uint>> _entryPrologues = [];
    private bool _entryLabelPending;
    private int _entryLabelOffset = -1;
    private readonly Dictionary<uint, uint> _variableValues = [];
    private readonly Dictionary<uint, int> _pendingStores = [];
    private readonly HashSet<int> _removedInstructions = [];
    private readonly HashSet<(uint Target, SpirvDecoration Decoration)> _plainDecorations = [];
    private readonly HashSet<uint> _namedTargets = [];

    private readonly record struct Definition(SpirvOp Opcode, uint Type, uint[] Operands);

    private sealed record BlockState(Dictionary<uint, uint> Values, Dictionary<InstructionKey, uint> Expressions);

    private readonly struct InstructionKey : IEquatable<InstructionKey>
    {
        private readonly int _hash;

        public InstructionKey(SpirvOp opcode, uint type, uint[] operands)
        {
            Opcode = opcode;
            Type = type;
            Operands = operands;
            var hash = new HashCode();
            hash.Add(opcode);
            hash.Add(type);
            foreach (var operand in operands)
            {
                hash.Add(operand);
            }

            _hash = hash.ToHashCode();
        }

        public SpirvOp Opcode { get; }

        public uint Type { get; }

        public uint[] Operands { get; }

        public bool Equals(InstructionKey other) =>
            _hash == other._hash && Opcode == other.Opcode && Type == other.Type &&
            Operands.AsSpan().SequenceEqual(other.Operands);

        public override bool Equals(object? obj) => obj is InstructionKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    public static bool KeepsUnreadVariables { get; set; }

    public uint CurrentFunction { get; private set; }

    public bool InheritBlockState { get; init; } = true;

    public bool InheritedBlocksHeld => !_loopHeaderReadStaleValue &&
        _inheritedLabels.All(entry => _loopHeaderBlocks.Contains(entry.Key) ||
            _predecessorCounts.GetValueOrDefault(entry.Key) == entry.Value);

    public void DeclareLoopHeader(uint label) => _loopHeaders.Add(label);

    public uint AddInstruction(SpirvOp opcode, uint resultType, params uint[] operands)
    {
        if (opcode == SpirvOp.Load)
        {
            return AddLoad(resultType, operands);
        }

        if (opcode == SpirvOp.AccessChain && TryAddElementPointer(resultType, operands, out var element))
        {
            return element;
        }

        NoteVariableUses(operands);
        if (TryFold(opcode, resultType, operands, out var folded) ||
            TrySimplify(opcode, resultType, operands, out folded))
        {
            return folded;
        }

        if (!IsValueOperation(opcode, operands))
        {
            var effect = EmitResult(_functions, opcode, resultType, operands);
            if (opcode == SpirvOp.FunctionCall)
            {
                ResetMemoryState();
            }

            return effect;
        }

        if (CanHoistToEntry(opcode, operands))
        {
            return EntryOperation(opcode, resultType, operands);
        }

        var lookup = new InstructionKey(opcode, resultType, operands);
        if (_blockValues.TryGetValue(lookup, out var existing))
        {
            return existing;
        }

        var result = EmitResult(_functions, opcode, resultType, operands);
        var key = new InstructionKey(opcode, resultType, operands.ToArray());
        _blockValues[key] = result;
        _definitions[result] = new Definition(opcode, resultType, key.Operands);
        return result;
    }

    public void AddStatement(SpirvOp opcode, params uint[] operands)
    {
        if (opcode == SpirvOp.Store)
        {
            AddStore(operands);
            return;
        }

        NoteVariableUses(operands);
        if (opcode == SpirvOp.LoopMerge)
        {
            LeaveLoopHeader();
        }

        Emit(_functions, opcode, operands);
        if (opcode is SpirvOp.Branch or SpirvOp.BranchConditional or SpirvOp.Switch)
        {
            NoteBranchTargets(opcode, operands);
            ResetBlockState();
        }
        else if (opcode is SpirvOp.Return or SpirvOp.ReturnValue or SpirvOp.Kill or SpirvOp.Unreachable)
        {
            ResetBlockState();
        }
        else if (opcode == SpirvOp.FunctionCall)
        {
            ResetMemoryState();
        }
    }

    private bool TryAddElementPointer(uint resultType, uint[] operands, out uint element)
    {
        element = 0;
        if (operands.Length < 2 || !IsTrackedVariable(operands[0]) || !_scalarPointerTypes.Contains(resultType))
        {
            return false;
        }

        for (var index = 1; index < operands.Length; index++)
        {
            if (!IsIntegerConstant(operands[index], out _))
            {
                return false;
            }
        }

        var lookup = new InstructionKey(SpirvOp.AccessChain, resultType, operands);
        if (!_blockValues.TryGetValue(lookup, out element))
        {
            element = EmitResult(_functions, SpirvOp.AccessChain, resultType, operands);
            var key = new InstructionKey(SpirvOp.AccessChain, resultType, operands.ToArray());
            _blockValues[key] = element;
            _definitions[element] = new Definition(SpirvOp.AccessChain, resultType, key.Operands);
        }

        _elementBases[element] = operands[0];
        return true;
    }

    private uint AddLoad(uint resultType, uint[] operands)
    {
        var pointer = operands[0];
        if (operands.Length == 1 && _inputVariables.Contains(pointer) && !_changingInputs.Contains(pointer) &&
            IsEntryAvailable())
        {
            return EntryLoad(resultType, pointer);
        }

        if (operands.Length != 1 || !IsTrackedPointer(pointer))
        {
            return EmitResult(_functions, SpirvOp.Load, resultType, operands);
        }

        if (_variableValues.TryGetValue(pointer, out var known) && TypeOf(known) == resultType)
        {
            if (_inheritedPointers.Contains(pointer))
            {
                _inheritedValueUsed = true;
            }

            return known;
        }

        var result = EmitResult(_functions, SpirvOp.Load, resultType, operands);
        _inheritedPointers.Remove(pointer);
        _pendingStores.Remove(pointer);
        if (_localVariables.Contains(pointer))
        {
            ForgetElements(pointer, keepValues: true);
        }

        _variableValues[pointer] = result;
        return result;
    }

    private void AddStore(uint[] operands)
    {
        var pointer = operands[0];
        if (operands.Length != 2 || !IsTrackedPointer(pointer))
        {
            if (_localVariables.Contains(pointer))
            {
                _variableValues.Remove(pointer);
                _pendingStores.Remove(pointer);
                ForgetElements(pointer, keepValues: false);
            }

            Emit(_functions, SpirvOp.Store, operands);
            return;
        }

        if (_elementBases.TryGetValue(pointer, out var elementBase))
        {
            _variableValues.Remove(elementBase);
            _pendingStores.Remove(elementBase);
        }
        else
        {
            ForgetElements(pointer, keepValues: false);
        }

        var value = operands[1];
        if (_variableValues.TryGetValue(pointer, out var current) && current == value)
        {
            return;
        }

        if (_pendingStores.TryGetValue(pointer, out var previous))
        {
            _removedInstructions.Add(previous);
        }

        _pendingStores[pointer] = _functions.Count;
        Emit(_functions, SpirvOp.Store, operands);
        _variableValues[pointer] = value;
        _inheritedPointers.Remove(pointer);
    }

    private uint EntryLoad(uint resultType, uint pointer)
    {
        if (_entryValues.TryGetValue(pointer, out var known) && TypeOf(known) == resultType)
        {
            return known;
        }

        var result = AppendToEntry(SpirvOp.Load, resultType, [pointer]);
        _entryValues[pointer] = result;
        if (_laneIdInputs.Contains(pointer))
        {
            _laneIdValues.Add(result);
        }

        return result;
    }

    private bool IsEntryAvailable() => CurrentFunction != 0 && !_entryLabelPending && _entryLabelOffset >= 0;

    private bool CanHoistToEntry(SpirvOp opcode, uint[] operands)
    {
        if (!IsEntryAvailable() || opcode is SpirvOp.ExtInst or SpirvOp.CompositeExtract or SpirvOp.CompositeInsert or
                SpirvOp.VectorShuffle or SpirvOp.AccessChain or SpirvOp.PtrAccessChain or SpirvOp.ArrayLength or
                SpirvOp.ConvertUToPtr or SpirvOp.UDiv or SpirvOp.SDiv or SpirvOp.UMod or SpirvOp.SRem or SpirvOp.SMod ||
            IsSubgroupOperation(opcode))
        {
            return false;
        }

        var usesEntryValue = false;
        foreach (var operand in operands)
        {
            if (_entryIds.Contains(operand))
            {
                usesEntryValue = true;
            }
            else if (!_scalarConstantValues.ContainsKey(operand) && !_wideConstantValues.ContainsKey(operand) &&
                !_floatConstantBits.ContainsKey(operand))
            {
                return false;
            }
        }

        return usesEntryValue;
    }

    private uint EntryOperation(SpirvOp opcode, uint resultType, uint[] operands)
    {
        if (_entryOperations.TryGetValue(new InstructionKey(opcode, resultType, operands), out var known))
        {
            return known;
        }

        var stored = operands.ToArray();
        var result = AppendToEntry(opcode, resultType, stored);
        _entryOperations[new InstructionKey(opcode, resultType, stored)] = result;
        _definitions[result] = new Definition(opcode, resultType, stored);
        return result;
    }

    private uint AppendToEntry(SpirvOp opcode, uint resultType, uint[] operands)
    {
        if (!_entryPrologues.TryGetValue(_entryLabelOffset, out var prologue))
        {
            prologue = [];
            _entryPrologues.Add(_entryLabelOffset, prologue);
        }

        var result = AllocateId();
        prologue.Add(((uint)(operands.Length + 3) << 16) | (uint)opcode);
        prologue.Add(resultType);
        prologue.Add(result);
        prologue.AddRange(operands);
        NoteResultType(result, resultType);
        _entryIds.Add(result);
        return result;
    }

    private bool IsTrackedVariable(uint pointer) =>
        _localVariables.Contains(pointer) && !_escapedVariables.Contains(pointer);

    private bool IsTrackedPointer(uint pointer) =>
        IsTrackedVariable(pointer) || (_elementBases.TryGetValue(pointer, out var variable) && IsTrackedVariable(variable));

    private void NoteVariableUses(uint[] operands)
    {
        foreach (var operand in operands)
        {
            if (_localVariables.Contains(operand))
            {
                EscapeVariable(operand);
            }
            else if (_elementBases.TryGetValue(operand, out var variable))
            {
                EscapeVariable(variable);
            }
        }
    }

    private void EscapeVariable(uint variable)
    {
        if (_escapedVariables.Add(variable))
        {
            _variableValues.Remove(variable);
            _pendingStores.Remove(variable);
            ForgetElements(variable, keepValues: false);
        }
    }

    private void ForgetElements(uint variable, bool keepValues)
    {
        foreach (var (element, _) in _pendingStores)
        {
            if (_elementBases.TryGetValue(element, out var owner) && owner == variable)
            {
                _pendingStores.Remove(element);
            }
        }

        if (keepValues)
        {
            return;
        }

        foreach (var (element, _) in _variableValues)
        {
            if (_elementBases.TryGetValue(element, out var owner) && owner == variable)
            {
                _variableValues.Remove(element);
            }
        }
    }

    private void NoteBranchTargets(SpirvOp opcode, uint[] operands)
    {
        var targets = new HashSet<uint>();
        switch (opcode)
        {
            case SpirvOp.Branch:
                targets.Add(operands[0]);
                break;
            case SpirvOp.BranchConditional:
                targets.Add(operands[1]);
                targets.Add(operands[2]);
                break;
            default:
                targets.Add(operands[1]);
                for (var index = 3; index < operands.Length; index += 2)
                {
                    targets.Add(operands[index]);
                }

                break;
        }

        BlockState? state = null;
        foreach (var target in targets)
        {
            _predecessorCounts[target] = _predecessorCounts.GetValueOrDefault(target) + 1;
            if (!InheritBlockState || _emittedLabels.Contains(target))
            {
                continue;
            }

            state ??= new BlockState(new Dictionary<uint, uint>(_variableValues), new Dictionary<InstructionKey, uint>(_blockValues));
            if (!_incomingStates.TryGetValue(target, out var incoming))
            {
                incoming = [];
                _incomingStates.Add(target, incoming);
            }

            incoming.Add((_currentBlock, state));
        }
    }

    private void EnterBlock(uint label)
    {
        _emittedLabels.Add(label);
        _currentBlock = label;
        ResetBlockState();
        if (!_incomingStates.Remove(label, out var incoming) || incoming.Count != _predecessorCounts.GetValueOrDefault(label))
        {
            return;
        }

        var first = incoming[0].State;
        foreach (var (expression, value) in first.Expressions)
        {
            if (incoming.All(entry => entry.State.Expressions.TryGetValue(expression, out var other) && other == value))
            {
                _blockValues[expression] = value;
            }
        }

        if (!_loopHeaders.Contains(label))
        {
            foreach (var (pointer, value) in first.Values)
            {
                var merged = MergeIncomingValue(pointer, value, incoming);
                if (merged != 0)
                {
                    _variableValues[pointer] = merged;
                    _inheritedPointers.Add(pointer);
                }
            }
        }

        _inheritedLabels[label] = incoming.Count;
    }

    private uint MergeIncomingValue(uint pointer, uint value, List<(uint Block, BlockState State)> incoming)
    {
        var type = TypeOf(value);
        var same = true;
        foreach (var (_, state) in incoming)
        {
            if (!state.Values.TryGetValue(pointer, out var other) || TypeOf(other) != type)
            {
                return 0;
            }

            same &= other == value;
        }

        if (same)
        {
            return value;
        }

        if (type == 0)
        {
            return 0;
        }

        var phi = AllocateId();
        var words = new uint[2 + (incoming.Count * 2)];
        words[0] = type;
        words[1] = phi;
        for (var index = 0; index < incoming.Count; index++)
        {
            words[2 + (index * 2)] = incoming[index].State.Values[pointer];
            words[3 + (index * 2)] = incoming[index].Block;
        }

        Emit(_functions, SpirvOp.Phi, words);
        NoteResultType(phi, type);
        return phi;
    }

    private void LeaveLoopHeader()
    {
        _loopHeaderBlocks.Add(_currentBlock);
        if (_inheritedValueUsed)
        {
            _loopHeaderReadStaleValue = true;
        }

        foreach (var pointer in _inheritedPointers)
        {
            _variableValues.Remove(pointer);
        }

        _inheritedPointers.Clear();
    }

    private void ResetMemoryState()
    {
        _variableValues.Clear();
        _pendingStores.Clear();
        _inheritedPointers.Clear();
        _inheritedValueUsed = false;
    }

    private void ResetBlockState()
    {
        ResetMemoryState();
        _blockValues.Clear();
    }

    private void NoteResultType(uint id, uint type) => _resultTypes[id] = type;

    private uint TypeOf(uint id) => _resultTypes.GetValueOrDefault(id);

    private bool HasType(uint id, uint type) => _resultTypes.TryGetValue(id, out var actual) && actual == type;

    private uint IntegerWidth(uint type) => _integerTypeInfo.TryGetValue(type, out var info) ? info.Width : 0;

    private bool IsUnsignedInteger(uint type, uint width) =>
        _integerTypeInfo.TryGetValue(type, out var info) && info.Width == width && !info.Signed;

    private bool IsFloat32Type(uint type) => _floatTypes.TryGetValue(32, out var floatType) && floatType == type;

    private bool Defines(uint id, SpirvOp opcode, out Definition definition) =>
        _definitions.TryGetValue(id, out definition) && definition.Opcode == opcode;

    private bool IsIntegerConstant(uint id, out ulong value)
    {
        if (_wideConstantValues.TryGetValue(id, out value))
        {
            return true;
        }

        if (_scalarConstantValues.TryGetValue(id, out var narrow) && IntegerWidth(TypeOf(id)) == 32)
        {
            value = narrow;
            return true;
        }

        value = 0;
        return false;
    }

    private uint IntegerConstant(uint type, ulong value) =>
        IntegerWidth(type) == 64 ? Constant64(type, value) : Constant(type, (uint)value);

    private static bool IsValueOperation(SpirvOp opcode, uint[] operands) => opcode switch
    {
        SpirvOp.ExtInst => operands.Length >= 2 && operands[1] is not (GlslModf or GlslFrexp),
        SpirvOp.AccessChain or SpirvOp.PtrAccessChain or SpirvOp.ArrayLength or SpirvOp.VectorExtractDynamic or SpirvOp.VectorInsertDynamic or
            SpirvOp.VectorShuffle or SpirvOp.CompositeConstruct or SpirvOp.CompositeExtract or SpirvOp.CompositeInsert or
            SpirvOp.CopyObject or SpirvOp.ConvertFToU or SpirvOp.ConvertFToS or SpirvOp.ConvertSToF or SpirvOp.ConvertUToF or
            SpirvOp.ConvertUToPtr or SpirvOp.UConvert or SpirvOp.SConvert or SpirvOp.FConvert or SpirvOp.Bitcast or
            SpirvOp.SNegate or SpirvOp.FNegate or SpirvOp.IAdd or SpirvOp.FAdd or SpirvOp.ISub or SpirvOp.FSub or
            SpirvOp.IMul or SpirvOp.FMul or SpirvOp.UDiv or SpirvOp.SDiv or SpirvOp.FDiv or SpirvOp.UMod or SpirvOp.SRem or
            SpirvOp.SMod or SpirvOp.FRem or SpirvOp.FMod or SpirvOp.IAddCarry or SpirvOp.ISubBorrow or
            SpirvOp.UMulExtended or SpirvOp.SMulExtended or SpirvOp.Any or SpirvOp.All or SpirvOp.IsNan or SpirvOp.IsInf or
            SpirvOp.LogicalEqual or SpirvOp.LogicalNotEqual or SpirvOp.LogicalOr or SpirvOp.LogicalAnd or
            SpirvOp.LogicalNot or SpirvOp.Select or SpirvOp.IEqual or SpirvOp.INotEqual or SpirvOp.UGreaterThan or
            SpirvOp.SGreaterThan or SpirvOp.UGreaterThanEqual or SpirvOp.SGreaterThanEqual or SpirvOp.ULessThan or
            SpirvOp.SLessThan or SpirvOp.ULessThanEqual or SpirvOp.SLessThanEqual or SpirvOp.FOrdEqual or
            SpirvOp.FUnordEqual or SpirvOp.FOrdNotEqual or SpirvOp.FUnordNotEqual or SpirvOp.FOrdLessThan or
            SpirvOp.FUnordLessThan or SpirvOp.FOrdGreaterThan or SpirvOp.FUnordGreaterThan or
            SpirvOp.FOrdLessThanEqual or SpirvOp.FUnordLessThanEqual or SpirvOp.FOrdGreaterThanEqual or
            SpirvOp.FUnordGreaterThanEqual or SpirvOp.ShiftRightLogical or SpirvOp.ShiftRightArithmetic or
            SpirvOp.ShiftLeftLogical or SpirvOp.BitwiseOr or SpirvOp.BitwiseXor or SpirvOp.BitwiseAnd or SpirvOp.Not or
            SpirvOp.BitFieldInsert or SpirvOp.BitFieldSExtract or SpirvOp.BitFieldUExtract or SpirvOp.BitReverse or
            SpirvOp.BitCount => true,
        _ => IsSubgroupOperation(opcode),
    };

    private static bool IsSubgroupOperation(SpirvOp opcode) => opcode is
        SpirvOp.GroupNonUniformElect or SpirvOp.GroupNonUniformAll or SpirvOp.GroupNonUniformAny or
        SpirvOp.GroupNonUniformAllEqual or SpirvOp.GroupNonUniformBroadcast or SpirvOp.GroupNonUniformBroadcastFirst or
        SpirvOp.GroupNonUniformBallot or SpirvOp.GroupNonUniformBallotBitCount or SpirvOp.GroupNonUniformShuffle or
        SpirvOp.GroupNonUniformShuffleXor or SpirvOp.GroupNonUniformShuffleUp or SpirvOp.GroupNonUniformShuffleDown or
        SpirvOp.GroupNonUniformUMin;

    private bool TrySimplify(SpirvOp opcode, uint resultType, uint[] operands, out uint result)
    {
        result = 0;
        switch (opcode)
        {
            case SpirvOp.Select when operands.Length == 3:
                if (operands[1] == operands[2])
                {
                    result = operands[1];
                    return true;
                }

                if (Defines(operands[2], SpirvOp.Select, out var falseSelection) && falseSelection.Operands[0] == operands[0])
                {
                    result = AddInstruction(SpirvOp.Select, resultType, operands[0], operands[1], falseSelection.Operands[2]);
                    return true;
                }

                if (Defines(operands[1], SpirvOp.Select, out var trueSelection) && trueSelection.Operands[0] == operands[0])
                {
                    result = AddInstruction(SpirvOp.Select, resultType, operands[0], trueSelection.Operands[1], operands[2]);
                    return true;
                }

                if (IsBoolType(resultType) && IsBoolScalarConstant(operands[1], out var whenTrue) &&
                    IsBoolScalarConstant(operands[2], out var whenFalse) && whenTrue != whenFalse)
                {
                    result = whenTrue ? operands[0] : AddInstruction(SpirvOp.LogicalNot, resultType, operands[0]);
                    return true;
                }

                return false;
            case SpirvOp.LogicalNot when operands.Length == 1 && Defines(operands[0], SpirvOp.LogicalNot, out var negated):
                result = negated.Operands[0];
                return true;
            case SpirvOp.LogicalAnd or SpirvOp.LogicalOr when operands.Length == 2 && operands[0] == operands[1]:
                result = operands[0];
                return true;
            case SpirvOp.BitFieldUExtract or SpirvOp.BitFieldSExtract when operands.Length == 3 &&
                IsIntegerConstant(operands[1], out var fieldOffset) && fieldOffset == 0 &&
                IsIntegerConstant(operands[2], out var fieldCount) && fieldCount == IntegerWidth(resultType) &&
                HasType(operands[0], resultType):
                result = operands[0];
                return true;
            case SpirvOp.ExtInst when operands.Length == 4 && GlslMinMax.Contains(operands[1]) &&
                operands[2] == operands[3] && HasType(operands[2], resultType):
                result = operands[2];
                return true;
            case SpirvOp.IEqual or SpirvOp.INotEqual when operands.Length == 2:
                return TrySimplifyZeroTest(opcode == SpirvOp.INotEqual, resultType, operands, out result);
            case SpirvOp.Bitcast when operands.Length == 1:
                return TrySimplifyBitcast(resultType, operands[0], out result);
            case SpirvOp.UConvert when operands.Length == 1:
                return TrySimplifyUConvert(resultType, operands[0], out result);
            case SpirvOp.ShiftRightLogical when operands.Length == 2 && IsUnsignedInteger(resultType, 64) &&
                IsIntegerConstant(operands[1], out var amount) && amount == 32 &&
                _integerTypes.TryGetValue((32, false), out var word) &&
                TrySplitHighWord(operands[0], word, 0, out var high):
                result = AddInstruction(SpirvOp.UConvert, resultType, high);
                return true;
        }

        return TrySimplifyIntegerIdentity(opcode, resultType, operands, out result);
    }

    private bool TrySimplifyIntegerIdentity(SpirvOp opcode, uint resultType, uint[] operands, out uint result)
    {
        result = 0;
        var width = IntegerWidth(resultType);
        if (operands.Length != 2 || width is not (32 or 64))
        {
            return false;
        }

        var allOnes = width == 64 ? ulong.MaxValue : uint.MaxValue;
        var leftConstant = IsIntegerConstant(operands[0], out var left);
        var rightConstant = IsIntegerConstant(operands[1], out var right);
        if (width == 64 && leftConstant && rightConstant && TryFoldWide(opcode, left, right, out var folded))
        {
            result = Constant64(resultType, folded);
            return true;
        }

        uint Keep(uint id) => HasType(id, resultType) ? id : 0;

        switch (opcode)
        {
            case SpirvOp.IAdd or SpirvOp.BitwiseOr or SpirvOp.BitwiseXor:
                if (rightConstant && right == 0)
                {
                    result = Keep(operands[0]);
                }
                else if (leftConstant && left == 0)
                {
                    result = Keep(operands[1]);
                }
                else if (opcode == SpirvOp.BitwiseOr && operands[0] == operands[1])
                {
                    result = Keep(operands[0]);
                }

                break;
            case SpirvOp.ISub or SpirvOp.ShiftLeftLogical or SpirvOp.ShiftRightLogical or SpirvOp.ShiftRightArithmetic:
                if (rightConstant && right == 0)
                {
                    result = Keep(operands[0]);
                }

                break;
            case SpirvOp.IMul:
                if (rightConstant && right == 1)
                {
                    result = Keep(operands[0]);
                }
                else if (leftConstant && left == 1)
                {
                    result = Keep(operands[1]);
                }

                break;
            case SpirvOp.BitwiseAnd:
                if ((rightConstant && right == 0) || (leftConstant && left == 0))
                {
                    result = IntegerConstant(resultType, 0);
                }
                else if (rightConstant && right == allOnes)
                {
                    result = Keep(operands[0]);
                }
                else if (leftConstant && left == allOnes)
                {
                    result = Keep(operands[1]);
                }
                else if (operands[0] == operands[1])
                {
                    result = Keep(operands[0]);
                }
                else if (rightConstant && Defines(operands[0], SpirvOp.BitwiseAnd, out var maskedAnd) &&
                    IsIntegerConstant(maskedAnd.Operands[1], out var innerMask))
                {
                    result = AddInstruction(SpirvOp.BitwiseAnd, resultType, maskedAnd.Operands[0],
                        IntegerConstant(resultType, innerMask & right));
                }
                else if (Defines(operands[0], SpirvOp.BitwiseAnd, out var leftAnd) && leftAnd.Operands.Contains(operands[1]))
                {
                    result = Keep(operands[0]);
                }
                else if (Defines(operands[1], SpirvOp.BitwiseAnd, out var rightAnd) && rightAnd.Operands.Contains(operands[0]))
                {
                    result = Keep(operands[1]);
                }

                break;
        }

        return result != 0;
    }

    private static bool TryFoldWide(SpirvOp opcode, ulong left, ulong right, out ulong result)
    {
        switch (opcode)
        {
            case SpirvOp.IAdd: result = left + right; return true;
            case SpirvOp.ISub: result = left - right; return true;
            case SpirvOp.IMul: result = left * right; return true;
            case SpirvOp.BitwiseAnd: result = left & right; return true;
            case SpirvOp.BitwiseOr: result = left | right; return true;
            case SpirvOp.BitwiseXor: result = left ^ right; return true;
            case SpirvOp.ShiftLeftLogical when right < 64: result = left << (int)right; return true;
            case SpirvOp.ShiftRightLogical when right < 64: result = left >> (int)right; return true;
            default: result = 0; return false;
        }
    }

    private bool TrySimplifyZeroTest(bool notEqual, uint resultType, uint[] operands, out uint result)
    {
        result = 0;
        uint tested;
        if (IsIntegerConstant(operands[1], out var right) && right == 0)
        {
            tested = operands[0];
        }
        else if (IsIntegerConstant(operands[0], out var left) && left == 0)
        {
            tested = operands[1];
        }
        else
        {
            return false;
        }

        if (Defines(tested, SpirvOp.Select, out var selection) &&
            IsIntegerConstant(selection.Operands[1], out var whenTrue) &&
            IsIntegerConstant(selection.Operands[2], out var whenFalse) &&
            (whenTrue == 0) != (whenFalse == 0))
        {
            var condition = selection.Operands[0];
            result = notEqual == (whenTrue != 0)
                ? condition
                : AddInstruction(SpirvOp.LogicalNot, resultType, condition);
            return true;
        }

        if (Defines(tested, SpirvOp.BitwiseAnd, out var masked) &&
            TryOwnLaneBallotTest(masked.Operands[0], masked.Operands[1], out var ownLane))
        {
            result = notEqual ? ownLane : AddInstruction(SpirvOp.LogicalNot, resultType, ownLane);
            return true;
        }

        if (IntegerWidth(TypeOf(tested)) == 64 && _integerTypes.TryGetValue((32, false), out var word) &&
            TrySplitHighWord(tested, word, 0, out var high) && IsIntegerConstant(high, out var highValue) &&
            highValue == 0 && TrySplitLowWord(tested, word, 0, out var low))
        {
            result = AddInstruction(notEqual ? SpirvOp.INotEqual : SpirvOp.IEqual, resultType, low, Constant(word, 0));
            return true;
        }

        return false;
    }

    private bool TryOwnLaneBallotTest(uint left, uint right, out uint condition)
    {
        condition = 0;
        foreach (var (ballotWord, laneBit) in new[] { (left, right), (right, left) })
        {
            if (!Defines(ballotWord, SpirvOp.CompositeExtract, out var extract) || extract.Operands is not [var vote, 0] ||
                !Defines(vote, SpirvOp.GroupNonUniformBallot, out var ballot) ||
                !IsIntegerConstant(ballot.Operands[0], out var scope) || scope != SubgroupScope ||
                !TryOwnLaneBit(laneBit, out var guard))
            {
                continue;
            }

            var voted = ballot.Operands[1];
            condition = guard == 0 ? voted : AddInstruction(SpirvOp.LogicalAnd, TypeOf(voted), guard, voted);
            return true;
        }

        return false;
    }

    private bool TryOwnLaneBit(uint value, out uint guard)
    {
        guard = 0;
        if (IsShiftedOne(value, out var amount) && _laneIdValues.Contains(amount))
        {
            return true;
        }

        if (!Defines(value, SpirvOp.Select, out var selection) ||
            !IsIntegerConstant(selection.Operands[2], out var otherwise) || otherwise != 0 ||
            !Defines(selection.Operands[0], SpirvOp.ULessThan, out var bound) ||
            !_laneIdValues.Contains(bound.Operands[0]) ||
            !IsIntegerConstant(bound.Operands[1], out var laneLimit) || laneLimit != 32 ||
            !IsShiftedOne(selection.Operands[1], out var shifted))
        {
            return false;
        }

        var lane = bound.Operands[0];
        if (shifted != lane && !(Defines(shifted, SpirvOp.BitwiseAnd, out var wrapped) &&
                wrapped.Operands[0] == lane && IsIntegerConstant(wrapped.Operands[1], out var wrapMask) && wrapMask == 31))
        {
            return false;
        }

        guard = selection.Operands[0];
        return true;
    }

    private bool IsShiftedOne(uint value, out uint amount)
    {
        amount = 0;
        if (!Defines(value, SpirvOp.ShiftLeftLogical, out var shift) || IntegerWidth(shift.Type) != 32 ||
            !IsIntegerConstant(shift.Operands[0], out var one) || one != 1)
        {
            return false;
        }

        amount = shift.Operands[1];
        return true;
    }

    private ulong MaxIntegerValue(uint value, int depth = 0)
    {
        var width = IntegerWidth(TypeOf(value));
        var unbounded = width == 64 ? ulong.MaxValue : uint.MaxValue;
        if (IsIntegerConstant(value, out var constant))
        {
            return constant;
        }

        if (depth > MaxWordSplitDepth || !_definitions.TryGetValue(value, out var definition))
        {
            return unbounded;
        }

        return definition.Opcode switch
        {
            SpirvOp.BitwiseAnd => Math.Min(MaxIntegerValue(definition.Operands[0], depth + 1),
                MaxIntegerValue(definition.Operands[1], depth + 1)),
            SpirvOp.UConvert => Math.Min(MaxIntegerValue(definition.Operands[0], depth + 1), unbounded),
            _ => unbounded,
        };
    }

    private bool TrySimplifyBitcast(uint resultType, uint value, out uint result)
    {
        result = 0;
        if (HasType(value, resultType))
        {
            result = value;
            return true;
        }

        var narrowResult = IntegerWidth(resultType) == 32 || IsFloat32Type(resultType);
        if (narrowResult)
        {
            if (IsIntegerConstant(value, out var bits) && IntegerWidth(TypeOf(value)) == 32)
            {
                result = Constant(resultType, (uint)bits);
                return true;
            }

            if (_floatConstantBits.TryGetValue(value, out var floatBits))
            {
                result = Constant(resultType, floatBits);
                return true;
            }
        }

        if (!Defines(value, SpirvOp.Bitcast, out var inner))
        {
            return false;
        }

        var source = inner.Operands[0];
        if (HasType(source, resultType))
        {
            result = source;
            return true;
        }

        var sourceType = TypeOf(source);
        if (narrowResult && (IntegerWidth(sourceType) == 32 || IsFloat32Type(sourceType)))
        {
            result = AddInstruction(SpirvOp.Bitcast, resultType, source);
            return true;
        }

        return false;
    }

    private bool TrySimplifyUConvert(uint resultType, uint value, out uint result)
    {
        result = 0;
        var width = IntegerWidth(resultType);
        if (width is not (32 or 64))
        {
            return false;
        }

        if (IsIntegerConstant(value, out var constant))
        {
            result = IntegerConstant(resultType, width == 32 ? (uint)constant : constant);
            return true;
        }

        var sourceWidth = IntegerWidth(TypeOf(value));
        if (Defines(value, SpirvOp.UConvert, out var inner) && sourceWidth > width && HasType(inner.Operands[0], resultType))
        {
            result = inner.Operands[0];
            return true;
        }

        if (width == 32 && sourceWidth == 64 && TrySplitLowWord(value, resultType, 0, out var low))
        {
            result = low;
            return true;
        }

        return false;
    }

    private bool TrySplitLowWord(uint value, uint wordType, int depth, out uint low)
    {
        low = 0;
        if (depth > MaxWordSplitDepth)
        {
            return false;
        }

        if (IsIntegerConstant(value, out var constant))
        {
            low = Constant(wordType, (uint)constant);
            return true;
        }

        if (!_definitions.TryGetValue(value, out var definition))
        {
            return false;
        }

        switch (definition.Opcode)
        {
            case SpirvOp.UConvert when HasType(definition.Operands[0], wordType):
                low = definition.Operands[0];
                return true;
            case SpirvOp.ShiftLeftLogical when IsIntegerConstant(definition.Operands[1], out var shift) && shift is >= 32 and < 64:
                low = Constant(wordType, 0);
                return true;
            case SpirvOp.ShiftLeftLogical when IsIntegerConstant(definition.Operands[0], out var shiftedOne) && shiftedOne == 1 &&
                MaxIntegerValue(definition.Operands[1]) < 32 &&
                TryNarrowShiftAmount(definition.Operands[1], wordType, depth + 1, out var amount):
                low = AddInstruction(SpirvOp.ShiftLeftLogical, wordType, Constant(wordType, 1), amount);
                return true;
            case SpirvOp.BitwiseOr or SpirvOp.BitwiseAnd or SpirvOp.BitwiseXor
                when TrySplitLowWord(definition.Operands[0], wordType, depth + 1, out var left) &&
                    TrySplitLowWord(definition.Operands[1], wordType, depth + 1, out var right):
                low = AddInstruction(definition.Opcode, wordType, left, right);
                return true;
            case SpirvOp.Not when TrySplitLowWord(definition.Operands[0], wordType, depth + 1, out var inverted):
                low = AddInstruction(SpirvOp.Not, wordType, inverted);
                return true;
            case SpirvOp.Select
                when TrySplitLowWord(definition.Operands[1], wordType, depth + 1, out var whenTrue) &&
                    TrySplitLowWord(definition.Operands[2], wordType, depth + 1, out var whenFalse):
                low = AddInstruction(SpirvOp.Select, wordType, definition.Operands[0], whenTrue, whenFalse);
                return true;
            default:
                return false;
        }
    }

    private bool TrySplitHighWord(uint value, uint wordType, int depth, out uint high)
    {
        high = 0;
        if (depth > MaxWordSplitDepth)
        {
            return false;
        }

        if (IsIntegerConstant(value, out var constant))
        {
            high = Constant(wordType, (uint)(constant >> 32));
            return true;
        }

        if (!_definitions.TryGetValue(value, out var definition))
        {
            return false;
        }

        switch (definition.Opcode)
        {
            case SpirvOp.UConvert when IntegerWidth(TypeOf(definition.Operands[0])) == 32:
                high = Constant(wordType, 0);
                return true;
            case SpirvOp.ShiftLeftLogical when IsIntegerConstant(definition.Operands[1], out var shift) && shift == 32:
                return TrySplitLowWord(definition.Operands[0], wordType, depth + 1, out high);
            case SpirvOp.ShiftLeftLogical when IsIntegerConstant(definition.Operands[0], out var shiftedOne) && shiftedOne == 1 &&
                MaxIntegerValue(definition.Operands[1]) < 32:
                high = Constant(wordType, 0);
                return true;
            case SpirvOp.BitwiseAnd when (TrySplitHighWord(definition.Operands[0], wordType, depth + 1, out var maskHigh) &&
                    IsIntegerConstant(maskHigh, out var maskValue) && maskValue == 0) ||
                (TrySplitHighWord(definition.Operands[1], wordType, depth + 1, out maskHigh) &&
                    IsIntegerConstant(maskHigh, out maskValue) && maskValue == 0):
                high = Constant(wordType, 0);
                return true;
            case SpirvOp.BitwiseOr or SpirvOp.BitwiseAnd or SpirvOp.BitwiseXor
                when TrySplitHighWord(definition.Operands[0], wordType, depth + 1, out var left) &&
                    TrySplitHighWord(definition.Operands[1], wordType, depth + 1, out var right):
                high = AddInstruction(definition.Opcode, wordType, left, right);
                return true;
            case SpirvOp.Not when TrySplitHighWord(definition.Operands[0], wordType, depth + 1, out var inverted):
                high = AddInstruction(SpirvOp.Not, wordType, inverted);
                return true;
            case SpirvOp.Select
                when TrySplitHighWord(definition.Operands[1], wordType, depth + 1, out var whenTrue) &&
                    TrySplitHighWord(definition.Operands[2], wordType, depth + 1, out var whenFalse):
                high = AddInstruction(SpirvOp.Select, wordType, definition.Operands[0], whenTrue, whenFalse);
                return true;
            default:
                return false;
        }
    }

    private bool TryNarrowShiftAmount(uint amount, uint wordType, int depth, out uint narrow)
    {
        if (IntegerWidth(TypeOf(amount)) == 32)
        {
            narrow = amount;
            return true;
        }

        return TrySplitLowWord(amount, wordType, depth, out narrow);
    }

    private bool IsRemovableDefinition(List<uint> words, int offset, int wordCount)
    {
        var opcode = (SpirvOp)(words[offset] & 0xFFFF);
        if (opcode == SpirvOp.Load)
        {
            return wordCount == 4 && (_localVariables.Contains(words[offset + 3]) || _elementBases.ContainsKey(words[offset + 3]));
        }

        if (wordCount < 3)
        {
            return false;
        }

        if (opcode == SpirvOp.Phi)
        {
            return true;
        }

        if (opcode == SpirvOp.ExtInst)
        {
            return wordCount >= 5 && words[offset + 4] is not (GlslModf or GlslFrexp);
        }

        return IsValueOperation(opcode, []);
    }

    private HashSet<uint> FindUnreadVariables(out HashSet<uint> unreadPointers)
    {
        var words = _functions;
        var chainBases = new Dictionary<uint, uint>();
        var read = new HashSet<uint>();
        for (var offset = 0; offset < words.Count; offset += (int)(words[offset] >> 16))
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            var first = 1;
            switch (opcode)
            {
                case SpirvOp.Variable:
                    continue;
                case SpirvOp.Store when wordCount == 3:
                    first = 2;
                    break;
                case SpirvOp.AccessChain when wordCount >= 4 && _localVariables.Contains(words[offset + 3]):
                    chainBases[words[offset + 2]] = words[offset + 3];
                    first = 4;
                    break;
            }

            for (var word = first; word < wordCount; word++)
            {
                var id = words[offset + word];
                if (_localVariables.Contains(id))
                {
                    read.Add(id);
                }
                else if (chainBases.TryGetValue(id, out var variable))
                {
                    read.Add(variable);
                }
            }
        }

        var unread = KeepsUnreadVariables ? [] : new HashSet<uint>(_localVariables.Where(variable => !read.Contains(variable)));
        unreadPointers = new HashSet<uint>(unread);
        foreach (var (chain, variable) in chainBases)
        {
            if (unread.Contains(variable))
            {
                unreadPointers.Add(chain);
            }
        }

        return unread;
    }

    private static List<uint> WithoutVariables(List<uint> section, HashSet<uint> variables)
    {
        if (variables.Count == 0)
        {
            return section;
        }

        var filtered = new List<uint>(section.Count);
        for (var offset = 0; offset < section.Count;)
        {
            var wordCount = (int)(section[offset] >> 16);
            if ((SpirvOp)(section[offset] & 0xFFFF) != SpirvOp.Variable || !variables.Contains(section[offset + 2]))
            {
                for (var word = 0; word < wordCount; word++)
                {
                    filtered.Add(section[offset + word]);
                }
            }

            offset += wordCount;
        }

        return filtered;
    }

    private static List<uint> WithoutInterfaces(List<uint> section, HashSet<uint> variables)
    {
        if (variables.Count == 0)
        {
            return section;
        }

        var filtered = new List<uint>(section.Count);
        for (var offset = 0; offset < section.Count;)
        {
            var wordCount = (int)(section[offset] >> 16);
            var nameEnd = offset + 3;
            while (nameEnd < offset + wordCount && !HasZeroByte(section[nameEnd]))
            {
                nameEnd++;
            }

            var kept = new List<uint>();
            for (var word = offset + 1; word < offset + wordCount; word++)
            {
                if (word <= nameEnd || !variables.Contains(section[word]))
                {
                    kept.Add(section[word]);
                }
            }

            filtered.Add(((uint)(kept.Count + 1) << 16) | (section[offset] & 0xFFFF));
            filtered.AddRange(kept);
            offset += wordCount;
        }

        return filtered;
    }

    private static bool HasZeroByte(uint word) =>
        (word & 0xFF) == 0 || (word & 0xFF00) == 0 || (word & 0xFF0000) == 0 || (word & 0xFF000000) == 0;

    private List<uint> CompactFunctions(HashSet<uint> removedIds, HashSet<uint> unreadPointers)
    {
        var words = _functions;
        var starts = new List<int>();
        for (var offset = 0; offset < words.Count; offset += (int)(words[offset] >> 16))
        {
            starts.Add(offset);
        }

        var removed = new bool[starts.Count];
        var uses = new Dictionary<uint, int>();
        var definers = new Dictionary<uint, int>();
        for (var index = 0; index < starts.Count; index++)
        {
            var offset = starts[index];
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (_removedInstructions.Contains(offset) ||
                (opcode == SpirvOp.Store && unreadPointers.Contains(words[offset + 1])) ||
                (opcode == SpirvOp.Variable && unreadPointers.Contains(words[offset + 2])))
            {
                removed[index] = true;
                continue;
            }

            var first = 1;
            if (IsRemovableDefinition(words, offset, wordCount))
            {
                definers[words[offset + 2]] = index;
                first = 3;
            }

            for (var word = first; word < wordCount; word++)
            {
                var id = words[offset + word];
                uses[id] = uses.GetValueOrDefault(id) + 1;
            }
        }

        var pending = new Stack<int>();
        foreach (var (id, index) in definers)
        {
            if (uses.GetValueOrDefault(id) == 0)
            {
                pending.Push(index);
            }
        }

        while (pending.TryPop(out var index))
        {
            if (removed[index])
            {
                continue;
            }

            removed[index] = true;
            var offset = starts[index];
            var wordCount = (int)(words[offset] >> 16);
            removedIds.Add(words[offset + 2]);
            for (var word = 3; word < wordCount; word++)
            {
                var id = words[offset + word];
                if (!uses.TryGetValue(id, out var count))
                {
                    continue;
                }

                uses[id] = --count;
                if (count == 0 && definers.TryGetValue(id, out var definer) && !removed[definer])
                {
                    pending.Push(definer);
                }
            }
        }

        var compacted = new List<uint>(words.Count);
        List<uint>? prologue = null;
        for (var index = 0; index < starts.Count; index++)
        {
            if (removed[index])
            {
                continue;
            }

            var offset = starts[index];
            var wordCount = (int)(words[offset] >> 16);
            if (prologue is not null && (SpirvOp)(words[offset] & 0xFFFF) != SpirvOp.Variable)
            {
                compacted.AddRange(prologue);
                prologue = null;
            }

            for (var word = 0; word < wordCount; word++)
            {
                compacted.Add(words[offset + word]);
            }

            if (_entryPrologues.TryGetValue(offset, out var entry))
            {
                prologue = entry;
            }
        }

        return compacted;
    }

    private static List<uint> WithoutTargets(List<uint> section, HashSet<uint> removedIds)
    {
        if (removedIds.Count == 0)
        {
            return section;
        }

        var filtered = new List<uint>(section.Count);
        for (var offset = 0; offset < section.Count;)
        {
            var wordCount = (int)(section[offset] >> 16);
            var opcode = (SpirvOp)(section[offset] & 0xFFFF);
            var dropped = opcode is SpirvOp.Name or SpirvOp.Decorate && removedIds.Contains(section[offset + 1]);
            if (!dropped)
            {
                for (var word = 0; word < wordCount; word++)
                {
                    filtered.Add(section[offset + word]);
                }
            }

            offset += wordCount;
        }

        return filtered;
    }
}
