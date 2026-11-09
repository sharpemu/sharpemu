// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpEmu.ShaderCompiler.Evaluation;

internal sealed partial class Gen5WaveInterpreter
{
    private const uint SignBit = 0x8000_0000u;
    private const uint MagnitudeBits = 0x7FFF_FFFFu;
    private const uint InfinityBits = 0x7F80_0000u;

    private bool ExecuteFloat(Gen5EvalInstruction instruction)
    {
        if (!TryExecMask(out var exec))
        {
            return false;
        }

        var op = instruction.Op;
        if (!TryReadFloatSource(instruction.Source0, 0, exec, out var a))
        {
            return false;
        }

        var b = a;
        if (op == Gen5EvalOp.VectorMulF32 && !TryReadFloatSource(instruction.Source1, 1, exec, out b))
        {
            return false;
        }

        switch (op)
        {
            case Gen5EvalOp.VectorConvertU32F32:
                ConvertLanes(a);
                WriteVector(instruction.Destination, ResultRow, exec);
                return true;
            case Gen5EvalOp.VectorMulF32:
                MultiplyLanes(a, b);
                break;
            case Gen5EvalOp.VectorReciprocalF32:
                ReciprocalLanes(a, exec);
                break;
            default:
                return Refuse(Gen5EvaluationRefusal.NotEvaluable);
        }

        if (!CheckFloats(ResultRow, exec))
        {
            return false;
        }

        WriteVector(instruction.Destination, ResultRow, exec);
        return true;
    }

    private void MultiplyLanes(int a, int b)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var x = Vector256.LoadUnsafe(ref file, (nuint)(a + lane)).AsSingle();
            var y = Vector256.LoadUnsafe(ref file, (nuint)(b + lane)).AsSingle();
            (x * y).AsUInt32().StoreUnsafe(ref file, (nuint)(ResultRow + lane));
        }
    }

    // Float to unsigned integer with the saturation the translator uses.
    private void ConvertLanes(int a)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var bits = Vector256.LoadUnsafe(ref file, (nuint)(a + lane));
            var value = bits.AsSingle();
            var low = Vector256.ConvertToInt32(value).AsUInt32();
            var high = Vector256.ConvertToInt32(value - Vector256.Create(2147483648f)).AsUInt32() + Vector256.Create(SignBit);
            var result = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(bits, Vector256.Create(0x4F00_0000u)), high, low);
            result = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(bits, Vector256.Create(0x4F80_0000u)), Vector256.Create(uint.MaxValue), result);
            result = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(bits, Vector256.Create(SignBit)), Vector256<uint>.Zero, result);
            result.StoreUnsafe(ref file, (nuint)(ResultRow + lane));
        }
    }

    // The nearest reciprocal, or for a probe run the neighbour on the requested side of the exact one.
    private void ReciprocalLanes(int a, ulong exec)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var inexact = 0ul;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var bits = Vector256.LoadUnsafe(ref file, (nuint)(a + lane));
            var value = bits.AsSingle();
            var nearest = Vector256<float>.One / value;
            var magnitude = bits & Vector256.Create(MagnitudeBits);
            var exactCase = Vector256.Equals(magnitude, Vector256<uint>.Zero) | Vector256.Equals(magnitude, Vector256.Create(InfinityBits)) |
                            Vector256.Equals(magnitude & Vector256.Create(0x7F_FFFFu), Vector256<uint>.Zero);
            inexact |= (ulong)(~exactCase).ExtractMostSignificantBits() << lane;
            var result = nearest.AsUInt32();
            if (_variant != Gen5FloatVariant.Nearest)
            {
                var exactLow = Vector256<double>.One / Vector256.WidenLower(value);
                var exactHigh = Vector256<double>.One / Vector256.WidenUpper(value);
                var aboveLow = Vector256.GreaterThan(Vector256.Abs(Vector256.WidenLower(nearest)), Vector256.Abs(exactLow)).AsInt64();
                var aboveHigh = Vector256.GreaterThan(Vector256.Abs(Vector256.WidenUpper(nearest)), Vector256.Abs(exactHigh)).AsInt64();
                var above = Vector256.Narrow(aboveLow, aboveHigh).AsUInt32();
                var positive = Vector256.GreaterThan(value, Vector256<float>.Zero).AsUInt32();
                var larger = _variant == Gen5FloatVariant.Up ? positive : ~positive;
                var adjust = ~exactCase & (above ^ larger);
                result += adjust & Vector256.ConditionalSelect(larger, Vector256<uint>.One, Vector256.Create(uint.MaxValue));
            }

            result.StoreUnsafe(ref file, (nuint)(ResultRow + lane));
        }

        if ((inexact & exec) != 0)
        {
            SawInexactApproximation = true;
        }
    }

    private bool CheckFloat(uint bits)
    {
        var magnitude = bits & MagnitudeBits;
        if (magnitude > InfinityBits)
        {
            return Refuse(Gen5EvaluationRefusal.FloatNaN);
        }

        if (magnitude != 0 && magnitude < 0x0080_0000u)
        {
            return Refuse(Gen5EvaluationRefusal.FloatDenormal);
        }

        if (!_request.PreservesFloatSpecials && (magnitude == InfinityBits || bits == SignBit))
        {
            return Refuse(Gen5EvaluationRefusal.FloatSpecial);
        }

        return true;
    }

    private bool TryReadFloatSource(in Gen5EvalOperand operand, int slot, ulong exec, out int row) =>
        TryReadSource(operand, slot, exec, floatConstant: true, out row) && CheckFloats(row, exec);

    // Lanes that are NaN, denormal, or a special the host may not preserve; the lane loop names the first one.
    private bool CheckFloats(int row, ulong exec)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var special = _request.PreservesFloatSpecials ? 0u : uint.MaxValue;
        var flagged = 0ul;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            var bits = Vector256.LoadUnsafe(ref file, (nuint)(row + lane));
            var magnitude = bits & Vector256.Create(MagnitudeBits);
            var bad = Vector256.GreaterThan(magnitude, Vector256.Create(InfinityBits)) |
                      Vector256.LessThan(magnitude - Vector256<uint>.One, Vector256.Create(0x007F_FFFFu)) |
                      ((Vector256.Equals(magnitude, Vector256.Create(InfinityBits)) | Vector256.Equals(bits, Vector256.Create(SignBit))) & Vector256.Create(special));
            flagged |= (ulong)bad.ExtractMostSignificantBits() << lane;
        }

        flagged &= exec;
        if (flagged == 0)
        {
            return true;
        }

        var remaining = exec;
        while (remaining != 0)
        {
            var lane = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            if (!CheckFloat(_vectors[row + lane]))
            {
                return false;
            }
        }

        return true;
    }
}
