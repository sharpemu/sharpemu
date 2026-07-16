// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        // ---- vector ALU ----

        private bool TryEmitVectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Control is Gen5SdwaControl or Gen5DppControl or Gen5Dpp8Control)
            {
                // SDWA byte/word selects and DPP lane shuffles arrive with the
                // full ALU parity phase; failing loudly keeps gaps visible.
                error = $"SDWA/DPP modifiers on {instruction.Opcode} are not translated yet";
                return false;
            }

            if (instruction.Opcode == "VNop")
            {
                return true;
            }

            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
            {
                return TryEmitVectorCompare(instruction, out error);
            }

            switch (instruction.Opcode)
            {
                case "VReadfirstlaneB32":
                {
                    // The value of the first EXEC-active lane lands in an SGPR.
                    // simd_min over active-lane indices finds that lane; shuffle
                    // reads its register. All lanes converge on the same value.
                    if (instruction.Destinations.Count == 0 ||
                        instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "invalid read-first-lane operands";
                        return false;
                    }

                    var value = SourceExpression(instruction.Sources[0], instruction);
                    var firstLane = Temp(
                        "uint",
                        "simd_min(exec ? sharpemu_lane : 0xFFFFFFFFu)");
                    var broadcast = Temp(
                        "uint",
                        $"simd_shuffle({value}, {firstLane} == 0xFFFFFFFFu ? 0u : {firstLane})");
                    StoreScalar(instruction.Destinations[0].Value, broadcast);
                    return true;
                }
                case "VMbcntLoU32B32":
                {
                    // Count set mask bits below this lane, plus src1.
                    var mask = SourceExpression(instruction.Sources[0], instruction);
                    var addend = SourceExpression(instruction.Sources[1], instruction);
                    StoreVector(
                        DestinationVector(instruction),
                        $"popcount({mask} & ((1u << sharpemu_lane) - 1u)) + {addend}");
                    return true;
                }
                case "VMbcntHiU32B32":
                {
                    // Wave32: the high mask half holds no lanes; pass through.
                    var addend = SourceExpression(instruction.Sources[1], instruction);
                    StoreVector(DestinationVector(instruction), addend);
                    return true;
                }
                case "VCndmaskB32":
                {
                    // dst = mask-bit(lane) ? src1 : src0. The mask is VCC for
                    // VOP2 and an explicit SGPR source for VOP3.
                    var source0 = FloatModifiedSource(instruction, 0);
                    var source1 = FloatModifiedSource(instruction, 1);
                    var mask = instruction.Sources.Count > 2
                        ? MaskBitExpression(instruction.Sources[2])
                        : "vcc";
                    StoreVector(
                        DestinationVector(instruction),
                        $"({mask}) ? {AsUInt(source1)} : {AsUInt(source0)}");
                    return true;
                }
                case "VMovB32":
                    StoreVector(
                        DestinationVector(instruction),
                        SourceExpression(instruction.Sources[0], instruction));
                    return true;
            }

            return TryEmitVectorArithmetic(instruction, out error);
        }

        private bool TryEmitVectorArithmetic(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            string? result = instruction.Opcode switch
            {
                // ---- float ----
                "VAddF32" => FloatResult(instruction, $"{F(instruction, 0)} + {F(instruction, 1)}"),
                "VSubF32" => FloatResult(instruction, $"{F(instruction, 0)} - {F(instruction, 1)}"),
                "VSubrevF32" => FloatResult(instruction, $"{F(instruction, 1)} - {F(instruction, 0)}"),
                "VMulF32" => FloatResult(instruction, $"{F(instruction, 0)} * {F(instruction, 1)}"),
                "VMinF32" => FloatResult(instruction, $"fmin({F(instruction, 0)}, {F(instruction, 1)})"),
                "VMaxF32" => FloatResult(instruction, $"fmax({F(instruction, 0)}, {F(instruction, 1)})"),
                "VFmaF32" or "VMadF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, {F(instruction, 2)})"),
                "VFmacF32" or "VMacF32" =>
                    FloatResult(
                        instruction,
                        $"fma({F(instruction, 0)}, {F(instruction, 1)}, as_type<float>(v[{DestinationVector(instruction)}]))"),
                "VMadAkF32" or "VFmaAkF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, {F(instruction, 2)})"),
                "VMadMkF32" or "VFmaMkF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 2)}, {F(instruction, 1)})"),
                "VFloorF32" => FloatResult(instruction, $"floor({F(instruction, 0)})"),
                "VCeilF32" => FloatResult(instruction, $"ceil({F(instruction, 0)})"),
                "VTruncF32" => FloatResult(instruction, $"trunc({F(instruction, 0)})"),
                "VRndneF32" => FloatResult(instruction, $"rint({F(instruction, 0)})"),
                "VFractF32" => FloatResult(instruction, $"({F(instruction, 0)} - floor({F(instruction, 0)}))"),
                "VSqrtF32" => FloatResult(instruction, $"sqrt({F(instruction, 0)})"),
                "VRsqF32" => FloatResult(instruction, $"rsqrt({F(instruction, 0)})"),
                "VRcpF32" or "VRcpIflagF32" => FloatResult(instruction, $"(1.0f / {F(instruction, 0)})"),
                "VLogF32" => FloatResult(instruction, $"log2({F(instruction, 0)})"),
                "VExpF32" => FloatResult(instruction, $"exp2({F(instruction, 0)})"),
                // GCN sin/cos take revolutions (radians pre-multiplied by
                // 1/2pi), mirroring the SPIR-V translator's Tau scale.
                "VSinF32" => FloatResult(instruction, $"sin({F(instruction, 0)} * {TauLiteral})"),
                "VCosF32" => FloatResult(instruction, $"cos({F(instruction, 0)} * {TauLiteral})"),
                "VLdexpF32" =>
                    FloatResult(instruction, $"ldexp({F(instruction, 0)}, as_type<int>({Raw(instruction, 1)}))"),
                "VMin3F32" =>
                    FloatResult(instruction, $"fmin(fmin({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMax3F32" =>
                    FloatResult(instruction, $"fmax(fmax({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMed3F32" =>
                    FloatResult(instruction, $"clamp({F(instruction, 2)}, fmin({F(instruction, 0)}, {F(instruction, 1)}), fmax({F(instruction, 0)}, {F(instruction, 1)}))"),

                // ---- conversions ----
                "VCvtF32I32" => FloatResult(instruction, $"(float)as_type<int>({Raw(instruction, 0)})"),
                "VCvtF32U32" => FloatResult(instruction, $"(float){Raw(instruction, 0)}"),
                "VCvtI32F32" => AsUInt($"(int)trunc({F(instruction, 0)})"),
                "VCvtU32F32" => $"(uint)clamp(trunc({F(instruction, 0)}), 0.0f, 4294967040.0f)",
                "VCvtFlrI32F32" => AsUInt($"(int)floor({F(instruction, 0)})"),
                "VCvtF32Ubyte0" => FloatResult(instruction, $"(float)({Raw(instruction, 0)} & 0xFFu)"),
                "VCvtF32Ubyte1" => FloatResult(instruction, $"(float)(({Raw(instruction, 0)} >> 8) & 0xFFu)"),
                "VCvtF32Ubyte2" => FloatResult(instruction, $"(float)(({Raw(instruction, 0)} >> 16) & 0xFFu)"),
                "VCvtF32Ubyte3" => FloatResult(instruction, $"(float)(({Raw(instruction, 0)} >> 24) & 0xFFu)"),

                // ---- integer ----
                "VAddU32" or "VAddI32" or "VAddCoU32" =>
                    $"({Raw(instruction, 0)} + {Raw(instruction, 1)})",
                "VSubU32" or "VSubI32" or "VSubCoU32" =>
                    $"({Raw(instruction, 0)} - {Raw(instruction, 1)})",
                "VSubrevU32" or "VSubrevI32" or "VSubrevCoU32" =>
                    $"({Raw(instruction, 1)} - {Raw(instruction, 0)})",
                "VMulLoU32" or "VMulLoI32" =>
                    $"({Raw(instruction, 0)} * {Raw(instruction, 1)})",
                "VMulHiU32" => $"mulhi({Raw(instruction, 0)}, {Raw(instruction, 1)})",
                "VMulHiI32" =>
                    AsUInt($"mulhi(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)}))"),
                "VMulU32U24" =>
                    $"(({Raw(instruction, 0)} & 0xFFFFFFu) * ({Raw(instruction, 1)} & 0xFFFFFFu))",
                "VMulHiU32U24" =>
                    $"mulhi({Raw(instruction, 0)} & 0xFFFFFFu, {Raw(instruction, 1)} & 0xFFFFFFu)",
                "VMadU32U24" =>
                    $"((({Raw(instruction, 0)} & 0xFFFFFFu) * ({Raw(instruction, 1)} & 0xFFFFFFu)) + {Raw(instruction, 2)})",
                "VAdd3U32" =>
                    $"({Raw(instruction, 0)} + {Raw(instruction, 1)} + {Raw(instruction, 2)})",
                "VAddLshlU32" =>
                    $"(({Raw(instruction, 0)} + {Raw(instruction, 1)}) << ({Raw(instruction, 2)} & 31u))",
                "VLshlAddU32" =>
                    $"(({Raw(instruction, 0)} << ({Raw(instruction, 1)} & 31u)) + {Raw(instruction, 2)})",
                "VMinU32" => $"min({Raw(instruction, 0)}, {Raw(instruction, 1)})",
                "VMaxU32" => $"max({Raw(instruction, 0)}, {Raw(instruction, 1)})",
                "VMinI32" =>
                    AsUInt($"min(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)}))"),
                "VMaxI32" =>
                    AsUInt($"max(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)}))"),
                "VMin3U32" =>
                    $"min(min({Raw(instruction, 0)}, {Raw(instruction, 1)}), {Raw(instruction, 2)})",
                "VMax3U32" =>
                    $"max(max({Raw(instruction, 0)}, {Raw(instruction, 1)}), {Raw(instruction, 2)})",
                "VMin3I32" =>
                    AsUInt($"min(min(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)})), as_type<int>({Raw(instruction, 2)}))"),
                "VMax3I32" =>
                    AsUInt($"max(max(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)})), as_type<int>({Raw(instruction, 2)}))"),
                "VMed3U32" =>
                    $"clamp({Raw(instruction, 2)}, min({Raw(instruction, 0)}, {Raw(instruction, 1)}), max({Raw(instruction, 0)}, {Raw(instruction, 1)}))",
                "VMed3I32" =>
                    AsUInt($"clamp(as_type<int>({Raw(instruction, 2)}), min(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)})), max(as_type<int>({Raw(instruction, 0)}), as_type<int>({Raw(instruction, 1)})))"),

                // ---- bitwise ----
                "VAndB32" => $"({Raw(instruction, 0)} & {Raw(instruction, 1)})",
                "VOrB32" => $"({Raw(instruction, 0)} | {Raw(instruction, 1)})",
                "VXorB32" => $"({Raw(instruction, 0)} ^ {Raw(instruction, 1)})",
                "VXnorB32" => $"~({Raw(instruction, 0)} ^ {Raw(instruction, 1)})",
                "VNotB32" => $"~{Raw(instruction, 0)}",
                "VAndOrB32" =>
                    $"(({Raw(instruction, 0)} & {Raw(instruction, 1)}) | {Raw(instruction, 2)})",
                "VOr3U32" =>
                    $"({Raw(instruction, 0)} | {Raw(instruction, 1)} | {Raw(instruction, 2)})",
                "VLshlOrU32" =>
                    $"(({Raw(instruction, 0)} << ({Raw(instruction, 1)} & 31u)) | {Raw(instruction, 2)})",
                "VLshlB32" => $"({Raw(instruction, 0)} << ({Raw(instruction, 1)} & 31u))",
                "VLshlrevB32" => $"({Raw(instruction, 1)} << ({Raw(instruction, 0)} & 31u))",
                "VLshrB32" => $"({Raw(instruction, 0)} >> ({Raw(instruction, 1)} & 31u))",
                "VLshrrevB32" => $"({Raw(instruction, 1)} >> ({Raw(instruction, 0)} & 31u))",
                "VAshrI32" =>
                    AsUInt($"(as_type<int>({Raw(instruction, 0)}) >> ({Raw(instruction, 1)} & 31u))"),
                "VAshrrevI32" =>
                    AsUInt($"(as_type<int>({Raw(instruction, 1)}) >> ({Raw(instruction, 0)} & 31u))"),
                "VBfeU32" =>
                    $"(({Raw(instruction, 0)} >> ({Raw(instruction, 1)} & 31u)) & ((1u << ({Raw(instruction, 2)} & 31u)) - 1u))",
                "VBfiB32" =>
                    $"(({Raw(instruction, 0)} & {Raw(instruction, 1)}) | (~{Raw(instruction, 0)} & {Raw(instruction, 2)}))",
                "VBfmB32" =>
                    $"((((1u << ({Raw(instruction, 0)} & 31u)) - 1u)) << ({Raw(instruction, 1)} & 31u))",
                "VBfrevB32" => $"reverse_bits({Raw(instruction, 0)})",
                "VBcntU32B32" => $"(popcount({Raw(instruction, 0)}) + {Raw(instruction, 1)})",
                "VFfblB32" =>
                    $"({Raw(instruction, 0)} == 0u ? 0xFFFFFFFFu : (uint)ctz({Raw(instruction, 0)}))",

                _ => null,
            };

            if (result is null)
            {
                error = $"unsupported vector opcode {instruction.Opcode}";
                return false;
            }

            StoreVector(DestinationVector(instruction), result);
            return true;
        }

        private const string TauLiteral = "6.2831853071795862f";

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            string condition;
            if (opcode is "VCmpTruF32" or "VCmpxTruF32" or "VCmpTI32" or "VCmpTU32")
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
                    ? $"(as_type<int>({Raw(instruction, 0)}) {op} as_type<int>({Raw(instruction, 1)}))"
                    : $"({Raw(instruction, 0)} {op} {Raw(instruction, 1)})";
            }

            // Vector compares fully overwrite the destination mask, but only
            // lanes enabled by EXEC can pass the test; balloting the raw
            // condition would leak results from disabled lanes.
            var active = Temp("bool", $"exec && {condition}");
            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                // GFX10 VCMPX writes EXEC only.
                Line($"exec = {active};");
            }
            else
            {
                Line($"vcc = {active};");
            }

            return true;
        }

        private static string TrimCompare(string opcode)
        {
            var trimmed = opcode.StartsWith("VCmpx", StringComparison.Ordinal)
                ? opcode["VCmpx".Length..]
                : opcode["VCmp".Length..];
            return trimmed[..^3];
        }

        // ---- scalar ALU ----

        private bool TryEmitScalarAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;

            if (opcode.StartsWith("SCmpk", StringComparison.Ordinal))
            {
                return TryEmitScalarCompare(
                    instruction,
                    opcode["SCmpk".Length..],
                    out error);
            }

            if (opcode.StartsWith("SCmp", StringComparison.Ordinal))
            {
                return TryEmitScalarCompare(
                    instruction,
                    opcode["SCmp".Length..],
                    out error);
            }

            if (TryEmitSaveexec(instruction, out var handled, out error))
            {
                if (handled)
                {
                    return true;
                }
            }
            else
            {
                return false;
            }

            switch (opcode)
            {
                case "SMovB32" or "SMovkI32":
                    StoreScalar(
                        DestinationScalar(instruction),
                        SourceExpression(instruction.Sources[0], instruction));
                    return true;
                case "SMovB64":
                {
                    // Wave32 masks fit the low register of the pair.
                    var pair = DestinationScalar(instruction);
                    StoreScalar(pair, SourceExpression(instruction.Sources[0], instruction));
                    StoreScalar(pair + 1, PairHighExpression(instruction.Sources[0]));
                    return true;
                }
                case "SCselectB32":
                    StoreScalar(
                        DestinationScalar(instruction),
                        $"(scc ? {S(instruction, 0)} : {S(instruction, 1)})");
                    return true;
                case "SCselectB64":
                {
                    var pair = DestinationScalar(instruction);
                    StoreScalar(pair, $"(scc ? {S(instruction, 0)} : {S(instruction, 1)})");
                    StoreScalar(pair + 1, "0u");
                    return true;
                }
                case "SBrevB32":
                    StoreScalar(DestinationScalar(instruction), $"reverse_bits({S(instruction, 0)})");
                    return true;
                case "SBcnt1I32B32":
                {
                    var result = Temp("uint", $"popcount({S(instruction, 0)})");
                    StoreScalar(DestinationScalar(instruction), result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SFF1I32B32":
                    StoreScalar(
                        DestinationScalar(instruction),
                        $"({S(instruction, 0)} == 0u ? 0xFFFFFFFFu : (uint)ctz({S(instruction, 0)}))");
                    return true;
                case "SNotB32":
                {
                    var result = Temp("uint", $"~{S(instruction, 0)}");
                    StoreScalar(DestinationScalar(instruction), result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SWqmB64":
                {
                    // Whole-quad-mode expansion is a pixel-stage concern; the
                    // SPIR-V translator treats it as a mask move as well.
                    var pair = DestinationScalar(instruction);
                    StoreScalar(pair, SourceExpression(instruction.Sources[0], instruction));
                    StoreScalar(pair + 1, PairHighExpression(instruction.Sources[0]));
                    return true;
                }
                case "SBfmB32":
                    StoreScalar(
                        DestinationScalar(instruction),
                        $"((((1u << ({S(instruction, 0)} & 31u)) - 1u)) << ({S(instruction, 1)} & 31u))");
                    return true;
                case "SBfeU32":
                {
                    var offset = $"({S(instruction, 1)} & 31u)";
                    var width = $"(({S(instruction, 1)} >> 16) & 0x7Fu)";
                    var result = Temp(
                        "uint",
                        $"{width} == 0u ? 0u : (({S(instruction, 0)} >> {offset}) & ({width} >= 32u ? 0xFFFFFFFFu : (1u << {width}) - 1u))");
                    StoreScalar(DestinationScalar(instruction), result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBfeI32":
                {
                    var offset = $"({S(instruction, 1)} & 31u)";
                    var width = $"(({S(instruction, 1)} >> 16) & 0x7Fu)";
                    var raw = Temp(
                        "uint",
                        $"{width} == 0u ? 0u : (({S(instruction, 0)} >> {offset}) & ({width} >= 32u ? 0xFFFFFFFFu : (1u << {width}) - 1u))");
                    var result = Temp(
                        "uint",
                        $"{width} == 0u || {width} >= 32u ? {raw} : (uint)((int)({raw} << (32u - {width})) >> (32u - {width}))");
                    StoreScalar(DestinationScalar(instruction), result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
            }

            // SCC-updating two-source forms.
            var (expression, sccExpression) = opcode switch
            {
                "SAddU32" =>
                    ($"({S(instruction, 0)} + {S(instruction, 1)})",
                     $"({S(instruction, 0)} + {S(instruction, 1)}) < {S(instruction, 0)}"),
                "SSubU32" =>
                    ($"({S(instruction, 0)} - {S(instruction, 1)})",
                     $"{S(instruction, 1)} > {S(instruction, 0)}"),
                "SAddcU32" =>
                    ($"({S(instruction, 0)} + {S(instruction, 1)} + (scc ? 1u : 0u))",
                     $"((ulong){S(instruction, 0)} + (ulong){S(instruction, 1)} + (scc ? 1ul : 0ul)) > 0xFFFFFFFFul"),
                "SSubbU32" =>
                    ($"({S(instruction, 0)} - {S(instruction, 1)} - (scc ? 1u : 0u))",
                     $"((ulong){S(instruction, 1)} + (scc ? 1ul : 0ul)) > (ulong){S(instruction, 0)}"),
                "SAddI32" or "SAddkI32" =>
                    ($"({S(instruction, 0)} + {S(instruction, 1)})",
                     $"((~({S(instruction, 0)} ^ {S(instruction, 1)}) & ({S(instruction, 0)} ^ ({S(instruction, 0)} + {S(instruction, 1)}))) >> 31) != 0u"),
                "SSubI32" =>
                    ($"({S(instruction, 0)} - {S(instruction, 1)})",
                     $"(((({S(instruction, 0)} ^ {S(instruction, 1)})) & ({S(instruction, 0)} ^ ({S(instruction, 0)} - {S(instruction, 1)}))) >> 31) != 0u"),
                "SMulI32" or "SMulkI32" =>
                    ($"({S(instruction, 0)} * {S(instruction, 1)})", string.Empty),
                "SMulHiU32" =>
                    ($"mulhi({S(instruction, 0)}, {S(instruction, 1)})", string.Empty),
                "SAndB32" or "SAndB64" =>
                    ($"({S(instruction, 0)} & {S(instruction, 1)})", "!= 0u"),
                "SOrB32" or "SOrB64" =>
                    ($"({S(instruction, 0)} | {S(instruction, 1)})", "!= 0u"),
                "SXorB32" or "SXorB64" =>
                    ($"({S(instruction, 0)} ^ {S(instruction, 1)})", "!= 0u"),
                "SNandB32" or "SNandB64" =>
                    ($"~({S(instruction, 0)} & {S(instruction, 1)})", "!= 0u"),
                "SNorB32" or "SNorB64" =>
                    ($"~({S(instruction, 0)} | {S(instruction, 1)})", "!= 0u"),
                "SXnorB32" or "SXnorB64" =>
                    ($"~({S(instruction, 0)} ^ {S(instruction, 1)})", "!= 0u"),
                "SNotB64" =>
                    ($"~{S(instruction, 0)}", "!= 0u"),
                "SAndn2B32" or "SAndn2B64" =>
                    ($"({S(instruction, 0)} & ~{S(instruction, 1)})", "!= 0u"),
                "SOrn2B32" or "SOrn2B64" =>
                    ($"({S(instruction, 0)} | ~{S(instruction, 1)})", "!= 0u"),
                "SAndn1B64" =>
                    ($"(~{S(instruction, 0)} & {S(instruction, 1)})", "!= 0u"),
                "SOrn1B64" =>
                    ($"(~{S(instruction, 0)} | {S(instruction, 1)})", "!= 0u"),
                "SLshlB32" or "SLshlB64" =>
                    ($"({S(instruction, 0)} << ({S(instruction, 1)} & 31u))", "!= 0u"),
                "SLshrB32" or "SLshrB64" =>
                    ($"({S(instruction, 0)} >> ({S(instruction, 1)} & 31u))", "!= 0u"),
                "SAshrI32" =>
                    ($"(uint)(as_type<int>({S(instruction, 0)}) >> ({S(instruction, 1)} & 31u))", "!= 0u"),
                "SLshl1AddU32" =>
                    ($"(({S(instruction, 0)} << 1) + {S(instruction, 1)})", string.Empty),
                "SLshl2AddU32" =>
                    ($"(({S(instruction, 0)} << 2) + {S(instruction, 1)})", string.Empty),
                "SLshl3AddU32" =>
                    ($"(({S(instruction, 0)} << 3) + {S(instruction, 1)})", string.Empty),
                "SLshl4AddU32" =>
                    ($"(({S(instruction, 0)} << 4) + {S(instruction, 1)})", string.Empty),
                "SMinU32" =>
                    ($"min({S(instruction, 0)}, {S(instruction, 1)})",
                     $"{S(instruction, 0)} <= {S(instruction, 1)}"),
                "SMaxU32" =>
                    ($"max({S(instruction, 0)}, {S(instruction, 1)})",
                     $"{S(instruction, 0)} >= {S(instruction, 1)}"),
                "SMinI32" =>
                    ($"(uint)min(as_type<int>({S(instruction, 0)}), as_type<int>({S(instruction, 1)}))",
                     $"as_type<int>({S(instruction, 0)}) <= as_type<int>({S(instruction, 1)})"),
                "SMaxI32" =>
                    ($"(uint)max(as_type<int>({S(instruction, 0)}), as_type<int>({S(instruction, 1)}))",
                     $"as_type<int>({S(instruction, 0)}) >= as_type<int>({S(instruction, 1)})"),
                _ => (string.Empty, string.Empty),
            };

            if (expression.Length == 0)
            {
                error = $"unsupported scalar opcode {opcode}";
                return false;
            }

            var value = Temp("uint", expression);
            var destination = DestinationScalar(instruction);
            StoreScalar(destination, value);
            if (opcode.EndsWith("B64", StringComparison.Ordinal))
            {
                StoreScalar(destination + 1, "0u");
            }

            if (sccExpression == "!= 0u")
            {
                Line($"scc = {value} != 0u;");
            }
            else if (sccExpression.Length != 0)
            {
                Line($"scc = {sccExpression};");
            }

            return true;
        }

        private bool TryEmitScalarCompare(
            Gen5ShaderInstruction instruction,
            string suffix,
            out string error)
        {
            error = string.Empty;
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
                error = $"unsupported scalar compare {instruction.Opcode}";
                return false;
            }

            Line(signed
                ? $"scc = as_type<int>({S(instruction, 0)}) {op} as_type<int>({S(instruction, 1)});"
                : $"scc = {S(instruction, 0)} {op} {S(instruction, 1)};");
            return true;
        }

        private bool TryEmitSaveexec(
            Gen5ShaderInstruction instruction,
            out bool handled,
            out string error)
        {
            handled = false;
            error = string.Empty;
            var opcode = instruction.Opcode;
            var index = opcode.IndexOf("Saveexec", StringComparison.Ordinal);
            if (index < 0)
            {
                return true;
            }

            // s_<op>_saveexec: dst = EXEC; EXEC = <op>(src0, EXEC); SCC = EXEC != 0.
            var operation = opcode[1..index];
            var source = SourceExpression(instruction.Sources[0], instruction);
            var savedExec = Temp("uint", "sharpemu_ballot(exec)");
            var combined = operation switch
            {
                "And" => $"({source} & {savedExec})",
                "Or" => $"({source} | {savedExec})",
                "Xor" => $"({source} ^ {savedExec})",
                "Nand" => $"~({source} & {savedExec})",
                "Nor" => $"~({source} | {savedExec})",
                "Xnor" => $"~({source} ^ {savedExec})",
                "Andn1" => $"(~{source} & {savedExec})",
                "Andn2" => $"({source} & ~{savedExec})",
                "Orn1" => $"(~{source} | {savedExec})",
                "Orn2" => $"({source} | ~{savedExec})",
                _ => string.Empty,
            };
            if (combined.Length == 0)
            {
                error = $"unsupported saveexec variant {opcode}";
                return false;
            }

            var mask = Temp("uint", combined);
            var destination = DestinationScalar(instruction);
            StoreScalar(destination, savedExec);
            if (opcode.EndsWith("B64", StringComparison.Ordinal))
            {
                StoreScalar(destination + 1, "0u");
            }

            Line($"exec = ({mask} >> sharpemu_lane & 1u) != 0u;");
            Line($"scc = {mask} != 0u;");
            handled = true;
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

        private uint DestinationScalar(Gen5ShaderInstruction instruction)
        {
            var destination = instruction.Destinations[0];
            return destination.Kind == Gen5OperandKind.ScalarRegister
                ? destination.Value
                : throw new NotSupportedException(
                    $"scalar destination expected in {instruction.Opcode}");
        }

        private string S(Gen5ShaderInstruction instruction, int index) =>
            SourceExpression(instruction.Sources[index], instruction);

        private string Raw(Gen5ShaderInstruction instruction, int index) =>
            SourceExpression(instruction.Sources[index], instruction);

        /// <summary>Float view of a source with VOP3 abs/neg modifiers applied.</summary>
        private string F(Gen5ShaderInstruction instruction, int index) =>
            FloatModifiedSource(instruction, index);

        private string FloatModifiedSource(Gen5ShaderInstruction instruction, int index)
        {
            var expression = AsFloat(SourceExpression(instruction.Sources[index], instruction));
            if (instruction.Control is Gen5Vop3Control control)
            {
                if ((control.AbsoluteMask & (1u << index)) != 0)
                {
                    expression = $"fabs({expression})";
                }

                if ((control.NegateMask & (1u << index)) != 0)
                {
                    expression = $"(-{expression})";
                }
            }

            return expression;
        }

        /// <summary>
        /// Wraps a float expression with VOP3 output modifiers and clamp, then
        /// bitcasts back to the register file's uint domain.
        /// </summary>
        private string FloatResult(Gen5ShaderInstruction instruction, string expression)
        {
            var (outputModifier, clamp) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.OutputModifier, control.Clamp),
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
                $"((s[{scalar.Value}] >> sharpemu_lane & 1u) != 0u)",
            _ => throw new NotSupportedException("mask operand must be a scalar register"),
        };

        /// <summary>High half of a 64-bit mask source pair (always empty on wave32).</summary>
        private string PairHighExpression(Gen5Operand operand) => operand switch
        {
            { Kind: Gen5OperandKind.ScalarRegister, Value: VccLoRegister or ExecLoRegister } => "0u",
            { Kind: Gen5OperandKind.ScalarRegister } scalar when
                scalar.Value + 1 < ScalarRegisterFileCount => $"s[{scalar.Value + 1}]",
            _ => "0u",
        };
    }
}
