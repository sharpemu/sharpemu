// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Evaluation;

internal static class Gen5EvaluableLowering
{
    internal enum Form : byte
    {
        Control,
        Scalar,
        Scalar64,
        ScalarImmediate,
        ScalarMemory,
        VectorInteger,
        VectorFloat,
        VectorLane,
        Buffer,
    }

    private static readonly Dictionary<string, (Gen5EvalOp Op, Form Form, int Parameter)> _opcodes = new(StringComparer.Ordinal)
    {
        ["SNop"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SSleep"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SWaitcnt"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SWaitcntDepctr"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SInstPrefetch"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["STtraceData"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SSetprio"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SClause"] = (Gen5EvalOp.Nop, Form.Control, 0),
        ["SEndpgm"] = (Gen5EvalOp.End, Form.Control, 0),
        ["SBranch"] = (Gen5EvalOp.Branch, Form.Control, 0),
        ["SCbranchScc0"] = (Gen5EvalOp.BranchScc0, Form.Control, 0),
        ["SCbranchScc1"] = (Gen5EvalOp.BranchScc1, Form.Control, 0),
        ["SCbranchVccz"] = (Gen5EvalOp.BranchVccZero, Form.Control, 0),
        ["SCbranchVccnz"] = (Gen5EvalOp.BranchVccNonZero, Form.Control, 0),
        ["SCbranchExecz"] = (Gen5EvalOp.BranchExecZero, Form.Control, 0),
        ["SCbranchExecnz"] = (Gen5EvalOp.BranchExecNonZero, Form.Control, 0),

        ["SMovB32"] = (Gen5EvalOp.ScalarMove, Form.Scalar, 0),
        ["SMovB64"] = (Gen5EvalOp.ScalarMove64, Form.Scalar64, 0),
        ["SNotB64"] = (Gen5EvalOp.ScalarNot64, Form.Scalar64, 0),
        ["SAddI32"] = (Gen5EvalOp.ScalarAddI, Form.Scalar, 0),
        ["SSubI32"] = (Gen5EvalOp.ScalarSubI, Form.Scalar, 0),
        ["SSubU32"] = (Gen5EvalOp.ScalarSubU, Form.Scalar, 0),
        ["SMulI32"] = (Gen5EvalOp.ScalarMul, Form.Scalar, 0),
        ["SMulHiU32"] = (Gen5EvalOp.ScalarMulHiU, Form.Scalar, 0),
        ["SCselectB64"] = (Gen5EvalOp.ScalarSelect64, Form.Scalar64, 0),
        ["SAndB64"] = (Gen5EvalOp.ScalarAnd64, Form.Scalar64, 0),
        ["SAndn2B64"] = (Gen5EvalOp.ScalarAndNot64, Form.Scalar64, 0),
        ["SLshlB32"] = (Gen5EvalOp.ScalarShiftLeft, Form.Scalar, 0),
        ["SLshl1AddU32"] = (Gen5EvalOp.ScalarShiftLeftAdd, Form.Scalar, 1),
        ["SLshl2AddU32"] = (Gen5EvalOp.ScalarShiftLeftAdd, Form.Scalar, 2),
        ["SLshl3AddU32"] = (Gen5EvalOp.ScalarShiftLeftAdd, Form.Scalar, 3),
        ["SLshl4AddU32"] = (Gen5EvalOp.ScalarShiftLeftAdd, Form.Scalar, 4),
        ["SBfmB32"] = (Gen5EvalOp.ScalarBitfieldMask, Form.Scalar, 0),
        ["SAndSaveexecB64"] = (Gen5EvalOp.ScalarAndSaveExec, Form.Scalar64, 0),
        ["SMovkI32"] = (Gen5EvalOp.ScalarMoveK, Form.ScalarImmediate, 0),
        ["SBufferLoadDword"] = (Gen5EvalOp.ScalarBufferLoad, Form.ScalarMemory, 1),
        ["SBufferLoadDwordx2"] = (Gen5EvalOp.ScalarBufferLoad, Form.ScalarMemory, 2),
        ["SBufferLoadDwordx4"] = (Gen5EvalOp.ScalarBufferLoad, Form.ScalarMemory, 4),
        ["SBufferLoadDwordx8"] = (Gen5EvalOp.ScalarBufferLoad, Form.ScalarMemory, 8),
        ["SLoadDword"] = (Gen5EvalOp.ScalarLoad, Form.ScalarMemory, 1),
        ["SLoadDwordx2"] = (Gen5EvalOp.ScalarLoad, Form.ScalarMemory, 2),
        ["SLoadDwordx4"] = (Gen5EvalOp.ScalarLoad, Form.ScalarMemory, 4),
        ["SLoadDwordx8"] = (Gen5EvalOp.ScalarLoad, Form.ScalarMemory, 8),

        ["VMovB32"] = (Gen5EvalOp.VectorMove, Form.VectorInteger, 0),
        ["VCndmaskB32"] = (Gen5EvalOp.VectorSelect, Form.VectorInteger, 0),
        ["VAddI32"] = (Gen5EvalOp.VectorAdd, Form.VectorInteger, 0),
        ["VSubI32"] = (Gen5EvalOp.VectorSub, Form.VectorInteger, 0),
        ["VSubrevI32"] = (Gen5EvalOp.VectorSubReverse, Form.VectorInteger, 0),
        ["VAddcU32"] = (Gen5EvalOp.VectorAddCarry, Form.VectorInteger, 0),
        ["VAddCoCiU32"] = (Gen5EvalOp.VectorAddCarry, Form.VectorInteger, 0),
        ["VMulLoU32"] = (Gen5EvalOp.VectorMulLo, Form.VectorInteger, 0),
        ["VMulHiU32"] = (Gen5EvalOp.VectorMulHiU, Form.VectorInteger, 0),
        ["VMulU32U24"] = (Gen5EvalOp.VectorMulU24, Form.VectorInteger, 0),
        ["VMadU32U24"] = (Gen5EvalOp.VectorMadU24, Form.VectorInteger, 0),
        ["VMadU64U32"] = (Gen5EvalOp.VectorMadU64U32, Form.VectorInteger, 0),
        ["VAndB32"] = (Gen5EvalOp.VectorAnd, Form.VectorInteger, 0),
        ["VLshlrevB32"] = (Gen5EvalOp.VectorShiftLeftReverse, Form.VectorInteger, 0),
        ["VLshrrevB32"] = (Gen5EvalOp.VectorShiftRightReverse, Form.VectorInteger, 0),
        ["VLshlAddU32"] = (Gen5EvalOp.VectorShiftLeftAdd, Form.VectorInteger, 0),
        ["VAndOrB32"] = (Gen5EvalOp.VectorAndOr, Form.VectorInteger, 0),
        ["VAdd3U32"] = (Gen5EvalOp.VectorAdd3, Form.VectorInteger, 0),
        ["VBfiB32"] = (Gen5EvalOp.VectorBitfieldInsert, Form.VectorInteger, 0),
        ["VMinU32"] = (Gen5EvalOp.VectorMinU, Form.VectorInteger, 0),
        ["VBcntU32B32"] = (Gen5EvalOp.VectorBitCount, Form.VectorInteger, 0),
        ["VCvtF32U32"] = (Gen5EvalOp.VectorConvertF32U32, Form.VectorInteger, 0),
        ["VReadfirstlaneB32"] = (Gen5EvalOp.VectorReadFirstLane, Form.VectorLane, 0),

        ["VCvtU32F32"] = (Gen5EvalOp.VectorConvertU32F32, Form.VectorFloat, 0),
        ["VMulF32"] = (Gen5EvalOp.VectorMulF32, Form.VectorFloat, 0),
        ["VRcpIflagF32"] = (Gen5EvalOp.VectorReciprocalF32, Form.VectorFloat, 0),

        ["BufferLoadDword"] = (Gen5EvalOp.BufferLoad, Form.Buffer, 1),
        ["BufferLoadDwordx2"] = (Gen5EvalOp.BufferLoad, Form.Buffer, 2),
        ["BufferLoadDwordx3"] = (Gen5EvalOp.BufferLoad, Form.Buffer, 3),
        ["BufferLoadDwordx4"] = (Gen5EvalOp.BufferLoad, Form.Buffer, 4),
        ["BufferLoadFormatX"] = (Gen5EvalOp.BufferLoad, Form.Buffer, -1),
        ["BufferStoreDword"] = (Gen5EvalOp.BufferStore, Form.Buffer, 1),
        ["BufferStoreDwordx2"] = (Gen5EvalOp.BufferStore, Form.Buffer, 2),
        ["BufferStoreDwordx3"] = (Gen5EvalOp.BufferStore, Form.Buffer, 3),
        ["BufferStoreDwordx4"] = (Gen5EvalOp.BufferStore, Form.Buffer, 4),
        ["BufferStoreFormatX"] = (Gen5EvalOp.BufferStore, Form.Buffer, -1),
    };

    internal static IEnumerable<(string Opcode, Form Form)> Entries => _opcodes.Select(entry => (entry.Key, entry.Value.Form));

    public static bool TryLower(Gen5ShaderInstruction instruction, Dictionary<uint, int> indexOfPc, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            return TryLowerCompare(instruction, out lowered, out reason);
        }

        if (instruction.Opcode.StartsWith("SCmp", StringComparison.Ordinal))
        {
            return TryLowerScalarCompare(instruction, out lowered, out reason);
        }

        if (!_opcodes.TryGetValue(instruction.Opcode, out var entry))
        {
            reason = "opcode " + instruction.Opcode;
            return false;
        }

        switch (entry.Form)
        {
            case Form.Control:
                return TryLowerControl(instruction, entry.Op, indexOfPc, out lowered, out reason);
            case Form.Scalar:
            case Form.Scalar64:
                return TryLowerScalar(instruction, entry.Op, entry.Parameter, out lowered, out reason);
            case Form.ScalarImmediate:
                return TryLowerScalarImmediate(instruction, entry.Op, out lowered, out reason);
            case Form.ScalarMemory:
                return TryLowerScalarMemory(instruction, entry.Op, entry.Parameter, out lowered, out reason);
            case Form.VectorInteger:
            case Form.VectorFloat:
            case Form.VectorLane:
                return TryLowerVector(instruction, entry.Op, entry.Form, out lowered, out reason);
            default:
                return TryLowerBuffer(instruction, entry.Op, entry.Parameter, out lowered, out reason);
        }
    }

    public static uint VectorRegistersUsed(Gen5EvalInstruction instruction)
    {
        var highest = 0u;
        void Note(Gen5EvalOperand operand, uint width = 1)
        {
            if (operand.Kind == Gen5EvalOperandKind.Vector)
            {
                highest = Math.Max(highest, operand.Register + width);
            }
        }

        Note(instruction.Source0);
        Note(instruction.Source1);
        Note(instruction.Source2, instruction.Op == Gen5EvalOp.VectorMadU64U32 ? 2u : 1u);
        switch (instruction.Op)
        {
            case Gen5EvalOp.BufferLoad:
            case Gen5EvalOp.BufferStore:
                highest = Math.Max(highest, instruction.VectorData + instruction.DwordCount);
                if (instruction.IndexEnabled || instruction.OffsetEnabled)
                {
                    highest = Math.Max(highest, instruction.VectorAddress + (instruction.IndexEnabled && instruction.OffsetEnabled ? 2u : 1u));
                }

                break;
            case Gen5EvalOp.VectorMadU64U32:
                highest = Math.Max(highest, instruction.Destination + 2);
                break;
            case >= Gen5EvalOp.VectorMove and <= Gen5EvalOp.VectorReciprocalF32
                when instruction.Op is not (Gen5EvalOp.VectorReadFirstLane or Gen5EvalOp.VectorCompare):
                highest = Math.Max(highest, instruction.Destination + 1);
                break;
        }

        return highest;
    }

    private static bool TryLowerControl(Gen5ShaderInstruction instruction, Gen5EvalOp op, Dictionary<uint, int> indexOfPc, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        var target = -1;
        if (op is >= Gen5EvalOp.Branch and <= Gen5EvalOp.BranchExecNonZero)
        {
            var offset = (short)(instruction.Words[0] & 0xFFFF);
            var targetPc = (long)instruction.Pc + 4 + offset * 4L;
            if (targetPc < 0 || !indexOfPc.TryGetValue((uint)targetPc, out target))
            {
                reason = "branch target";
                return false;
            }
        }

        lowered = new Gen5EvalInstruction { Op = op, Pc = instruction.Pc, Target = target };
        return true;
    }

    private static bool TryLowerScalar(Gen5ShaderInstruction instruction, Gen5EvalOp op, int parameter, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Control is not null || instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
            instruction.Sources.Count == 0)
        {
            reason = "scalar operands";
            return false;
        }

        var source1 = Gen5EvalOperand.None;
        if (!TryOperand(instruction.Sources[0], out var source0) ||
            (instruction.Sources.Count > 1 && !TryOperand(instruction.Sources[1], out source1)))
        {
            reason = "scalar source";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = op,
            Pc = instruction.Pc,
            Destination = instruction.Destinations[0].Value,
            Source0 = source0,
            Source1 = source1,
            Parameter = parameter,
        };
        return true;
    }

    private static bool TryLowerScalarImmediate(Gen5ShaderInstruction instruction, Gen5EvalOp op, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
        {
            reason = "scalar operands";
            return false;
        }

        var immediate = unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
        lowered = new Gen5EvalInstruction
        {
            Op = op,
            Pc = instruction.Pc,
            Destination = instruction.Destinations[0].Value,
            Source0 = Constant(immediate),
        };
        return true;
    }

    private static bool TryLowerScalarCompare(Gen5ShaderInstruction instruction, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        var opcode = instruction.Opcode;
        var immediateForm = opcode.StartsWith("SCmpk", StringComparison.Ordinal);
        var body = opcode[(immediateForm ? 5 : 4)..];
        if (body.Length != 5 || !TryParseIntegerType(body[2..], out var type) || !TryParseIntegerPredicate(body[..2], out var compare))
        {
            reason = "opcode " + opcode;
            return false;
        }

        if (immediateForm)
        {
            if (instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                reason = "scalar operands";
                return false;
            }

            var raw = instruction.Words[0] & 0xFFFF;
            var immediate = type == Gen5EvalType.U32 ? raw : unchecked((uint)(short)raw);
            lowered = new Gen5EvalInstruction
            {
                Op = Gen5EvalOp.ScalarCompareK,
                Pc = instruction.Pc,
                Compare = compare,
                Type = type,
                Source0 = new Gen5EvalOperand(Gen5EvalOperandKind.Scalar, instruction.Destinations[0].Value, 0, 0, 0),
                Source1 = Constant(immediate),
            };
            return true;
        }

        if (instruction.Sources.Count != 2 || !TryOperand(instruction.Sources[0], out var source0) || !TryOperand(instruction.Sources[1], out var source1))
        {
            reason = "scalar source";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = Gen5EvalOp.ScalarCompare,
            Pc = instruction.Pc,
            Compare = compare,
            Type = type,
            Source0 = source0,
            Source1 = source1,
        };
        return true;
    }

    private static bool TryLowerScalarMemory(Gen5ShaderInstruction instruction, Gen5EvalOp op, int count, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Control is not Gen5ScalarMemoryControl control || control.DestinationCount != count ||
            instruction.Destinations.Count != count || instruction.Sources.Count == 0 || instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
        {
            reason = "scalar memory operands";
            return false;
        }

        var first = instruction.Destinations[0];
        if (first.Kind != Gen5OperandKind.ScalarRegister || first.Value + (uint)count > Gen5WaveState.Vcc + 2)
        {
            reason = "scalar memory destination";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = op,
            Pc = instruction.Pc,
            Destination = first.Value,
            ScalarResource = instruction.Sources[0].Value,
            OffsetBytes = control.ImmediateOffsetBytes,
            DynamicOffsetRegister = control.DynamicOffsetRegister,
            DwordCount = (uint)count,
        };
        return true;
    }

    private static bool TryLowerVector(Gen5ShaderInstruction instruction, Gen5EvalOp op, Form form, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Destinations.Count != 1)
        {
            reason = "vector destination";
            return false;
        }

        var destination = instruction.Destinations[0];
        var scalarDestination = op == Gen5EvalOp.VectorReadFirstLane;
        if (destination.Kind != (scalarDestination ? Gen5OperandKind.ScalarRegister : Gen5OperandKind.VectorRegister))
        {
            reason = "vector destination";
            return false;
        }

        if (scalarDestination && destination.Value > Gen5WaveState.Vcc + 1)
        {
            reason = "lane read destination";
            return false;
        }

        var sources = new Gen5EvalOperand[3];
        for (var index = 0; index < Math.Min(3, instruction.Sources.Count); index++)
        {
            if (!TryOperand(instruction.Sources[index], out sources[index]))
            {
                reason = "vector source";
                return false;
            }
        }

        var maskDestination = Gen5WaveState.Vcc;
        switch (instruction.Control)
        {
            case null:
                break;
            case Gen5Vop3Control vop3:
                if (HasModifiers(vop3))
                {
                    reason = "vop3 modifiers";
                    return false;
                }

                maskDestination = vop3.ScalarDestination ?? maskDestination;
                break;
            case Gen5SdwaControl sdwa:
                if (HasModifiers(sdwa) || sdwa.ScalarDestination is not null || scalarDestination || form == Form.VectorFloat)
                {
                    reason = "sdwa modifiers";
                    return false;
                }

                break;
            default:
                reason = "vector control";
                return false;
        }

        if (maskDestination > 106)
        {
            reason = "mask destination";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = op,
            Pc = instruction.Pc,
            Destination = destination.Value,
            MaskDestination = maskDestination,
            HasCarryInOperand = op is Gen5EvalOp.VectorAddCarry or Gen5EvalOp.VectorSelect && instruction.Sources.Count > 2,
            Source0 = sources[0],
            Source1 = sources[1],
            Source2 = sources[2],
        };
        return true;
    }

    private static bool TryLowerCompare(Gen5ShaderInstruction instruction, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        var opcode = instruction.Opcode;
        var writesExec = opcode.StartsWith("VCmpx", StringComparison.Ordinal);
        var body = opcode[(writesExec ? 5 : 4)..];
        if (body.Length < 4 || !TryParseIntegerType(body[^3..], out var type) || !TryParseIntegerPredicate(body[..^3], out var compare))
        {
            reason = "opcode " + opcode;
            return false;
        }

        if (instruction.Sources.Count < 2 || !TryOperand(instruction.Sources[0], out var source0) || !TryOperand(instruction.Sources[1], out var source1))
        {
            reason = "compare source";
            return false;
        }

        var destination = Gen5WaveState.Vcc;
        switch (instruction.Control)
        {
            case null:
                break;
            case Gen5Vop3Control vop3:
                if (HasModifiers(vop3))
                {
                    reason = "vop3 modifiers";
                    return false;
                }

                destination = vop3.ScalarDestination ?? destination;
                break;
            case Gen5SdwaControl sdwa:
                if (HasModifiers(sdwa))
                {
                    reason = "sdwa modifiers";
                    return false;
                }

                destination = sdwa.ScalarDestination ?? destination;
                break;
            default:
                reason = "compare control";
                return false;
        }

        if (!writesExec && destination > 106)
        {
            reason = "mask destination";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = Gen5EvalOp.VectorCompare,
            Pc = instruction.Pc,
            Compare = compare,
            Type = type,
            WritesExec = writesExec,
            MaskDestination = destination,
            Source0 = source0,
            Source1 = source1,
        };
        return true;
    }

    private static bool TryLowerBuffer(Gen5ShaderInstruction instruction, Gen5EvalOp op, int count, out Gen5EvalInstruction lowered, out string reason)
    {
        lowered = null!;
        reason = string.Empty;
        if (instruction.Encoding != Gen5ShaderEncoding.Mubuf || instruction.Control is not Gen5BufferMemoryControl control || control.Typed ||
            instruction.Sources.Count < 3 || instruction.Words.Count < 2)
        {
            reason = "buffer operands";
            return false;
        }

        var word = instruction.Words[0];
        var extra = instruction.Words[1];
        var lds = ((word >> 16) & 1) != 0;
        var textureFailEnable = ((extra >> 23) & 1) != 0;
        if (lds || textureFailEnable)
        {
            reason = "buffer lds or tfe";
            return false;
        }

        if (!TryOperand(instruction.Sources[2], out var scalarOffset) || scalarOffset.Kind is Gen5EvalOperandKind.Vector or Gen5EvalOperandKind.Scc or Gen5EvalOperandKind.VccZero or Gen5EvalOperandKind.ExecZero)
        {
            reason = "buffer offset";
            return false;
        }

        var formatted = count < 0;
        var dwords = formatted ? 1u : (uint)count;
        if (control.DwordCount != dwords || control.ScalarResource > 100)
        {
            reason = "buffer operands";
            return false;
        }

        lowered = new Gen5EvalInstruction
        {
            Op = op,
            Pc = instruction.Pc,
            Source2 = scalarOffset,
            VectorAddress = control.VectorAddress,
            VectorData = control.VectorData,
            ScalarResource = control.ScalarResource,
            OffsetBytes = control.OffsetBytes,
            IndexEnabled = control.IndexEnabled,
            OffsetEnabled = control.OffsetEnabled,
            DwordCount = dwords,
            Formatted = formatted,
        };
        return true;
    }

    private static bool TryOperand(Gen5Operand operand, out Gen5EvalOperand lowered)
    {
        lowered = default;
        switch (operand.Kind)
        {
            case Gen5OperandKind.ScalarRegister:
                if (operand.Value > 127 || operand.Value == 125)
                {
                    return false;
                }

                lowered = new Gen5EvalOperand(Gen5EvalOperandKind.Scalar, operand.Value, 0, 0, 0);
                return true;
            case Gen5OperandKind.VectorRegister:
                lowered = new Gen5EvalOperand(Gen5EvalOperandKind.Vector, operand.Value, 0, 0, 0);
                return operand.Value < 256;
            case Gen5OperandKind.LiteralConstant:
                lowered = Constant(operand.Value);
                return true;
            case Gen5OperandKind.EncodedConstant:
                switch (operand.Value)
                {
                    case 251:
                        lowered = new Gen5EvalOperand(Gen5EvalOperandKind.VccZero, 0, 0, 0, 0);
                        return true;
                    case 252:
                        lowered = new Gen5EvalOperand(Gen5EvalOperandKind.ExecZero, 0, 0, 0, 0);
                        return true;
                    case 253:
                        lowered = new Gen5EvalOperand(Gen5EvalOperandKind.Scc, 0, 0, 0, 0);
                        return true;
                }

                if (Gen5InlineConstants.IsAperture(operand.Value) || operand.Value == 239 ||
                    !Gen5InlineConstants.TryDecode(operand.Value, out var bits))
                {
                    return false;
                }

                var floatBits = operand.Value switch
                {
                    >= 128 and <= 192 => BitConverter.SingleToUInt32Bits(operand.Value - 128),
                    >= 193 and <= 208 => BitConverter.SingleToUInt32Bits(-(float)(operand.Value - 192)),
                    _ => bits,
                };
                var bits64 = operand.Value is >= 193 and <= 208 ? unchecked((ulong)(long)(int)bits) : bits;
                lowered = new Gen5EvalOperand(Gen5EvalOperandKind.Constant, 0, bits, floatBits, bits64);
                return true;
            default:
                return false;
        }
    }

    private static Gen5EvalOperand Constant(uint value) => new(Gen5EvalOperandKind.Constant, 0, value, value, value);

    private static bool HasModifiers(Gen5Vop3Control control) =>
        (control.AbsoluteMask | control.NegateMask | control.OutputModifier | control.OperandSelect) != 0 || control.Clamp;

    private static bool HasModifiers(Gen5SdwaControl control) =>
        (control.AbsoluteMask | control.NegateMask | control.OutputModifier) != 0 || control.Clamp ||
        control.Source0Select != 6 || control.Source1Select != 6 || control.DestinationSelect != 6;

    private static bool TryParseIntegerType(string name, out Gen5EvalType type)
    {
        type = name == "I32" ? Gen5EvalType.I32 : Gen5EvalType.U32;
        return name is "U32" or "I32";
    }

    private static bool TryParseIntegerPredicate(string name, out Gen5EvalCompare compare)
    {
        compare = name switch
        {
            "F" => Gen5EvalCompare.False,
            "Lt" => Gen5EvalCompare.Less,
            "Eq" => Gen5EvalCompare.Equal,
            "Le" => Gen5EvalCompare.LessEqual,
            "Gt" => Gen5EvalCompare.Greater,
            "Ne" or "Lg" => Gen5EvalCompare.NotEqual,
            "Ge" => Gen5EvalCompare.GreaterEqual,
            "T" => Gen5EvalCompare.True,
            _ => (Gen5EvalCompare)255,
        };
        return compare != (Gen5EvalCompare)255;
    }
}
