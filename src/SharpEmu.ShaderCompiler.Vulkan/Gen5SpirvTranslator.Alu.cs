// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitVectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "VNop")
            {
                return true;
            }

            if (instruction.Control is Gen5SdwaControl sdwa &&
                (sdwa.Source0Select == 7 ||
                 sdwa.Source1Select == 7 ||
                 sdwa.DestinationSelect == 7 ||
                 sdwa.DestinationUnused == 3))
            {
                error = $"reserved SDWA selector/modifier in {instruction.Opcode}";
                return false;
            }

            if (instruction.Control is Gen5DppControl dppControl &&
                !IsSupportedDppControl(dppControl.Control))
            {
                error = $"unsupported DPP16 control 0x{dppControl.Control:X3}";
                return false;
            }

            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
            {
                return TryEmitVectorCompare(instruction, out error);
            }

            if (instruction.Opcode == "VReadfirstlaneB32")
            {
                if (instruction.Destinations.Count == 0 ||
                    instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                    instruction.Sources.Count == 0)
                {
                    error = "invalid read-first-lane operands";
                    return false;
                }

                var value = GetRawSource(instruction, 0);
                if (_subgroupInvocationIdInput != 0)
                {
                    if (_emulateWave64)
                    {
                        value = BroadcastFirstWave64Active(value);
                    }
                    else
                    {
                        // SPIR-V's BroadcastFirst uses the first host-active
                        // invocation. Guest EXEC is modeled as data, so obtain the
                        // guest-active mask explicitly and broadcast from its first
                        // set lane instead. This also updates the private SGPR copy
                        // for lanes that are currently disabled and may be restored
                        // by a later saveexec sequence.
                        var activeLanes = _module.AddInstruction(
                            SpirvOp.GroupNonUniformBallot,
                            _uvec4Type,
                            UInt(3),
                            Load(_boolType, _exec));
                        var activeLow = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _uintType,
                            activeLanes,
                            0);
                        var firstActiveLane = Ext(73, _uintType, activeLow);
                        value = _module.AddInstruction(
                            SpirvOp.GroupNonUniformBroadcast,
                            _uintType,
                            UInt(3),
                            value,
                            firstActiveLane);
                    }
                }

                StoreS(instruction.Destinations[0].Value, value);
                return true;
            }

            if (instruction.Opcode == "VReadlaneB32")
            {
                return TryEmitReadlane(instruction, out error);
            }

            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing vector destination";
                return false;
            }

            uint result;
            switch (instruction.Opcode)
            {
                case "VMovB32":
                    result = GetRawSource(instruction, 0);
                    break;
                case "VWritelaneB32":
                {
                    // vdst[lane(src1)] = src0
                    // Per-lane: if current lane == src1, write src0, else keep old value.
                    var oldValue = LoadV(destination);
                    var src0 = GetRawSource(instruction, 0);
                    var laneSelect = GetRawSource(instruction, 1);
                    var currentLane = _subgroupInvocationIdInput != 0
                        ? BitwiseAnd(
                            Load(_uintType, _subgroupInvocationIdInput),
                            UInt(RdnaWaveLaneCount - 1))
                        : UInt(0);
                    var isTargetLane = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        currentLane,
                        laneSelect);
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        isTargetLane,
                        src0,
                        oldValue);
                    // Writelane writes to a specific lane regardless of exec mask.
                    StoreV(destination, result, guardWithExec: false);
                    return true;
                }
                case "VCndmaskB32":
                {
                    var condition = instruction.Sources.Count > 2
                        ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                        : Load(_boolType, _vcc);
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        condition,
                        GetRawSource(instruction, 1),
                        GetRawSource(instruction, 0));
                    break;
                }
                case "VCvtU32F32":
                    result = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    break;
                case "VCvtI32F32":
                case "VCvtRpiI32F32":
                case "VCvtFlrI32F32":
                {
                    var source = GetFloatSource(instruction, 0);
                    if (instruction.Opcode == "VCvtRpiI32F32")
                    {
                        source = Ext(9, _floatType, source);
                    }
                    else if (instruction.Opcode == "VCvtFlrI32F32")
                    {
                        source = Ext(8, _floatType, source);
                    }

                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertFToS, _intType, source));
                    break;
                }
                case "VCvtF32I32":
                {
                    var signed = Bitcast(_intType, GetRawSource(instruction, 0));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, signed));
                    break;
                }
                case "VCvtF32U32":
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ConvertUToF,
                            _floatType,
                            GetRawSource(instruction, 0)));
                    break;
                case "VCvtF32Ubyte0":
                case "VCvtF32Ubyte1":
                case "VCvtF32Ubyte2":
                case "VCvtF32Ubyte3":
                {
                    var shift = (uint)(instruction.Opcode[^1] - '0') * 8;
                    var raw = ShiftRightLogical(GetRawSource(instruction, 0), UInt(shift));
                    raw = BitwiseAnd(raw, UInt(0xFF));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, raw));
                    break;
                }
                case "VCvtF16F32":
                {
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        Float(0));
                    result = BitwiseAnd(Ext(58, _uintType, vector), UInt(0xFFFF));
                    break;
                }
                case "VCvtF32F16":
                {
                    var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, 0));
                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        unpacked,
                        0);
                    result = Bitcast(_uintType, value);
                    break;
                }
                case "VCvtOffF32I4":
                    result = EmitCvtOffF32I4(instruction);
                    break;
                case "VCvtPkU8F32":
                {
                    var converted = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    var offset = ShiftLeftLogical(
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(3)),
                        UInt(3));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        GetRawSource(instruction, 2),
                        converted,
                        offset,
                        UInt(8));
                    break;
                }
                case "VRcpF32":
                case "VRcpIflagF32":
                    result = EmitFloatResult(
                        instruction,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            Float(1),
                            GetFloatSource(instruction, 0)));
                    break;
                case "VLogF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(30, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VLdexpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            53,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            Bitcast(_intType, GetRawSource(instruction, 1))));
                    break;
                case "VExpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(29, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRsqF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(32, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFractF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(10, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VTruncF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(3, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VCeilF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(9, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRndneF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(2, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFloorF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(8, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSqrtF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(31, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSinF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            13,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VCosF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            14,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VAddF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FAdd);
                    break;
                case "VSubF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub);
                    break;
                case "VSubrevF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub, reverse: true);
                    break;
                case "VMulF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FMul);
                    break;
                case "VMinF32":
                    result = EmitFloatExtBinary(instruction, 37);
                    break;
                case "VMaxF32":
                    result = EmitFloatExtBinary(instruction, 40);
                    break;
                case "VMadF32":
                case "VFmaF32":
                case "VMadMkF32":
                case "VMadAkF32":
                case "VFmaMkF32":
                case "VFmaAkF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            GetFloatSource(instruction, 2)));
                    break;
                case "VMacF32":
                case "VFmacF32":
                {
                    var addend = Bitcast(_floatType, LoadV(destination));
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            addend));
                    break;
                }
                case "VMin3F32":
                    result = EmitFloatTernaryExt(instruction, 37);
                    break;
                case "VMax3F32":
                    result = EmitFloatTernaryExt(instruction, 40);
                    break;
                case "VAndB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseAnd);
                    break;
                case "VOrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseOr);
                    break;
                case "VXorB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseXor);
                    break;
                case "VXnorB32":
                {
                    var xor = EmitIntegerBinary(instruction, SpirvOp.BitwiseXor);
                    result = _module.AddInstruction(SpirvOp.Not, _uintType, xor);
                    break;
                }
                case "VNotB32":
                    result = _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VBfrevB32":
                    result = _module.AddInstruction(
                        SpirvOp.BitReverse,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VFfblB32":
                    result = Bitcast(
                        _uintType,
                        Ext(
                            73,
                            _intType,
                            Bitcast(_intType, GetRawSource(instruction, 0))));
                    break;
                case "VAddI32":
                case "VAddU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.IAdd);
                    break;
                case "VAddcU32":
                case "VAddCoCiU32":
                    result = EmitAddWithCarry(instruction);
                    break;
                case "VSubI32":
                case "VSubU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub);
                    break;
                case "VSubrevI32":
                case "VSubrevU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub, reverse: true);
                    break;
                case "VSubbU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: false);
                    break;
                case "VSubbrevU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: true);
                    break;
                case "VMulLoU32":
                case "VMulLoI32":
                case "VMulU32U24":
                    result = EmitIntegerBinary(instruction, SpirvOp.IMul);
                    break;
                case "VMulHiU32":
                case "VMulHiU32U24":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    if (instruction.Opcode == "VMulHiU32U24")
                    {
                        left = BitwiseAnd(left, UInt(0x00FF_FFFF));
                        right = BitwiseAnd(right, UInt(0x00FF_FFFF));
                    }

                    var wideLeft = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        left);
                    var wideRight = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        right);
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _ulongType,
                        wideLeft,
                        wideRight);
                    result = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        ShiftRightLogical64(
                            product,
                            _module.Constant64(_ulongType, 32)));
                    break;
                }
                case "VMulHiI32":
                {
                    var wideLeft = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, GetRawSource(instruction, 0)));
                    var wideRight = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, GetRawSource(instruction, 1)));
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _longType,
                        wideLeft,
                        wideRight);
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.SConvert,
                            _intType,
                            _module.AddInstruction(
                                SpirvOp.ShiftRightArithmetic,
                                _longType,
                                product,
                                _module.Constant64(_longType, 32))));
                    break;
                }
                case "VBcntU32B32":
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.BitCount,
                            _uintType,
                            GetRawSource(instruction, 0)),
                        GetRawSource(instruction, 1));
                    break;
                case "VMbcntHiU32B32":
                {
                    var guestLane = GuestWaveLane();
                    var lane = BitwiseAnd(guestLane, UInt(31));
                    var partialMask = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        ShiftLeftLogical(UInt(1), lane),
                        UInt(1));
                    var countedBits = _module.AddInstruction(
                        SpirvOp.BitCount,
                        _uintType,
                        BitwiseAnd(GetRawSource(instruction, 0), partialMask));
                    var isUpperHalf = _module.AddInstruction(
                        SpirvOp.UGreaterThanEqual,
                        _boolType,
                        guestLane,
                        UInt(32));
                    result = IAdd(
                        GetRawSource(instruction, 1),
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            isUpperHalf,
                            countedBits,
                            UInt(0)));
                    break;
                }
                case "VMbcntLoU32B32":
                {
                    var guestLane = GuestWaveLane();
                    var lane = BitwiseAnd(guestLane, UInt(31));
                    var partialMask = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        ShiftLeftLogical(UInt(1), lane),
                        UInt(1));
                    var lowBitsMask = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.UGreaterThanEqual,
                            _boolType,
                            guestLane,
                            UInt(32)),
                        UInt(uint.MaxValue),
                        partialMask);
                    result = IAdd(
                        GetRawSource(instruction, 1),
                        _module.AddInstruction(
                            SpirvOp.BitCount,
                            _uintType,
                            BitwiseAnd(GetRawSource(instruction, 0), lowBitsMask)));
                    break;
                }
                case "VBfmB32":
                {
                    var width = BitwiseAnd(GetRawSource(instruction, 0), UInt(31));
                    var lowMask = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        ShiftLeftLogical(UInt(1), width),
                        UInt(1));
                    result = ShiftLeftLogical(lowMask, GetRawSource(instruction, 1));
                    break;
                }
                case "VMadU32U24":
                {
                    var left = BitwiseAnd(
                        GetRawSource(instruction, 0),
                        UInt(0x00FF_FFFF));
                    var right = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(0x00FF_FFFF));
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VMadU32U16":
                {
                    var left = BitwiseAnd(
                        GetRawSource(instruction, 0),
                        UInt(0xFFFF));
                    var right = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(0xFFFF));
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VLshrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftRightLogical);
                    break;
                case "VLshrrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftRightLogical,
                        reverse: true);
                    break;
                case "VLshlB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftLeftLogical);
                    break;
                case "VLshlrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftLeftLogical,
                        reverse: true);
                    break;
                case "VAshrI32":
                case "VAshrrevI32":
                {
                    var reverse = instruction.Opcode == "VAshrrevI32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = ShiftRightArithmetic(left, right);
                    break;
                }
                case "VLshlAddU32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = IAdd(shifted, GetRawSource(instruction, 2));
                    break;
                }
                case "VLshlOrU32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = BitwiseOr(
                        shifted,
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VAndOrB32":
                    result = BitwiseOr(
                        BitwiseAnd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VOr3U32":
                    result = BitwiseOr(
                        BitwiseOr(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VPermlane16B32":
                    result = EmitPermlane16(instruction, exchangeRows: false);
                    break;
                case "VPermlanex16B32":
                    result = EmitPermlane16(instruction, exchangeRows: true);
                    break;
                case "VAddLshlU32":
                {
                    var added = IAdd(
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    result = ShiftLeftLogical(added, GetRawSource(instruction, 2));
                    break;
                }
                case "VAdd3U32":
                    result = IAdd(
                        IAdd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinU32":
                    result = Ext(
                        38,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMaxU32":
                    result = Ext(
                        41,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMin3U32":
                    result = Ext(
                        38,
                        _uintType,
                        Ext(
                            38,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMax3U32":
                    result = Ext(
                        41,
                        _uintType,
                        Ext(
                            41,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinI32":
                case "VMaxI32":
                {
                    var signedResult = Ext(
                        instruction.Opcode == "VMinI32" ? 39u : 42u,
                        _intType,
                        Bitcast(_intType, GetRawSource(instruction, 0)),
                        Bitcast(_intType, GetRawSource(instruction, 1)));
                    result = Bitcast(_uintType, signedResult);
                    break;
                }
                case "VMin3I32":
                case "VMax3I32":
                {
                    var operation = instruction.Opcode == "VMin3I32" ? 39u : 42u;
                    var left = Bitcast(
                        _intType,
                        GetRawSource(instruction, 0));
                    var middle = Bitcast(
                        _intType,
                        GetRawSource(instruction, 1));
                    var right = Bitcast(
                        _intType,
                        GetRawSource(instruction, 2));
                    result = Bitcast(
                        _uintType,
                        Ext(
                            operation,
                            _intType,
                            Ext(operation, _intType, left, middle),
                            right));
                    break;
                }
                case "VMed3U32":
                {
                    var left = GetRawSource(instruction, 0);
                    var middle = GetRawSource(instruction, 1);
                    var right = GetRawSource(instruction, 2);
                    var low = Ext(38, _uintType, left, middle);
                    var high = Ext(41, _uintType, left, middle);
                    result = Ext(
                        41,
                        _uintType,
                        low,
                        Ext(38, _uintType, high, right));
                    break;
                }
                case "VMed3I32":
                {
                    var left = Bitcast(_intType, GetRawSource(instruction, 0));
                    var middle = Bitcast(_intType, GetRawSource(instruction, 1));
                    var right = Bitcast(_intType, GetRawSource(instruction, 2));
                    var low = Ext(39, _intType, left, middle);
                    var high = Ext(42, _intType, left, middle);
                    result = Bitcast(
                        _uintType,
                        Ext(
                            42,
                            _intType,
                            low,
                            Ext(39, _intType, high, right)));
                    break;
                }
                case "VMed3F32":
                {
                    var left = GetFloatSource(instruction, 0);
                    var middle = GetFloatSource(instruction, 1);
                    var right = GetFloatSource(instruction, 2);
                    var low = Ext(37, _floatType, left, middle);
                    var high = Ext(40, _floatType, left, middle);
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            40,
                            _floatType,
                            low,
                            Ext(37, _floatType, high, right)));
                    break;
                }
                case "VCubeidF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Id);
                    break;
                case "VCubescF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Sc);
                    break;
                case "VCubetcF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Tc);
                    break;
                case "VCubemaF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Ma);
                    break;
                case "VAddCoU32":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    result = IAdd(left, right);
                    var carry = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        result,
                        left);
                    StoreCarryOut(instruction, carry);
                    break;
                }
                case "VSubCoU32":
                case "VSubrevCoU32":
                {
                    var reverse = instruction.Opcode == "VSubrevCoU32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
                    var borrow = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        left,
                        right);
                    StoreCarryOut(instruction, borrow);
                    break;
                }
                case "VMadU64U32":
                {
                    // V_MAD_U64_U32 writes a 64-bit VGPR pair. The first two
                    // sources are 32-bit factors; the third is a 64-bit addend
                    // held in a VGPR or SGPR pair. Its SDST receives the carry
                    // mask for the unsigned 64-bit addition.
                    var wideLeft = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        GetRawSource(instruction, 0));
                    var wideRight = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        GetRawSource(instruction, 1));
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _ulongType,
                        wideLeft,
                        wideRight);
                    var addend = GetRawSource64(instruction, 2);
                    var wideResult = _module.AddInstruction(
                        SpirvOp.IAdd,
                        _ulongType,
                        product,
                        addend);
                    var carry = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        wideResult,
                        addend);
                    result = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        wideResult);
                    var high = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        ShiftRightLogical64(
                            wideResult,
                            _module.Constant64(_ulongType, 32)));
                    StoreV(destination + 1, high);
                    StoreCarryOut(instruction, carry);
                    break;
                }
                case "VBfeU32":
                {
                    var width = BitwiseAnd(GetRawSource(instruction, 2), UInt(31));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldUExtract,
                        _uintType,
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)),
                        width);
                    break;
                }
                case "VBfiB32":
                {
                    var mask = GetRawSource(instruction, 0);
                    var insert = GetRawSource(instruction, 1);
                    var source = GetRawSource(instruction, 2);
                    result = _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _uintType,
                        BitwiseAnd(mask, insert),
                        BitwiseAnd(
                            _module.AddInstruction(SpirvOp.Not, _uintType, mask),
                            source));
                    break;
                }
                case "VCvtPkrtzF16F32":
                {
                    var first = TruncateFloat32ForPack(GetFloatSource(instruction, 0));
                    var second = TruncateFloat32ForPack(GetFloatSource(instruction, 1));
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        first,
                        second);
                    result = Ext(58, _uintType, vector);
                    StorePackedHalf(
                        destination,
                        vector);
                    break;
                }
                case "VCvtPknormI16F32":
                case "VCvtPknormU16F32":
                {
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        GetFloatSource(instruction, 1));
                    // GLSL.std.450 PackSnorm2x16 / PackUnorm2x16 match the
                    // RDNA saturated normalized conversion and packing rules.
                    result = Ext(
                        instruction.Opcode == "VCvtPknormI16F32" ? 56u : 57u,
                        _uintType,
                        vector);
                    break;
                }
                default:
                    error = $"unsupported vector opcode {instruction.Opcode}";
                    return false;
            }

            if (instruction.Control is Gen5DppControl dpp)
            {
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsDppWriteEnabled(dpp),
                    result,
                    LoadV(destination));
            }

            if (instruction.Control is Gen5SdwaControl destinationControl &&
                destinationControl.ScalarDestination is null)
            {
                result = ApplySdwaDestination(
                    destinationControl,
                    result,
                    LoadV(destination));
            }

            StoreV(destination, result);
            return true;
        }

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            uint condition = _module.ConstantBool(false);
            var opcode = instruction.Opcode;
            if (opcode is "VCmpClassF32" or "VCmpxClassF32")
            {
                var source = GetFloatSource(instruction, 0);
                var raw = GetRawSource(instruction, 0);
                var mask = GetRawSource(instruction, 1);
                var negative = IsNotZero(BitwiseAnd(raw, UInt(0x8000_0000)));
                var positive = _module.AddInstruction(
                    SpirvOp.LogicalNot,
                    _boolType,
                    negative);
                var nan = _module.AddInstruction(SpirvOp.IsNan, _boolType, source);
                var infinity =
                    _module.AddInstruction(SpirvOp.IsInf, _boolType, source);
                var zero = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    source,
                    Float(0));
                var absolute = Ext(4, _floatType, source);
                var nonzero = _module.AddInstruction(
                    SpirvOp.FOrdGreaterThan,
                    _boolType,
                    absolute,
                    Float(0));
                var belowNormal = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    absolute,
                    Bitcast(_floatType, UInt(0x0080_0000)));
                var subnormal = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    nonzero,
                    belowNormal);
                var special = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    nan,
                    _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        infinity,
                        _module.AddInstruction(
                            SpirvOp.LogicalOr,
                            _boolType,
                            zero,
                            subnormal)));
                var normal = _module.AddInstruction(
                    SpirvOp.LogicalNot,
                    _boolType,
                    special);

                uint MaskedClass(uint bits, uint value)
                {
                    var enabled = IsNotZero(BitwiseAnd(mask, UInt(bits)));
                    return _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        enabled,
                        value);
                }

                uint SignedClass(uint negativeBit, uint positiveBit, uint value)
                {
                    var negativeClass = MaskedClass(
                        negativeBit,
                        _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            negative,
                            value));
                    var positiveClass = MaskedClass(
                        positiveBit,
                        _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            positive,
                            value));
                    return _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        negativeClass,
                        positiveClass);
                }

                condition = MaskedClass(0x003, nan);
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x004, 0x200, infinity));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x008, 0x100, normal));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x010, 0x080, subnormal));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x020, 0x040, zero));
            }
            else if (opcode is "VCmpFF32" or "VCmpxFF32" or "VCmpFI32" or "VCmpFU32")
            {
                condition = _module.ConstantBool(false);
            }
            else if (opcode is "VCmpTruF32" or "VCmpxTruF32" or "VCmpTI32" or "VCmpTU32")
            {
                condition = _module.ConstantBool(true);
            }
            else if (opcode is
                     "VCmpOF32" or "VCmpxOF32" or
                     "VCmpUF32" or "VCmpxUF32")
            {
                var left = GetFloatSource(instruction, 0);
                var right = GetFloatSource(instruction, 1);
                var unordered = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    _module.AddInstruction(SpirvOp.IsNan, _boolType, left),
                    _module.AddInstruction(SpirvOp.IsNan, _boolType, right));
                condition = opcode is "VCmpUF32" or "VCmpxUF32"
                    ? unordered
                    : _module.AddInstruction(SpirvOp.LogicalNot, _boolType, unordered);
            }
            else if (opcode is not ("VCmpClassF32" or "VCmpxClassF32") &&
                     opcode.EndsWith("F32", StringComparison.Ordinal))
            {
                var left = GetFloatSource(instruction, 0);
                var right = GetFloatSource(instruction, 1);
                var operation = opcode switch
                {
                    "VCmpLtF32" or "VCmpxLtF32" => SpirvOp.FOrdLessThan,
                    "VCmpEqF32" or "VCmpxEqF32" => SpirvOp.FOrdEqual,
                    "VCmpLeF32" or "VCmpxLeF32" => SpirvOp.FOrdLessThanEqual,
                    "VCmpGtF32" or "VCmpxGtF32" => SpirvOp.FOrdGreaterThan,
                    "VCmpLgF32" or "VCmpxLgF32" => SpirvOp.FOrdNotEqual,
                    "VCmpGeF32" or "VCmpxGeF32" => SpirvOp.FOrdGreaterThanEqual,
                    "VCmpNeqF32" or "VCmpxNeqF32" => SpirvOp.FUnordNotEqual,
                    "VCmpNltF32" or "VCmpxNltF32" => SpirvOp.FUnordGreaterThanEqual,
                    "VCmpNleF32" or "VCmpxNleF32" => SpirvOp.FUnordGreaterThan,
                    "VCmpNgtF32" or "VCmpxNgtF32" => SpirvOp.FUnordLessThanEqual,
                    "VCmpNgeF32" or "VCmpxNgeF32" => SpirvOp.FUnordLessThan,
                    "VCmpNlgF32" or "VCmpxNlgF32" => SpirvOp.FUnordEqual,
                    _ => SpirvOp.Nop,
                };
                if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported float compare {opcode}";
                    return false;
                }

                condition = _module.AddInstruction(operation, _boolType, left, right);
            }
            else if (opcode is not ("VCmpClassF32" or "VCmpxClassF32"))
            {
                var left = GetRawSource(instruction, 0);
                var right = GetRawSource(instruction, 1);
                var signed = opcode.EndsWith("I32", StringComparison.Ordinal);
                if (signed)
                {
                    left = Bitcast(_intType, left);
                    right = Bitcast(_intType, right);
                }

                var operation = opcode switch
                {
                    "VCmpEqI32" or "VCmpxEqI32" or
                    "VCmpEqU32" or "VCmpxEqU32" => SpirvOp.IEqual,
                    "VCmpNeI32" or "VCmpxNeI32" or
                    "VCmpNeU32" or "VCmpxNeU32" => SpirvOp.INotEqual,
                    "VCmpLtI32" or "VCmpxLtI32" => SpirvOp.SLessThan,
                    "VCmpLeI32" or "VCmpxLeI32" => SpirvOp.SLessThanEqual,
                    "VCmpGtI32" or "VCmpxGtI32" => SpirvOp.SGreaterThan,
                    "VCmpGeI32" or "VCmpxGeI32" => SpirvOp.SGreaterThanEqual,
                    "VCmpLtU32" or "VCmpxLtU32" => SpirvOp.ULessThan,
                    "VCmpLeU32" or "VCmpxLeU32" => SpirvOp.ULessThanEqual,
                    "VCmpGtU32" or "VCmpxGtU32" => SpirvOp.UGreaterThan,
                    "VCmpGeU32" or "VCmpxGeU32" => SpirvOp.UGreaterThanEqual,
                    _ => SpirvOp.Nop,
                };
                if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported integer compare {opcode}";
                    return false;
                }

                condition = _module.AddInstruction(operation, _boolType, left, right);
            }

            if (_state.Program.Address == 0x0000000500781200ul &&
                ((instruction.Pc == 0x4D4 &&
                  Environment.GetEnvironmentVariable(
                      "SHARPEMU_FORCE_TITLE_COMPARE_4D4") == "1") ||
                 (instruction.Pc == 0x540 &&
                  Environment.GetEnvironmentVariable(
                      "SHARPEMU_FORCE_TITLE_COMPARE_540") == "1")))
            {
                condition = _module.ConstantBool(true);
            }

            // Vector compares fully overwrite the destination mask, but only
            // lanes enabled by EXEC can pass the test: VCC = EXEC & condition.
            // Balloting the raw condition leaks results from disabled lanes
            // into later saveexec/branch sequences.
            var activeCondition = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                condition);
            if (instruction.Control is Gen5DppControl compareDpp)
            {
                activeCondition = _module.AddInstruction(
                    SpirvOp.Select,
                    _boolType,
                    IsDppWriteEnabled(compareDpp),
                    activeCondition,
                    Load(_boolType, _vcc));
            }

            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                // GFX10 VCMPX is EXEC-only. The SDWA bits that older
                // generations exposed as an explicit scalar destination must
                // not overwrite VCC/an SGPR pair here. Guest shaders rely on
                // those registers retaining a saved EXEC mask for later
                // reconvergence.
                StoreWaveMask(126, activeCondition);
            }
            else
            {
                var compareDestination = instruction.Control is Gen5SdwaControl
                    { ScalarDestination: { } scalarDestination }
                    ? scalarDestination
                    : 106u;
                StoreWaveMask(compareDestination, activeCondition);
            }

            return true;
        }

        private bool TryEmitScalarAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                return TryEmitScalarCompare(instruction, out error);
            }

            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "missing scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopk)
            {
                var immediate = unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
                if (instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
                {
                    return TryEmitScalarCompareK(instruction, destination, immediate, out error);
                }

                var current = LoadS(destination);
                var value = instruction.Opcode switch
                {
                    "SMovkI32" => UInt(immediate),
                    "SAddkI32" => IAdd(current, UInt(immediate)),
                    "SMulkI32" => _module.AddInstruction(
                        SpirvOp.IMul,
                        _uintType,
                        current,
                        UInt(immediate)),
                    _ => 0u,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar immediate {instruction.Opcode}";
                    return false;
                }

                StoreS(destination, value);
                return true;
            }

            if (instruction.Opcode == "SGetpcB64")
            {
                var pc = _state.Program.Address +
                    instruction.Pc +
                    (ulong)(instruction.Words.Count * sizeof(uint));
                StoreS(destination, UInt((uint)pc));
                StoreS(destination + 1, UInt((uint)(pc >> 32)));
                return true;
            }

            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
                instruction.Opcode is "SWqmB64" or "SBfeU64" or "SBfeI64")
            {
                return TryEmitScalar64(instruction, destination, out error);
            }

            var left = GetRawSource(instruction, 0);
            if (instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                var oldExec64 = BooleanToWaveMask(Load(_boolType, _exec));
                var oldExec = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    oldExec64);
                var notLeft = _module.AddInstruction(SpirvOp.Not, _uintType, left);
                var notOldExec = _module.AddInstruction(SpirvOp.Not, _uintType, oldExec);
                var newExec = instruction.Opcode switch
                {
                    "SAndSaveexecB32" => BitwiseAnd(oldExec, left),
                    "SOrSaveexecB32" => BitwiseOr(oldExec, left),
                    "SXorSaveexecB32" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _uintType, oldExec, left),
                    "SAndn1SaveexecB32" => BitwiseAnd(notLeft, oldExec),
                    "SAndn2SaveexecB32" => BitwiseAnd(left, notOldExec),
                    "SOrn1SaveexecB32" => BitwiseOr(notLeft, oldExec),
                    "SOrn2SaveexecB32" => BitwiseOr(left, notOldExec),
                    "SNandSaveexecB32" => _module.AddInstruction(
                        SpirvOp.Not, _uintType, BitwiseAnd(left, oldExec)),
                    "SNorSaveexecB32" => _module.AddInstruction(
                        SpirvOp.Not, _uintType, BitwiseOr(left, oldExec)),
                    "SXnorSaveexecB32" => _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _uintType,
                            left,
                            oldExec)),
                    _ => 0u,
                };
                if (newExec == 0)
                {
                    error = $"unsupported scalar 32-bit saveexec opcode {instruction.Opcode}";
                    return false;
                }

                StoreS(destination, oldExec);
                // B32 saveexec is the Wave32 form; EXEC_HI is zero.
                var newExec64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    newExec);
                StoreS64(126, newExec64);
                Store(_scc, IsNotZero(newExec));
                return true;
            }

            uint result;
            switch (instruction.Opcode)
            {
                case "SMovB32":
                    result = left;
                    break;
                case "SNotB32":
                    result = _module.AddInstruction(SpirvOp.Not, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBrevB32":
                    result = _module.AddInstruction(SpirvOp.BitReverse, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBcnt1I32B32":
                    result = _module.AddInstruction(SpirvOp.BitCount, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SFF1I32B32":
                    result = Ext(73, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBitset1B32":
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        LoadS(destination),
                        UInt(1),
                        BitwiseAnd(left, UInt(31)),
                        UInt(1));
                    StoreS(destination, result);
                    return true;
                default:
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = $"missing scalar source for {instruction.Opcode}";
                        return false;
                    }

                    var right = GetRawSource(instruction, 1);
                    switch (instruction.Opcode)
                    {
                        case "SAddU32":
                            result = IAdd(left, right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                left));
                            break;
                        case "SSubU32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left));
                            break;
                        case "SAddI32":
                            result = IAdd(left, right);
                            Store(_scc, SignedAddOverflow(left, right, result));
                            break;
                        case "SSubI32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, SignedSubOverflow(left, right, result));
                            break;
                        case "SAddcU32":
                        {
                            var carryIn = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = IAdd(left, right);
                            result = IAdd(partial, carryIn);
                            var firstCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                partial,
                                left);
                            var secondCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                partial);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstCarry,
                                    secondCarry));
                            break;
                        }
                        case "SSubbU32":
                        {
                            var borrow = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                partial,
                                borrow);
                            var firstBorrow = _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left);
                            var secondBorrow = _module.AddInstruction(
                                SpirvOp.LogicalAnd,
                                _boolType,
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    borrow,
                                    UInt(1)),
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    right,
                                    left));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstBorrow,
                                    secondBorrow));
                            break;
                        }
                        case "SMulI32":
                            result = _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                left,
                                right);
                            break;
                        case "SMulHiU32":
                        {
                            var product = _module.AddInstruction(
                                SpirvOp.IMul,
                                _ulongType,
                                _module.AddInstruction(SpirvOp.UConvert, _ulongType, left),
                                _module.AddInstruction(SpirvOp.UConvert, _ulongType, right));
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                ShiftRightLogical64(
                                    product,
                                    _module.Constant64(_ulongType, 32)));
                            break;
                        }
                        case "SAndB32":
                            result = BitwiseAnd(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXorB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseXor,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAndn2B32":
                            result = BitwiseAnd(
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrn2B32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNandB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                BitwiseAnd(left, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseOr,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXnorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseXor,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshlB32":
                            result = ShiftLeftLogical(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshrB32":
                            result = ShiftRightLogical(
                                left,
                                BitwiseAnd(right, UInt(31)));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAshrI32":
                            result = ShiftRightArithmetic(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SBfmB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitFieldInsert,
                                _uintType,
                                UInt(0),
                                UInt(uint.MaxValue),
                                BitwiseAnd(right, UInt(31)),
                                BitwiseAnd(left, UInt(31)));
                            break;
                        case "SBfeU32":
                        case "SBfeI32":
                        {
                            var offset = BitwiseAnd(right, UInt(31));
                            var requestedWidth = BitwiseAnd(
                                ShiftRightLogical(right, UInt(16)),
                                UInt(0x7F));
                            var remaining = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                UInt(32),
                                offset);
                            var width = Ext(
                                38,
                                _uintType,
                                requestedWidth,
                                remaining);
                            result = instruction.Opcode == "SBfeI32"
                                ? Bitcast(
                                    _uintType,
                                    _module.AddInstruction(
                                        SpirvOp.BitFieldSExtract,
                                        _intType,
                                        Bitcast(_intType, left),
                                        offset,
                                        width))
                                : _module.AddInstruction(
                                    SpirvOp.BitFieldUExtract,
                                    _uintType,
                                    left,
                                    offset,
                                    width);
                            Store(_scc, IsNotZero(result));
                            break;
                        }
                        case "SCselectB32":
                            result = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                left,
                                right);
                            break;
                        case "SMinU32":
                            result = Ext(38, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.ULessThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMinI32":
                            result = Bitcast(
                                _uintType,
                                Ext(39, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SLessThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SMaxU32":
                            result = Ext(41, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.UGreaterThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMaxI32":
                            result = Bitcast(
                                _uintType,
                                Ext(42, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SGreaterThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SLshl1AddU32":
                        case "SLshl2AddU32":
                        case "SLshl3AddU32":
                        case "SLshl4AddU32":
                        {
                            var shift = (uint)(instruction.Opcode[5] - '0');
                            result = IAdd(
                                ShiftLeftLogical(left, UInt(shift)),
                                right);
                            break;
                        }
                        case "SPackLlB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                ShiftLeftLogical(right, UInt(16)));
                            break;
                        case "SPackLhB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                BitwiseAnd(right, UInt(0xFFFF0000)));
                            break;
                        case "SPackHhB32B16":
                            result = BitwiseOr(
                                ShiftRightLogical(left, UInt(16)),
                                BitwiseAnd(right, UInt(0xFFFF0000)));
                            break;
                        default:
                            error = $"unsupported scalar opcode {instruction.Opcode}";
                            return false;
                    }

                    break;
                }
            }

            StoreS(destination, result);
            return true;
        }

        private bool TryEmitScalarCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 2)
            {
                error = "missing scalar compare source";
                return false;
            }

            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var shifted = ShiftRightLogical(
                    left,
                    BitwiseAnd(right, UInt(31)));
                var isSet = IsNotZero(BitwiseAnd(shifted, UInt(1)));
                Store(
                    _scc,
                    instruction.Opcode == "SBitcmp1B32"
                        ? isSet
                        : _module.AddInstruction(
                            SpirvOp.LogicalNot,
                            _boolType,
                            isSet));
                return true;
            }

            var operation = instruction.Opcode switch
            {
                "SCmpEqI32" or "SCmpEqU32" => SpirvOp.IEqual,
                "SCmpLgI32" or "SCmpLgU32" => SpirvOp.INotEqual,
                "SCmpGtI32" => SpirvOp.SGreaterThan,
                "SCmpGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpLtI32" => SpirvOp.SLessThan,
                "SCmpLeI32" => SpirvOp.SLessThanEqual,
                "SCmpGtU32" => SpirvOp.UGreaterThan,
                "SCmpGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpLtU32" => SpirvOp.ULessThan,
                "SCmpLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitScalarCompareK(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint immediate,
            out string error)
        {
            error = string.Empty;
            var left = LoadS(destination);
            var right = UInt(immediate);
            var operation = instruction.Opcode switch
            {
                "SCmpkEqI32" or "SCmpkEqU32" => SpirvOp.IEqual,
                "SCmpkLgI32" or "SCmpkLgU32" => SpirvOp.INotEqual,
                "SCmpkGtI32" => SpirvOp.SGreaterThan,
                "SCmpkGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpkLtI32" => SpirvOp.SLessThan,
                "SCmpkLeI32" => SpirvOp.SLessThanEqual,
                "SCmpkGtU32" => SpirvOp.UGreaterThan,
                "SCmpkGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpkLtU32" => SpirvOp.ULessThan,
                "SCmpkLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar immediate compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitScalar64(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var left = GetRawSource64(instruction, 0);
            if (instruction.Opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                var oldExec = BooleanToWaveMask(Load(_boolType, _exec));
                var notLeft = _module.AddInstruction(SpirvOp.Not, _ulongType, left);
                var newExec = instruction.Opcode switch
                {
                    "SAndSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, oldExec, left),
                    "SOrSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, oldExec, left),
                    "SXorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, oldExec, left),
                    "SAndn2SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        left,
                        _module.AddInstruction(
                            SpirvOp.Not,
                            _ulongType,
                            oldExec)),
                    "SAndn1SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        notLeft,
                        oldExec),
                    "SOrn1SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        notLeft,
                        oldExec),
                    "SOrn2SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        left,
                        _module.AddInstruction(
                            SpirvOp.Not,
                            _ulongType,
                            oldExec)),
                    "SNandSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd,
                            _ulongType,
                            left,
                            oldExec)),
                    "SNorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr,
                            _ulongType,
                            left,
                            oldExec)),
                    "SXnorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _ulongType,
                            left,
                            oldExec)),
                    _ => 0u,
                };
                if (newExec == 0)
                {
                    error =
                        $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                    return false;
                }

                StoreS64(destination, oldExec);
                StoreS64(126, newExec);
                Store(_scc, IsNotZero64(newExec));
                return true;
            }

            if (instruction.Opcode is "SLshlB64" or "SLshrB64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit shift source";
                    return false;
                }

                var shift = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    GetRawSource(instruction, 1));
                var shiftedValue = instruction.Opcode == "SLshlB64"
                    ? ShiftLeftLogical64(left, shift)
                    : ShiftRightLogical64(left, shift);
                StoreS64(destination, shiftedValue);
                Store(_scc, IsNotZero64(shiftedValue));
                return true;
            }

            if (instruction.Opcode is "SBfeU64" or "SBfeI64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit bitfield source";
                    return false;
                }

                var control = GetRawSource(instruction, 1);
                var offset = BitwiseAnd(control, UInt(63));
                var requestedWidth = BitwiseAnd(
                    ShiftRightLogical(control, UInt(16)),
                    UInt(0x7F));
                var remaining = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    UInt(64),
                    offset);
                var width = Ext(
                    38,
                    _uintType,
                    requestedWidth,
                    remaining);
                var offset64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    offset);
                var width64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    width);
                var one64 = _module.Constant64(_ulongType, 1);
                var shifted = ShiftRightLogical64(left, offset64);
                var partialMask = _module.AddInstruction(
                    SpirvOp.ISub,
                    _ulongType,
                    ShiftLeftLogical64(one64, width64),
                    one64);
                var fullWidth = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    width,
                    UInt(64));
                var mask = _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    fullWidth,
                    _module.Constant64(_ulongType, ulong.MaxValue),
                    partialMask);
                var extracted = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    shifted,
                    mask);
                if (instruction.Opcode == "SBfeI64")
                {
                    var signShift = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        width,
                        UInt(1));
                    var signBit = ShiftLeftLogical64(
                        one64,
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _ulongType,
                            signShift));
                    var signExtended = _module.AddInstruction(
                        SpirvOp.ISub,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _ulongType,
                            extracted,
                            signBit),
                        signBit);
                    extracted = _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            width,
                            UInt(0)),
                        _module.Constant64(_ulongType, 0),
                        signExtended);
                }

                StoreS64(destination, extracted);
                Store(_scc, IsNotZero64(extracted));
                return true;
            }

            if (instruction.Opcode == "SBfmB64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit bitfield-mask source";
                    return false;
                }

                var width = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    BitwiseAnd(GetRawSource(instruction, 0), UInt(63)));
                var offset = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    BitwiseAnd(GetRawSource(instruction, 1), UInt(63)));
                // Width is masked to 0..63, so (1 << width) never invokes an
                // undefined 64-bit shift. This naturally yields zero for a
                // zero-width mask and avoids OpBitFieldInsert, which the
                // MoltenVK/SPIRV-Cross path rejects for 64-bit integers.
                var lowMask = _module.AddInstruction(
                    SpirvOp.ISub,
                    _ulongType,
                    ShiftLeftLogical64(
                        _module.Constant64(_ulongType, 1),
                        width),
                    _module.Constant64(_ulongType, 1));
                var maskValue = ShiftLeftLogical64(lowMask, offset);
                StoreS64(destination, maskValue);
                Store(_scc, IsNotZero64(maskValue));
                return true;
            }

            uint value;
            if (instruction.Opcode == "SMovB64")
            {
                value = left;
            }
            else if (instruction.Opcode == "SWqmB64")
            {
                var quadAny = _module.AddInstruction(
                    SpirvOp.BitwiseOr,
                    _ulongType,
                    left,
                    _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        ShiftRightLogical64(left, _module.Constant64(_ulongType, 1)),
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr,
                            _ulongType,
                            ShiftRightLogical64(left, _module.Constant64(_ulongType, 2)),
                            ShiftRightLogical64(left, _module.Constant64(_ulongType, 3)))));
                quadAny = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    quadAny,
                    _module.Constant64(_ulongType, 0x1111_1111_1111_1111UL));
                value = _module.AddInstruction(
                    SpirvOp.IMul,
                    _ulongType,
                    quadAny,
                    _module.Constant64(_ulongType, 0xFUL));
            }
            else if (instruction.Opcode == "SNotB64")
            {
                value = _module.AddInstruction(SpirvOp.Not, _ulongType, left);
            }
            else
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit source";
                    return false;
                }

                var right = GetRawSource64(instruction, 1);
                value = instruction.Opcode switch
                {
                    "SAndB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, left, right),
                    "SOrB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, left, right),
                    "SXorB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, left, right),
                    "SNandB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, left, right)),
                    "SNorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr, _ulongType, left, right)),
                    "SXnorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor, _ulongType, left, right)),
                    "SAndn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SAndn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SOrn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SOrn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SCselectB64" => _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        Load(_boolType, _scc),
                        left,
                        right),
                    _ => 0,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                    return false;
                }
            }

            StoreS64(destination, value);
            if (instruction.Opcode is
                "SNotB64" or
                "SAndB64" or
                "SOrB64" or
                "SXorB64" or
                "SAndn1B64" or
                "SAndn2B64" or
                "SOrn1B64" or
                "SOrn2B64" or
                "SNandB64" or
                "SNorB64" or
                "SXnorB64")
            {
                Store(_scc, IsNotZero64(value));
            }

            return true;
        }

        private uint GetRawSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool applySdwaIntegerModifiers = true)
        {
            if ((uint)sourceIndex >= instruction.Sources.Count)
            {
                throw new InvalidOperationException($"missing source {sourceIndex}");
            }

            var operand = instruction.Sources[sourceIndex];
            uint value = operand.Kind switch
            {
                Gen5OperandKind.VectorRegister => LoadV(operand.Value),
                Gen5OperandKind.ScalarRegister => LoadS(operand.Value),
                Gen5OperandKind.LiteralConstant => UInt(operand.Value),
                Gen5OperandKind.EncodedConstant when operand.Value == 251 =>
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        LogicalNot(SubgroupAny(Load(_boolType, _vcc))),
                        UInt(1),
                        UInt(0)),
                Gen5OperandKind.EncodedConstant when operand.Value == 252 =>
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        LogicalNot(SubgroupAny(Load(_boolType, _exec))),
                        UInt(1),
                        UInt(0)),
                Gen5OperandKind.EncodedConstant when operand.Value == 253 =>
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        Load(_boolType, _scc),
                        UInt(1),
                        UInt(0)),
                Gen5OperandKind.EncodedConstant when TryDecodeInlineConstant(
                    operand.Value,
                    out var inline) => UInt(inline),
                _ => throw new InvalidOperationException($"unsupported source {operand}"),
            };

            // DPP16 remaps src0 across lanes before the VALU operation. The IR
            // has always preserved this control, but treating it as an ordinary
            // local VGPR read breaks every wave reduction used by the XPR
            // renderer (min/max/OR scans become value-with-self operations).
            if (sourceIndex == 0 &&
                instruction.Control is Gen5DppControl dpp)
            {
                value = ApplyDppSource(dpp, value);
            }
            else if (sourceIndex == 0 &&
                     instruction.Control is Gen5Dpp8Control dpp8)
            {
                value = ApplyDpp8Source(dpp8, value);
            }

            if (instruction.Control is Gen5SdwaControl sdwa)
            {
                var selector = sourceIndex switch
                {
                    0 => sdwa.Source0Select,
                    1 => sdwa.Source1Select,
                    _ => 6u,
                };
                value = selector switch
                {
                    0 => BitwiseAnd(value, UInt(0xFF)),
                    1 => BitwiseAnd(ShiftRightLogical(value, UInt(8)), UInt(0xFF)),
                    2 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFF)),
                    3 => BitwiseAnd(ShiftRightLogical(value, UInt(24)), UInt(0xFF)),
                    4 => BitwiseAnd(value, UInt(0xFFFF)),
                    5 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFFFF)),
                    _ => value,
                };
                var signExtend = sourceIndex switch
                {
                    0 => sdwa.Source0SignExtend,
                    1 => sdwa.Source1SignExtend,
                    _ => false,
                };
                if (signExtend && selector != 6)
                {
                    var width = selector <= 3 ? 8u : 16u;
                    value = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, value),
                            UInt(0),
                            UInt(width)));
                }

                if (applySdwaIntegerModifiers)
                {
                    if ((sdwa.AbsoluteMask & (1u << sourceIndex)) != 0)
                    {
                        value = Bitcast(
                            _uintType,
                            Ext(5, _intType, Bitcast(_intType, value)));
                    }

                    if ((sdwa.NegateMask & (1u << sourceIndex)) != 0)
                    {
                        value = _module.AddInstruction(
                            SpirvOp.ISub,
                            _uintType,
                            UInt(0),
                            value);
                    }
                }
            }

            return value;
        }

        private uint ApplyDpp8Source(Gen5Dpp8Control control, uint value)
        {
            var lane = GuestWaveLane();
            var laneInGroup = BitwiseAnd(lane, UInt(7));
            var selector = UInt(control.LaneSelectors & 7);
            for (var index = 1u; index < 8; index++)
            {
                selector = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        laneInGroup,
                        UInt(index)),
                    UInt((control.LaneSelectors >> checked((int)(index * 3))) & 7),
                    selector);
            }

            var targetLane = IAdd(BitwiseAnd(lane, UInt(0xFFFF_FFF8)), selector);
            targetLane = BitwiseAnd(targetLane, UInt(31));
            var shuffled = _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),
                value,
                targetLane);
            if (control.FetchInactive)
            {
                return shuffled;
            }

            var activeWord = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                Load(_boolType, _exec),
                UInt(1),
                UInt(0));
            var sourceActive = IsNotZero(
                _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    activeWord,
                    targetLane));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                sourceActive,
                shuffled,
                UInt(0));
        }

        private uint ApplyDppSource(Gen5DppControl control, uint value)
        {
            GetDppSourceLane(control, out var targetLane, out var inRange);
            var lane = GuestWaveLane();
            var safeTarget = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                inRange,
                targetLane,
                lane);
            safeTarget = BitwiseAnd(safeTarget, UInt(31));
            var shuffled = _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),
                value,
                safeTarget);

            var sourceAvailable = inRange;
            if (!control.FetchInactive)
            {
                var activeWord = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    UInt(1),
                    UInt(0));
                var shuffledActive = _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    activeWord,
                    safeTarget);
                sourceAvailable = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    sourceAvailable,
                    IsNotZero(shuffledActive));
            }

            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                sourceAvailable,
                shuffled,
                UInt(0));
        }

        private uint ApplySdwaDestination(
            Gen5SdwaControl control,
            uint value,
            uint previous)
        {
            var (shift, width) = control.DestinationSelect switch
            {
                0 => (0u, 8u),
                1 => (8u, 8u),
                2 => (16u, 8u),
                3 => (24u, 8u),
                4 => (0u, 16u),
                5 => (16u, 16u),
                _ => (0u, 32u),
            };
            if (width == 32)
            {
                return value;
            }

            var lowMask = width == 8 ? 0xFFu : 0xFFFFu;
            var fieldMask = lowMask << checked((int)shift);
            var upperStart = shift + width;
            var upperMask = upperStart == 32
                ? 0u
                : uint.MaxValue << checked((int)upperStart);
            var positioned = ShiftLeftLogical(
                BitwiseAnd(value, UInt(lowMask)),
                UInt(shift));
            return control.DestinationUnused switch
            {
                0 => positioned,
                1 => BitwiseOr(
                    positioned,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        IsNotZero(BitwiseAnd(positioned, UInt(1u << checked((int)(shift + width - 1))))),
                        UInt(upperMask),
                        UInt(0))),
                2 => BitwiseOr(
                    BitwiseAnd(previous, UInt(~fieldMask)),
                    positioned),
                _ => throw new InvalidOperationException("reserved SDWA destination-unused mode"),
            };
        }

        private static bool IsSupportedDppControl(uint control) =>
            control <= 0xFF ||
            control is >= 0x101 and <= 0x10F or
                >= 0x111 and <= 0x11F or
                >= 0x121 and <= 0x12F or
                0x140 or 0x141 or
                >= 0x150 and <= 0x15F or
                >= 0x160 and <= 0x16F;

        private void GetDppSourceLane(
            Gen5DppControl control,
            out uint targetLane,
            out uint inRange)
        {
            var lane = GuestWaveLane();
            var rowBase = BitwiseAnd(lane, UInt(0xFFFF_FFF0));
            var rowLane = BitwiseAnd(lane, UInt(15));
            var dpp = control.Control;
            inRange = _module.ConstantBool(true);

            if (dpp <= 0xFF)
            {
                var quadLane = BitwiseAnd(lane, UInt(3));
                var selected = UInt(dpp & 3);
                for (var index = 1u; index < 4; index++)
                {
                    selected = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            quadLane,
                            UInt(index)),
                        UInt((dpp >> checked((int)(index * 2))) & 3),
                        selected);
                }

                targetLane = IAdd(BitwiseAnd(lane, UInt(0xFFFF_FFFC)), selected);
                return;
            }

            if (dpp is >= 0x101 and <= 0x10F)
            {
                var shift = UInt(dpp & 15);
                var shifted = IAdd(rowLane, shift);
                inRange = _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    shifted,
                    UInt(16));
                targetLane = IAdd(rowBase, BitwiseAnd(shifted, UInt(15)));
                return;
            }

            if (dpp is >= 0x111 and <= 0x11F)
            {
                var shift = UInt(dpp & 15);
                inRange = _module.AddInstruction(
                    SpirvOp.UGreaterThanEqual,
                    _boolType,
                    rowLane,
                    shift);
                targetLane = IAdd(
                    rowBase,
                    BitwiseAnd(
                        _module.AddInstruction(SpirvOp.ISub, _uintType, rowLane, shift),
                        UInt(15)));
                return;
            }

            if (dpp is >= 0x121 and <= 0x12F)
            {
                targetLane = IAdd(
                    rowBase,
                    BitwiseAnd(
                        _module.AddInstruction(
                            SpirvOp.ISub,
                            _uintType,
                            rowLane,
                            UInt(dpp & 15)),
                        UInt(15)));
                return;
            }

            targetLane = dpp switch
            {
                0x140 => IAdd(rowBase, _module.AddInstruction(
                    SpirvOp.ISub, _uintType, UInt(15), rowLane)),
                0x141 => IAdd(
                    BitwiseAnd(lane, UInt(0xFFFF_FFF8)),
                    _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        UInt(7),
                        BitwiseAnd(lane, UInt(7)))),
                >= 0x150 and <= 0x15F => IAdd(rowBase, UInt(dpp & 15)),
                >= 0x160 and <= 0x16F => IAdd(
                    rowBase,
                    BitwiseXor(rowLane, UInt(dpp & 15))),
                _ => lane,
            };
        }

        private uint IsDppWriteEnabled(Gen5DppControl control)
        {
            GetDppSourceLane(control, out _, out var inRange);
            var lane = GuestWaveLane();
            var row = ShiftRightLogical(lane, UInt(4));
            var bank = BitwiseAnd(lane, UInt(3));
            var rowEnabled = IsNotZero(BitwiseAnd(
                UInt(control.RowMask),
                ShiftLeftLogical(UInt(1), row)));
            var bankEnabled = IsNotZero(BitwiseAnd(
                UInt(control.BankMask),
                ShiftLeftLogical(UInt(1), bank)));
            var sourceAllowsWrite = control.BoundControl
                ? _module.ConstantBool(true)
                : inRange;
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                rowEnabled,
                _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    bankEnabled,
                    sourceAllowsWrite));
        }

        private uint GetFloatSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            uint value;
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                operand.Value is >= 128 and <= 192)
            {
                value = Float(operand.Value - 128);
            }
            else if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                     operand.Value is >= 193 and <= 208)
            {
                value = Float(-(operand.Value - 192));
            }
            else
            {
                value = Bitcast(
                    _floatType,
                    GetRawSource(
                        instruction,
                        sourceIndex,
                        applySdwaIntegerModifiers: false));
            }

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
                value = Ext(4, _floatType, value);
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                value = _module.AddInstruction(SpirvOp.FNegate, _floatType, value);
            }

            return value;
        }

        private uint GetRawSource64(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                return LoadS64(operand.Value);
            }

            if (operand.Kind == Gen5OperandKind.VectorRegister)
            {
                var vectorLow = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    LoadV(operand.Value));
                var high = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    LoadV(operand.Value + 1));
                high = ShiftLeftLogical64(
                    high,
                    _module.Constant64(_ulongType, 32));
                return _module.AddInstruction(
                    SpirvOp.BitwiseOr,
                    _ulongType,
                    vectorLow,
                    high);
            }

            // Scalar inline negative constants are signed immediates. B64
            // consumers sign-extend them, so -1 denotes a full 64-bit mask.
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                operand.Value is >= 193 and <= 208)
            {
                var signed = -(long)(operand.Value - 192);
                return _module.Constant64(_ulongType, unchecked((ulong)signed));
            }

            var low = GetRawSource(instruction, sourceIndex);
            return _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
        }

        private uint LoadS64(uint register)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _ulongType, LoadS(register));
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadS(register + 1));
            high = ShiftLeftLogical64(high, _module.Constant64(_ulongType, 32));
            return _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, low, high);
        }

        private void StoreS64(uint register, uint value)
        {
            StoreS(
                register,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            var high = ShiftRightLogical64(
                value,
                _module.Constant64(_ulongType, 32));
            StoreS(
                register + 1,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, high));
        }

        private uint EmitFloatBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetFloatSource(instruction, reverse ? 1 : 0);
            var right = GetFloatSource(instruction, reverse ? 0 : 1);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(operation, _floatType, left, right));
        }

        private uint EmitFloatExtBinary(
            Gen5ShaderInstruction instruction,
            uint operation) =>
            EmitFloatResult(
                instruction,
                Ext(
                    operation,
                    _floatType,
                    GetFloatSource(instruction, 0),
                    GetFloatSource(instruction, 1)));

        private uint EmitFloatTernaryExt(
            Gen5ShaderInstruction instruction,
            uint operation)
        {
            var first = Ext(
                operation,
                _floatType,
                GetFloatSource(instruction, 0),
                GetFloatSource(instruction, 1));
            return EmitFloatResult(
                instruction,
                Ext(operation, _floatType, first, GetFloatSource(instruction, 2)));
        }

        private uint EmitIntegerBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            if (operation == SpirvOp.ShiftLeftLogical)
            {
                return ShiftLeftLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightLogical)
            {
                return ShiftRightLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightArithmetic)
            {
                return ShiftRightArithmetic(left, right);
            }

            return _module.AddInstruction(operation, _uintType, left, right);
        }

        private enum CubeCoordinate
        {
            Id,
            Sc,
            Tc,
            Ma,
        }

        private uint EmitCvtOffF32I4(Gen5ShaderInstruction instruction)
        {
            var index = BitwiseAnd(GetRawSource(instruction, 0), UInt(15));
            ReadOnlySpan<float> table =
            [
                0.0f,
                0.0625f,
                0.1250f,
                0.1875f,
                0.2500f,
                0.3125f,
                0.3750f,
                0.4375f,
                -0.5000f,
                -0.4375f,
                -0.3750f,
                -0.3125f,
                -0.2500f,
                -0.1875f,
                -0.1250f,
                -0.0625f,
            ];

            var result = UInt(BitConverter.SingleToUInt32Bits(table[^1]));
            for (var tableIndex = table.Length - 2; tableIndex >= 0; tableIndex--)
            {
                var matches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    index,
                    UInt((uint)tableIndex));
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(BitConverter.SingleToUInt32Bits(table[tableIndex])),
                    result);
            }

            return result;
        }

        private uint EmitCubeCoordinate(
            Gen5ShaderInstruction instruction,
            CubeCoordinate coordinate)
        {
            var x = GetFloatSource(instruction, 0);
            var y = GetFloatSource(instruction, 1);
            var z = GetFloatSource(instruction, 2);
            var nx = _module.AddInstruction(SpirvOp.FNegate, _floatType, x);
            var ny = _module.AddInstruction(SpirvOp.FNegate, _floatType, y);
            var nz = _module.AddInstruction(SpirvOp.FNegate, _floatType, z);
            var ax = Ext(4, _floatType, x);
            var ay = Ext(4, _floatType, y);
            var az = Ext(4, _floatType, z);
            var amaxXY = Ext(40, _floatType, ax, ay);
            var amax = Ext(40, _floatType, az, amaxXY);
            var ma = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                Float(2),
                amax);
            if (coordinate == CubeCoordinate.Ma)
            {
                return EmitFloatResult(instruction, ma);
            }

            var isZMax = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                az,
                amaxXY);
            var yGreaterOrEqualX = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                ay,
                ax);
            var isYMax = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                _module.AddInstruction(SpirvOp.LogicalNot, _boolType, isZMax),
                yGreaterOrEqualX);
            if (coordinate == CubeCoordinate.Id)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isYNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    y,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    Float(5),
                    Float(4));
                var yCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYNeg,
                    Float(3),
                    Float(2));
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    Float(1),
                    Float(0));
                var xyCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    yGreaterOrEqualX,
                    yCase,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        xyCase));
            }

            if (coordinate == CubeCoordinate.Sc)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    nx,
                    x);
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    z,
                    nz);
                var nonZCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    x,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        nonZCase));
            }

            var tcIsYNeg = _module.AddInstruction(
                SpirvOp.FOrdLessThan,
                _boolType,
                y,
                Float(0));
            var tcYCase = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                tcIsYNeg,
                nz,
                z);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    tcYCase,
                    ny));
        }

        private uint EmitAddWithCarry(Gen5ShaderInstruction instruction)
        {
            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            var carryMask = instruction.Sources.Count > 2
                ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                : Load(_boolType, _vcc);
            var carryIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                carryMask,
                UInt(1),
                UInt(0));
            var partial = IAdd(left, right);
            var result = IAdd(partial, carryIn);
            var carry = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, partial, left),
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, result, partial));
            StoreCarryOut(instruction, carry);
            return result;
        }

        private uint EmitSubtractWithBorrow(
            Gen5ShaderInstruction instruction,
            bool reverse)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            var borrowMask = instruction.Sources.Count > 2
                ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                : Load(_boolType, _vcc);
            var borrowIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                borrowMask,
                UInt(1),
                UInt(0));
            var partial = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
            var result = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                partial,
                borrowIn);
            var borrow = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, left, right),
                _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    partial,
                    borrowIn));
            StoreCarryOut(instruction, borrow);
            return result;
        }

        private uint BroadcastFirstWave64Active(uint value)
        {
            var lane = GuestWaveLane();
            EmitConditional(
                _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    lane,
                    UInt(0)),
                () => Store(WaveBroadcastScratchPointer(), UInt(0)));
            EmitWave64Barrier();

            var activeMask = BooleanToWaveMask(Load(_boolType, _exec));
            var lowMask = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                activeMask);
            var highMask = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(
                    activeMask,
                    _module.Constant64(_ulongType, 32)));
            var hasLow = IsNotZero(lowMask);
            var hasHigh = IsNotZero(highMask);
            var firstLow = Ext(73, _uintType, lowMask);
            var firstHigh = IAdd(UInt(32), Ext(73, _uintType, highMask));
            var firstLane = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                hasLow,
                firstLow,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    hasHigh,
                    firstHigh,
                    UInt(0)));
            var isFirst = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                lane,
                firstLane);
            EmitConditional(
                _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    isFirst,
                    _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        hasLow,
                        hasHigh)),
                () => Store(WaveBroadcastScratchPointer(), value));
            EmitWave64Barrier();
            var result = Load(_uintType, WaveBroadcastScratchPointer());
            EmitWave64Barrier();
            return result;
        }

        private void StoreCarryOut(
            Gen5ShaderInstruction instruction,
            uint carry)
        {
            var activeCarry = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                carry);
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } register })
            {
                StoreWaveMask(register, activeCarry);
                return;
            }

            StoreWaveMask(106, activeCarry);
        }

        private bool TryEmitReadlane(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "VReadlaneB32 expects scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            var src0 = GetRawSource(instruction, 0);

            if (_subgroupInvocationIdInput != 0)
            {
                // sdst = vsrc0[lane(src1)] — broadcast from the specified lane.
                var laneSelect = GetRawSource(instruction, 1);
                var broadcast = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBroadcast,
                    _uintType,
                    UInt(3),  // Subgroup scope
                    src0,
                    laneSelect);
                StoreS(destination, broadcast);
            }
            else
            {
                // Fallback: no subgroup ops, read current lane's value.
                StoreS(destination, src0);
            }

            return true;
        }

        private uint EmitPermlane16(
            Gen5ShaderInstruction instruction,
            bool exchangeRows)
        {
            if (instruction.Control is not Gen5Vop3Control control ||
                (control.OperandSelect & ~3u) != 0 ||
                control.AbsoluteMask != 0 ||
                control.NegateMask != 0 ||
                control.OutputModifier != 0 ||
                control.Clamp)
            {
                throw new InvalidOperationException(
                    $"invalid permlane modifiers for {instruction.Opcode}");
            }

            var value = GetRawSource(instruction, 0);
            var selectorLow = GetRawSource(instruction, 1);
            var selectorHigh = GetRawSource(instruction, 2);
            var lane = GuestWaveLane();
            var localLane = BitwiseAnd(lane, UInt(15));
            var lowHalf = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                localLane,
                UInt(8));
            var lowShift = ShiftLeftLogical(localLane, UInt(2));
            var highLane = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                localLane,
                UInt(8));
            var highShift = ShiftLeftLogical(highLane, UInt(2));
            var lowSelector = BitwiseAnd(
                ShiftRightLogical(selectorLow, lowShift),
                UInt(15));
            var highSelector = BitwiseAnd(
                ShiftRightLogical(selectorHigh, highShift),
                UInt(15));
            var selector = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                lowHalf,
                lowSelector,
                highSelector);
            var rowBase = BitwiseAnd(lane, UInt(0xFFFF_FFF0));
            if (exchangeRows)
            {
                rowBase = BitwiseXor(rowBase, UInt(16));
            }

            var targetLane = IAdd(rowBase, selector);
            targetLane = BitwiseAnd(targetLane, UInt(31));
            var shuffled = _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),
                value,
                targetLane);
            var fetchInactive = (control.OperandSelect & 1) != 0;
            if (fetchInactive)
            {
                return shuffled;
            }

            var activeWord = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                Load(_boolType, _exec),
                UInt(1),
                UInt(0));
            var sourceActive = IsNotZero(
                _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    activeWord,
                    targetLane));
            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                sourceActive,
                shuffled,
                UInt(0));
        }

        private uint EmitFloatResult(
            Gen5ShaderInstruction instruction,
            uint value)
        {
            uint outputModifier = 0;
            var clamp = false;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
                case Gen5SdwaControl control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
            }

            value = outputModifier switch
            {
                1 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(2)),
                2 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(4)),
                3 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(0.5f)),
                _ => value,
            };
            if (clamp)
            {
                value = Ext(43, _floatType, value, Float(0), Float(1));
            }

            return Bitcast(_uintType, value);
        }

        private uint TruncateFloat32ForPack(uint value)
        {
            var raw = BitwiseAnd(
                Bitcast(_uintType, value),
                UInt(0xFFFF_E000));
            return Bitcast(_floatType, raw);
        }

        private uint Ext(uint operation, uint resultType, params uint[] operands)
        {
            var values = new uint[2 + operands.Length];
            values[0] = _glsl;
            values[1] = operation;
            operands.CopyTo(values, 2);
            return _module.AddInstruction(SpirvOp.ExtInst, resultType, values);
        }

        private uint IsNotZero(uint value) =>
            _module.AddInstruction(SpirvOp.INotEqual, _boolType, value, UInt(0));

        private uint IsNotZero64(uint value) =>
            _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                value,
                _module.Constant64(_ulongType, 0));

        private uint SignBit(uint value) =>
            ShiftRightLogical(value, UInt(31));

        private uint SignedAddOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var sameSourceSign = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                sameSourceSign,
                resultSignChanged);
        }

        private uint SignedSubOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var differentSourceSign = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                differentSourceSign,
                resultSignChanged);
        }

        private static bool TryDecodeInlineConstant(uint encoded, out uint value) =>
            Gen5InlineConstants.TryDecode(encoded, out value);
    }
}
