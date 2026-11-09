// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpEmu.ShaderCompiler.Evaluation;

internal sealed partial class Gen5WaveInterpreter
{
    private bool ExecuteVector(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec))
        {
            return false;
        }

        var op = instruction.Op;
        var sources = SourceCount(op);
        var b = 0;
        var c = 0;
        if (!TryReadSource(instruction.Source0, 0, exec, floatConstant: false, out var a) ||
            (sources > 1 && !TryReadSource(instruction.Source1, 1, exec, floatConstant: false, out b)) ||
            (sources > 2 && op != Gen5EvalOp.VectorMadU64U32 && !TryReadSource(instruction.Source2, 2, exec, floatConstant: false, out c)))
        {
            return false;
        }

        if (sources < 2)
        {
            b = a;
        }

        if (sources < 3 || op == Gen5EvalOp.VectorMadU64U32)
        {
            c = a;
        }

        var destination = (int)instruction.Destination * MaxLanes;
        switch (op)
        {
            case Gen5EvalOp.VectorAddCarry:
                return ExecuteAddCarry(instruction, exec, a, b);
            case Gen5EvalOp.VectorMadU64U32:
                return ExecuteMadU64(instruction, exec, a, b);
            case Gen5EvalOp.VectorMove:
                CopyLanes(a, destination, exec);
                break;
            case Gen5EvalOp.VectorSelect:
            {
                ulong condition;
                if (instruction.HasCarryInOperand ? !TryReadMaskOperand(instruction.Source2, out condition) : !TryReadWaveMask(Gen5WaveState.Vcc, out condition))
                {
                    return false;
                }

                SelectLanes(a, b, condition, destination, exec);
                break;
            }
            case Gen5EvalOp.VectorBitCount:
                BitCountLanes(a, b, destination, exec);
                break;
            case Gen5EvalOp.VectorAdd: Lanes<AddOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorSub: Lanes<SubOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorSubReverse: Lanes<SubOp>(b, a, c, destination, exec); break;
            case Gen5EvalOp.VectorMulLo: Lanes<MulOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorMulHiU: Lanes<MulHiUOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorMulU24: Lanes<MulU24Op>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorMadU24: Lanes<MadU24Op>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorAnd: Lanes<AndOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorShiftLeftReverse: Lanes<ShiftLeftOp>(b, a, c, destination, exec); break;
            case Gen5EvalOp.VectorShiftRightReverse: Lanes<ShiftRightOp>(b, a, c, destination, exec); break;
            case Gen5EvalOp.VectorShiftLeftAdd: Lanes<ShiftLeftAddOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorAndOr: Lanes<AndOrOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorAdd3: Lanes<Add3Op>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorBitfieldInsert: Lanes<BitfieldInsertOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorMinU: Lanes<MinUOp>(a, b, c, destination, exec); break;
            case Gen5EvalOp.VectorConvertF32U32: Lanes<ConvertF32U32Op>(a, b, c, destination, exec); break;
            default:
                return Refuse(Gen5EvaluationRefusal.NotEvaluable);
        }

        NoteWritten(instruction.Destination, exec);
        return true;
    }

    private static int SourceCount(Gen5EvalOp op) => op switch
    {
        Gen5EvalOp.VectorMove or Gen5EvalOp.VectorConvertF32U32 => 1,
        Gen5EvalOp.VectorMadU24 or Gen5EvalOp.VectorMadU64U32 or Gen5EvalOp.VectorShiftLeftAdd or Gen5EvalOp.VectorAndOr or Gen5EvalOp.VectorAdd3 or
            Gen5EvalOp.VectorBitfieldInsert => 3,
        _ => 2,
    };

    private void BitCountLanes(int a, int b, int destination, ulong exec)
    {
        var v = _vectors;
        var remaining = exec;
        while (remaining != 0)
        {
            var lane = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            v[destination + lane] = (uint)BitOperations.PopCount(v[a + lane]) + v[b + lane];
        }
    }

    private interface ILaneOp
    {
        static abstract Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c);
    }

    private void Lanes<TOp>(int a, int b, int c, int destination, ulong mask)
        where TOp : struct, ILaneOp
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var full = mask == _laneMask;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var value = TOp.Apply(
                Vector256.LoadUnsafe(ref file, (nuint)(a + lane)),
                Vector256.LoadUnsafe(ref file, (nuint)(b + lane)),
                Vector256.LoadUnsafe(ref file, (nuint)(c + lane)));
            Store(ref file, destination + lane, value, mask, lane, full);
        }
    }

    private void SelectLanes(int a, int b, ulong condition, int destination, ulong mask)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var full = mask == _laneMask;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var value = Vector256.ConditionalSelect(LaneMask(condition, lane),
                Vector256.LoadUnsafe(ref file, (nuint)(b + lane)),
                Vector256.LoadUnsafe(ref file, (nuint)(a + lane)));
            Store(ref file, destination + lane, value, mask, lane, full);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ShiftLeftLanes(Vector256<uint> value, Vector256<uint> count)
    {
        count &= Vector256.Create(31u);
        if (Avx2.IsSupported)
        {
            return Avx2.ShiftLeftLogicalVariable(value, count);
        }

        Span<uint> lanes = stackalloc uint[LanesPerVector];
        for (var lane = 0; lane < LanesPerVector; lane++)
        {
            lanes[lane] = value[lane] << (int)count[lane];
        }

        return Vector256.Create<uint>(lanes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ShiftRightLanes(Vector256<uint> value, Vector256<uint> count)
    {
        count &= Vector256.Create(31u);
        if (Avx2.IsSupported)
        {
            return Avx2.ShiftRightLogicalVariable(value, count);
        }

        Span<uint> lanes = stackalloc uint[LanesPerVector];
        for (var lane = 0; lane < LanesPerVector; lane++)
        {
            lanes[lane] = value[lane] >> (int)count[lane];
        }

        return Vector256.Create<uint>(lanes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> MultiplyHigh(Vector256<uint> a, Vector256<uint> b)
    {
        if (Avx2.IsSupported)
        {
            var even = Avx2.ShiftRightLogical(Avx2.Multiply(a, b), 32).AsUInt32();
            var odd = Avx2.Multiply(Avx2.ShiftRightLogical(a.AsUInt64(), 32).AsUInt32(), Avx2.ShiftRightLogical(b.AsUInt64(), 32).AsUInt32()).AsUInt32();
            return even | (odd & Vector256.Create(0xFFFF_FFFF_0000_0000ul).AsUInt32());
        }

        Span<uint> lanes = stackalloc uint[LanesPerVector];
        for (var lane = 0; lane < LanesPerVector; lane++)
        {
            lanes[lane] = (uint)(((ulong)a[lane] * b[lane]) >> 32);
        }

        return Vector256.Create<uint>(lanes);
    }

    private readonly struct MulHiUOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => MultiplyHigh(a, b); }
    private readonly struct ConvertF32U32Op : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => (Vector256.ConvertToSingle(Vector256.ShiftRightLogical(a, 16).AsInt32()) * Vector256.Create(65536f) + Vector256.ConvertToSingle((a & Vector256.Create(0xFFFFu)).AsInt32())).AsUInt32(); }
    private readonly struct AddOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => a + b; }
    private readonly struct SubOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => a - b; }
    private readonly struct MulOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => a * b; }
    private readonly struct MulU24Op : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => (a & Vector256.Create(0xFF_FFFFu)) * (b & Vector256.Create(0xFF_FFFFu)); }
    private readonly struct MadU24Op : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => MulU24Op.Apply(a, b, c) + c; }
    private readonly struct AndOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => a & b; }
    private readonly struct ShiftLeftOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => ShiftLeftLanes(a, b); }
    private readonly struct ShiftRightOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => ShiftRightLanes(a, b); }
    private readonly struct ShiftLeftAddOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => ShiftLeftLanes(a, b) + c; }
    private readonly struct AndOrOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => (a & b) | c; }
    private readonly struct Add3Op : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => a + b + c; }
    private readonly struct BitfieldInsertOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => (a & b) | (~a & c); }
    private readonly struct MinUOp : ILaneOp { public static Vector256<uint> Apply(Vector256<uint> a, Vector256<uint> b, Vector256<uint> c) => Vector256.Min(a, b); }

    private bool ExecuteAddCarry(Gen5EvalInstruction instruction, ulong exec, int a, int b)
    {
        ulong carryIn;
        if (instruction.HasCarryInOperand ? !TryReadMaskOperand(instruction.Source2, out carryIn) : !TryReadWaveMask(Gen5WaveState.Vcc, out carryIn))
        {
            return false;
        }

        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var carryOut = 0ul;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var left = Vector256.LoadUnsafe(ref file, (nuint)(a + lane));
            var right = Vector256.LoadUnsafe(ref file, (nuint)(b + lane));
            var partial = left + right;
            var result = partial + (LaneMask(carryIn, lane) & Vector256<uint>.One);
            var carry = Vector256.LessThan(partial, left) | Vector256.LessThan(result, partial);
            result.StoreUnsafe(ref file, (nuint)(ResultRow + lane));
            carryOut |= (ulong)carry.ExtractMostSignificantBits() << lane;
        }

        WriteVector(instruction.Destination, ResultRow, exec);
        return TryWriteWaveMask(instruction.MaskDestination, carryOut & exec);
    }

    private bool ExecuteMadU64(Gen5EvalInstruction instruction, ulong exec, int a, int b)
    {
        var operand = instruction.Source2;
        var addendLow = 0ul;
        var uniform = operand.Kind != Gen5EvalOperandKind.Vector;
        var register = (int)operand.Register;
        if (!uniform)
        {
            if ((exec & ~_vectorDefined[register]) != 0 || (exec & ~_vectorDefined[register + 1]) != 0)
            {
                return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
            }
        }
        else
        {
            if (!TryReadScalar64(operand, out addendLow, out var low, out var high))
            {
                return false;
            }

            if (!low || !high)
            {
                return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
            }
        }

        var v = _vectors;
        var carryOut = 0ul;
        for (var lane = 0; lane < _width; lane++)
        {
            var addend = uniform ? addendLow : v[register * MaxLanes + lane] | ((ulong)v[(register + 1) * MaxLanes + lane] << 32);
            var wide = (ulong)v[a + lane] * v[b + lane] + addend;
            v[ResultRow + lane] = (uint)wide;
            v[ResultRow + MaxLanes + lane] = (uint)(wide >> 32);
            carryOut |= wide < addend ? 1ul << lane : 0;
        }

        WriteVector(instruction.Destination, ResultRow, exec);
        WriteVector(instruction.Destination + 1, ResultRow + MaxLanes, exec);
        return TryWriteWaveMask(instruction.MaskDestination, carryOut & exec);
    }

    private bool ExecuteCompare(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec))
        {
            return false;
        }

        if (!TryReadSource(instruction.Source0, 0, exec, floatConstant: false, out var a) ||
            !TryReadSource(instruction.Source1, 1, exec, floatConstant: false, out var b))
        {
            return false;
        }

        var condition = CompareLanes(instruction.Compare, instruction.Type == Gen5EvalType.I32, a, b) & exec;
        return TryWriteWaveMask(instruction.WritesExec ? Gen5WaveState.Exec : instruction.MaskDestination, condition);
    }

    private ulong CompareLanes(Gen5EvalCompare compare, bool signed, int a, int b)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var less = 0ul;
        var equal = 0ul;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var left = Vector256.LoadUnsafe(ref file, (nuint)(a + lane));
            var right = Vector256.LoadUnsafe(ref file, (nuint)(b + lane));
            var below = signed ? Vector256.LessThan(left.AsInt32(), right.AsInt32()).AsUInt32() : Vector256.LessThan(left, right);
            less |= (ulong)below.ExtractMostSignificantBits() << lane;
            equal |= (ulong)Vector256.Equals(left, right).ExtractMostSignificantBits() << lane;
        }

        return compare switch
        {
            Gen5EvalCompare.False => 0,
            Gen5EvalCompare.Less => less,
            Gen5EvalCompare.Equal => equal,
            Gen5EvalCompare.LessEqual => less | equal,
            Gen5EvalCompare.Greater => ~(less | equal),
            Gen5EvalCompare.NotEqual => ~equal,
            Gen5EvalCompare.GreaterEqual => ~less,
            _ => ulong.MaxValue,
        };
    }

    private bool ExecuteLaneRead(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec))
        {
            return false;
        }

        if (exec == 0)
        {
            return Refuse(Gen5EvaluationRefusal.NoActiveLane);
        }

        var lane = BitOperations.TrailingZeroCount(exec);
        uint value;
        if (instruction.Source0.Kind == Gen5EvalOperandKind.Vector)
        {
            var register = (int)instruction.Source0.Register;
            if (((_vectorDefined[register] >> lane) & 1) == 0)
            {
                return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
            }

            value = _vectors[register * MaxLanes + lane];
        }
        else if (!TryReadScalarOperand(instruction.Source0, out value))
        {
            return false;
        }

        SetScalar(instruction.Destination, value);
        return true;
    }

    private bool ExecuteBuffer(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec))
        {
            return false;
        }

        if (exec == 0)
        {
            return true;
        }

        if (!TryReadDescriptor(instruction.ScalarResource, out var word0, out var word1, out var word2, out var word3))
        {
            return false;
        }

        var baseAddress = word0 | ((ulong)(word1 & 0xFFFF) << 32);
        var stride = (word1 >> 16) & 0x3FFF;
        var swizzled = (word1 >> 30) != 0;
        var addThreadId = ((word3 >> 23) & 1) != 0;
        var resourceType = word3 >> 30;
        if (swizzled || addThreadId || resourceType != 0)
        {
            return Refuse(Gen5EvaluationRefusal.Descriptor);
        }

        var store = instruction.Op == Gen5EvalOp.BufferStore;
        if (instruction.Formatted)
        {
            var format = (word3 >> 12) & 0x7F;
            if (store && format == 0)
            {
                return true;
            }

            if (store ? format is not (20 or 21 or 22) : format != 20 || (word3 & 7) != 4)
            {
                return Refuse(Gen5EvaluationRefusal.Descriptor);
            }
        }

        if ((baseAddress & 3) != 0)
        {
            return Refuse(Gen5EvaluationRefusal.Unaligned);
        }

        if (!TryReadScalarOperand(instruction.Source2, out var scalarOffset))
        {
            return false;
        }

        var footprint = stride == 0 ? word2 : (ulong)stride * word2;
        var addressRegister = (int)instruction.VectorAddress;
        if ((instruction.IndexEnabled || instruction.OffsetEnabled) && (exec & ~_vectorDefined[addressRegister]) != 0)
        {
            return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }

        var offsetRegister = addressRegister + (instruction.IndexEnabled ? 1 : 0);
        if (instruction.IndexEnabled && instruction.OffsetEnabled && (exec & ~_vectorDefined[offsetRegister]) != 0)
        {
            return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }

        var dwords = (int)instruction.DwordCount;
        var data = (int)instruction.VectorData;
        if (store)
        {
            for (var component = 0; component < dwords; component++)
            {
                if ((exec & ~_vectorDefined[data + component]) != 0)
                {
                    return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
                }
            }
        }

        var v = _vectors;
        var windowEnd = baseAddress + footprint;
        var constantOffset = unchecked((uint)instruction.OffsetBytes + scalarOffset);
        var indexed = instruction.IndexEnabled;
        var offsetted = instruction.OffsetEnabled;
        var span = (ulong)dwords * sizeof(uint);
        var run = TryBufferRun(store, exec, baseAddress, windowEnd, footprint, constantOffset, stride, indexed ? addressRegister : -1,
            offsetted ? offsetRegister : -1, dwords, data);
        if (run != BufferRun.NotARun)
        {
            return run == BufferRun.Done;
        }

        var remaining = exec;
        while (remaining != 0)
        {
            var lane = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            var index = indexed ? v[addressRegister * MaxLanes + lane] : 0u;
            var offset = offsetted ? v[offsetRegister * MaxLanes + lane] : 0u;
            var byteAddress = unchecked(constantOffset + offset + index * stride);
            if ((byteAddress & 3) != 0)
            {
                return Refuse(Gen5EvaluationRefusal.Unaligned);
            }

            if ((ulong)byteAddress + span > footprint)
            {
                return Refuse(Gen5EvaluationRefusal.OutOfBounds);
            }

            var address = baseAddress + byteAddress;
            for (var component = 0; component < dwords; component++, address += sizeof(uint))
            {
                if (store)
                {
                    if (_reads.WasRead(address))
                    {
                        return Refuse(Gen5EvaluationRefusal.ReadAfterWrite);
                    }

                    var refusal = _writes.Write(address, v[(data + component) * MaxLanes + lane], (_wave << 6) | lane);
                    if (refusal != Gen5EvaluationRefusal.None)
                    {
                        return Refuse(refusal);
                    }
                }
                else if (!TryLoad(address, baseAddress, windowEnd, out v[LoadRow + component * MaxLanes + lane]))
                {
                    return false;
                }
            }
        }

        if (!store)
        {
            for (var component = 0; component < dwords; component++)
            {
                WriteVector((uint)(data + component), LoadRow + component * MaxLanes, exec);
            }
        }

        return true;
    }

    private readonly uint[] _run = new uint[4 * MaxLanes];

    private BufferRun RefuseRun(Gen5EvaluationRefusal refusal)
    {
        Refuse(refusal);
        return BufferRun.Refused;
    }

    private enum BufferRun
    {
        NotARun,
        Done,
        Refused,
    }

    // Lanes whose records lie back to back in bounds move as one run of words.
    private BufferRun TryBufferRun(bool store, ulong exec, ulong baseAddress, ulong windowEnd, ulong footprint, uint constantOffset, uint stride,
        int indexRegister, int offsetRegister, int dwords, int data)
    {
        var first = BitOperations.TrailingZeroCount(exec);
        var lanes = BitOperations.PopCount(exec);
        var span = (uint)(dwords * sizeof(uint));
        if (footprint < span)
        {
            return BufferRun.NotARun;
        }

        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var limit = Vector256.Create((uint)Math.Min(footprint - span, uint.MaxValue));
        var steps = Vector256.Create(0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u) * Vector256.Create(span);
        var start = 0u;
        var firstAddress = 0u;
        var flagged = 0ul;
        var scattered = 0ul;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var addresses = Vector256.Create(constantOffset);
            if (indexRegister >= 0)
            {
                addresses += Vector256.LoadUnsafe(ref file, (nuint)(indexRegister * MaxLanes + lane)) * Vector256.Create(stride);
            }

            if (offsetRegister >= 0)
            {
                addresses += Vector256.LoadUnsafe(ref file, (nuint)(offsetRegister * MaxLanes + lane));
            }

            if (lane == (first & ~(LanesPerVector - 1)))
            {
                firstAddress = addresses.GetElement(first & (LanesPerVector - 1));
                start = firstAddress - (uint)first * span;
            }

            var expected = Vector256.Create(start + (uint)lane * span) + steps;
            addresses.StoreUnsafe(ref file, (nuint)(ResultRow + lane));
            var bad = Vector256.GreaterThan(addresses, limit) | ~Vector256.Equals(addresses & Vector256.Create(3u), Vector256<uint>.Zero);
            flagged |= (ulong)bad.ExtractMostSignificantBits() << lane;
            scattered |= (ulong)(~Vector256.Equals(addresses, expected)).ExtractMostSignificantBits() << lane;
        }

        if ((flagged & exec) != 0)
        {
            return BufferRun.NotARun;
        }

        if ((scattered & exec) != 0 || exec >> first != (lanes == 64 ? ulong.MaxValue : (1ul << lanes) - 1))
        {
            return GatherLanes(store, exec, baseAddress, windowEnd, dwords, data);
        }

        var address = baseAddress + firstAddress;
        var words = lanes * dwords;
        var run = _run.AsSpan(0, words);
        var v = _vectors;
        if (store)
        {
            if (_reads.AnyRead(address, words))
            {
                return RefuseRun(Gen5EvaluationRefusal.ReadAfterWrite);
            }

            var written = _writes.WriteRun(address, v, data * MaxLanes, MaxLanes, dwords, first, lanes, _wave << 6);
            return written == Gen5EvaluationRefusal.None ? BufferRun.Done : RefuseRun(written);
        }

        for (var word = 0; word < words; word++)
        {
            if (_writes.IsWritten(address + (ulong)word * sizeof(uint)))
            {
                return RefuseRun(Gen5EvaluationRefusal.ReadAfterWrite);
            }
        }

        var read = _reads.TryReadRun(address, run, baseAddress, windowEnd);
        if (read != Gen5EvaluationRefusal.None)
        {
            return RefuseRun(read);
        }

        for (var component = 0; component < dwords; component++)
        {
            var row = (data + component) * MaxLanes;
            for (var lane = 0; lane < lanes; lane++)
            {
                v[row + first + lane] = run[lane * dwords + component];
            }

            NoteWritten((uint)(data + component), exec);
        }

        return BufferRun.Done;
    }

    // Lanes in bounds whose records are not back to back; the byte offsets are in the result row.
    private BufferRun GatherLanes(bool store, ulong exec, ulong baseAddress, ulong windowEnd, int dwords, int data)
    {
        var v = _vectors;
        var reads = _reads;
        var writes = _writes;
        var writerBase = _wave << 6;
        var remaining = exec;
        while (remaining != 0)
        {
            var lane = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            var address = baseAddress + v[ResultRow + lane];
            for (var component = 0; component < dwords; component++, address += sizeof(uint))
            {
                if (store)
                {
                    if (reads.WasRead(address))
                    {
                        return RefuseRun(Gen5EvaluationRefusal.ReadAfterWrite);
                    }

                    var refusal = writes.Write(address, v[(data + component) * MaxLanes + lane], writerBase | lane);
                    if (refusal != Gen5EvaluationRefusal.None)
                    {
                        return RefuseRun(refusal);
                    }

                    continue;
                }

                if (writes.IsWritten(address))
                {
                    return RefuseRun(Gen5EvaluationRefusal.ReadAfterWrite);
                }

                var read = reads.TryRead(address, baseAddress, windowEnd, kernelRead: true, out v[LoadRow + component * MaxLanes + lane]);
                if (read != Gen5EvaluationRefusal.None)
                {
                    return RefuseRun(read);
                }
            }
        }

        if (!store)
        {
            for (var component = 0; component < dwords; component++)
            {
                WriteVector((uint)(data + component), LoadRow + component * MaxLanes, exec);
            }
        }

        return BufferRun.Done;
    }
}
