// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.ShaderCompiler.Resources;

// Evaluates one graph operation over concrete operands. Bool values are 0 or 1, U32
// values use the low dword, F32 values are the bits of the low dword.
public static class ScalarOperationSemantics
{
    public static bool TryEvaluate(ScalarOperation operation, ReadOnlySpan<ulong> operands, out ulong result)
    {
        result = 0;
        var first = operands.Length > 0 ? operands[0] : 0;
        var second = operands.Length > 1 ? operands[1] : 0;
        var third = operands.Length > 2 ? operands[2] : 0;
        var fourth = operands.Length > 3 ? operands[3] : 0;
        var first32 = (uint)first;
        var second32 = (uint)second;
        switch (operation)
        {
            case ScalarOperation.ConvertF32U32:
                result = BitConverter.SingleToUInt32Bits((float)first32);
                return true;
            case ScalarOperation.ConvertU32F32:
            {
                var value = Float(first);
                if (!float.IsFinite(value) || value < 0f || (double)value > uint.MaxValue)
                {
                    return false;
                }

                result = (uint)value;
                return true;
            }
            case ScalarOperation.ConvertF32S32:
                result = BitConverter.SingleToUInt32Bits((float)(int)first32);
                return true;
            case ScalarOperation.ConvertS32F32:
            {
                var value = Float(first);
                if (!float.IsFinite(value) || (double)value < int.MinValue || (double)value > int.MaxValue)
                {
                    return false;
                }

                result = unchecked((uint)(int)value);
                return true;
            }
            case ScalarOperation.Construct64:
                result = first32 | ((ulong)second32 << 32);
                return true;
            case ScalarOperation.Extract64:
                if (second32 >= 2)
                {
                    return false;
                }

                result = (uint)(first >> (int)(second32 * 32));
                return true;
            case ScalarOperation.AddCarry32:
            {
                var sum = (ulong)first32 + second32;
                result = (uint)sum | ((sum >> 32) << 32);
                return true;
            }
            case ScalarOperation.IAdd32:
                result = unchecked(first32 + second32);
                return true;
            case ScalarOperation.IAdd64:
                result = unchecked(first + second);
                return true;
            case ScalarOperation.ISub32:
                result = unchecked(first32 - second32);
                return true;
            case ScalarOperation.ISub64:
                result = unchecked(first - second);
                return true;
            case ScalarOperation.IMul32:
                result = unchecked(first32 * second32);
                return true;
            case ScalarOperation.IMul64:
                result = unchecked(first * second);
                return true;
            case ScalarOperation.UMin32:
                result = Math.Min(first32, second32);
                return true;
            case ScalarOperation.UMax32:
                result = Math.Max(first32, second32);
                return true;
            case ScalarOperation.SMin32:
                result = unchecked((uint)Math.Min((int)first32, (int)second32));
                return true;
            case ScalarOperation.SMax32:
                result = unchecked((uint)Math.Max((int)first32, (int)second32));
                return true;
            case ScalarOperation.IAbs32:
                result = unchecked((uint)Math.Abs((long)(int)first32));
                return true;
            case ScalarOperation.UMulHi32:
                result = (uint)(((ulong)first32 * second32) >> 32);
                return true;
            case ScalarOperation.SMulHi32:
                result = unchecked((uint)(((long)(int)first32 * (int)second32) >> 32));
                return true;
            case ScalarOperation.BitCount32:
                result = (uint)BitOperations.PopCount(first32);
                return true;
            case ScalarOperation.BitReverse32:
                result = ReverseBits(first32);
                return true;
            case ScalarOperation.FindLowestBit32:
                result = first32 == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount(first32);
                return true;
            case ScalarOperation.FindHighestBit32:
                result = first32 == 0 ? uint.MaxValue : (uint)(31 - BitOperations.LeadingZeroCount(first32));
                return true;
            case ScalarOperation.ShiftLeft32:
                result = first32 << (int)(second32 & 31);
                return true;
            case ScalarOperation.ShiftLeft64:
                result = first << (int)(second32 & 63);
                return true;
            case ScalarOperation.ShiftRightLogical32:
                result = first32 >> (int)(second32 & 31);
                return true;
            case ScalarOperation.ShiftRightLogical64:
                result = first >> (int)(second32 & 63);
                return true;
            case ScalarOperation.ShiftRightArithmetic32:
                result = unchecked((uint)((int)first32 >> (int)(second32 & 31)));
                return true;
            case ScalarOperation.ShiftRightArithmetic64:
                result = unchecked((ulong)((long)first >> (int)(second32 & 63)));
                return true;
            case ScalarOperation.And32:
                result = first32 & second32;
                return true;
            case ScalarOperation.And64:
                result = first & second;
                return true;
            case ScalarOperation.Or32:
                result = first32 | second32;
                return true;
            case ScalarOperation.Xor32:
                result = first32 ^ second32;
                return true;
            case ScalarOperation.Not32:
                result = ~first32;
                return true;
            case ScalarOperation.QuadMask32:
                result = ((first32 | (first32 >> 1) | (first32 >> 2) | (first32 >> 3)) & 0x1111_1111u) * 0xFu;
                return true;
            case ScalarOperation.BitFieldUExtract:
            case ScalarOperation.BitFieldSExtract:
            {
                var offset = second32;
                var width = (uint)third;
                if (offset > 32 || width > 32 - offset)
                {
                    return false;
                }

                if (width == 0)
                {
                    result = 0;
                    return true;
                }

                var mask = width == 32 ? uint.MaxValue : (1u << (int)width) - 1;
                var bits = (first32 >> (int)offset) & mask;
                if (operation == ScalarOperation.BitFieldSExtract && width < 32 && (bits & (1u << (int)(width - 1))) != 0)
                {
                    bits |= ~mask;
                }

                result = bits;
                return true;
            }
            case ScalarOperation.BitFieldInsert:
            {
                var offset = (uint)third;
                var width = (uint)fourth;
                if (offset > 32 || width > 32 - offset)
                {
                    return false;
                }

                if (width == 0)
                {
                    result = first32;
                    return true;
                }

                var mask = width == 32 ? uint.MaxValue : ((1u << (int)width) - 1) << (int)offset;
                result = (first32 & ~mask) | ((second32 << (int)offset) & mask);
                return true;
            }
            case ScalarOperation.ULessThan32:
                result = first32 < second32 ? 1u : 0u;
                return true;
            case ScalarOperation.ULessThanEqual32:
                result = first32 <= second32 ? 1u : 0u;
                return true;
            case ScalarOperation.UGreaterThan32:
                result = first32 > second32 ? 1u : 0u;
                return true;
            case ScalarOperation.UGreaterThanEqual32:
                result = first32 >= second32 ? 1u : 0u;
                return true;
            case ScalarOperation.SLessThan32:
                result = (int)first32 < (int)second32 ? 1u : 0u;
                return true;
            case ScalarOperation.SLessThanEqual32:
                result = (int)first32 <= (int)second32 ? 1u : 0u;
                return true;
            case ScalarOperation.SGreaterThan32:
                result = (int)first32 > (int)second32 ? 1u : 0u;
                return true;
            case ScalarOperation.SGreaterThanEqual32:
                result = (int)first32 >= (int)second32 ? 1u : 0u;
                return true;
            case ScalarOperation.IEqual32:
                result = first32 == second32 ? 1u : 0u;
                return true;
            case ScalarOperation.INotEqual32:
                result = first32 != second32 ? 1u : 0u;
                return true;
            case ScalarOperation.IEqual64:
                result = first == second ? 1u : 0u;
                return true;
            case ScalarOperation.INotEqual64:
                result = first != second ? 1u : 0u;
                return true;
            case ScalarOperation.LogicalAnd:
                result = first != 0 && second != 0 ? 1u : 0u;
                return true;
            case ScalarOperation.LogicalOr:
                result = first != 0 || second != 0 ? 1u : 0u;
                return true;
            case ScalarOperation.LogicalXor:
                result = (first != 0) != (second != 0) ? 1u : 0u;
                return true;
            case ScalarOperation.LogicalNot:
                result = first == 0 ? 1u : 0u;
                return true;
            case ScalarOperation.FMul:
                result = BitConverter.SingleToUInt32Bits(Float(first) * Float(second));
                return true;
            case ScalarOperation.FAdd:
                result = BitConverter.SingleToUInt32Bits(Float(first) + Float(second));
                return true;
            case ScalarOperation.FSub:
                result = BitConverter.SingleToUInt32Bits(Float(first) - Float(second));
                return true;
            case ScalarOperation.FMin:
                result = BitConverter.SingleToUInt32Bits(MathF.Min(Float(first), Float(second)));
                return true;
            case ScalarOperation.FMax:
                result = BitConverter.SingleToUInt32Bits(MathF.Max(Float(first), Float(second)));
                return true;
            case ScalarOperation.FTrunc:
                result = BitConverter.SingleToUInt32Bits(MathF.Truncate(Float(first)));
                return true;
            case ScalarOperation.FIsNan:
                result = float.IsNaN(Float(first)) ? 1u : 0u;
                return true;
            case ScalarOperation.FLessThanEqual:
                result = Float(first) <= Float(second) ? 1u : 0u;
                return true;
            case ScalarOperation.FGreaterThanEqual:
                result = Float(first) >= Float(second) ? 1u : 0u;
                return true;
            case ScalarOperation.FLessThan:
                result = Float(first) < Float(second) ? 1u : 0u;
                return true;
            case ScalarOperation.FGreaterThan:
                result = Float(first) > Float(second) ? 1u : 0u;
                return true;
            case ScalarOperation.FEqual:
                result = Float(first) == Float(second) ? 1u : 0u;
                return true;
            case ScalarOperation.FNotEqual:
                result = Float(first) != Float(second) ? 1u : 0u;
                return true;
            default:
                return false;
        }
    }

    private static float Float(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);

    private static uint ReverseBits(uint value)
    {
        value = ((value >> 1) & 0x55555555u) | ((value & 0x55555555u) << 1);
        value = ((value >> 2) & 0x33333333u) | ((value & 0x33333333u) << 2);
        value = ((value >> 4) & 0x0F0F0F0Fu) | ((value & 0x0F0F0F0Fu) << 4);
        value = ((value >> 8) & 0x00FF00FFu) | ((value & 0x00FF00FFu) << 8);
        return (value >> 16) | (value << 16);
    }
}
