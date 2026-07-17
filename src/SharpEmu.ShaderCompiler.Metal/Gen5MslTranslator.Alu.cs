// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const string TauLiteral = "6.2831853071795862f";

        // ---- vector ALU ----

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

            switch (instruction.Opcode)
            {
                case "VReadfirstlaneB32":
                {
                    if (instruction.Destinations.Count == 0 ||
                        instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                        instruction.Sources.Count == 0)
                    {
                        error = "invalid read-first-lane operands";
                        return false;
                    }

                    // Under the single-lane graphics model the "first active
                    // lane" is always this lane; a real simd_shuffle would read
                    // another fragment's value. Compute broadcasts from the
                    // first guest-active lane (the ballot of EXEC), matching
                    // the SPIR-V translator — SPIR-V's own BroadcastFirst uses
                    // the first host-active invocation, which may be a lane the
                    // guest has masked off.
                    var value = RawSource(instruction, 0);
                    if (IsSingleLaneStage)
                    {
                        StoreScalar(instruction.Destinations[0].Value, Temp("uint", value));
                        return true;
                    }

                    if (IsWave64)
                    {
                        StoreScalar(
                            instruction.Destinations[0].Value,
                            EmitWave64ReadFirstLane(value));
                        return true;
                    }

                    var mask = Temp("uint", "sharpemu_ballot(exec)");
                    var firstLane = Temp("uint", $"{mask} == 0u ? 0u : (uint)ctz({mask})");
                    StoreScalar(
                        instruction.Destinations[0].Value,
                        Temp("uint", ShuffleLane(value, firstLane)));
                    return true;
                }
                case "VReadlaneB32":
                {
                    if (instruction.Destinations.Count == 0 ||
                        instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "VReadlaneB32 expects scalar destination";
                        return false;
                    }

                    var value = RawSource(instruction, 0);
                    var lane = Temp("uint", $"({RawSource(instruction, 1)}) & 31u");
                    StoreScalar(
                        instruction.Destinations[0].Value,
                        Temp("uint", ShuffleLane(value, lane)));
                    return true;
                }
                case "VWritelaneB32":
                {
                    // vdst[lane(src1)] = src0; a writelane lands regardless of EXEC.
                    var destination = DestinationVector(instruction);
                    var source = RawSource(instruction, 0);
                    var lane = RawSource(instruction, 1);
                    StoreVector(
                        destination,
                        $"(sharpemu_lane == (({lane}) & 31u)) ? ({source}) : v[{destination}]",
                        guardWithExec: false);
                    return true;
                }
                case "VCndmaskB32":
                {
                    // dst = mask-bit(lane) ? src1 : src0. Sources are raw (no
                    // float modifiers), matching the SPIR-V translator; the mask
                    // is VCC for VOP2 and an explicit SGPR operand for VOP3.
                    var mask = instruction.Sources.Count > 2
                        ? MaskBitExpression(instruction.Sources[2])
                        : "vcc";
                    StoreVector(
                        DestinationVector(instruction),
                        $"({mask}) ? ({RawSource(instruction, 1)}) : ({RawSource(instruction, 0)})");
                    return true;
                }
            }

            return TryEmitVectorValue(instruction, out error);
        }

        private bool TryEmitVectorValue(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var destination = DestinationVector(instruction);
            string? expression = instruction.Opcode switch
            {
                "VMovB32" => RawSource(instruction, 0),

                // ---- float arithmetic ----
                "VAddF32" => FloatResult(instruction, $"{F(instruction, 0)} + {F(instruction, 1)}"),
                "VSubF32" => FloatResult(instruction, $"{F(instruction, 0)} - {F(instruction, 1)}"),
                "VSubrevF32" => FloatResult(instruction, $"{F(instruction, 1)} - {F(instruction, 0)}"),
                "VMulF32" => FloatResult(instruction, $"{F(instruction, 0)} * {F(instruction, 1)}"),
                "VMinF32" => FloatResult(instruction, $"fmin({F(instruction, 0)}, {F(instruction, 1)})"),
                "VMaxF32" => FloatResult(instruction, $"fmax({F(instruction, 0)}, {F(instruction, 1)})"),
                // The decoder normalizes mk/ak literal placement, so every MAD/FMA
                // form is fma(src0, src1, src2) exactly like the SPIR-V translator.
                "VFmaF32" or "VMadF32" or "VMadAkF32" or "VMadMkF32" or "VFmaAkF32" or "VFmaMkF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, {F(instruction, 2)})"),
                "VFmacF32" or "VMacF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, as_type<float>(v[{destination}]))"),
                "VFloorF32" => FloatResult(instruction, $"floor({F(instruction, 0)})"),
                "VCeilF32" => FloatResult(instruction, $"ceil({F(instruction, 0)})"),
                "VTruncF32" => FloatResult(instruction, $"trunc({F(instruction, 0)})"),
                "VRndneF32" => FloatResult(instruction, $"rint({F(instruction, 0)})"),
                "VFractF32" => FloatResult(instruction, $"fract({F(instruction, 0)})"),
                "VSqrtF32" => FloatResult(instruction, $"sqrt({F(instruction, 0)})"),
                "VRsqF32" => FloatResult(instruction, $"rsqrt({F(instruction, 0)})"),
                "VRcpF32" or "VRcpIflagF32" => FloatResult(instruction, $"(1.0f / {F(instruction, 0)})"),
                "VLogF32" => FloatResult(instruction, $"log2({F(instruction, 0)})"),
                "VExpF32" => FloatResult(instruction, $"exp2({F(instruction, 0)})"),
                // GCN sin/cos take revolutions; mirror the SPIR-V Tau prescale.
                "VSinF32" => FloatResult(instruction, $"sin({F(instruction, 0)} * {TauLiteral})"),
                "VCosF32" => FloatResult(instruction, $"cos({F(instruction, 0)} * {TauLiteral})"),
                "VLdexpF32" =>
                    FloatResult(instruction, $"ldexp({F(instruction, 0)}, as_type<int>({RawSource(instruction, 1)}))"),
                "VMin3F32" =>
                    FloatResult(instruction, $"fmin(fmin({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMax3F32" =>
                    FloatResult(instruction, $"fmax(fmax({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMed3F32" =>
                    FloatResult(instruction, $"fmax(fmin({F(instruction, 0)}, {F(instruction, 1)}), fmin(fmax({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)}))"),

                // ---- conversions ----
                "VCvtF32I32" => FloatResult(instruction, $"(float)as_type<int>({RawSource(instruction, 0)})"),
                "VCvtF32U32" => FloatResult(instruction, $"(float)({RawSource(instruction, 0)})"),
                "VCvtU32F32" => $"(uint)({F(instruction, 0)})",
                "VCvtI32F32" => AsUInt($"(int)({F(instruction, 0)})"),
                // RPI rounds toward positive infinity; FLR toward negative.
                "VCvtRpiI32F32" => AsUInt($"(int)ceil({F(instruction, 0)})"),
                "VCvtFlrI32F32" => AsUInt($"(int)floor({F(instruction, 0)})"),
                "VCvtF32Ubyte0" => FloatResult(instruction, $"(float)(({RawSource(instruction, 0)}) & 0xFFu)"),
                "VCvtF32Ubyte1" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 8) & 0xFFu)"),
                "VCvtF32Ubyte2" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 16) & 0xFFu)"),
                "VCvtF32Ubyte3" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 24) & 0xFFu)"),
                "VCvtF16F32" =>
                    $"((uint)as_type<ushort>(half({F(instruction, 0)})))",
                "VCvtF32F16" =>
                    AsUInt($"(float)as_type<half>((ushort)(({RawSource(instruction, 0)}) & 0xFFFFu))"),
                "VCvtOffF32I4" =>
                    AsUInt($"sharpemu_off_i4_table[({RawSource(instruction, 0)}) & 15u]"),
                "VCvtPkU8F32" =>
                    EmitCvtPkU8F32(instruction),
                "VCvtPkrtzF16F32" =>
                    EmitCvtPkrtzF16F32(instruction),
                "VCvtPknormI16F32" =>
                    $"pack_float_to_snorm2x16(float2({F(instruction, 0)}, {F(instruction, 1)}))",
                "VCvtPknormU16F32" =>
                    $"pack_float_to_unorm2x16(float2({F(instruction, 0)}, {F(instruction, 1)}))",

                // ---- integer arithmetic ----
                "VAddU32" or "VAddI32" =>
                    $"(({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}))",
                "VSubU32" or "VSubI32" =>
                    $"(({RawSource(instruction, 0)}) - ({RawSource(instruction, 1)}))",
                "VSubrevU32" or "VSubrevI32" =>
                    $"(({RawSource(instruction, 1)}) - ({RawSource(instruction, 0)}))",
                // The SPIR-V translator treats the U24 multiply as a full 32-bit
                // multiply (only the Hi/Mad forms mask); mirror it exactly.
                "VMulLoU32" or "VMulLoI32" or "VMulU32U24" =>
                    $"(({RawSource(instruction, 0)}) * ({RawSource(instruction, 1)}))",
                "VMulHiU32" =>
                    $"mulhi({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMulHiU32U24" =>
                    $"mulhi(({RawSource(instruction, 0)}) & 0xFFFFFFu, ({RawSource(instruction, 1)}) & 0xFFFFFFu)",
                "VMulHiI32" =>
                    AsUInt($"mulhi(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMadU32U24" =>
                    $"(((({RawSource(instruction, 0)}) & 0xFFFFFFu) * (({RawSource(instruction, 1)}) & 0xFFFFFFu)) + ({RawSource(instruction, 2)}))",
                "VMadU32U16" =>
                    $"(((({RawSource(instruction, 0)}) & 0xFFFFu) * (({RawSource(instruction, 1)}) & 0xFFFFu)) + ({RawSource(instruction, 2)}))",
                "VAdd3U32" =>
                    $"(({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}) + ({RawSource(instruction, 2)}))",
                "VAddLshlU32" =>
                    $"((({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)})) << (({RawSource(instruction, 2)}) & 31u))",
                "VLshlAddU32" =>
                    $"((({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u)) + ({RawSource(instruction, 2)}))",
                "VMinU32" => $"min({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMaxU32" => $"max({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMinI32" =>
                    AsUInt($"min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMaxI32" =>
                    AsUInt($"max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMin3U32" =>
                    $"min(min({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)})",
                "VMax3U32" =>
                    $"max(max({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)})",
                "VMin3I32" =>
                    AsUInt($"min(min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)}))"),
                "VMax3I32" =>
                    AsUInt($"max(max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)}))"),
                "VMed3U32" =>
                    $"max(min({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), min(max({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)}))",
                "VMed3I32" =>
                    AsUInt($"max(min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), min(max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)})))"),

                // ---- bitwise ----
                "VAndB32" => $"(({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)}))",
                "VOrB32" => $"(({RawSource(instruction, 0)}) | ({RawSource(instruction, 1)}))",
                "VXorB32" => $"(({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)}))",
                "VXnorB32" => $"~(({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)}))",
                "VNotB32" => $"~({RawSource(instruction, 0)})",
                "VAndOrB32" =>
                    $"((({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)})) | ({RawSource(instruction, 2)}))",
                "VOr3U32" =>
                    $"(({RawSource(instruction, 0)}) | ({RawSource(instruction, 1)}) | ({RawSource(instruction, 2)}))",
                "VLshlOrU32" =>
                    $"((({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u)) | ({RawSource(instruction, 2)}))",
                "VLshlB32" => $"(({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u))",
                "VLshlrevB32" => $"(({RawSource(instruction, 1)}) << (({RawSource(instruction, 0)}) & 31u))",
                "VLshrB32" => $"(({RawSource(instruction, 0)}) >> (({RawSource(instruction, 1)}) & 31u))",
                "VLshrrevB32" => $"(({RawSource(instruction, 1)}) >> (({RawSource(instruction, 0)}) & 31u))",
                "VAshrI32" =>
                    AsUInt($"(as_type<int>({RawSource(instruction, 0)}) >> (({RawSource(instruction, 1)}) & 31u))"),
                "VAshrrevI32" =>
                    AsUInt($"(as_type<int>({RawSource(instruction, 1)}) >> (({RawSource(instruction, 0)}) & 31u))"),
                "VBfeU32" =>
                    $"extract_bits({RawSource(instruction, 0)}, ({RawSource(instruction, 1)}) & 31u, ({RawSource(instruction, 2)}) & 31u)",
                "VBfiB32" =>
                    $"((({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)})) | (~({RawSource(instruction, 0)}) & ({RawSource(instruction, 2)})))",
                "VBfmB32" =>
                    $"(((1u << (({RawSource(instruction, 0)}) & 31u)) - 1u) << (({RawSource(instruction, 1)}) & 31u))",
                "VBfrevB32" => $"reverse_bits({RawSource(instruction, 0)})",
                "VBcntU32B32" => $"(popcount({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}))",
                "VFfblB32" =>
                    $"(({RawSource(instruction, 0)}) == 0u ? 0xFFFFFFFFu : (uint)ctz({RawSource(instruction, 0)}))",

                // ---- wave / lane ----
                // mbcnt reads the mask dword the guest passes (no cross-lane
                // op), so only the per-lane thread-mask math differs by wave
                // size. Wave64 lanes 32..63 count the whole low half in mbcnt_lo
                // and their own partial in mbcnt_hi; a 1u << lane for lane>=32
                // would be undefined, so those are split out.
                "VMbcntLoU32B32" => IsWave64
                    ? $"((sharpemu_lane >= 32u ? popcount({RawSource(instruction, 0)}) : popcount(({RawSource(instruction, 0)}) & ((1u << sharpemu_lane) - 1u))) + ({RawSource(instruction, 1)}))"
                    : $"(popcount(({RawSource(instruction, 0)}) & ((1u << sharpemu_lane) - 1u)) + ({RawSource(instruction, 1)}))",
                "VMbcntHiU32B32" => IsWave64
                    ? $"((sharpemu_lane >= 32u ? popcount(({RawSource(instruction, 0)}) & ((1u << (sharpemu_lane - 32u)) - 1u)) : 0u) + ({RawSource(instruction, 1)}))"
                    // Wave32: the high mask half holds no lanes; pass the addend.
                    : RawSource(instruction, 1),
                "VPermlane16B32" => EmitPermlane16(instruction, exchangeRows: false),
                "VPermlanex16B32" => EmitPermlane16(instruction, exchangeRows: true),

                // ---- cube map helpers ----
                "VCubeidF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Id),
                "VCubescF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Sc),
                "VCubetcF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Tc),
                "VCubemaF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Ma),

                _ => null,
            };

            if (expression is null)
            {
                switch (instruction.Opcode)
                {
                    case "VAddCoU32":
                    {
                        var left = Temp("uint", RawSource(instruction, 0));
                        var right = Temp("uint", RawSource(instruction, 1));
                        var sum = Temp("uint", $"{left} + {right}");
                        StoreCarryOut(instruction, $"{sum} < {left}");
                        expression = sum;
                        break;
                    }
                    case "VSubCoU32":
                    case "VSubrevCoU32":
                    {
                        var reverse = instruction.Opcode == "VSubrevCoU32";
                        var left = Temp("uint", RawSource(instruction, reverse ? 1 : 0));
                        var right = Temp("uint", RawSource(instruction, reverse ? 0 : 1));
                        StoreCarryOut(instruction, $"{left} < {right}");
                        expression = $"({left} - {right})";
                        break;
                    }
                    case "VAddcU32":
                    case "VAddCoCiU32":
                    {
                        var left = Temp("uint", RawSource(instruction, 0));
                        var right = Temp("uint", RawSource(instruction, 1));
                        var carryIn = instruction.Sources.Count > 2
                            ? MaskBitExpression(instruction.Sources[2])
                            : "vcc";
                        var partial = Temp("uint", $"{left} + {right}");
                        var sum = Temp("uint", $"{partial} + (({carryIn}) ? 1u : 0u)");
                        StoreCarryOut(instruction, $"({partial} < {left}) || ({sum} < {partial})");
                        expression = sum;
                        break;
                    }
                    case "VSubbU32":
                    case "VSubbrevU32":
                    {
                        var reverse = instruction.Opcode == "VSubbrevU32";
                        var left = Temp("uint", RawSource(instruction, reverse ? 1 : 0));
                        var right = Temp("uint", RawSource(instruction, reverse ? 0 : 1));
                        var borrowIn = instruction.Sources.Count > 2
                            ? MaskBitExpression(instruction.Sources[2])
                            : "vcc";
                        var borrow = Temp("uint", $"({borrowIn}) ? 1u : 0u");
                        var partial = Temp("uint", $"{left} - {right}");
                        StoreCarryOut(instruction, $"({left} < {right}) || ({partial} < {borrow})");
                        expression = $"({partial} - {borrow})";
                        break;
                    }
                    case "VMadU64U32":
                    {
                        // 64-bit product+addend into a VGPR pair, carry to SDST.
                        var product = Temp(
                            "ulong",
                            $"(ulong)({RawSource(instruction, 0)}) * (ulong)({RawSource(instruction, 1)})");
                        var addend = Temp("ulong", RawSource64(instruction, 2));
                        var wide = Temp("ulong", $"{product} + {addend}");
                        StoreCarryOut(instruction, $"{wide} < {addend}");
                        StoreVector(destination + 1, $"(uint)({wide} >> 32)");
                        expression = $"(uint){wide}";
                        break;
                    }
                    default:
                        error = $"unsupported vector opcode {instruction.Opcode}";
                        return false;
                }
            }

            var result = Temp("uint", expression);
            if (instruction.Control is Gen5DppControl dpp)
            {
                var writeEnabled = EmitDppWriteEnabled(dpp);
                result = Temp("uint", $"({writeEnabled}) ? {result} : v[{destination}]");
            }

            if (instruction.Control is Gen5SdwaControl { ScalarDestination: null } sdwaDestination)
            {
                result = ApplySdwaDestination(sdwaDestination, result, $"v[{destination}]");
            }

            StoreVector(destination, result);
            return true;
        }

        private string EmitCvtPkU8F32(Gen5ShaderInstruction instruction)
        {
            var converted = Temp("uint", $"(uint)({F(instruction, 0)})");
            var offset = Temp("uint", $"(({RawSource(instruction, 1)}) & 3u) << 3");
            var baseValue = Temp("uint", RawSource(instruction, 2));
            return $"(({baseValue} & ~(0xFFu << {offset})) | (({converted} & 0xFFu) << {offset}))";
        }

        private string EmitCvtPkrtzF16F32(Gen5ShaderInstruction instruction)
        {
            // Round-to-zero via mantissa truncation before the half conversion,
            // mirroring the SPIR-V translator's TruncateFloat32ForPack.
            var first = Temp(
                "float",
                $"as_type<float>(as_type<uint>({F(instruction, 0)}) & 0xFFFFE000u)");
            var second = Temp(
                "float",
                $"as_type<float>(as_type<uint>({F(instruction, 1)}) & 0xFFFFE000u)");
            return $"(((uint)as_type<ushort>(half({first}))) | (((uint)as_type<ushort>(half({second}))) << 16))";
        }

        // ---- DPP / SDWA machinery ----

        private static bool IsSupportedDppControl(uint control) =>
            control <= 0xFF ||
            control is >= 0x101 and <= 0x10F or
                >= 0x111 and <= 0x11F or
                >= 0x121 and <= 0x12F or
                0x140 or 0x141 or
                >= 0x150 and <= 0x15F or
                >= 0x160 and <= 0x16F;

        /// <summary>Target lane + in-range flag for a DPP16 control.</summary>
        private (string TargetLane, string InRange) EmitDppSourceLane(Gen5DppControl control)
        {
            var dpp = control.Control;
            if (dpp <= 0xFF)
            {
                // Quad permute: two selector bits per lane-in-quad.
                var selected = Temp(
                    "uint",
                    $"({dpp}u >> ((sharpemu_lane & 3u) * 2u)) & 3u");
                return (Temp("uint", $"(sharpemu_lane & 0xFFFFFFFCu) + {selected}"), "true");
            }

            if (dpp is >= 0x101 and <= 0x10F)
            {
                // row_shl
                var shifted = Temp("uint", $"(sharpemu_lane & 15u) + {dpp & 15}u");
                var inRange = Temp("bool", $"{shifted} < 16u");
                return (Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + ({shifted} & 15u)"), inRange);
            }

            if (dpp is >= 0x111 and <= 0x11F)
            {
                // row_shr
                var inRange = Temp("bool", $"(sharpemu_lane & 15u) >= {dpp & 15}u");
                return (
                    Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + (((sharpemu_lane & 15u) - {dpp & 15}u) & 15u)"),
                    inRange);
            }

            if (dpp is >= 0x121 and <= 0x12F)
            {
                // row_ror
                return (
                    Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + (((sharpemu_lane & 15u) - {dpp & 15}u) & 15u)"),
                    "true");
            }

            var target = dpp switch
            {
                0x140 => "(sharpemu_lane & 0xFFFFFFF0u) + (15u - (sharpemu_lane & 15u))",
                0x141 => "(sharpemu_lane & 0xFFFFFFF8u) + (7u - (sharpemu_lane & 7u))",
                >= 0x150 and <= 0x15F => $"(sharpemu_lane & 0xFFFFFFF0u) + {dpp & 15}u",
                >= 0x160 and <= 0x16F => $"(sharpemu_lane & 0xFFFFFFF0u) + ((sharpemu_lane & 15u) ^ {dpp & 15}u)",
                _ => "sharpemu_lane",
            };
            return (Temp("uint", target), "true");
        }

        // Under the single-lane graphics model every shuffle-select resolves
        // to the lane's own value (the register conceptually holds this
        // thread's value in every lane); compute lanes are real simdgroup
        // threads and shuffle for real. Mirrors the SPIR-V translator's
        // no-subgroup fallback for graphics stages.
        private bool IsSingleLaneStage => _stage != Gen5MslStage.Compute;

        private string ShuffleLane(string value, string targetLane) =>
            IsSingleLaneStage ? value : $"simd_shuffle({value}, (ushort){targetLane})";

        private string LaneActiveExpression(string targetLane) =>
            IsSingleLaneStage ? "exec" : $"simd_shuffle(exec ? 1u : 0u, (ushort){targetLane}) != 0u";

        private string ApplyDppSource(Gen5DppControl control, string value)
        {
            var stored = Temp("uint", value);
            var (targetLane, inRange) = EmitDppSourceLane(control);
            var safeTarget = Temp("uint", $"(({inRange}) ? {targetLane} : sharpemu_lane) & 31u");
            var shuffled = Temp("uint", ShuffleLane(stored, safeTarget));
            if (control.FetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(safeTarget));
            return Temp("uint", $"(({inRange}) && {sourceActive}) ? {shuffled} : 0u");
        }

        private string ApplyDpp8Source(Gen5Dpp8Control control, string value)
        {
            var stored = Temp("uint", value);
            var selector = Temp(
                "uint",
                $"({control.LaneSelectors}u >> ((sharpemu_lane & 7u) * 3u)) & 7u");
            var targetLane = Temp("uint", $"((sharpemu_lane & 0xFFFFFFF8u) + {selector}) & 31u");
            var shuffled = Temp("uint", ShuffleLane(stored, targetLane));
            if (control.FetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(targetLane));
            return Temp("uint", $"{sourceActive} ? {shuffled} : 0u");
        }

        private string EmitDppWriteEnabled(Gen5DppControl control)
        {
            var (_, inRange) = EmitDppSourceLane(control);
            var rowEnabled = $"(({control.RowMask}u >> (sharpemu_lane >> 4)) & 1u) != 0u";
            var bankEnabled = $"(({control.BankMask}u >> (sharpemu_lane & 3u)) & 1u) != 0u";
            var sourceAllows = control.BoundControl ? "true" : inRange;
            return Temp("bool", $"({rowEnabled}) && ({bankEnabled}) && ({sourceAllows})");
        }

        private string ApplySdwaDestination(
            Gen5SdwaControl control,
            string value,
            string previous)
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
            var fieldMask = lowMask << (int)shift;
            var upperStart = shift + width;
            var upperMask = upperStart == 32 ? 0u : uint.MaxValue << (int)upperStart;
            var positioned = Temp("uint", $"(({value}) & 0x{lowMask:X}u) << {shift}");
            return control.DestinationUnused switch
            {
                // 0: unused bits zeroed. 1: sign-extend upward. 2: preserve.
                0 => positioned,
                1 => Temp(
                    "uint",
                    $"{positioned} | ((({positioned} & 0x{1u << (int)(shift + width - 1):X}u) != 0u) ? 0x{upperMask:X}u : 0u)"),
                2 => Temp("uint", $"(({previous}) & 0x{~fieldMask:X}u) | {positioned}"),
                _ => throw new InvalidOperationException("reserved SDWA destination-unused mode"),
            };
        }

        // ---- compares ----

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            string condition;
            if (opcode is "VCmpClassF32" or "VCmpxClassF32")
            {
                condition = EmitCompareClass(instruction);
            }
            else if (opcode is "VCmpTruF32" or "VCmpxTruF32" or "VCmpTI32" or "VCmpTU32")
            {
                condition = "true";
            }
            else if (opcode is "VCmpFF32" or "VCmpxFF32" or "VCmpFI32" or "VCmpFU32")
            {
                condition = "false";
            }
            else if (opcode is "VCmpOF32" or "VCmpxOF32")
            {
                condition = $"(!isnan({F(instruction, 0)}) && !isnan({F(instruction, 1)}))";
            }
            else if (opcode is "VCmpUF32" or "VCmpxUF32")
            {
                condition = $"(isnan({F(instruction, 0)}) || isnan({F(instruction, 1)}))";
            }
            else if (opcode.EndsWith("F32", StringComparison.Ordinal))
            {
                // Ordered compares are the plain C operators (false on NaN);
                // the Nxx forms are their unordered negations (true on NaN).
                var (op, unordered) = TrimCompare(opcode) switch
                {
                    "Lt" => ("<", false),
                    "Eq" => ("==", false),
                    "Le" => ("<=", false),
                    "Gt" => (">", false),
                    "Lg" => ("!=", false),
                    "Ge" => (">=", false),
                    "Neq" => ("==", true),
                    "Nlt" => ("<", true),
                    "Nle" => ("<=", true),
                    "Ngt" => (">", true),
                    "Nge" => (">=", true),
                    "Nlg" => ("!=", true),
                    _ => (string.Empty, false),
                };
                if (op.Length == 0)
                {
                    error = $"unsupported float compare {opcode}";
                    return false;
                }

                var comparison = $"({F(instruction, 0)} {op} {F(instruction, 1)})";
                condition = unordered ? $"(!{comparison})" : comparison;
            }
            else
            {
                var signed = opcode.EndsWith("I32", StringComparison.Ordinal);
                var op = TrimCompare(opcode) switch
                {
                    "Eq" => "==",
                    "Ne" => "!=",
                    "Lt" => "<",
                    "Le" => "<=",
                    "Gt" => ">",
                    "Ge" => ">=",
                    _ => string.Empty,
                };
                if (op.Length == 0)
                {
                    error = $"unsupported integer compare {opcode}";
                    return false;
                }

                condition = signed
                    ? $"(as_type<int>({RawSource(instruction, 0)}) {op} as_type<int>({RawSource(instruction, 1)}))"
                    : $"(({RawSource(instruction, 0)}) {op} ({RawSource(instruction, 1)}))";
            }

            // Only EXEC-enabled lanes can pass; balloting the raw condition
            // would leak results from disabled lanes into saveexec/branches.
            var active = Temp("bool", $"exec && {condition}");
            if (instruction.Control is Gen5DppControl compareDpp)
            {
                var writeEnabled = EmitDppWriteEnabled(compareDpp);
                active = Temp("bool", $"({writeEnabled}) ? {active} : vcc");
            }

            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                // GFX10 VCMPX writes EXEC only.
                Line($"exec = {active};");
                EmitBallotStore(ExecLoRegister, "exec");
            }
            else
            {
                var target = instruction.Control is Gen5SdwaControl
                    { ScalarDestination: { } scalarDestination }
                    ? scalarDestination
                    : VccLoRegister;
                StoreMaskBit(target, active);
            }

            return true;
        }

        private string EmitCompareClass(Gen5ShaderInstruction instruction)
        {
            var source = Temp("float", F(instruction, 0));
            var raw = Temp("uint", RawSource(instruction, 0));
            var mask = Temp("uint", RawSource(instruction, 1));
            var negative = Temp("bool", $"({raw} & 0x80000000u) != 0u");
            var nan = Temp("bool", $"isnan({source})");
            var infinite = Temp("bool", $"isinf({source})");
            var zero = Temp("bool", $"{source} == 0.0f");
            var subnormal = Temp(
                "bool",
                $"fabs({source}) > 0.0f && fabs({source}) < as_type<float>(0x00800000u)");
            var normal = Temp(
                "bool",
                $"!({nan} || {infinite} || {zero} || {subnormal})");
            // Class bits: 0 sNaN, 1 qNaN, 2 -inf, 3 -normal, 4 -subnormal,
            // 5 -zero, 6 +zero, 7 +subnormal, 8 +normal, 9 +inf.
            return Temp(
                "bool",
                $"((({mask} & 3u) != 0u) && {nan}) || " +
                $"((({mask} >> 2) & 1u) != 0u && {infinite} && {negative}) || " +
                $"((({mask} >> 3) & 1u) != 0u && {normal} && {negative}) || " +
                $"((({mask} >> 4) & 1u) != 0u && {subnormal} && {negative}) || " +
                $"((({mask} >> 5) & 1u) != 0u && {zero} && {negative}) || " +
                $"((({mask} >> 6) & 1u) != 0u && {zero} && !{negative}) || " +
                $"((({mask} >> 7) & 1u) != 0u && {subnormal} && !{negative}) || " +
                $"((({mask} >> 8) & 1u) != 0u && {normal} && !{negative}) || " +
                $"((({mask} >> 9) & 1u) != 0u && {infinite} && !{negative})");
        }

        private static string TrimCompare(string opcode)
        {
            var trimmed = opcode.StartsWith("VCmpx", StringComparison.Ordinal)
                ? opcode["VCmpx".Length..]
                : opcode["VCmp".Length..];
            return trimmed[..^3];
        }

        private void StoreCarryOut(Gen5ShaderInstruction instruction, string carryCondition)
        {
            var active = Temp("bool", $"exec && ({carryCondition})");
            var target = instruction.Control is Gen5Vop3Control { ScalarDestination: { } register }
                ? register
                : VccLoRegister;
            StoreMaskBit(target, active);
        }

        /// <summary>
        /// Writes this lane's bit of a wave mask: VCC/EXEC update the per-lane
        /// bool and mirror the ballot into their architectural SGPRs; a plain
        /// SGPR receives the ballot of the per-lane condition.
        /// </summary>
        private void StoreMaskBit(uint register, string condition)
        {
            switch (register)
            {
                case VccLoRegister:
                    Line($"vcc = {condition};");
                    EmitBallotStore(VccLoRegister, "vcc");
                    return;
                case ExecLoRegister:
                    Line($"exec = {condition};");
                    EmitBallotStore(ExecLoRegister, "exec");
                    return;
                default:
                    if (register < ScalarRegisterFileCount)
                    {
                        EmitBallotStore(register, condition);
                    }

                    return;
            }
        }

        /// <summary>Broadcasts <paramref name="value"/> from the first guest-active
        /// lane (lowest set bit of the 64-lane EXEC mask) to all lanes, through the
        /// threadgroup broadcast slot — mirroring the SPIR-V translator's
        /// BroadcastFirstWave64Active. Returns the temp holding the result.</summary>
        private string EmitWave64ReadFirstLane(string value)
        {
            Line("if (sharpemu_lane == 0u) { sharpemu_wave_scratch[2] = 0u; }");
            // 64-lane EXEC mask across both halves (slots 0/1), broadcast in 2.
            Line("sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot(exec);");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            var lo = Temp("uint", "sharpemu_wave_scratch[0]");
            var hi = Temp("uint", "sharpemu_wave_scratch[1]");
            var first = Temp(
                "uint",
                $"({lo} != 0u) ? (uint)ctz({lo}) : (({hi} != 0u) ? (32u + (uint)ctz({hi})) : 0u)");
            var anyActive = Temp("bool", $"(({lo}) | ({hi})) != 0u");
            Line($"if ({anyActive} && sharpemu_lane == {first}) {{ sharpemu_wave_scratch[2] = {value}; }}");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            var result = Temp("uint", "sharpemu_wave_scratch[2]");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            return result;
        }

        /// <summary>Stores the wave ballot of <paramref name="condition"/> into the
        /// mask register pair (low, low+1). Wave32 fills the low dword and clears
        /// the high; wave64 bridges both 32-wide halves through threadgroup
        /// scratch so the pair holds the full 64-lane mask. The bridging barriers
        /// are safe because the guest program's scalar PC keeps all 64 lanes in
        /// lockstep through the dispatcher (one wave per threadgroup).</summary>
        private void EmitBallotStore(uint loRegister, string condition)
        {
            var hiRegister = loRegister + 1;
            if (!IsWave64)
            {
                Line($"s[{loRegister}] = sharpemu_ballot({condition});");
                if (hiRegister < ScalarRegisterFileCount)
                {
                    Line($"s[{hiRegister}] = 0u;");
                }

                return;
            }

            // simd_ballot is uniform across a simdgroup, so every lane of a half
            // writes the same 32-bit value to that half's slot — no first-lane
            // guard needed. Barrier, read both halves, barrier before the slot
            // can be reused by the next ballot.
            Line($"sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot({condition});");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            Line($"s[{loRegister}] = sharpemu_wave_scratch[0];");
            if (hiRegister < ScalarRegisterFileCount)
            {
                Line($"s[{hiRegister}] = sharpemu_wave_scratch[1];");
            }

            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
        }

        // ---- permlane / cube ----

        private string EmitPermlane16(Gen5ShaderInstruction instruction, bool exchangeRows)
        {
            if (instruction.Control is not Gen5Vop3Control control ||
                (control.OperandSelect & ~3u) != 0 ||
                control.AbsoluteMask != 0 ||
                control.NegateMask != 0 ||
                control.OutputModifier != 0 ||
                control.Clamp)
            {
                throw new NotSupportedException(
                    $"invalid permlane modifiers for {instruction.Opcode}");
            }

            var value = Temp("uint", RawSource(instruction, 0));
            var selectorLow = Temp("uint", RawSource(instruction, 1));
            var selectorHigh = Temp("uint", RawSource(instruction, 2));
            var localLane = Temp("uint", "sharpemu_lane & 15u");
            var selector = Temp(
                "uint",
                $"({localLane} < 8u ? ({selectorLow} >> ({localLane} << 2)) : ({selectorHigh} >> (({localLane} - 8u) << 2))) & 15u");
            var rowBase = exchangeRows
                ? "((sharpemu_lane & 0xFFFFFFF0u) ^ 16u)"
                : "(sharpemu_lane & 0xFFFFFFF0u)";
            var targetLane = Temp("uint", $"({rowBase} + {selector}) & 31u");
            var shuffled = Temp("uint", ShuffleLane(value, targetLane));
            var fetchInactive = (control.OperandSelect & 1) != 0;
            if (fetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(targetLane));
            return Temp("uint", $"{sourceActive} ? {shuffled} : 0u");
        }

        private enum CubeCoordinate
        {
            Id,
            Sc,
            Tc,
            Ma,
        }

        private string EmitCubeCoordinate(
            Gen5ShaderInstruction instruction,
            CubeCoordinate coordinate)
        {
            var x = Temp("float", F(instruction, 0));
            var y = Temp("float", F(instruction, 1));
            var z = Temp("float", F(instruction, 2));
            var amaxXY = Temp("float", $"fmax(fabs({x}), fabs({y}))");
            var amax = Temp("float", $"fmax(fabs({z}), {amaxXY})");
            if (coordinate == CubeCoordinate.Ma)
            {
                return FloatResult(instruction, $"2.0f * {amax}");
            }

            var isZMax = Temp("bool", $"fabs({z}) >= {amaxXY}");
            var yGeX = Temp("bool", $"fabs({y}) >= fabs({x})");
            var isYMax = Temp("bool", $"!{isZMax} && {yGeX}");
            switch (coordinate)
            {
                case CubeCoordinate.Id:
                {
                    var zCase = $"({z} < 0.0f ? 5.0f : 4.0f)";
                    var yCase = $"({y} < 0.0f ? 3.0f : 2.0f)";
                    var xCase = $"({x} < 0.0f ? 1.0f : 0.0f)";
                    return FloatResult(
                        instruction,
                        $"({isZMax} ? {zCase} : ({yGeX} ? {yCase} : {xCase}))");
                }
                case CubeCoordinate.Sc:
                {
                    var zCase = $"({z} < 0.0f ? (-{x}) : {x})";
                    var xCase = $"({x} < 0.0f ? {z} : (-{z}))";
                    return FloatResult(
                        instruction,
                        $"({isZMax} ? {zCase} : ({isYMax} ? {x} : {xCase}))");
                }
                default:
                {
                    var yCase = $"({y} < 0.0f ? (-{z}) : {z})";
                    return FloatResult(
                        instruction,
                        $"({isYMax} ? {yCase} : (-{y}))");
                }
            }
        }

        // ---- scalar ALU ----

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

                var value = instruction.Opcode switch
                {
                    "SMovkI32" => FormatUInt(immediate),
                    "SAddkI32" => $"({ScalarExpression(destination)} + {FormatUInt(immediate)})",
                    "SMulkI32" => $"({ScalarExpression(destination)} * {FormatUInt(immediate)})",
                    _ => string.Empty,
                };
                if (value.Length == 0)
                {
                    error = $"unsupported scalar immediate {instruction.Opcode}";
                    return false;
                }

                StoreScalar(destination, Temp("uint", value));
                return true;
            }

            if (instruction.Opcode == "SGetpcB64")
            {
                var pc = _state.Program.Address +
                    instruction.Pc +
                    (ulong)(instruction.Words.Count * sizeof(uint));
                StoreScalar(destination, FormatUInt((uint)pc));
                StoreScalar(destination + 1, FormatUInt((uint)(pc >> 32)));
                return true;
            }

            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
                instruction.Opcode is "SBfeU64" or "SBfeI64")
            {
                return TryEmitScalar64(instruction, destination, out error);
            }

            var left = Temp("uint", RawSource(instruction, 0));
            if (instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                var oldExec = Temp("uint", $"s[{ExecLoRegister}]");
                var operation = instruction.Opcode[1..instruction.Opcode.IndexOf(
                    "Saveexec",
                    StringComparison.Ordinal)];
                var combined = operation switch
                {
                    "And" => $"({left} & {oldExec})",
                    "Or" => $"({left} | {oldExec})",
                    "Xor" => $"({left} ^ {oldExec})",
                    "Nand" => $"~({left} & {oldExec})",
                    "Nor" => $"~({left} | {oldExec})",
                    "Xnor" => $"~({left} ^ {oldExec})",
                    "Andn1" => $"(~{left} & {oldExec})",
                    "Andn2" => $"({left} & ~{oldExec})",
                    "Orn1" => $"(~{left} | {oldExec})",
                    "Orn2" => $"({left} | ~{oldExec})",
                    _ => string.Empty,
                };
                if (combined.Length == 0)
                {
                    error = $"unsupported scalar 32-bit saveexec opcode {instruction.Opcode}";
                    return false;
                }

                var mask = Temp("uint", combined);
                StoreScalar(destination, oldExec);
                Line($"s[{ExecLoRegister}] = {mask};");
                Line($"s[{ExecHiRegister}] = 0u;");
                Line($"exec = (({mask} >> sharpemu_lane) & 1u) != 0u;");
                Line($"scc = {mask} != 0u;");
                return true;
            }

            switch (instruction.Opcode)
            {
                case "SMovB32":
                    StoreScalar(destination, left);
                    return true;
                case "SNotB32":
                {
                    var result = Temp("uint", $"~{left}");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBrevB32":
                {
                    var result = Temp("uint", $"reverse_bits({left})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBcnt1I32B32":
                {
                    var result = Temp("uint", $"popcount({left})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SFF1I32B32":
                {
                    var result = Temp(
                        "uint",
                        $"{left} == 0u ? 0xFFFFFFFFu : (uint)ctz({left})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBitset1B32":
                    StoreScalar(
                        destination,
                        $"{ScalarExpression(destination)} | (1u << ({left} & 31u))");
                    return true;
            }

            if (instruction.Sources.Count < 2)
            {
                error = $"missing scalar source for {instruction.Opcode}";
                return false;
            }

            var right = Temp("uint", RawSource(instruction, 1));
            string resultExpression;
            string sccStatement;
            switch (instruction.Opcode)
            {
                case "SAddU32":
                    resultExpression = $"({left} + {right})";
                    sccStatement = "RESULT < " + left;
                    break;
                case "SSubU32":
                    resultExpression = $"({left} - {right})";
                    sccStatement = $"{right} > {left}";
                    break;
                case "SAddI32":
                    resultExpression = $"({left} + {right})";
                    sccStatement = $"((~({left} ^ {right}) & ({left} ^ RESULT)) >> 31) != 0u";
                    break;
                case "SSubI32":
                    resultExpression = $"({left} - {right})";
                    sccStatement = $"(((({left} ^ {right})) & ({left} ^ RESULT)) >> 31) != 0u";
                    break;
                case "SAddcU32":
                {
                    var partial = Temp("uint", $"{left} + {right}");
                    var sum = Temp("uint", $"{partial} + (scc ? 1u : 0u)");
                    Line($"scc = ({partial} < {left}) || ({sum} < {partial});");
                    StoreScalar(destination, sum);
                    return true;
                }
                case "SSubbU32":
                {
                    var borrow = Temp("uint", "scc ? 1u : 0u");
                    var partial = Temp("uint", $"{left} - {right}");
                    var difference = Temp("uint", $"{partial} - {borrow}");
                    Line($"scc = ({right} > {left}) || (({borrow} == 1u) && ({right} == {left}));");
                    StoreScalar(destination, difference);
                    return true;
                }
                case "SMulI32":
                    resultExpression = $"({left} * {right})";
                    sccStatement = string.Empty;
                    break;
                case "SMulHiU32":
                    resultExpression = $"mulhi({left}, {right})";
                    sccStatement = string.Empty;
                    break;
                case "SAndB32":
                    resultExpression = $"({left} & {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SOrB32":
                    resultExpression = $"({left} | {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SXorB32":
                    resultExpression = $"({left} ^ {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SNandB32":
                    resultExpression = $"~({left} & {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SNorB32":
                    resultExpression = $"~({left} | {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SXnorB32":
                    resultExpression = $"~({left} ^ {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SAndn2B32":
                    resultExpression = $"({left} & ~{right})";
                    sccStatement = "NONZERO";
                    break;
                case "SOrn2B32":
                    resultExpression = $"({left} | ~{right})";
                    sccStatement = "NONZERO";
                    break;
                case "SLshlB32":
                    resultExpression = $"({left} << ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SLshrB32":
                    resultExpression = $"({left} >> ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SAshrI32":
                    resultExpression = $"(uint)(as_type<int>({left}) >> ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SBfmB32":
                    resultExpression = $"(((1u << ({left} & 31u)) - 1u) << ({right} & 31u))";
                    sccStatement = string.Empty;
                    break;
                case "SBfeU32":
                case "SBfeI32":
                {
                    // Width clamps to the bits remaining above the offset.
                    var offset = Temp("uint", $"{right} & 31u");
                    var width = Temp(
                        "uint",
                        $"min(({right} >> 16) & 0x7Fu, 32u - {offset})");
                    var result = instruction.Opcode == "SBfeI32"
                        ? Temp(
                            "uint",
                            $"{width} == 0u ? 0u : (uint)extract_bits(as_type<int>({left}), {offset}, {width})")
                        : Temp(
                            "uint",
                            $"{width} == 0u ? 0u : extract_bits({left}, {offset}, {width})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SCselectB32":
                    resultExpression = $"(scc ? {left} : {right})";
                    sccStatement = string.Empty;
                    break;
                case "SMinU32":
                    resultExpression = $"min({left}, {right})";
                    sccStatement = $"{left} < {right}";
                    break;
                case "SMaxU32":
                    resultExpression = $"max({left}, {right})";
                    sccStatement = $"{left} > {right}";
                    break;
                case "SMinI32":
                    resultExpression = $"(uint)min(as_type<int>({left}), as_type<int>({right}))";
                    sccStatement = $"as_type<int>({left}) < as_type<int>({right})";
                    break;
                case "SMaxI32":
                    resultExpression = $"(uint)max(as_type<int>({left}), as_type<int>({right}))";
                    sccStatement = $"as_type<int>({left}) > as_type<int>({right})";
                    break;
                case "SLshl1AddU32":
                case "SLshl2AddU32":
                case "SLshl3AddU32":
                case "SLshl4AddU32":
                {
                    var shift = (uint)(instruction.Opcode[5] - '0');
                    resultExpression = $"(({left} << {shift}) + {right})";
                    sccStatement = string.Empty;
                    break;
                }
                case "SPackLlB32B16":
                    resultExpression = $"(({left} & 0xFFFFu) | ({right} << 16))";
                    sccStatement = string.Empty;
                    break;
                case "SPackLhB32B16":
                    resultExpression = $"(({left} & 0xFFFFu) | ({right} & 0xFFFF0000u))";
                    sccStatement = string.Empty;
                    break;
                case "SPackHhB32B16":
                    resultExpression = $"(({left} >> 16) | ({right} & 0xFFFF0000u))";
                    sccStatement = string.Empty;
                    break;
                default:
                    error = $"unsupported scalar opcode {instruction.Opcode}";
                    return false;
            }

            var value2 = Temp("uint", resultExpression);
            StoreScalar(destination, value2);
            if (sccStatement == "NONZERO")
            {
                Line($"scc = {value2} != 0u;");
            }
            else if (sccStatement.Length != 0)
            {
                Line($"scc = {sccStatement.Replace("RESULT", value2)};");
            }

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

            var left = Temp("uint", RawSource(instruction, 0));
            var right = Temp("uint", RawSource(instruction, 1));
            if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var isSet = $"(({left} >> ({right} & 31u)) & 1u) != 0u";
                Line(instruction.Opcode == "SBitcmp1B32"
                    ? $"scc = {isSet};"
                    : $"scc = !({isSet});");
                return true;
            }

            return TryEmitScalarCompareCore(instruction.Opcode, "SCmp", left, right, out error);
        }

        private bool TryEmitScalarCompareK(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint immediate,
            out string error) =>
            TryEmitScalarCompareCore(
                instruction.Opcode,
                "SCmpk",
                ScalarExpression(destination),
                FormatUInt(immediate),
                out error);

        private bool TryEmitScalarCompareCore(
            string opcode,
            string prefix,
            string left,
            string right,
            out string error)
        {
            error = string.Empty;
            var suffix = opcode[prefix.Length..];
            var signed = suffix.EndsWith("I32", StringComparison.Ordinal);
            var op = suffix[..^3] switch
            {
                "Eq" => "==",
                "Lg" => "!=",
                "Gt" => ">",
                "Ge" => ">=",
                "Lt" => "<",
                "Le" => "<=",
                _ => string.Empty,
            };
            if (op.Length == 0)
            {
                error = $"unsupported scalar compare {opcode}";
                return false;
            }

            Line(signed
                ? $"scc = as_type<int>({left}) {op} as_type<int>({right});"
                : $"scc = ({left}) {op} ({right});");
            return true;
        }

        // ---- 64-bit scalar ops over register pairs ----

        private bool TryEmitScalar64(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var left = Temp("ulong", RawSource64(instruction, 0));
            if (instruction.Opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                var oldExec = Temp("ulong", Scalar64Expression(ExecLoRegister));
                var operation = instruction.Opcode[1..instruction.Opcode.IndexOf(
                    "Saveexec",
                    StringComparison.Ordinal)];
                var combined = operation switch
                {
                    "And" => $"({left} & {oldExec})",
                    "Or" => $"({left} | {oldExec})",
                    "Xor" => $"({left} ^ {oldExec})",
                    "Nand" => $"~({left} & {oldExec})",
                    "Nor" => $"~({left} | {oldExec})",
                    "Xnor" => $"~({left} ^ {oldExec})",
                    "Andn1" => $"(~{left} & {oldExec})",
                    "Andn2" => $"({left} & ~{oldExec})",
                    "Orn1" => $"(~{left} | {oldExec})",
                    "Orn2" => $"({left} | ~{oldExec})",
                    _ => string.Empty,
                };
                if (combined.Length == 0)
                {
                    error = $"unsupported scalar 64-bit saveexec opcode {instruction.Opcode}";
                    return false;
                }

                var mask = Temp("ulong", combined);
                StoreScalar64(destination, oldExec);
                Line($"s[{ExecLoRegister}] = (uint){mask};");
                Line($"s[{ExecHiRegister}] = (uint)({mask} >> 32);");
                Line($"exec = ((((uint){mask}) >> sharpemu_lane) & 1u) != 0u;");
                Line($"scc = {mask} != 0ul;");
                return true;
            }

            string value;
            var setsScc = true;
            switch (instruction.Opcode)
            {
                case "SMovB64":
                    value = left;
                    setsScc = false;
                    break;
                case "SNotB64":
                    value = $"~{left}";
                    break;
                case "SWqmB64":
                {
                    // Whole-quad mode: each 4-lane group becomes all-ones if any
                    // of its bits is set.
                    var quadAny = Temp(
                        "ulong",
                        $"({left} | ({left} >> 1) | ({left} >> 2) | ({left} >> 3)) & 0x1111111111111111ul");
                    value = $"({quadAny} * 0xFul)";
                    break;
                }
                case "SLshlB64" or "SLshrB64":
                {
                    var shift = Temp("uint", $"({RawSource(instruction, 1)}) & 63u");
                    value = instruction.Opcode == "SLshlB64"
                        ? $"({left} << {shift})"
                        : $"({left} >> {shift})";
                    break;
                }
                case "SBfmB64":
                {
                    var width = Temp("ulong", $"(ulong)(({RawSource(instruction, 0)}) & 63u)");
                    var offset = Temp("ulong", $"(ulong)(({RawSource(instruction, 1)}) & 63u)");
                    value = $"((((1ul << {width}) - 1ul)) << {offset})";
                    break;
                }
                case "SBfeU64" or "SBfeI64":
                {
                    var control = Temp("uint", RawSource(instruction, 1));
                    var offset = Temp("uint", $"{control} & 63u");
                    var width = Temp("uint", $"min(({control} >> 16) & 0x7Fu, 64u - {offset})");
                    var mask = Temp(
                        "ulong",
                        $"{width} >= 64u ? 0xFFFFFFFFFFFFFFFFul : ((1ul << {width}) - 1ul)");
                    var extracted = Temp("ulong", $"({left} >> {offset}) & {mask}");
                    if (instruction.Opcode == "SBfeI64")
                    {
                        var signBit = Temp(
                            "ulong",
                            $"{width} == 0u ? 0ul : (1ul << ({width} - 1u))");
                        extracted = Temp(
                            "ulong",
                            $"{width} == 0u ? 0ul : (({extracted} ^ {signBit}) - {signBit})");
                    }

                    value = extracted;
                    break;
                }
                default:
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing scalar 64-bit source";
                        return false;
                    }

                    var right = Temp("ulong", RawSource64(instruction, 1));
                    value = instruction.Opcode switch
                    {
                        "SAndB64" => $"({left} & {right})",
                        "SOrB64" => $"({left} | {right})",
                        "SXorB64" => $"({left} ^ {right})",
                        "SNandB64" => $"~({left} & {right})",
                        "SNorB64" => $"~({left} | {right})",
                        "SXnorB64" => $"~({left} ^ {right})",
                        "SAndn1B64" => $"(~{left} & {right})",
                        "SAndn2B64" => $"({left} & ~{right})",
                        "SOrn1B64" => $"(~{left} | {right})",
                        "SOrn2B64" => $"({left} | ~{right})",
                        "SCselectB64" => $"(scc ? {left} : {right})",
                        _ => string.Empty,
                    };
                    if (value.Length == 0)
                    {
                        error = $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                        return false;
                    }

                    setsScc = instruction.Opcode != "SCselectB64";
                    break;
                }
            }

            var stored = Temp("ulong", value);
            StoreScalar64(destination, stored);
            if (setsScc)
            {
                Line($"scc = {stored} != 0ul;");
            }

            return true;
        }

        // ---- operand helpers ----

        private uint DestinationVector(Gen5ShaderInstruction instruction)
        {
            var destination = instruction.Destinations[0];
            return destination.Kind == Gen5OperandKind.VectorRegister
                ? destination.Value
                : throw new NotSupportedException(
                    $"vector destination expected in {instruction.Opcode}");
        }

        /// <summary>
        /// Raw 32-bit source with DPP/DPP8 lane remapping on src0 and SDWA
        /// byte/word selection + integer modifiers, mirroring GetRawSource.
        /// </summary>
        private string RawSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool applySdwaIntegerModifiers = true)
        {
            var value = SourceExpression(instruction.Sources[sourceIndex], instruction);
            if (sourceIndex == 0 && instruction.Control is Gen5DppControl dpp)
            {
                value = ApplyDppSource(dpp, value);
            }
            else if (sourceIndex == 0 && instruction.Control is Gen5Dpp8Control dpp8)
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
                    0 => $"(({value}) & 0xFFu)",
                    1 => $"((({value}) >> 8) & 0xFFu)",
                    2 => $"((({value}) >> 16) & 0xFFu)",
                    3 => $"((({value}) >> 24) & 0xFFu)",
                    4 => $"(({value}) & 0xFFFFu)",
                    5 => $"((({value}) >> 16) & 0xFFFFu)",
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
                    value = $"(uint)extract_bits(as_type<int>({value}), 0u, {width}u)";
                }

                if (applySdwaIntegerModifiers)
                {
                    if ((sdwa.AbsoluteMask & (1u << sourceIndex)) != 0)
                    {
                        value = $"(uint)abs(as_type<int>({value}))";
                    }

                    if ((sdwa.NegateMask & (1u << sourceIndex)) != 0)
                    {
                        value = $"(0u - ({value}))";
                    }
                }
            }

            return value;
        }

        /// <summary>64-bit source: SGPR/VGPR pair, sign-extended inline, or zero-extended 32-bit.</summary>
        private string RawSource64(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            switch (operand.Kind)
            {
                case Gen5OperandKind.ScalarRegister:
                    return Scalar64Expression(operand.Value);
                case Gen5OperandKind.VectorRegister:
                    return $"((ulong)v[{operand.Value}] | ((ulong)v[{operand.Value + 1}] << 32))";
                case Gen5OperandKind.EncodedConstant when operand.Value is >= 193 and <= 208:
                {
                    // Inline negatives sign-extend: -1 denotes a full 64-bit mask.
                    var signed = -(long)(operand.Value - 192);
                    return $"0x{unchecked((ulong)signed):X}ul";
                }
                default:
                    return $"(ulong)({RawSource(instruction, sourceIndex)})";
            }
        }

        private string Scalar64Expression(uint register) => register switch
        {
            // VCC/EXEC read their architectural SGPR pairs like any other
            // register — programs park plain data there (see StoreScalar).
            _ when register + 1 < ScalarRegisterFileCount =>
                $"((ulong)s[{register}] | ((ulong)s[{register + 1}] << 32))",
            _ => "0ul",
        };

        private void StoreScalar64(uint register, string ulongValue)
        {
            StoreScalar(register, $"(uint)({ulongValue})");
            StoreScalar(register + 1, $"(uint)(({ulongValue}) >> 32)");
        }

        /// <summary>Float view of a source with abs/neg modifiers from VOP3/SDWA/DPP.</summary>
        private string F(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var expression = AsFloat(
                RawSource(instruction, sourceIndex, applySdwaIntegerModifiers: false));
            var (absoluteMask, negateMask) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.AbsoluteMask, control.NegateMask),
                Gen5SdwaControl control => (control.AbsoluteMask, control.NegateMask),
                Gen5DppControl control => (control.AbsoluteMask, control.NegateMask),
                _ => (0u, 0u),
            };
            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                expression = $"fabs({expression})";
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                expression = $"(-{expression})";
            }

            return expression;
        }

        /// <summary>
        /// Wraps a float expression with VOP3/SDWA output modifiers and clamp,
        /// then bitcasts back to the register file's uint domain.
        /// </summary>
        private string FloatResult(Gen5ShaderInstruction instruction, string expression)
        {
            var (outputModifier, clamp) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.OutputModifier, control.Clamp),
                Gen5SdwaControl control => (control.OutputModifier, control.Clamp),
                _ => (0u, false),
            };
            expression = outputModifier switch
            {
                1 => $"(({expression}) * 2.0f)",
                2 => $"(({expression}) * 4.0f)",
                3 => $"(({expression}) * 0.5f)",
                _ => expression,
            };
            if (clamp)
            {
                expression = $"clamp({expression}, 0.0f, 1.0f)";
            }

            return AsUInt($"({expression})");
        }

        /// <summary>The lane's bit of a mask operand (VCC/EXEC/SGPR mask).</summary>
        private string MaskBitExpression(Gen5Operand operand) => operand switch
        {
            { Kind: Gen5OperandKind.ScalarRegister, Value: VccLoRegister } => "vcc",
            { Kind: Gen5OperandKind.ScalarRegister, Value: ExecLoRegister } => "exec",
            { Kind: Gen5OperandKind.ScalarRegister } scalar =>
                $"(((s[{scalar.Value}] >> sharpemu_lane) & 1u) != 0u)",
            _ => throw new NotSupportedException("mask operand must be a scalar register"),
        };
    }
}
