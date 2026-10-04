// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // GCN special-function instructions consume FP32 denormals as signed zero.
        // Express that behavior with integer operations so it is independent of the
        // Vulkan driver's floating-point denormal mode.
        private uint EmitFlushF32DenormToSignedZero(uint value)
        {
            var bits = Bitcast(_uintType, value);
            var absolute = _module.AddInstruction(
                SpirvOp.BitwiseAnd,
                _uintType,
                bits,
                UInt(0x7FFF_FFFF));
            var sign = _module.AddInstruction(
                SpirvOp.BitwiseAnd,
                _uintType,
                bits,
                UInt(0x8000_0000));
            var nonZero = IsNotZero(absolute);
            var subnormal = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                absolute,
                UInt(0x0080_0000));
            var flush = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                nonZero,
                subnormal);
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                flush,
                sign,
                bits);
            return Bitcast(_floatType, selected);
        }

        // The guest SIN/COS input is measured in cycles. Reduce it before the
        // GLSL radians conversion, including the hardware's large-finite rule.
        private uint EmitTrigCycleF32(uint source, bool preserveSignedZero)
        {
            var fraction = Ext(10, _floatType, source);
            var bits = Bitcast(_uintType, source);
            var absolute = _module.AddInstruction(
                SpirvOp.BitwiseAnd,
                _uintType,
                bits,
                UInt(0x7FFF_FFFF));
            var large = _module.AddInstruction(
                SpirvOp.UGreaterThanEqual,
                _boolType,
                absolute,
                UInt(0x4B00_0000));
            var finite = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                absolute,
                UInt(0x7F80_0000));
            var largeFinite = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                large,
                finite);
            var reduced = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                largeFinite,
                Float(0),
                fraction);
            if (!preserveSignedZero)
            {
                return reduced;
            }

            var zero = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                absolute,
                UInt(0));
            return _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                zero,
                source,
                reduced);
        }

        // SPIR-V leaves NaN and out-of-range float-to-integer conversions undefined. Select a
        // safe operand before conversion, then restore the guest's exact saturated boundary.
        private uint EmitSaturatingF32ToU32(
            uint value,
            float upperBound,
            float safeUpper,
            uint upperResult)
        {
            var zero = Float(0);
            var nan = _module.AddInstruction(SpirvOp.IsNan, _boolType, value);
            var low = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                value,
                zero);
            var high = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                value,
                Float(upperBound));
            var truncated = Ext(3, _floatType, value);
            var nanOrLow = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                nan,
                low);
            var safeLow = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                nanOrLow,
                zero,
                truncated);
            var safe = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                high,
                Float(safeUpper),
                safeLow);
            var converted = _module.AddInstruction(
                SpirvOp.ConvertFToU,
                _uintType,
                safe);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                high,
                UInt(upperResult),
                converted);
        }

        private uint EmitSaturatingF32ToI32(uint value)
        {
            var lowerBound = Float(-2147483648.0f);
            var upperBound = Float(2147483648.0f);
            var nan = _module.AddInstruction(SpirvOp.IsNan, _boolType, value);
            var low = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                value,
                lowerBound);
            var high = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                value,
                upperBound);
            var truncated = Ext(3, _floatType, value);
            var safeLow = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                low,
                lowerBound,
                truncated);
            var safeHigh = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                high,
                Float(2147483520.0f),
                safeLow);
            var safe = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                nan,
                Float(0),
                safeHigh);
            var converted = Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ConvertFToS,
                    _intType,
                    safe));
            var clampedHigh = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                high,
                UInt(0x7FFF_FFFF),
                converted);
            var clamped = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                low,
                UInt(0x8000_0000),
                clampedHigh);
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                nan,
                UInt(0),
                clamped);
        }
    }
}
