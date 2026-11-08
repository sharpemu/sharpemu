// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Evaluation;

internal sealed partial class Gen5WaveInterpreter
{
    private bool ExecuteScalar(Gen5EvalInstruction instruction)
    {
        var op = instruction.Op;
        switch (op)
        {
            case Gen5EvalOp.ScalarMove64:
            case Gen5EvalOp.ScalarNot64:
            case Gen5EvalOp.ScalarSelect64:
            case Gen5EvalOp.ScalarAnd64:
            case Gen5EvalOp.ScalarAndNot64:
                return ExecuteScalar64(instruction);
            case Gen5EvalOp.ScalarAndSaveExec:
                return ExecuteAndSaveExec(instruction);
            case Gen5EvalOp.ScalarMoveK:
                SetScalar(instruction.Destination, instruction.Source0.Bits);
                return true;
            case Gen5EvalOp.ScalarCompare:
            case Gen5EvalOp.ScalarCompareK:
            {
                if (!TryReadScalarOperand(instruction.Source0, out var left) || !TryReadScalarOperand(instruction.Source1, out var right))
                {
                    return false;
                }

                SetScc(CompareInteger(instruction.Compare, instruction.Type, left, right));
                return true;
            }
        }

        if (!TryReadScalarOperand(instruction.Source0, out var a))
        {
            return false;
        }

        if (op == Gen5EvalOp.ScalarMove)
        {
            SetScalar(instruction.Destination, a);
            return true;
        }

        if (!TryReadScalarOperand(instruction.Source1, out var b))
        {
            return false;
        }

        uint result;
        switch (op)
        {
            case Gen5EvalOp.ScalarAddI:
                result = a + b;
                SetScc(SignedAddOverflow(a, b, result));
                break;
            case Gen5EvalOp.ScalarSubI:
                result = a - b;
                SetScc(SignedSubOverflow(a, b, result));
                break;
            case Gen5EvalOp.ScalarSubU:
                result = a - b;
                SetScc(b > a);
                break;
            case Gen5EvalOp.ScalarMul:
                result = a * b;
                break;
            case Gen5EvalOp.ScalarMulHiU:
                result = (uint)(((ulong)a * b) >> 32);
                break;
            case Gen5EvalOp.ScalarShiftLeft:
                result = a << (int)(b & 31);
                SetScc(result != 0);
                break;
            case Gen5EvalOp.ScalarShiftLeftAdd:
                result = (a << instruction.Parameter) + b;
                break;
            case Gen5EvalOp.ScalarBitfieldMask:
            {
                var count = a & 31;
                var offset = b & 31;
                if (offset + count > 32)
                {
                    return Refuse(Gen5EvaluationRefusal.UndefinedBehaviour);
                }

                result = count == 0 ? 0 : (uint.MaxValue >> (int)(32 - count)) << (int)offset;
                break;
            }
            default:
                return Refuse(Gen5EvaluationRefusal.NotEvaluable);
        }

        SetScalar(instruction.Destination, result);
        return true;
    }

    private bool ExecuteScalar64(Gen5EvalInstruction instruction)
    {
        var op = instruction.Op;
        if (!TryReadScalar64(instruction.Source0, out var a, out var aLow, out var aHigh))
        {
            return false;
        }

        ulong b = 0;
        bool bLow = true, bHigh = true;
        if (op is not (Gen5EvalOp.ScalarMove64 or Gen5EvalOp.ScalarNot64) && !TryReadScalar64(instruction.Source1, out b, out bLow, out bHigh))
        {
            return false;
        }

        if (op == Gen5EvalOp.ScalarSelect64 && !_sccDefined)
        {
            return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }

        var low = aLow && bLow;
        var high = aHigh && bHigh;
        ulong value;
        switch (op)
        {
            case Gen5EvalOp.ScalarMove64:
                value = a;
                break;
            case Gen5EvalOp.ScalarNot64:
                value = ~a;
                break;
            case Gen5EvalOp.ScalarSelect64:
                value = _scc ? a : b;
                low = _scc ? aLow : bLow;
                high = _scc ? aHigh : bHigh;
                break;
            case Gen5EvalOp.ScalarAnd64:
                value = a & b;
                break;
            case Gen5EvalOp.ScalarAndNot64:
                value = a & ~b;
                break;
            default:
                return Refuse(Gen5EvaluationRefusal.NotEvaluable);
        }

        if (!TryWriteScalar64(instruction.Destination, value, low, high))
        {
            return false;
        }

        if (op is not (Gen5EvalOp.ScalarMove64 or Gen5EvalOp.ScalarSelect64))
        {
            SetScc64(value, low, high);
        }

        return true;
    }

    private bool ExecuteAndSaveExec(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec) || !TryReadScalar64(instruction.Source0, out var source, out var sourceLow, out var sourceHigh))
        {
            return false;
        }

        if (!sourceLow || (_width == 64 && !sourceHigh))
        {
            return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }

        var updated = exec & source & _laneMask;
        if (!TryWriteScalar64(instruction.Destination, exec, true, _width == 64))
        {
            return false;
        }

        SetScalar(Gen5WaveState.Exec, (uint)updated);
        if (_width == 64)
        {
            SetScalar(Gen5WaveState.Exec + 1, (uint)(updated >> 32));
        }
        else
        {
            ForgetScalar(Gen5WaveState.Exec + 1);
        }

        SetScc(updated != 0);
        return true;
    }

    private bool ExecuteScalarMemory(Gen5EvalInstruction instruction)
    {
        var dynamic = 0u;
        if (instruction.DynamicOffsetRegister is { } offsetRegister && !TryReadScalarRegister(offsetRegister, out dynamic))
        {
            return false;
        }

        Span<uint> loaded = stackalloc uint[16];
        var count = (int)instruction.DwordCount;
        var resource = instruction.ScalarResource;
        if (instruction.Op == Gen5EvalOp.ScalarBufferLoad)
        {
            if (!TryReadDescriptor(resource, out var word0, out var word1, out var word2, out _))
            {
                return false;
            }

            var baseAddress = word0 | ((ulong)(word1 & 0xFFFF) << 32);
            var stride = (word1 >> 16) & 0x3FFF;
            var size = stride == 0 ? word2 : (ulong)stride * word2;
            var start = dynamic + unchecked((uint)instruction.OffsetBytes);
            if ((baseAddress & 3) != 0 || (start & 3) != 0)
            {
                return Refuse(Gen5EvaluationRefusal.Unaligned);
            }

            for (var component = 0; component < count; component++)
            {
                var offset = start + (uint)component * sizeof(uint);
                if ((ulong)offset + sizeof(uint) > size)
                {
                    return Refuse(Gen5EvaluationRefusal.OutOfBounds);
                }

                if (!TryLoad(baseAddress + offset, baseAddress, baseAddress + size, out loaded[component]))
                {
                    return false;
                }
            }
        }
        else
        {
            if (!TryReadScalarRegister(resource, out var low) || !TryReadScalarRegister(resource + 1, out var high))
            {
                return false;
            }

            var address = (low | ((ulong)high << 32)) + dynamic + unchecked((ulong)(long)instruction.OffsetBytes);
            if ((address & 3) != 0 || address >> 48 != 0)
            {
                return Refuse(Gen5EvaluationRefusal.Unaligned);
            }

            var end = address + (ulong)count * sizeof(uint);
            for (var component = 0; component < count; component++)
            {
                if (!TryLoad(address + (ulong)component * sizeof(uint), address, end, out loaded[component]))
                {
                    return false;
                }
            }
        }

        for (var component = 0; component < count; component++)
        {
            SetScalar(instruction.Destination + (uint)component, loaded[component]);
        }

        return true;
    }

    private bool TryLoad(ulong address, ulong windowStart, ulong windowEnd, out uint value)
    {
        value = 0;
        if (_writes.IsWritten(address))
        {
            return Refuse(Gen5EvaluationRefusal.ReadAfterWrite);
        }

        var refusal = _reads.TryRead(address, windowStart, windowEnd, kernelRead: true, out value);
        return refusal == Gen5EvaluationRefusal.None || Refuse(refusal);
    }

    private bool TryReadDescriptor(uint register, out uint word0, out uint word1, out uint word2, out uint word3)
    {
        word0 = word1 = word2 = word3 = 0;
        return register + 3 <= 127 &&
               TryReadScalarRegister(register, out word0) && TryReadScalarRegister(register + 1, out word1) &&
               TryReadScalarRegister(register + 2, out word2) && TryReadScalarRegister(register + 3, out word3);
    }

    private bool TryReadScalar64(in Gen5EvalOperand operand, out ulong value, out bool lowDefined, out bool highDefined)
    {
        value = 0;
        lowDefined = highDefined = true;
        switch (operand.Kind)
        {
            case Gen5EvalOperandKind.Scalar:
            {
                var register = operand.Register;
                if ((register & 1) != 0 || register > Gen5WaveState.Exec)
                {
                    return Refuse(Gen5EvaluationRefusal.MaskRegister);
                }

                lowDefined = IsScalarDefined(register);
                highDefined = IsScalarDefined(register + 1);
                value = _scalars[register] | ((ulong)_scalars[register + 1] << 32);
                return true;
            }
            case Gen5EvalOperandKind.Constant:
                value = operand.Bits64;
                return true;
            default:
                if (!TryReadScalarOperand(operand, out var narrow))
                {
                    return false;
                }

                value = narrow;
                return true;
        }
    }

    private bool TryWriteScalar64(uint register, ulong value, bool lowDefined, bool highDefined)
    {
        if ((register & 1) != 0 || register > Gen5WaveState.Exec)
        {
            return Refuse(Gen5EvaluationRefusal.MaskRegister);
        }

        if (lowDefined)
        {
            SetScalar(register, (uint)value);
        }
        else
        {
            ForgetScalar(register);
        }

        if (highDefined)
        {
            SetScalar(register + 1, (uint)(value >> 32));
        }
        else
        {
            ForgetScalar(register + 1);
        }

        return true;
    }

    private void SetScc(bool value)
    {
        _scc = value;
        _sccDefined = true;
    }

    private void SetScc64(ulong value, bool lowDefined, bool highDefined)
    {
        var low = (uint)value;
        var high = (uint)(value >> 32);
        if ((lowDefined && highDefined) || (lowDefined && low != 0) || (highDefined && high != 0))
        {
            SetScc((lowDefined && low != 0) || (highDefined && high != 0));
        }
        else
        {
            _sccDefined = false;
        }
    }

    private static bool SignedAddOverflow(uint left, uint right, uint result) => (((left ^ result) & (right ^ result)) >> 31) != 0;

    private static bool SignedSubOverflow(uint left, uint right, uint result) => (((left ^ right) & (left ^ result)) >> 31) != 0;

    private static bool CompareInteger(Gen5EvalCompare compare, Gen5EvalType type, uint left, uint right)
    {
        var signed = type == Gen5EvalType.I32;
        return compare switch
        {
            Gen5EvalCompare.False => false,
            Gen5EvalCompare.Less => signed ? (int)left < (int)right : left < right,
            Gen5EvalCompare.Equal => left == right,
            Gen5EvalCompare.LessEqual => signed ? (int)left <= (int)right : left <= right,
            Gen5EvalCompare.Greater => signed ? (int)left > (int)right : left > right,
            Gen5EvalCompare.NotEqual => left != right,
            Gen5EvalCompare.GreaterEqual => signed ? (int)left >= (int)right : left >= right,
            _ => true,
        };
    }
}
