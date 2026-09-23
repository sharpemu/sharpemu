// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ScalarValueGraph
{
    private const int ScalarRegisterCount = 256;
    private const int VectorRegisterCount = 256;
    private const uint VccLow = 106;
    private const uint VccHigh = 107;
    private const uint M0 = 124;
    private const uint ExecLow = 126;
    private const uint ExecHigh = 127;
    private const uint NullScalarRegister = 125;
    private const uint FullWaveMask = 0xFFFF_FFFF;
    private const int BlockVisitLimit = 64;

    // Walks the control-flow graph to a fixpoint. Registers hold graph values, joins
    // create phis per register, and a final pass records each memory access's handles.
    private sealed class Builder(ScalarValueGraph graph)
    {
        private readonly ScalarValueGraph _graph = graph;
        private readonly Gen5ShaderProgram _program = graph.Program;
        private readonly Dictionary<(int Block, int Slot), ScalarValue> _phis = [];
        private readonly Dictionary<uint, int> _blockByPc = [];
        private RegisterState?[] _entry = [];
        private RegisterState?[] _exit = [];
        private bool _recording;

        public void Run()
        {
            var controlFlow = _graph.ControlFlow;
            var blockCount = controlFlow.Blocks.Count;
            _entry = new RegisterState?[blockCount];
            _exit = new RegisterState?[blockCount];
            for (var block = 0; block < blockCount; block++)
            {
                foreach (var instruction in _program.Instructions)
                {
                    if (instruction.Pc >= controlFlow.Blocks[block].StartPc && instruction.Pc < controlFlow.Blocks[block].EndPc)
                    {
                        _blockByPc[instruction.Pc] = block;
                    }
                }
            }

            _graph.Accesses = new MemoryAccessBinding?[_graph.Memory.Count];
            if (blockCount == 0)
            {
                return;
            }

            var visits = new int[blockCount];
            var queued = new bool[blockCount];
            var processingPriorities = BuildBlockProcessingPriorities();
            var worklist = new PriorityQueue<int, int>();
            worklist.Enqueue(0, processingPriorities[0]);
            queued[0] = true;
            while (worklist.Count != 0)
            {
                var block = worklist.Dequeue();
                queued[block] = false;
                if (visits[block]++ > BlockVisitLimit)
                {
                    throw new ResourcePlanException(
                        $"value graph did not converge: block {block} pc=0x{controlFlow.Blocks[block].StartPc:X8}");
                }

                var entry = MergePredecessors(block);
                _entry[block] = entry;
                var exit = Transfer(block, entry.Clone());
                var changed = _exit[block] is not { } previous || !previous.SameAs(exit);
                _exit[block] = exit;
                if (!changed)
                {
                    continue;
                }

                foreach (var successor in controlFlow.Successors[block])
                {
                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        worklist.Enqueue(successor, processingPriorities[successor]);
                    }
                }
            }

            _recording = true;
            for (var block = 0; block < blockCount; block++)
            {
                if (_entry[block] is { } entry)
                {
                    Transfer(block, entry.Clone());
                }
            }
        }

        // Reverse postorder completes acyclic predecessors before their joins.
        // Loop back edges still use the convergence check in the worklist.
        private int[] BuildBlockProcessingPriorities()
        {
            var successors = _graph.ControlFlow.Successors;
            var priorities = new int[successors.Count];
            var visited = new bool[successors.Count];
            var pending = new Stack<(int Block, int NextSuccessor)>();
            var nextPriority = successors.Count;
            visited[0] = true;
            pending.Push((0, 0));
            while (pending.TryPop(out var position))
            {
                if (position.NextSuccessor == successors[position.Block].Count)
                {
                    priorities[position.Block] = --nextPriority;
                    continue;
                }

                pending.Push((position.Block, position.NextSuccessor + 1));
                var successor = successors[position.Block][position.NextSuccessor];
                if (!visited[successor])
                {
                    visited[successor] = true;
                    pending.Push((successor, 0));
                }
            }
            return priorities;
        }

        private RegisterState InitialState()
        {
            var state = new RegisterState(_graph);
            for (uint index = 0; index < _graph.UserDataCount; index++)
            {
                var register = _graph.UserDataBase + index;
                if (register < ScalarRegisterCount)
                {
                    state.Scalars[register] = _graph.UserData(register);
                }
            }

            state.Scalars[VccLow] = _graph.Constant(0u);
            state.Scalars[VccHigh] = _graph.Constant(0u);
            state.Scalars[ExecLow] = _graph.Constant(FullWaveMask);
            state.Scalars[ExecHigh] = _graph.Constant(0u);
            state.Exec = _graph.Constant(true);
            state.Vcc = _graph.Constant(false);
            state.Scc = _graph.Constant(false);
            return state;
        }

        private RegisterState MergePredecessors(int block)
        {
            var predecessors = _graph.ControlFlow.Predecessors[block];
            if (block == 0 && predecessors.Count == 0)
            {
                return InitialState();
            }

            var visited = new List<(int Block, RegisterState State)>();
            var pending = false;
            foreach (var predecessor in predecessors)
            {
                if (_exit[predecessor] is { } exit)
                {
                    visited.Add((predecessor, exit));
                }
                else
                {
                    pending = true;
                }
            }

            var merged = new RegisterState(_graph);
            if (block == 0)
            {
                // The entry block joins the initial registers with its back edges.
                visited.Insert(0, (-1, InitialState()));
            }

            if (visited.Count == 0)
            {
                return merged;
            }

            for (var register = 0; register < ScalarRegisterCount; register++)
            {
                merged.Scalars[register] = MergeSlot(block, register, visited, pending, state => state.Scalars[register]);
            }

            for (var register = 0; register < VectorRegisterCount; register++)
            {
                merged.Vectors[register] = MergeSlot(block, ScalarRegisterCount + register, visited, pending, state => state.Vectors[register]);
            }

            merged.Exec = MergeSlot(block, RegisterState.ExecSlot, visited, pending, state => state.Exec);
            merged.Vcc = MergeSlot(block, RegisterState.VccSlot, visited, pending, state => state.Vcc);
            merged.Scc = MergeSlot(block, RegisterState.SccSlot, visited, pending, state => state.Scc);

            var laneKeys = new HashSet<(uint Register, uint Lane)>(visited[0].State.Lanes.Keys);
            foreach (var (_, state) in visited.Skip(1))
            {
                laneKeys.IntersectWith(state.Lanes.Keys);
            }

            foreach (var key in laneKeys)
            {
                var value = MergeSlot(block, RegisterState.LaneSlot(key.Register, key.Lane), visited, pending, state => state.Lanes[key]);
                if (!value.IsUndefined)
                {
                    merged.Lanes[key] = value;
                }
            }

            var maskKeys = new HashSet<uint>(visited[0].State.ThreadBits.Keys);
            foreach (var (_, state) in visited.Skip(1))
            {
                maskKeys.IntersectWith(state.ThreadBits.Keys);
            }

            foreach (var key in maskKeys)
            {
                var value = MergeSlot(block, RegisterState.MaskSlot(key), visited, pending, state => state.ThreadBits[key]);
                if (!value.IsUndefined)
                {
                    merged.ThreadBits[key] = value;
                }
            }

            return merged;
        }

        // Equal incoming values pass through; differing or still unknown ones meet in a
        // phi owned by this block and register. Any undefined input stays undefined.
        private ScalarValue MergeSlot(
            int block,
            int slot,
            List<(int Block, RegisterState State)> visited,
            bool pending,
            Func<RegisterState, ScalarValue> read)
        {
            var first = read(visited[0].State);
            var same = true;
            foreach (var (_, state) in visited)
            {
                var value = read(state);
                if (value.IsUndefined)
                {
                    return _graph.Undefined(first.Type);
                }

                same &= ReferenceEquals(value, first);
            }

            if (same && !pending)
            {
                return first;
            }

            if (!_phis.TryGetValue((block, slot), out var phi))
            {
                phi = _graph.Phi(block, first.Type);
                _phis[(block, slot)] = phi;
            }

            var predecessors = _graph.ControlFlow.Predecessors[block];
            var order = block == 0 ? new[] { -1 }.Concat(predecessors).ToArray() : predecessors.ToArray();
            var operands = new ScalarValue[order.Length];
            for (var index = 0; index < order.Length; index++)
            {
                var source = visited.FirstOrDefault(entry => entry.Block == order[index]);
                operands[index] = source.State is null ? phi : read(source.State);
            }

            phi.SetPhiOperands(order, operands);
            return phi;
        }

        private RegisterState Transfer(int block, RegisterState state)
        {
            var range = _graph.ControlFlow.Blocks[block];
            foreach (var instruction in _program.Instructions)
            {
                if (instruction.Pc < range.StartPc || instruction.Pc >= range.EndPc)
                {
                    continue;
                }

                Apply(instruction, state);
            }

            return state;
        }

        private void Apply(Gen5ShaderInstruction instruction, RegisterState state)
        {
            switch (instruction.Encoding)
            {
                case Gen5ShaderEncoding.Sop1:
                case Gen5ShaderEncoding.Sop2:
                case Gen5ShaderEncoding.Sopk:
                    ApplyScalarAlu(instruction, state);
                    return;
                case Gen5ShaderEncoding.Sopc:
                    ApplyScalarCompare(instruction, state);
                    return;
                case Gen5ShaderEncoding.Sopp:
                    if (_recording)
                    {
                        var condition = instruction.Opcode switch
                        {
                            "SCbranchScc1" => state.Scc,
                            "SCbranchScc0" => Unary(ScalarOperation.LogicalNot, state.Scc),
                            "SCbranchVccnz" => state.Vcc,
                            "SCbranchVccz" => Unary(ScalarOperation.LogicalNot, state.Vcc),
                            "SCbranchExecnz" => state.Exec,
                            "SCbranchExecz" => Unary(ScalarOperation.LogicalNot, state.Exec),
                            _ => null,
                        };
                        if (condition is not null) _graph.BranchConditions[instruction.Pc] = condition;
                    }
                    return;
                case Gen5ShaderEncoding.Smem:
                case Gen5ShaderEncoding.Smrd:
                    ApplyScalarLoad(instruction, state);
                    return;
                case Gen5ShaderEncoding.Vop1:
                case Gen5ShaderEncoding.Vop2:
                case Gen5ShaderEncoding.Vop3:
                case Gen5ShaderEncoding.Vopc:
                case Gen5ShaderEncoding.Vop3p:
                    ApplyVectorAlu(instruction, state);
                    return;
                default:
                    ApplyMemory(instruction, state);
                    return;
            }
        }

        // ---- scalar ALU ----

        private void ApplyScalarAlu(Gen5ShaderInstruction instruction, RegisterState state)
        {
            var opcode = instruction.Opcode;
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister, Value: < ScalarRegisterCount } destination)
            {
                return;
            }

            var destinationRegister = destination.Value;
            switch (opcode)
            {
                case "SMovB32":
                    state.WriteScalar(destinationRegister, Read(instruction.Sources[0], state));
                    return;
                case "SMovkI32":
                    state.WriteScalar(destinationRegister, _graph.Constant(unchecked((uint)(short)instruction.Sources[0].Value)));
                    return;
                case "SAddkI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.IAdd32, state.Scalars[destinationRegister], _graph.Constant(unchecked((uint)(short)instruction.Sources[0].Value))));
                    return;
                case "SMulkI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.IMul32, state.Scalars[destinationRegister], _graph.Constant(unchecked((uint)(short)instruction.Sources[0].Value))));
                    return;
                case "SGetpcB64":
                {
                    var address = _graph.Operation(ScalarOperation.IAdd64, ScalarValueType.U64, _graph.ShaderBase(),
                        _graph.Constant((ulong)instruction.Pc + (ulong)(instruction.Words.Count * sizeof(uint))));
                    state.WritePair(destinationRegister, Extract(address, 0), Extract(address, 1));
                    return;
                }
                case "SSetpcB64":
                case "SSwappcB64":
                    return;
                case "SMovB64":
                    ApplyMovePair(instruction, state, destinationRegister);
                    return;
                case "SCselectB32":
                    state.WriteScalar(destinationRegister, _graph.Select(state.Scc, Read(instruction.Sources[0], state), Read(instruction.Sources[1], state)));
                    return;
                case "SCselectB64":
                {
                    var (leftLow, leftHigh) = ReadPair(instruction.Sources[0], state);
                    var (rightLow, rightHigh) = ReadPair(instruction.Sources[1], state);
                    var mask = _graph.Select(state.Scc, MaskOf(instruction.Sources[0], state), MaskOf(instruction.Sources[1], state));
                    WriteMaskPair(state, destinationRegister, _graph.Select(state.Scc, leftLow, rightLow), _graph.Select(state.Scc, leftHigh, rightHigh), mask);
                    return;
                }
            }

            if (opcode.EndsWith("SaveexecB64", StringComparison.Ordinal) || opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                ApplySaveExec(instruction, state, destinationRegister);
                return;
            }

            if (opcode is "SAndB64" or "SOrB64" or "SXorB64" or "SAndn2B64" or "SOrn2B64" or "SNandB64" or "SNorB64" or "SXnorB64" or "SNotB64")
            {
                ApplyMaskPairOperation(instruction, state, destinationRegister);
                return;
            }

            if (opcode is "SLshlB64" or "SLshrB64" or "SAshrI64")
            {
                var (low, high) = ReadPair(instruction.Sources[0], state);
                var count = Binary(ScalarOperation.And32, Read(instruction.Sources[1], state), _graph.Constant(63u));
                var operation = opcode switch
                {
                    "SLshlB64" => ScalarOperation.ShiftLeft64,
                    "SLshrB64" => ScalarOperation.ShiftRightLogical64,
                    _ => ScalarOperation.ShiftRightArithmetic64,
                };
                var result = _graph.Operation(operation, ScalarValueType.U64, Construct(low, high), count);
                state.WritePair(destinationRegister, Extract(result, 0), Extract(result, 1));
                state.Scc = Bool(ScalarOperation.INotEqual64, result, _graph.Constant(0ul));
                return;
            }

            if (opcode == "SBfeU64")
            {
                var (low, high) = ReadPair(instruction.Sources[0], state);
                var field = Read(instruction.Sources[1], state);
                var offset = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, field, _graph.Constant(0u), _graph.Constant(6u));
                var rawCount = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, field, _graph.Constant(16u), _graph.Constant(7u));
                var count = Binary(ScalarOperation.UMin32, rawCount, Binary(ScalarOperation.ISub32, _graph.Constant(64u), offset));
                var shifted = _graph.Operation(ScalarOperation.ShiftRightLogical64, ScalarValueType.U64, Construct(low, high), offset);
                var result = _graph.Operation(ScalarOperation.And64, ScalarValueType.U64, shifted, RightMask64(count));
                state.WritePair(destinationRegister, Extract(result, 0), Extract(result, 1));
                state.Scc = Bool(ScalarOperation.INotEqual64, result, _graph.Constant(0ul));
                return;
            }

            if (opcode == "SBfmB64")
            {
                var count = Binary(ScalarOperation.And32, Read(instruction.Sources[0], state), _graph.Constant(63u));
                var offset = Binary(ScalarOperation.And32, Read(instruction.Sources[1], state), _graph.Constant(63u));
                var result = _graph.Operation(ScalarOperation.ShiftLeft64, ScalarValueType.U64, RightMask64(count), offset);
                state.WritePair(destinationRegister, Extract(result, 0), Extract(result, 1));
                return;
            }

            if (opcode is "SBcnt1I32B64" or "SFF1I32B64" or "SWqmB64" or "SBfeI64")
            {
                // Bit counting, lane scans and quad masks are not uniform descriptor values.
                var (low, high) = ReadPair(instruction.Sources[0], state);
                switch (opcode)
                {
                    case "SBcnt1I32B64":
                        state.WriteScalar(destinationRegister, Binary(ScalarOperation.IAdd32, Unary(ScalarOperation.BitCount32, low), Unary(ScalarOperation.BitCount32, high)));
                        state.Scc = Bool(ScalarOperation.INotEqual32, state.Scalars[destinationRegister], _graph.Constant(0u));
                        break;
                    case "SFF1I32B64":
                    {
                        var lowLsb = Unary(ScalarOperation.FindLowestBit32, low);
                        var highLsb = Binary(ScalarOperation.IAdd32, Unary(ScalarOperation.FindLowestBit32, high), _graph.Constant(32u));
                        var result = _graph.Select(Bool(ScalarOperation.INotEqual32, low, _graph.Constant(0u)), lowLsb,
                            _graph.Select(Bool(ScalarOperation.INotEqual32, high, _graph.Constant(0u)), highLsb, _graph.Constant(uint.MaxValue)));
                        state.WriteScalar(destinationRegister, result);
                        break;
                    }
                    default:
                        state.WritePair(destinationRegister, _graph.Undefined(ScalarValueType.U32), _graph.Undefined(ScalarValueType.U32));
                        state.Scc = _graph.Undefined(ScalarValueType.Bool);
                        break;
                }

                return;
            }

            if (instruction.Sources.Count == 0)
            {
                state.WriteScalar(destinationRegister, _graph.Undefined(ScalarValueType.U32));
                return;
            }

            var left = Read(instruction.Sources[0], state);
            switch (opcode)
            {
                case "SNotB32":
                    state.WriteScalar(destinationRegister, Unary(ScalarOperation.Not32, left));
                    state.Scc = NotZero(state.Scalars[destinationRegister]);
                    return;
                case "SAbsI32":
                {
                    var sign = Binary(ScalarOperation.ISub32, _graph.Constant(0u),
                        Binary(ScalarOperation.ShiftRightLogical32, left, _graph.Constant(31u)));
                    var magnitude = Binary(ScalarOperation.ISub32, Binary(ScalarOperation.Xor32, left, sign), sign);
                    state.WriteScalar(destinationRegister, magnitude);
                    state.Scc = NotZero(magnitude);
                    return;
                }
                case "SWqmB32":
                    state.WriteScalar(destinationRegister, Unary(ScalarOperation.QuadMask32, left));
                    state.Scc = NotZero(state.Scalars[destinationRegister]);
                    return;
                case "SBrevB32":
                    state.WriteScalar(destinationRegister, Unary(ScalarOperation.BitReverse32, left));
                    return;
                case "SBcnt1I32B32":
                    state.WriteScalar(destinationRegister, Unary(ScalarOperation.BitCount32, left));
                    state.Scc = NotZero(state.Scalars[destinationRegister]);
                    return;
                case "SFF1I32B32":
                    state.WriteScalar(destinationRegister, _graph.FindLowestSetBit(left, instruction.Pc));
                    return;
                case "SBitset0B32":
                {
                    var bit = Binary(ScalarOperation.ShiftLeft32, _graph.Constant(1u), Binary(ScalarOperation.And32, left, _graph.Constant(31u)));
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.And32, state.Scalars[destinationRegister], Unary(ScalarOperation.Not32, bit)));
                    return;
                }
                case "SBitset1B32":
                {
                    var bit = Binary(ScalarOperation.ShiftLeft32, _graph.Constant(1u), Binary(ScalarOperation.And32, left, _graph.Constant(31u)));
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.Or32, state.Scalars[destinationRegister], bit));
                    return;
                }
            }

            if (instruction.Sources.Count < 2)
            {
                state.WriteScalar(destinationRegister, _graph.Undefined(ScalarValueType.U32));
                return;
            }

            var right = Read(instruction.Sources[1], state);
            switch (opcode)
            {
                case "SAddU32":
                {
                    var sum = Binary64(ScalarOperation.AddCarry32, left, right);
                    state.WriteScalar(destinationRegister, Extract(sum, 0));
                    state.Scc = NotZero(Extract(sum, 1));
                    return;
                }
                case "SAddcU32":
                {
                    var carryIn = _graph.Select(state.Scc, _graph.Constant(1u), _graph.Constant(0u));
                    var first = Binary64(ScalarOperation.AddCarry32, left, right);
                    var second = Binary64(ScalarOperation.AddCarry32, Extract(first, 0), carryIn);
                    state.WriteScalar(destinationRegister, Extract(second, 0));
                    state.Scc = NotZero(Binary(ScalarOperation.Or32, Extract(first, 1), Extract(second, 1)));
                    return;
                }
                case "SSubU32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.ISub32, left, right));
                    state.Scc = Bool(ScalarOperation.UGreaterThan32, right, left);
                    return;
                case "SSubbU32":
                {
                    var borrowIn = _graph.Select(state.Scc, _graph.Constant(1u), _graph.Constant(0u));
                    var partial = Binary(ScalarOperation.ISub32, left, right);
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.ISub32, partial, borrowIn));
                    state.Scc = Bool(ScalarOperation.LogicalOr, Bool(ScalarOperation.UGreaterThan32, right, left), Bool(ScalarOperation.UGreaterThan32, borrowIn, partial));
                    return;
                }
                case "SAddI32":
                case "SSubI32":
                {
                    var subtract = opcode == "SSubI32";
                    var result = Binary(subtract ? ScalarOperation.ISub32 : ScalarOperation.IAdd32, left, right);
                    var shift = _graph.Constant(31u);
                    var leftSign = Binary(ScalarOperation.ShiftRightLogical32, left, shift);
                    var rightSign = Binary(ScalarOperation.ShiftRightLogical32, right, shift);
                    var outSign = Binary(ScalarOperation.ShiftRightLogical32, result, shift);
                    var inputs = subtract ? Bool(ScalarOperation.INotEqual32, leftSign, rightSign) : Bool(ScalarOperation.IEqual32, leftSign, rightSign);
                    state.WriteScalar(destinationRegister, result);
                    state.Scc = Bool(ScalarOperation.LogicalAnd, inputs, Bool(ScalarOperation.INotEqual32, leftSign, outSign));
                    return;
                }
                case "SMinI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.SMin32, left, right));
                    state.Scc = Bool(ScalarOperation.SLessThan32, left, right);
                    return;
                case "SMinU32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.UMin32, left, right));
                    state.Scc = Bool(ScalarOperation.ULessThan32, left, right);
                    return;
                case "SMaxI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.SMax32, left, right));
                    state.Scc = Bool(ScalarOperation.SGreaterThan32, left, right);
                    return;
                case "SMaxU32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.UMax32, left, right));
                    state.Scc = Bool(ScalarOperation.UGreaterThan32, left, right);
                    return;
                case "SAndB32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.And32, left, right));
                    return;
                case "SOrB32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.Or32, left, right));
                    return;
                case "SXorB32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.Xor32, left, right));
                    return;
                case "SAndn2B32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.And32, left, Unary(ScalarOperation.Not32, right)));
                    return;
                case "SOrn2B32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.Or32, left, Unary(ScalarOperation.Not32, right)));
                    return;
                case "SNandB32":
                    WriteWithSccFlag(state, destinationRegister, Unary(ScalarOperation.Not32, Binary(ScalarOperation.And32, left, right)));
                    return;
                case "SNorB32":
                    WriteWithSccFlag(state, destinationRegister, Unary(ScalarOperation.Not32, Binary(ScalarOperation.Or32, left, right)));
                    return;
                case "SXnorB32":
                    WriteWithSccFlag(state, destinationRegister, Unary(ScalarOperation.Not32, Binary(ScalarOperation.Xor32, left, right)));
                    return;
                case "SLshlB32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.ShiftLeft32, left, Binary(ScalarOperation.And32, right, _graph.Constant(31u))));
                    return;
                case "SLshrB32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.ShiftRightLogical32, left, Binary(ScalarOperation.And32, right, _graph.Constant(31u))));
                    return;
                case "SAshrI32":
                    WriteWithSccFlag(state, destinationRegister, Binary(ScalarOperation.ShiftRightArithmetic32, left, Binary(ScalarOperation.And32, right, _graph.Constant(31u))));
                    return;
                case "SBfmB32":
                {
                    var count = Binary(ScalarOperation.And32, left, _graph.Constant(31u));
                    var offset = Binary(ScalarOperation.And32, right, _graph.Constant(31u));
                    state.WriteScalar(destinationRegister, _graph.Operation(ScalarOperation.BitFieldInsert, ScalarValueType.U32, _graph.Constant(0u), _graph.Constant(uint.MaxValue), offset, count));
                    return;
                }
                case "SMulI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.IMul32, left, right));
                    return;
                case "SMulHiU32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.UMulHi32, left, right));
                    return;
                case "SMulHiI32":
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.SMulHi32, left, right));
                    return;
                case "SBfeU32":
                case "SBfeI32":
                {
                    var offset = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, right, _graph.Constant(0u), _graph.Constant(5u));
                    var rawCount = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, right, _graph.Constant(16u), _graph.Constant(7u));
                    var count = Binary(ScalarOperation.UMin32, rawCount, Binary(ScalarOperation.ISub32, _graph.Constant(32u), offset));
                    var operation = opcode == "SBfeU32" ? ScalarOperation.BitFieldUExtract : ScalarOperation.BitFieldSExtract;
                    WriteWithSccFlag(state, destinationRegister, _graph.Operation(operation, ScalarValueType.U32, left, offset, count));
                    return;
                }
                case "SAbsdiffI32":
                    WriteWithSccFlag(state, destinationRegister, Unary(ScalarOperation.IAbs32, Binary(ScalarOperation.ISub32, left, right)));
                    return;
                case "SLshl1AddU32":
                case "SLshl2AddU32":
                case "SLshl3AddU32":
                case "SLshl4AddU32":
                {
                    var amount = (uint)(opcode[5] - '0');
                    var shifted = Binary(ScalarOperation.ShiftLeft32, left, _graph.Constant(amount));
                    var result = Binary(ScalarOperation.IAdd32, shifted, right);
                    var addCarry = Bool(ScalarOperation.ULessThan32, result, shifted);
                    var shiftedOut = Binary(ScalarOperation.ShiftRightLogical32, left, _graph.Constant(32u - amount));
                    state.WriteScalar(destinationRegister, result);
                    state.Scc = Bool(ScalarOperation.LogicalOr, addCarry, NotZero(shiftedOut));
                    return;
                }
                case "SPackLlB32B16":
                case "SPackLhB32B16":
                case "SPackHhB32B16":
                {
                    var low = opcode == "SPackHhB32B16" ? Binary(ScalarOperation.ShiftRightLogical32, left, _graph.Constant(16u)) : left;
                    var high = opcode == "SPackLlB32B16" ? right : Binary(ScalarOperation.ShiftRightLogical32, right, _graph.Constant(16u));
                    var lowBits = Binary(ScalarOperation.And32, low, _graph.Constant(0xFFFFu));
                    var highBits = Binary(ScalarOperation.ShiftLeft32, Binary(ScalarOperation.And32, high, _graph.Constant(0xFFFFu)), _graph.Constant(16u));
                    state.WriteScalar(destinationRegister, Binary(ScalarOperation.Or32, lowBits, highBits));
                    return;
                }
                default:
                    state.WriteScalar(destinationRegister, _graph.Undefined(ScalarValueType.U32));
                    if (opcode.EndsWith("64", StringComparison.Ordinal))
                    {
                        state.WriteScalar(destinationRegister + 1, _graph.Undefined(ScalarValueType.U32));
                    }

                    state.Scc = _graph.Undefined(ScalarValueType.Bool);
                    return;
            }
        }

        private void WriteWithSccFlag(RegisterState state, uint destinationRegister, ScalarValue result)
        {
            state.WriteScalar(destinationRegister, result);
            state.Scc = NotZero(result);
        }

        private void ApplyMovePair(Gen5ShaderInstruction instruction, RegisterState state, uint destinationRegister)
        {
            var source = instruction.Sources[0];
            var (low, high) = ReadPair(source, state);
            var mask = MaskOf(source, state);
            WriteMaskPair(state, destinationRegister, low, high, mask);
        }

        // Writes a dword pair; a pair that is a lane mask also keeps its mask value, and a
        // write to EXEC or VCC updates the active mask.
        // A lane mask a vector instruction writes to an SGPR: in wave32 it fills that register
        // alone, and the next one keeps its value. It is often live: a pointer's high half, or
        // VCC_HI, which compilers use as an ordinary SGPR in wave32.
        private void WriteVectorMask(RegisterState state, uint destinationRegister, ScalarValue low, ScalarValue mask)
        {
            if (_graph.WaveSize != 32)
            {
                WriteMaskPair(state, destinationRegister, low, _graph.Constant(0u), mask);
                return;
            }

            state.WriteScalar(destinationRegister, low);
            switch (destinationRegister)
            {
                case ExecLow:
                    state.Exec = mask;
                    break;
                case VccLow:
                    state.Vcc = mask;
                    break;
                default:
                    if (!mask.IsUndefined)
                    {
                        state.ThreadBits[destinationRegister] = mask;
                    }

                    break;
            }
        }

        private void WriteMaskPair(RegisterState state, uint destinationRegister, ScalarValue low, ScalarValue high, ScalarValue mask)
        {
            state.WritePair(destinationRegister, low, high);
            switch (destinationRegister)
            {
                case ExecLow:
                    state.Exec = mask;
                    break;
                case VccLow:
                    state.Vcc = mask;
                    break;
                default:
                    if (!mask.IsUndefined)
                    {
                        state.ThreadBits[destinationRegister] = mask;
                    }

                    break;
            }
        }

        private void ApplySaveExec(Gen5ShaderInstruction instruction, RegisterState state, uint destinationRegister)
        {
            var opcode = instruction.Opcode;
            var wide = opcode.EndsWith("B64", StringComparison.Ordinal);
            var oldExec = state.Exec;
            var oldLow = state.Scalars[ExecLow];
            var oldHigh = state.Scalars[ExecHigh];
            var source = MaskOf(instruction.Sources[0], state);
            var (sourceLow, sourceHigh) = wide ? ReadPair(instruction.Sources[0], state) : (Read(instruction.Sources[0], state), _graph.Constant(0u));
            var name = opcode[1..opcode.IndexOf("Saveexec", StringComparison.Ordinal)];
            ScalarValue Combine(ScalarValue oldMask, ScalarValue sourceMask, ScalarValue oldBits, ScalarValue sourceBits, out ScalarValue raw)
            {
                switch (name)
                {
                    case "And":
                        raw = Binary(ScalarOperation.And32, oldBits, sourceBits);
                        return Bool(ScalarOperation.LogicalAnd, oldMask, sourceMask);
                    case "Or":
                        raw = Binary(ScalarOperation.Or32, oldBits, sourceBits);
                        return Bool(ScalarOperation.LogicalOr, oldMask, sourceMask);
                    case "Xor":
                        raw = Binary(ScalarOperation.Xor32, oldBits, sourceBits);
                        return Bool(ScalarOperation.LogicalXor, oldMask, sourceMask);
                    case "Andn1":
                        raw = Binary(ScalarOperation.And32, Unary(ScalarOperation.Not32, sourceBits), oldBits);
                        return Bool(ScalarOperation.LogicalAnd, Bool(ScalarOperation.LogicalNot, sourceMask), oldMask);
                    case "Andn2":
                        raw = Binary(ScalarOperation.And32, sourceBits, Unary(ScalarOperation.Not32, oldBits));
                        return Bool(ScalarOperation.LogicalAnd, sourceMask, Bool(ScalarOperation.LogicalNot, oldMask));
                    case "Orn1":
                        raw = Binary(ScalarOperation.Or32, Unary(ScalarOperation.Not32, sourceBits), oldBits);
                        return Bool(ScalarOperation.LogicalOr, Bool(ScalarOperation.LogicalNot, sourceMask), oldMask);
                    case "Orn2":
                        raw = Binary(ScalarOperation.Or32, sourceBits, Unary(ScalarOperation.Not32, oldBits));
                        return Bool(ScalarOperation.LogicalOr, sourceMask, Bool(ScalarOperation.LogicalNot, oldMask));
                    case "Nand":
                        raw = Unary(ScalarOperation.Not32, Binary(ScalarOperation.And32, sourceBits, oldBits));
                        return Bool(ScalarOperation.LogicalNot, Bool(ScalarOperation.LogicalAnd, sourceMask, oldMask));
                    case "Nor":
                        raw = Unary(ScalarOperation.Not32, Binary(ScalarOperation.Or32, sourceBits, oldBits));
                        return Bool(ScalarOperation.LogicalNot, Bool(ScalarOperation.LogicalOr, sourceMask, oldMask));
                    default:
                        raw = Unary(ScalarOperation.Not32, Binary(ScalarOperation.Xor32, oldBits, sourceBits));
                        return Bool(ScalarOperation.LogicalNot, Bool(ScalarOperation.LogicalXor, oldMask, sourceMask));
                }
            }

            var newExec = Combine(oldExec, source, oldLow, sourceLow, out var newLow);
            Combine(oldExec, source, oldHigh, sourceHigh, out var newHigh);
            if (wide)
            {
                state.WritePair(destinationRegister, oldLow, oldHigh);
            }
            else
            {
                state.WriteScalar(destinationRegister, oldLow);
                newHigh = _graph.Constant(0u);
            }

            state.ThreadBits[destinationRegister] = oldExec;
            state.WritePair(ExecLow, newLow, newHigh);
            state.Exec = newExec;
            state.Scc = newExec;
        }

        private void ApplyMaskPairOperation(Gen5ShaderInstruction instruction, RegisterState state, uint destinationRegister)
        {
            var opcode = instruction.Opcode;
            var unary = opcode == "SNotB64";
            var (leftLow, leftHigh) = ReadPair(instruction.Sources[0], state);
            var leftMask = MaskOf(instruction.Sources[0], state);
            var (rightLow, rightHigh) = unary ? (_graph.Constant(0u), _graph.Constant(0u)) : ReadPair(instruction.Sources[1], state);
            var rightMask = unary ? _graph.Constant(false) : MaskOf(instruction.Sources[1], state);
            var bitOperation = opcode switch
            {
                "SAndB64" or "SAndn2B64" or "SNandB64" or "SNotB64" => ScalarOperation.And32,
                "SOrB64" or "SOrn2B64" or "SNorB64" => ScalarOperation.Or32,
                _ => ScalarOperation.Xor32,
            };
            var logicalOperation = bitOperation switch
            {
                ScalarOperation.And32 => ScalarOperation.LogicalAnd,
                ScalarOperation.Or32 => ScalarOperation.LogicalOr,
                _ => ScalarOperation.LogicalXor,
            };
            var negateRight = opcode is "SAndn2B64" or "SOrn2B64";
            var negateResult = opcode is "SNandB64" or "SNorB64" or "SXnorB64";

            ScalarValue Combine(ScalarValue left, ScalarValue right)
            {
                if (unary)
                {
                    return Unary(ScalarOperation.Not32, left);
                }

                var value = Binary(bitOperation, left, negateRight ? Unary(ScalarOperation.Not32, right) : right);
                return negateResult ? Unary(ScalarOperation.Not32, value) : value;
            }

            ScalarValue mask;
            if (unary)
            {
                mask = Bool(ScalarOperation.LogicalNot, leftMask);
            }
            else
            {
                mask = Bool(logicalOperation, leftMask, negateRight ? Bool(ScalarOperation.LogicalNot, rightMask) : rightMask);
                if (negateResult)
                {
                    mask = Bool(ScalarOperation.LogicalNot, mask);
                }
            }

            var low = Combine(leftLow, rightLow);
            var high = Combine(leftHigh, rightHigh);
            WriteMaskPair(state, destinationRegister, low, high, mask);
            state.Scc = destinationRegister is ExecLow or VccLow ? mask : NotZero(Binary(ScalarOperation.Or32, low, high));
        }

        private void ApplyScalarCompare(Gen5ShaderInstruction instruction, RegisterState state)
        {
            var opcode = instruction.Opcode;
            if (instruction.Sources.Count != 2)
            {
                state.Scc = _graph.Undefined(ScalarValueType.Bool);
                return;
            }

            if (opcode is "SCmpEqU64" or "SCmpLgU64")
            {
                var (leftLow, leftHigh) = ReadPair(instruction.Sources[0], state);
                var (rightLow, rightHigh) = ReadPair(instruction.Sources[1], state);
                state.Scc = Bool(opcode == "SCmpEqU64" ? ScalarOperation.IEqual64 : ScalarOperation.INotEqual64,
                    Construct(leftLow, leftHigh), Construct(rightLow, rightHigh));
                return;
            }

            var left = Read(instruction.Sources[0], state);
            var right = Read(instruction.Sources[1], state);
            if (opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var offset = Binary(ScalarOperation.And32, right, _graph.Constant(31u));
                var bit = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, left, offset, _graph.Constant(1u));
                state.Scc = Bool(ScalarOperation.IEqual32, bit, _graph.Constant(opcode == "SBitcmp1B32" ? 1u : 0u));
                return;
            }

            if (opcode is "SBitcmp0B64" or "SBitcmp1B64")
            {
                var (low, high) = ReadPair(instruction.Sources[0], state);
                var offset = Binary(ScalarOperation.And32, right, _graph.Constant(63u));
                var wordOffset = Binary(ScalarOperation.And32, offset, _graph.Constant(31u));
                var lowBit = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, low, wordOffset, _graph.Constant(1u));
                var highBit = _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, high, wordOffset, _graph.Constant(1u));
                var bit = _graph.Select(Bool(ScalarOperation.ULessThan32, offset, _graph.Constant(32u)), lowBit, highBit);
                state.Scc = Bool(ScalarOperation.IEqual32, bit, _graph.Constant(opcode == "SBitcmp1B64" ? 1u : 0u));
                return;
            }

            state.Scc = Compare(opcode["SCmp".Length..], left, right);
        }

        private ScalarValue Compare(string suffix, ScalarValue left, ScalarValue right) => suffix switch
        {
            "EqI32" or "EqU32" or "kEqI32" or "kEqU32" => Bool(ScalarOperation.IEqual32, left, right),
            "LgI32" or "LgU32" or "kLgI32" or "kLgU32" => Bool(ScalarOperation.INotEqual32, left, right),
            "GtU32" or "kGtU32" => Bool(ScalarOperation.UGreaterThan32, left, right),
            "GeU32" or "kGeU32" => Bool(ScalarOperation.UGreaterThanEqual32, left, right),
            "LtU32" or "kLtU32" => Bool(ScalarOperation.ULessThan32, left, right),
            "LeU32" or "kLeU32" => Bool(ScalarOperation.ULessThanEqual32, left, right),
            "GtI32" or "kGtI32" => Bool(ScalarOperation.SGreaterThan32, left, right),
            "GeI32" or "kGeI32" => Bool(ScalarOperation.SGreaterThanEqual32, left, right),
            "LtI32" or "kLtI32" => Bool(ScalarOperation.SLessThan32, left, right),
            "LeI32" or "kLeI32" => Bool(ScalarOperation.SLessThanEqual32, left, right),
            _ => _graph.Undefined(ScalarValueType.Bool),
        };

        // ---- scalar loads ----

        private void ApplyScalarLoad(Gen5ShaderInstruction instruction, RegisterState state)
        {
            if (instruction.Control is not Gen5ScalarMemoryControl control ||
                instruction.Sources.Count == 0 ||
                instruction.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } baseOperand)
            {
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister)
                    {
                        state.WriteScalar(destination.Value, _graph.Undefined(ScalarValueType.U32));
                    }
                }

                return;
            }

            var isBuffer = instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal);
            var scalarBase = baseOperand.Value;
            var handle = isBuffer
                ? _graph.Handle(ScalarValueKind.BufferHandle, state.Read(scalarBase), state.Read(scalarBase + 1), state.Read(scalarBase + 2), state.Read(scalarBase + 3))
                : _graph.Handle(ScalarValueKind.AddressHandle, state.Read(scalarBase), state.Read(scalarBase + 1));
            var offset = control.DynamicOffsetRegister is { } offsetRegister
                ? state.Read(offsetRegister)
                : _graph.Constant(0u);
            for (uint component = 0; component < control.DestinationCount; component++)
            {
                if (!_graph.Memory.TryGetIndex(instruction.Pc, component, out var memoryIndex))
                {
                    continue;
                }

                var read = _graph.MemoryRead(
                    isBuffer ? ScalarValueKind.ScalarBufferWord : ScalarValueKind.ScalarAddressWord,
                    handle,
                    offset,
                    memoryIndex);
                if (component < instruction.Destinations.Count &&
                    instruction.Destinations[(int)component] is { Kind: Gen5OperandKind.ScalarRegister } destination)
                {
                    state.WriteScalar(destination.Value, read);
                }

                if (_recording)
                {
                    _graph.Accesses[memoryIndex] = new MemoryAccessBinding(handle, null, read);
                }
            }
        }

        // ---- vector ALU (uniform domain) ----

        private void ApplyVectorAlu(Gen5ShaderInstruction instruction, RegisterState state)
        {
            var opcode = instruction.Opcode;
            if (opcode == "VWritelaneB32")
            {
                ApplyWriteLane(instruction, state);
                return;
            }

            if (opcode == "VReadlaneB32")
            {
                ApplyReadLane(instruction, state);
                return;
            }

            if (opcode == "VReadfirstlaneB32")
            {
                if (instruction.Destinations.Count == 1 && instruction.Destinations[0] is { Kind: Gen5OperandKind.ScalarRegister } scalarDestinationOperand &&
                    instruction.Sources.Count == 1)
                {
                    var source = ReadVectorOperand(instruction.Sources[0], state);
                    // Keep the selector shape even when its lane value is GPU-dependent.
                    state.WriteScalar(scalarDestinationOperand.Value, _graph.FirstLane(source, state.Exec, instruction.Pc));
                }

                return;
            }

            // Any other write to a vector register replaces its lanes and its uniform value.
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.VectorRegister)
                {
                    state.ClearLanes(destination.Value);
                }
            }

            if (instruction.Encoding == Gen5ShaderEncoding.Vopc ||
                (instruction.Encoding == Gen5ShaderEncoding.Vop3 && opcode.StartsWith("VCmp", StringComparison.Ordinal)))
            {
                ApplyVectorCompare(instruction, state);
                return;
            }

            var hasModifiers = instruction.Control is Gen5Vop3Control { AbsoluteMask: not 0 } or Gen5Vop3Control { NegateMask: not 0 } or
                Gen5Vop3Control { Clamp: true } or Gen5Vop3Control { OutputModifier: not 0 } or Gen5Vop3Control { OperandSelect: not 0 } or
                Gen5SdwaControl or Gen5DppControl or Gen5Dpp8Control or Gen5Vop3pControl;
            var value = hasModifiers ? _graph.Undefined(ScalarValueType.U32) : VectorResult(instruction, state);
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } carryDestination } && !hasModifiers &&
                opcode is "VAddCoU32" or "VSubCoU32" or "VSubrevCoU32" or "VAddCoCiU32" or "VSubCoCiU32" or "VSubrevCoCiU32" or "VMadU64U32")
            {
                var carry = value.IsUndefined ? _graph.Undefined(ScalarValueType.Bool) : state.CarryOut;
                WriteVectorMask(state, carryDestination, _graph.Select(carry, _graph.Constant(1u), _graph.Constant(0u)), carry);
            }
            else if (opcode is "VAddcU32" or "VSubbU32" or "VSubbrevU32" or "VAddCoU32" or "VSubCoU32" or "VSubrevCoU32")
            {
                var carry = value.IsUndefined ? _graph.Undefined(ScalarValueType.Bool) : state.CarryOut;
                WriteVectorMask(state, VccLow, _graph.Select(carry, _graph.Constant(1u), _graph.Constant(0u)), carry);
            }

            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.VectorRegister)
                {
                    state.WriteVector(destination.Value, value);
                    // Multi-dword results are not modelled; the second dword is undefined.
                    value = _graph.Undefined(ScalarValueType.U32);
                }
                else if (destination.Kind == Gen5OperandKind.ScalarRegister)
                {
                    state.WriteScalar(destination.Value, _graph.Undefined(ScalarValueType.U32));
                }
            }
        }

        private ScalarValue VectorResult(Gen5ShaderInstruction instruction, RegisterState state)
        {
            var opcode = instruction.Opcode;
            var sources = instruction.Sources;
            ScalarValue Source(int index) => index < sources.Count ? ReadVectorOperand(sources[index], state) : _graph.Undefined(ScalarValueType.U32);
            ScalarValue Shift(ScalarValue value, ScalarValue count, ScalarOperation operation) =>
                Binary(operation, value, Binary(ScalarOperation.And32, count, _graph.Constant(31u)));
            ScalarValue Low24(ScalarValue value) =>
                _graph.Operation(ScalarOperation.BitFieldUExtract, ScalarValueType.U32, value, _graph.Constant(0u), _graph.Constant(24u));
            ScalarValue SignedLow24(ScalarValue value) =>
                _graph.Operation(ScalarOperation.BitFieldSExtract, ScalarValueType.U32, value, _graph.Constant(0u), _graph.Constant(24u));

            switch (opcode)
            {
                case "VMovB32":
                    return Source(0);
                case "VAddI32":
                    return Binary(ScalarOperation.IAdd32, Source(0), Source(1));
                case "VSubI32":
                    return Binary(ScalarOperation.ISub32, Source(0), Source(1));
                case "VSubrevI32":
                    return Binary(ScalarOperation.ISub32, Source(1), Source(0));
                case "VAddCoU32":
                {
                    var sum = Binary64(ScalarOperation.AddCarry32, Source(0), Source(1));
                    state.CarryOut = Bool(ScalarOperation.LogicalAnd, state.Exec, NotZero(Extract(sum, 1)));
                    return Extract(sum, 0);
                }
                case "VSubCoU32":
                case "VSubrevCoU32":
                {
                    var left = opcode == "VSubCoU32" ? Source(0) : Source(1);
                    var right = opcode == "VSubCoU32" ? Source(1) : Source(0);
                    state.CarryOut = Bool(ScalarOperation.LogicalAnd, state.Exec, Bool(ScalarOperation.UGreaterThan32, right, left));
                    return Binary(ScalarOperation.ISub32, left, right);
                }
                case "VAddcU32":
                case "VAddCoCiU32":
                {
                    var carryIn = _graph.Select(instruction.Sources.Count > 2 ? MaskOf(sources[2], state) : state.Vcc, _graph.Constant(1u), _graph.Constant(0u));
                    var first = Binary64(ScalarOperation.AddCarry32, Source(0), Source(1));
                    var second = Binary64(ScalarOperation.AddCarry32, Extract(first, 0), carryIn);
                    state.CarryOut = Bool(ScalarOperation.LogicalAnd, state.Exec, NotZero(Binary(ScalarOperation.Or32, Extract(first, 1), Extract(second, 1))));
                    return Extract(second, 0);
                }
                case "VMulLoU32":
                case "VMulLoI32":
                    return Binary(ScalarOperation.IMul32, Source(0), Source(1));
                case "VMulHiU32":
                    return Binary(ScalarOperation.UMulHi32, Source(0), Source(1));
                case "VMulHiI32":
                    return Binary(ScalarOperation.SMulHi32, Source(0), Source(1));
                case "VMulU32U24":
                    return Binary(ScalarOperation.IMul32, Low24(Source(0)), Low24(Source(1)));
                case "VMulI32I24":
                    return Binary(ScalarOperation.IMul32, SignedLow24(Source(0)), SignedLow24(Source(1)));
                case "VCvtPkU16U32":
                {
                    var low = Binary(ScalarOperation.UMin32, Source(0), _graph.Constant(0xFFFFu));
                    var high = Binary(ScalarOperation.UMin32, Source(1), _graph.Constant(0xFFFFu));
                    return Binary(ScalarOperation.Or32, low,
                        Binary(ScalarOperation.ShiftLeft32, high, _graph.Constant(16u)));
                }
                case "VCvtPkI16I32":
                {
                    ScalarValue ClampSigned16(ScalarValue value) =>
                        Binary(ScalarOperation.SMin32,
                            Binary(ScalarOperation.SMax32, value, _graph.Constant(0xFFFF8000u)), _graph.Constant(0x7FFFu));
                    var low = Binary(ScalarOperation.And32, ClampSigned16(Source(0)), _graph.Constant(0xFFFFu));
                    var high = Binary(ScalarOperation.ShiftLeft32, ClampSigned16(Source(1)), _graph.Constant(16u));
                    return Binary(ScalarOperation.Or32, low, high);
                }
                case "VMadU32U24":
                    return Binary(ScalarOperation.IAdd32, Binary(ScalarOperation.IMul32, Low24(Source(0)), Low24(Source(1))), Source(2));
                case "VLshlrevB32":
                    return Shift(Source(1), Source(0), ScalarOperation.ShiftLeft32);
                case "VLshrrevB32":
                    return Shift(Source(1), Source(0), ScalarOperation.ShiftRightLogical32);
                case "VAshrrevI32":
                    return Shift(Source(1), Source(0), ScalarOperation.ShiftRightArithmetic32);
                case "VLshlB32":
                    return Shift(Source(0), Source(1), ScalarOperation.ShiftLeft32);
                case "VLshrB32":
                    return Shift(Source(0), Source(1), ScalarOperation.ShiftRightLogical32);
                case "VAshrI32":
                    return Shift(Source(0), Source(1), ScalarOperation.ShiftRightArithmetic32);
                case "VAndB32":
                    return Binary(ScalarOperation.And32, Source(0), Source(1));
                case "VOrB32":
                    return Binary(ScalarOperation.Or32, Source(0), Source(1));
                case "VXorB32":
                    return Binary(ScalarOperation.Xor32, Source(0), Source(1));
                case "VXor3B32":
                    return Binary(ScalarOperation.Xor32,
                        Binary(ScalarOperation.Xor32, Source(0), Source(1)), Source(2));
                case "VXnorB32":
                    return Unary(ScalarOperation.Not32, Binary(ScalarOperation.Xor32, Source(0), Source(1)));
                case "VNotB32":
                    return Unary(ScalarOperation.Not32, Source(0));
                case "VMinU32":
                    return Binary(ScalarOperation.UMin32, Source(0), Source(1));
                case "VMaxU32":
                    return Binary(ScalarOperation.UMax32, Source(0), Source(1));
                case "VMinI32":
                    return Binary(ScalarOperation.SMin32, Source(0), Source(1));
                case "VMaxI32":
                    return Binary(ScalarOperation.SMax32, Source(0), Source(1));
                case "VBfeU32":
                case "VBfeI32":
                {
                    var offset = Binary(ScalarOperation.And32, Source(1), _graph.Constant(31u));
                    var rawCount = Binary(ScalarOperation.And32, Source(2), _graph.Constant(31u));
                    var count = Binary(ScalarOperation.UMin32, rawCount, Binary(ScalarOperation.ISub32, _graph.Constant(32u), offset));
                    return _graph.Operation(opcode == "VBfeU32" ? ScalarOperation.BitFieldUExtract : ScalarOperation.BitFieldSExtract, ScalarValueType.U32, Source(0), offset, count);
                }
                case "VBfiB32":
                {
                    var bits = Source(0);
                    return Binary(ScalarOperation.Or32, Binary(ScalarOperation.And32, bits, Source(1)), Binary(ScalarOperation.And32, Unary(ScalarOperation.Not32, bits), Source(2)));
                }
                case "VBfmB32":
                {
                    var count = Binary(ScalarOperation.And32, Source(0), _graph.Constant(31u));
                    var offset = Binary(ScalarOperation.And32, Source(1), _graph.Constant(31u));
                    return _graph.Operation(ScalarOperation.BitFieldInsert, ScalarValueType.U32, _graph.Constant(0u), _graph.Constant(uint.MaxValue), offset, count);
                }
                case "VLshlAddU32":
                    return Binary(ScalarOperation.IAdd32, Shift(Source(0), Source(1), ScalarOperation.ShiftLeft32), Source(2));
                case "VAddLshlU32":
                    return Shift(Binary(ScalarOperation.IAdd32, Source(0), Source(1)), Source(2), ScalarOperation.ShiftLeft32);
                case "VLshlOrU32":
                    return Binary(ScalarOperation.Or32, Shift(Source(0), Source(1), ScalarOperation.ShiftLeft32), Source(2));
                case "VAndOrB32":
                    return Binary(ScalarOperation.Or32, Binary(ScalarOperation.And32, Source(0), Source(1)), Source(2));
                case "VOr3U32":
                    return Binary(ScalarOperation.Or32, Binary(ScalarOperation.Or32, Source(0), Source(1)), Source(2));
                case "VAdd3U32":
                    return Binary(ScalarOperation.IAdd32, Binary(ScalarOperation.IAdd32, Source(0), Source(1)), Source(2));
                case "VXadU32":
                    return Binary(ScalarOperation.IAdd32, Binary(ScalarOperation.Xor32, Source(0), Source(1)), Source(2));
                case "VSadU32":
                {
                    var low = Binary(ScalarOperation.UMin32, Source(0), Source(1));
                    var high = Binary(ScalarOperation.UMax32, Source(0), Source(1));
                    return Binary(ScalarOperation.IAdd32, Binary(ScalarOperation.ISub32, high, low), Source(2));
                }
                case "VCndmaskB32":
                {
                    var condition = sources.Count > 2 ? MaskOf(sources[2], state) : state.Vcc;
                    return _graph.Select(condition, Source(1), Source(0));
                }
                case "VCvtF32U32":
                    return Unary(ScalarOperation.ConvertF32U32, Source(0));
                case "VCvtU32F32":
                    return Unary(ScalarOperation.ConvertU32F32, Source(0));
                case "VCvtF32I32":
                    return Unary(ScalarOperation.ConvertF32S32, Source(0));
                case "VCvtI32F32":
                    return Unary(ScalarOperation.ConvertS32F32, Source(0));
                case "VMulF32":
                    return Binary(ScalarOperation.FMul, Source(0), Source(1));
                case "VAddF32":
                    return Binary(ScalarOperation.FAdd, Source(0), Source(1));
                case "VSubF32":
                    return Binary(ScalarOperation.FSub, Source(0), Source(1));
                case "VSubrevF32":
                    return Binary(ScalarOperation.FSub, Source(1), Source(0));
                case "VMinF32":
                    return Binary(ScalarOperation.FMin, Source(0), Source(1));
                case "VMaxF32":
                    return Binary(ScalarOperation.FMax, Source(0), Source(1));
                case "VTruncF32":
                    return Unary(ScalarOperation.FTrunc, Source(0));
                default:
                    return _graph.Undefined(ScalarValueType.U32);
            }
        }

        private void ApplyVectorCompare(Gen5ShaderInstruction instruction, RegisterState state)
        {
            var opcode = instruction.Opcode;
            var left = instruction.Sources.Count > 0 ? ReadVectorOperand(instruction.Sources[0], state) : _graph.Undefined(ScalarValueType.U32);
            var right = instruction.Sources.Count > 1 ? ReadVectorOperand(instruction.Sources[1], state) : _graph.Undefined(ScalarValueType.U32);
            var updatesExecutionMask = opcode.StartsWith("VCmpx", StringComparison.Ordinal);
            var suffix = opcode[(updatesExecutionMask ? "VCmpx".Length : "VCmp".Length)..];
            var result = instruction.Control is Gen5SdwaControl or Gen5Vop3Control { AbsoluteMask: not 0 } or Gen5Vop3Control { NegateMask: not 0 }
                ? _graph.Undefined(ScalarValueType.Bool)
                : suffix switch
                {
                    "EqU32" or "EqI32" => Bool(ScalarOperation.IEqual32, left, right),
                    "NeU32" or "NeI32" => Bool(ScalarOperation.INotEqual32, left, right),
                    "LtU32" => Bool(ScalarOperation.ULessThan32, left, right),
                    "GtU32" => Bool(ScalarOperation.UGreaterThan32, left, right),
                    "LeU32" => Bool(ScalarOperation.ULessThanEqual32, left, right),
                    "GeU32" => Bool(ScalarOperation.UGreaterThanEqual32, left, right),
                    "LtI32" => Bool(ScalarOperation.SLessThan32, left, right),
                    "GtI32" => Bool(ScalarOperation.SGreaterThan32, left, right),
                    "LeI32" => Bool(ScalarOperation.SLessThanEqual32, left, right),
                    "GeI32" => Bool(ScalarOperation.SGreaterThanEqual32, left, right),
                    "LeF32" => Bool(ScalarOperation.FLessThanEqual, left, right),
                    "GeF32" => Bool(ScalarOperation.FGreaterThanEqual, left, right),
                    "LtF32" => Bool(ScalarOperation.FLessThan, left, right),
                    "GtF32" => Bool(ScalarOperation.FGreaterThan, left, right),
                    "EqF32" => Bool(ScalarOperation.FEqual, left, right),
                    "NeqF32" or "LgF32" => Bool(ScalarOperation.FNotEqual, left, right),
                    _ => _graph.Undefined(ScalarValueType.Bool),
                };
            var masked = result.IsUndefined ? result : Bool(ScalarOperation.LogicalAnd, state.Exec, result);
            var raw = _graph.Select(masked, _graph.Constant(1u), _graph.Constant(0u));
            // Execution-mask comparisons leave the vector condition registers unchanged.
            if (updatesExecutionMask)
            {
                WriteVectorMask(state, ExecLow, raw, masked);
                return;
            }

            var scalarDestination = instruction.Destinations.FirstOrDefault(destination => destination.Kind == Gen5OperandKind.ScalarRegister);
            if (scalarDestination.Kind == Gen5OperandKind.ScalarRegister && instruction.Destinations.Count != 0)
            {
                WriteVectorMask(state, scalarDestination.Value, raw, masked);
            }
            else
            {
                WriteVectorMask(state, VccLow, raw, masked);
            }
        }

        private void ApplyWriteLane(Gen5ShaderInstruction instruction, RegisterState state)
        {
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0] is not { Kind: Gen5OperandKind.VectorRegister } destination ||
                instruction.Sources.Count < 2)
            {
                return;
            }

            var lane = Read(instruction.Sources[1], state);
            state.WriteVector(destination.Value, _graph.Undefined(ScalarValueType.U32));
            if (!lane.IsConstant)
            {
                state.ClearLanes(destination.Value);
                return;
            }

            var value = Read(instruction.Sources[0], state);
            var key = (destination.Value, lane.ConstantU32 & 63);
            if (value.IsUndefined)
            {
                state.Lanes.Remove(key);
            }
            else
            {
                state.Lanes[key] = value;
            }
        }

        private void ApplyReadLane(Gen5ShaderInstruction instruction, RegisterState state)
        {
            if (instruction.Destinations.Count != 1 ||
                instruction.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } destination)
            {
                return;
            }

            var lane = instruction.Sources.Count > 1 ? Read(instruction.Sources[1], state) : _graph.Undefined(ScalarValueType.U32);
            if (instruction.Sources.Count < 2 ||
                instruction.Sources[0] is not { Kind: Gen5OperandKind.VectorRegister } source ||
                !lane.IsConstant ||
                !state.Lanes.TryGetValue((source.Value, lane.ConstantU32 & 63), out var value))
            {
                state.WriteScalar(destination.Value, _graph.Undefined(ScalarValueType.U32));
                return;
            }

            state.WriteScalar(destination.Value, value);
        }

        // ---- memory instructions ----

        private void ApplyMemory(Gen5ShaderInstruction instruction, RegisterState state)
        {
            if (_recording && _graph.Memory.TryGetIndex(instruction.Pc, 0, out var memoryIndex))
            {
                _graph.Accesses[memoryIndex] = instruction.Control switch
                {
                    Gen5BufferMemoryControl buffer => new MemoryAccessBinding(
                        _graph.Handle(ScalarValueKind.BufferHandle, state.Read(buffer.ScalarResource), state.Read(buffer.ScalarResource + 1),
                            state.Read(buffer.ScalarResource + 2), state.Read(buffer.ScalarResource + 3)),
                        null,
                        null),
                    Gen5ImageControl image => new MemoryAccessBinding(
                        _graph.Handle(ScalarValueKind.ImageHandle, Enumerable.Range(0, 8).Select(index => state.Read(image.ScalarResource + (uint)index)).ToArray()),
                        _graph.Memory[memoryIndex].NeedsSampler
                            ? _graph.Handle(ScalarValueKind.SamplerHandle, Enumerable.Range(0, 4).Select(index => state.Read(image.ScalarSampler + (uint)index)).ToArray())
                            : null,
                        null),
                    Gen5GlobalMemoryControl global => new MemoryAccessBinding(
                        AddressHandleOf(global, state),
                        null,
                        null,
                        global.UsesFlatAddress ? null : state.ReadVector(global.VectorAddress)),
                    _ => null,
                };
            }

            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.VectorRegister)
                {
                    state.ClearLanes(destination.Value);
                    state.WriteVector(destination.Value, _graph.Undefined(ScalarValueType.U32));
                }
                else if (destination.Kind == Gen5OperandKind.ScalarRegister)
                {
                    state.WriteScalar(destination.Value, _graph.Undefined(ScalarValueType.U32));
                }
            }
        }

        // A scalar address pair when the access has one, else the vector address pair.
        private ScalarValue AddressHandleOf(Gen5GlobalMemoryControl control, RegisterState state)
        {
            if (control.ScalarAddress < ScalarRegisterCount - 1 && control.ScalarAddress != NullScalarRegister)
            {
                return _graph.Handle(ScalarValueKind.AddressHandle, state.Read(control.ScalarAddress), state.Read(control.ScalarAddress + 1));
            }

            return _graph.Handle(ScalarValueKind.AddressHandle, state.ReadVector(control.VectorAddress), state.ReadVector(control.VectorAddress + 1));
        }

        // ---- operand helpers ----

        private ScalarValue Read(Gen5Operand operand, RegisterState state)
        {
            switch (operand.Kind)
            {
                case Gen5OperandKind.ScalarRegister:
                    return state.Read(operand.Value);
                case Gen5OperandKind.LiteralConstant:
                    return _graph.Constant(operand.Value);
                case Gen5OperandKind.EncodedConstant:
                    switch (operand.Value)
                    {
                        case 251:
                            return _graph.Select(state.Vcc, _graph.Constant(0u), _graph.Constant(1u));
                        case 252:
                            return _graph.Select(state.Exec, _graph.Constant(0u), _graph.Constant(1u));
                        case 253:
                            return _graph.Select(state.Scc, _graph.Constant(1u), _graph.Constant(0u));
                    }

                    return Gen5InlineConstants.TryDecode(operand.Value, out var value)
                        ? _graph.Constant(value)
                        : _graph.Undefined(ScalarValueType.U32);
                default:
                    return _graph.Undefined(ScalarValueType.U32);
            }
        }

        private ScalarValue ReadVectorOperand(Gen5Operand operand, RegisterState state) =>
            operand.Kind == Gen5OperandKind.VectorRegister ? state.ReadVector(operand.Value) : Read(operand, state);

        private (ScalarValue Low, ScalarValue High) ReadPair(Gen5Operand operand, RegisterState state)
        {
            if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                return (state.Read(operand.Value), state.Read(operand.Value + 1));
            }

            var low = Read(operand, state);
            var negative = operand.Kind == Gen5OperandKind.EncodedConstant && operand.Value is >= 193 and <= 208;
            return (low, _graph.Constant(negative ? uint.MaxValue : 0u));
        }

        // The lane-mask meaning of an operand: EXEC, VCC, a pair written as a mask, or
        // whether the raw pair is nonzero.
        private ScalarValue MaskOf(Gen5Operand operand, RegisterState state)
        {
            if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                switch (operand.Value)
                {
                    case ExecLow:
                        return state.Exec;
                    case VccLow:
                        return state.Vcc;
                }

                if (state.ThreadBits.TryGetValue(operand.Value, out var mask))
                {
                    return mask;
                }
            }

            var (low, high) = ReadPair(operand, state);
            return NotZero(Binary(ScalarOperation.Or32, low, high));
        }

        private ScalarValue Unary(ScalarOperation operation, ScalarValue value) =>
            _graph.Operation(operation, ScalarValueType.U32, value);

        private ScalarValue Binary(ScalarOperation operation, ScalarValue left, ScalarValue right) =>
            _graph.Operation(operation, ScalarValueType.U32, left, right);

        private ScalarValue Binary64(ScalarOperation operation, ScalarValue left, ScalarValue right) =>
            _graph.Operation(operation, ScalarValueType.U64, left, right);

        private ScalarValue Bool(ScalarOperation operation, ScalarValue left, ScalarValue right) =>
            _graph.Operation(operation, ScalarValueType.Bool, left, right);

        private ScalarValue Bool(ScalarOperation operation, ScalarValue value) =>
            _graph.Operation(operation, ScalarValueType.Bool, value);

        private ScalarValue NotZero(ScalarValue value) => Bool(ScalarOperation.INotEqual32, value, _graph.Constant(0u));

        private ScalarValue Construct(ScalarValue low, ScalarValue high) =>
            _graph.Operation(ScalarOperation.Construct64, ScalarValueType.U64, low, high);

        private ScalarValue Extract(ScalarValue pair, uint index) =>
            _graph.Operation(ScalarOperation.Extract64, ScalarValueType.U32, pair, _graph.Constant(index));

        private ScalarValue RightMask32(ScalarValue count) =>
            _graph.Operation(ScalarOperation.BitFieldInsert, ScalarValueType.U32, _graph.Constant(0u), _graph.Constant(uint.MaxValue), _graph.Constant(0u), count);

        private ScalarValue RightMask64(ScalarValue count)
        {
            var below32 = Bool(ScalarOperation.ULessThan32, count, _graph.Constant(32u));
            var above32 = Bool(ScalarOperation.UGreaterThan32, count, _graph.Constant(32u));
            var lowCount = _graph.Select(below32, count, _graph.Constant(32u));
            var highCount = _graph.Select(above32, Binary(ScalarOperation.ISub32, count, _graph.Constant(32u)), _graph.Constant(0u));
            return Construct(RightMask32(lowCount), RightMask32(highCount));
        }
    }

    // The register file of one program point: scalar, uniform vector and lane values,
    // the mask meaning of scalar pairs, and the EXEC, VCC and SCC states.
    private sealed class RegisterState
    {
        public const int ExecSlot = 4096;
        public const int VccSlot = 4097;
        public const int SccSlot = 4098;

        private readonly ScalarValueGraph _graph;

        public RegisterState(ScalarValueGraph graph)
        {
            _graph = graph;
            Scalars = new ScalarValue[ScalarRegisterCount];
            Vectors = new ScalarValue[VectorRegisterCount];
            var undefined = graph.Undefined(ScalarValueType.U32);
            Array.Fill(Scalars, undefined);
            Array.Fill(Vectors, undefined);
            Exec = graph.Undefined(ScalarValueType.Bool);
            Vcc = Exec;
            Scc = Exec;
            CarryOut = Exec;
        }

        public ScalarValue[] Scalars { get; }
        public ScalarValue[] Vectors { get; }
        public Dictionary<(uint Register, uint Lane), ScalarValue> Lanes { get; } = [];
        public Dictionary<uint, ScalarValue> ThreadBits { get; } = [];
        public ScalarValue Exec { get; set; }
        public ScalarValue Vcc { get; set; }
        public ScalarValue Scc { get; set; }
        public ScalarValue CarryOut { get; set; }

        public static int LaneSlot(uint register, uint lane) => 8192 + (int)(register * 64 + lane);

        public static int MaskSlot(uint register) => 32768 + (int)register;

        public ScalarValue Read(uint register) =>
            register < ScalarRegisterCount ? Scalars[register] : _graph.Undefined(ScalarValueType.U32);

        public ScalarValue ReadVector(uint register) =>
            register < VectorRegisterCount ? Vectors[register] : _graph.Undefined(ScalarValueType.U32);

        public void WriteScalar(uint register, ScalarValue value)
        {
            if (register >= ScalarRegisterCount)
            {
                return;
            }

            Scalars[register] = value;
            ThreadBits.Remove(register);
            ThreadBits.Remove(register - 1);
            if (register == ExecLow)
            {
                Exec = _graph.Undefined(ScalarValueType.Bool);
            }
            else if (register == VccLow)
            {
                Vcc = _graph.Undefined(ScalarValueType.Bool);
            }
        }

        public void WritePair(uint register, ScalarValue low, ScalarValue high)
        {
            WriteScalar(register, low);
            WriteScalar(register + 1, high);
        }

        // A vector write reaches the active lanes only; other lanes keep the old value.
        public void WriteVector(uint register, ScalarValue value)
        {
            if (register >= VectorRegisterCount)
            {
                return;
            }

            Vectors[register] = value.IsUndefined ? value : _graph.Select(Exec, value, Vectors[register]);
        }

        public void ClearLanes(uint register)
        {
            if (Lanes.Count == 0)
            {
                return;
            }

            foreach (var key in Lanes.Keys.Where(key => key.Register == register).ToArray())
            {
                Lanes.Remove(key);
            }
        }

        public RegisterState Clone()
        {
            var clone = new RegisterState(_graph);
            Array.Copy(Scalars, clone.Scalars, Scalars.Length);
            Array.Copy(Vectors, clone.Vectors, Vectors.Length);
            foreach (var (key, value) in Lanes)
            {
                clone.Lanes[key] = value;
            }

            foreach (var (key, value) in ThreadBits)
            {
                clone.ThreadBits[key] = value;
            }

            clone.Exec = Exec;
            clone.Vcc = Vcc;
            clone.Scc = Scc;
            clone.CarryOut = CarryOut;
            return clone;
        }

        public bool SameAs(RegisterState other)
        {
            for (var index = 0; index < Scalars.Length; index++)
            {
                if (!SameValue(Scalars[index], other.Scalars[index]) || !SameValue(Vectors[index], other.Vectors[index]))
                {
                    return false;
                }
            }

            if (!SameValue(Exec, other.Exec) || !SameValue(Vcc, other.Vcc) || !SameValue(Scc, other.Scc))
            {
                return false;
            }

            if (Lanes.Count != other.Lanes.Count || ThreadBits.Count != other.ThreadBits.Count)
            {
                return false;
            }

            foreach (var (key, value) in Lanes)
            {
                if (!other.Lanes.TryGetValue(key, out var otherValue) || !ReferenceEquals(value, otherValue))
                {
                    return false;
                }
            }

            foreach (var (key, value) in ThreadBits)
            {
                if (!other.ThreadBits.TryGetValue(key, out var otherValue) || !ReferenceEquals(value, otherValue))
                {
                    return false;
                }
            }

            return true;
        }

        // Two undefined values compare equal so an undefined slot does not keep the walk alive.
        private static bool SameValue(ScalarValue left, ScalarValue right) =>
            ReferenceEquals(left, right) || (left.IsUndefined && right.IsUndefined);
    }
}
