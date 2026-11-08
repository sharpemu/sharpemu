// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
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
    }
}
