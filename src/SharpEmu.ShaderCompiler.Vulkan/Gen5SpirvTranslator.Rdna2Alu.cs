// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // VOP3P opcodes 0x00..0x0d operate on two independent 16-bit
        // integer lanes. Source selectors choose the half routed to each
        // result lane and the packed NEG controls flip that lane's sign bit
        // (they are not a two's-complement integer negate).
        private bool TryEmitPackedInteger16(
            Gen5ShaderInstruction instruction,
            out uint result,
            out string error)
        {
            result = 0;
            error = string.Empty;
            if (instruction.Control is not Gen5Vop3pControl control)
            {
                error = $"missing vop3p control for {instruction.Opcode}";
                return false;
            }

            // Integer VOP3P encodings do not define a saturation/clamp result.
            // Reject the modifier explicitly instead of silently producing an
            // unclamped value that can corrupt packed indices.
            if (control.Clamp)
            {
                error = $"VOP3P integer clamp is not implemented for {instruction.Opcode}";
                return false;
            }

            var requiredSources = instruction.Opcode is "VPkMadI16" or "VPkMadU16"
                ? 3
                : 2;
            if (instruction.Sources.Count < requiredSources)
            {
                error = $"missing packed integer source for {instruction.Opcode}";
                return false;
            }

            var low = EmitPackedInteger16Lane(instruction, control, highLane: false);
            var high = EmitPackedInteger16Lane(instruction, control, highLane: true);
            result = BitwiseOr(
                BitwiseAnd(low, UInt(0xFFFF)),
                ShiftLeftLogical(BitwiseAnd(high, UInt(0xFFFF)), UInt(16)));
            return true;
        }

        private uint EmitPackedInteger16Lane(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            bool highLane)
        {
            var opcode = instruction.Opcode;
            var isSignedMinMax = opcode is "VPkMaxI16" or "VPkMinI16";
            var left = GetPackedInteger16Source(
                instruction,
                control,
                0,
                highLane,
                signExtend: isSignedMinMax);
            var right = GetPackedInteger16Source(
                instruction,
                control,
                1,
                highLane,
                signExtend: isSignedMinMax || opcode == "VPkAshrrevI16");

            return opcode switch
            {
                "VPkMadI16" => IAdd(
                    _module.AddInstruction(SpirvOp.IMul, _uintType, left, right),
                    GetPackedInteger16Source(
                        instruction,
                        control,
                        2,
                        highLane,
                        signExtend: true)),
                "VPkMadU16" => IAdd(
                    _module.AddInstruction(SpirvOp.IMul, _uintType, left, right),
                    GetPackedInteger16Source(
                        instruction,
                        control,
                        2,
                        highLane,
                        signExtend: false)),
                "VPkMulLoU16" => _module.AddInstruction(
                    SpirvOp.IMul,
                    _uintType,
                    left,
                    right),
                "VPkAddI16" or "VPkAddU16" => IAdd(left, right),
                "VPkSubI16" or "VPkSubU16" => ISubU(left, right),
                "VPkLshlrevB16" => ShiftLeftLogical(
                    right,
                    BitwiseAnd(left, UInt(15))),
                "VPkLshrrevB16" => ShiftRightLogical(
                    right,
                    BitwiseAnd(left, UInt(15))),
                "VPkAshrrevI16" => Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.ShiftRightArithmetic,
                        _intType,
                        Bitcast(_intType, right),
                        BitwiseAnd(left, UInt(15)))),
                "VPkMaxI16" => SelectPackedSigned(left, right, isMax: true),
                "VPkMinI16" => SelectPackedSigned(left, right, isMax: false),
                "VPkMaxU16" => SelectPackedUnsigned(left, right, isMax: true),
                "VPkMinU16" => SelectPackedUnsigned(left, right, isMax: false),
                _ => throw new InvalidOperationException(
                    $"unsupported packed integer opcode {opcode}"),
            };
        }

        private uint GetPackedInteger16Source(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int sourceIndex,
            bool highLane,
            bool signExtend)
        {
            var raw = GetRawSource(instruction, sourceIndex);
            var selectMask = highLane ? control.OpSelHiMask : control.OpSelMask;
            var lane = ((selectMask >> sourceIndex) & 1) != 0
                ? ShiftRightLogical(raw, UInt(16))
                : raw;
            lane = BitwiseAnd(lane, UInt(0xFFFF));

            // RDNA packed integer NEG is a sign-bit toggle, including for the
            // unsigned operations. This is observable for values such as 1,
            // which becomes 0x8001 rather than 0xffff.
            var negateMask = highLane ? control.NegHiMask : control.NegLoMask;
            if (((negateMask >> sourceIndex) & 1) != 0)
            {
                lane = BitwiseXor(lane, UInt(0x8000));
            }

            if (!signExtend)
            {
                return lane;
            }

            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, lane),
                    UInt(0),
                    UInt(16)));
        }

        private uint SelectPackedSigned(uint left, uint right, bool isMax)
        {
            var condition = _module.AddInstruction(
                isMax ? SpirvOp.SGreaterThan : SpirvOp.SLessThan,
                _boolType,
                Bitcast(_intType, left),
                Bitcast(_intType, right));
            return SelectU(condition, left, right);
        }

        private uint SelectPackedUnsigned(uint left, uint right, bool isMax)
        {
            var condition = _module.AddInstruction(
                isMax ? SpirvOp.UGreaterThan : SpirvOp.ULessThan,
                _boolType,
                left,
                right);
            return SelectU(condition, left, right);
        }

        private bool TryEmitFloat64VectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister ||
                instruction.Sources.Count == 0)
            {
                error = $"invalid FP64 operands for {instruction.Opcode}";
                return false;
            }

            _module.AddCapability(SpirvCapability.Float64);
            var doubleType = _module.TypeFloat(64);
            var destination = instruction.Destinations[0].Value;

            if (instruction.Opcode == "VCvtF32F64")
            {
                var source = Bitcast(
                    doubleType,
                    GetFloat64SourceBits(instruction, 0));
                var converted = _module.AddInstruction(
                    SpirvOp.FConvert,
                    _floatType,
                    source);
                StoreV(destination, EmitFloatResult(instruction, converted));
                return true;
            }

            uint value;
            if (instruction.Opcode == "VCvtF64I32")
            {
                value = _module.AddInstruction(
                    SpirvOp.ConvertSToF,
                    doubleType,
                    Bitcast(_intType, GetRawSource(instruction, 0)));
            }
            else
            {
                var source = Bitcast(
                    doubleType,
                    GetFloat64SourceBits(instruction, 0));
                value = _module.AddInstruction(
                    SpirvOp.FDiv,
                    doubleType,
                    Double(doubleType, 1.0),
                    source);
            }

            StoreV64(destination, Bitcast(_ulongType, value));
            return true;
        }

        private uint GetFloat64SourceBits(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            uint bits;
            if (operand.Kind == Gen5OperandKind.LiteralConstant)
            {
                // FP64 literals occupy the high dword and have an implicit
                // zero low dword in the GCN scalar-source encoding.
                bits = _module.Constant64(
                    _ulongType,
                    (ulong)operand.Value << 32);
            }
            else if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                     operand.Value is >= 240 and <= 248 &&
                     Gen5InlineConstants.TryDecode(operand.Value, out var floatBits))
            {
                // Floating inline constants are promoted numerically to F64.
                // The architectural 1/(2*pi) constant has more precision than
                // its f32 spelling and therefore needs its exact f64 payload.
                var doubleBits = floatBits == 0x3E22_F983u
                    ? 0x3FC4_5F30_6DC9_C882UL
                    : unchecked((ulong)BitConverter.DoubleToInt64Bits(
                        BitConverter.UInt32BitsToSingle(floatBits)));
                bits = _module.Constant64(_ulongType, doubleBits);
            }
            else
            {
                bits = GetRawSource64(instruction, sourceIndex);
            }

            if (instruction.Control is not Gen5Vop3Control control)
            {
                return bits;
            }

            if ((control.AbsoluteMask & (1u << sourceIndex)) != 0)
            {
                bits = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    bits,
                    _module.Constant64(_ulongType, 0x7FFF_FFFF_FFFF_FFFFUL));
            }

            if ((control.NegateMask & (1u << sourceIndex)) != 0)
            {
                bits = _module.AddInstruction(
                    SpirvOp.BitwiseXor,
                    _ulongType,
                    bits,
                    _module.Constant64(_ulongType, 0x8000_0000_0000_0000UL));
            }

            return bits;
        }

        private void StoreV64(uint register, uint value)
        {
            StoreV(
                register,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            StoreV(
                register + 1,
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    ShiftRightLogical64(
                        value,
                        _module.Constant64(_ulongType, 32))));
        }

        private uint Double(uint doubleType, double value) =>
            _module.Constant64(
                doubleType,
                unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

        private uint GetInteger16Source(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool signed)
        {
            var value = GetRawSource(
                instruction,
                sourceIndex,
                applySdwaIntegerModifiers: false);
            if (instruction.Control is Gen5Vop3Control selectionControl &&
                (selectionControl.OperandSelect & (1u << sourceIndex)) != 0)
            {
                value = ShiftRightLogical(value, UInt(16));
            }

            value = BitwiseAnd(value, UInt(0xFFFF));
            uint absoluteMask = 0;
            uint negateMask = 0;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5SdwaControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5DppControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
            }

            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                value = BitwiseAnd(value, UInt(0x7FFF));
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                value = BitwiseXor(value, UInt(0x8000));
            }

            if (!signed)
            {
                return value;
            }

            return Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.BitFieldSExtract,
                    _intType,
                    Bitcast(_intType, value),
                    UInt(0),
                    UInt(16)));
        }

        private bool TryEmitNativeInteger16Shift(
            Gen5ShaderInstruction instruction,
            uint destination,
            out uint result,
            out string error)
        {
            result = 0;
            error = string.Empty;
            if (instruction.Sources.Count < 2 ||
                instruction.Control is not Gen5Vop3Control control ||
                control.AbsoluteMask != 0 ||
                control.NegateMask != 0 ||
                control.OutputModifier != 0 ||
                control.Clamp ||
                control.ScalarDestination is not null)
            {
                error = $"invalid native 16-bit shift modifiers in {instruction.Opcode}";
                return false;
            }

            var count = BitwiseAnd(
                GetInteger16Source(instruction, 0, signed: false),
                UInt(15));
            var arithmetic = instruction.Opcode == "VAshrrevI16";
            var value = GetInteger16Source(instruction, 1, signed: arithmetic);
            var shifted = instruction.Opcode switch
            {
                "VLshlrevB16" => ShiftLeftLogical(value, count),
                "VLshrrevB16" => ShiftRightLogical(value, count),
                _ => ShiftRightArithmetic(value, count),
            };
            result = MergeInteger16Result(instruction, destination, shifted);
            return true;
        }

        private uint MergeInteger16Result(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint value)
        {
            value = BitwiseAnd(value, UInt(0xFFFF));
            // The common SDWA destination path applies DST_SEL/DST_UNUSED
            // after this switch, so leave its result in the low bits here.
            if (instruction.Control is Gen5SdwaControl)
            {
                return value;
            }

            var current = LoadV(destination);
            return instruction.Control is Gen5Vop3Control control &&
                   (control.OperandSelect & 8u) != 0
                ? BitwiseOr(
                    BitwiseAnd(current, UInt(0x0000_FFFF)),
                    ShiftLeftLogical(value, UInt(16)))
                : BitwiseOr(BitwiseAnd(current, UInt(0xFFFF_0000)), value);
        }

        private uint GetDestinationFloat16(
            Gen5ShaderInstruction instruction,
            uint destination)
        {
            var value = LoadV(destination);
            if (instruction.Control is Gen5Vop3Control control &&
                (control.OperandSelect & 8u) != 0)
            {
                value = ShiftRightLogical(value, UInt(16));
            }

            return Bitcast(_floatType, EmitHalfToFloat(value));
        }

        private uint EmitFloat16UnaryExt(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint operation) =>
            EmitFloat16Result(
                instruction,
                destination,
                Ext(operation, _floatType, GetFloat16Source(instruction, 0)));

        private uint EmitInvalidNegativeFloat16Result(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint source,
            uint value)
        {
            var negative = _module.AddInstruction(
                SpirvOp.FOrdLessThan,
                _boolType,
                source,
                Float(0));
            var clamp = instruction.Control switch
            {
                Gen5Vop3Control control => control.Clamp,
                Gen5SdwaControl control => control.Clamp,
                _ => false,
            };
            var invalid = MergeInteger16Result(
                instruction,
                destination,
                UInt(clamp ? 0u : 0xFE00u));
            return SelectU(
                negative,
                invalid,
                EmitFloat16Result(instruction, destination, value));
        }

        private uint EmitFloat16TrigResult(
            Gen5ShaderInstruction instruction,
            uint destination,
            bool sine)
        {
            var source = GetFloat16Source(instruction, 0);
            var radians = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                EmitTrigCycleF32(source, preserveSignedZero: sine),
                Float(MathF.Tau));
            var result = Ext(sine ? 13u : 14u, _floatType, radians);
            var fraction = Ext(10, _floatType, source);
            uint cardinal;
            if (sine)
            {
                var whole = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    fraction,
                    Float(0));
                var half = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    fraction,
                    Float(0.5f));
                var magnitude = BitwiseAnd(
                    Bitcast(_uintType, source),
                    UInt(0x7FFF_FFFF));
                cardinal = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        whole,
                        IsNotZero(magnitude)),
                    half);
            }
            else
            {
                var quarter = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    fraction,
                    Float(0.25f));
                var threeQuarters = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    fraction,
                    Float(0.75f));
                cardinal = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    quarter,
                    threeQuarters);
            }

            result = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                cardinal,
                Float(0),
                result);
            var normal = EmitFloat16Result(instruction, destination, result);
            var sourceMagnitude = BitwiseAnd(
                Bitcast(_uintType, source),
                UInt(0x7FFF_FFFF));
            var infinite = Equal(sourceMagnitude, 0x7F80_0000);
            var clamp = instruction.Control switch
            {
                Gen5Vop3Control control => control.Clamp,
                Gen5SdwaControl control => control.Clamp,
                _ => false,
            };
            var invalid = MergeInteger16Result(
                instruction,
                destination,
                UInt(clamp ? 0u : 0xFE00u));
            return SelectU(infinite, invalid, normal);
        }

        private uint EmitFrexpExponent(Gen5ShaderInstruction instruction)
        {
            var bits = Bitcast(_uintType, GetFloatSource(instruction, 0));
            var exponent = BitwiseAnd(ShiftRightLogical(bits, UInt(23)), UInt(0xFF));
            var mantissa = BitwiseAnd(bits, UInt(0x007F_FFFF));
            var normal = ISubU(exponent, UInt(126));
            var mostSignificant = Ext(75, _uintType, mantissa);
            var subnormal = ISubU(mostSignificant, UInt(148));
            var denormal = SelectU(IsNotZero(mantissa), subnormal, UInt(0));
            var finite = SelectU(IsNotZero(exponent), normal, denormal);
            return SelectU(Equal(exponent, 0xFF), UInt(0), finite);
        }

        private uint EmitFrexpMantissa(Gen5ShaderInstruction instruction)
        {
            var bits = Bitcast(_uintType, GetFloatSource(instruction, 0));
            var exponent = BitwiseAnd(ShiftRightLogical(bits, UInt(23)), UInt(0xFF));
            var mantissa = BitwiseAnd(bits, UInt(0x007F_FFFF));
            var sign = BitwiseAnd(bits, UInt(0x8000_0000));
            var baseBits = BitwiseOr(sign, UInt(0x3F00_0000));
            var normal = BitwiseOr(baseBits, mantissa);
            var mostSignificant = Ext(75, _uintType, mantissa);
            var shift = ISubU(UInt(23), mostSignificant);
            var fraction = BitwiseAnd(
                ShiftLeftLogical(mantissa, shift),
                UInt(0x007F_FFFF));
            var subnormal = BitwiseOr(baseBits, fraction);
            var finite = SelectU(
                IsNotZero(exponent),
                normal,
                SelectU(Equal(mantissa, 0), bits, subnormal));
            var resultBits = SelectU(Equal(exponent, 0xFF), bits, finite);
            return EmitFloatResult(
                instruction,
                Bitcast(_floatType, resultBits));
        }
    }
}
