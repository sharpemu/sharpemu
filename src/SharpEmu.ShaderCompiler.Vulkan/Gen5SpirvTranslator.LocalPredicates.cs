// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private readonly HashSet<uint> _localComparisonPredicates = new();

        private void FindLocalComparisonPredicates(IReadOnlyList<ShaderBlock> blocks)
        {
            if (_stage != Gen5SpirvStage.Compute || !_emulateWave64) return;
            foreach (var block in blocks)
            {
                var maskIsUnused = false;
                for (var index = block.EndIndex - 1; index >= block.StartIndex; index--)
                {
                    var instruction = _request.Program.Instructions[index];
                    if (instruction.Opcode == "SEndpgm")
                    {
                        maskIsUnused = true;
                        continue;
                    }
                    if (instruction.Opcode == "SAndB64" &&
                        instruction.Destinations.Count == 1 &&
                        instruction.Destinations[0] == Gen5Operand.Scalar(106) && HasNoMaskSource(instruction))
                    {
                        maskIsUnused = true;
                        continue;
                    }
                    var ordinaryComparison = instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) &&
                        !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) && instruction.Control is null;
                    if (ordinaryComparison && HasNoMaskSource(instruction))
                    {
                        if (maskIsUnused) _localComparisonPredicates.Add(instruction.Pc);
                        maskIsUnused = true;
                        continue;
                    }
                    if (!PreservesLocalComparison(instruction)) maskIsUnused = false;
                }
            }
        }

        private static bool HasNoMaskSource(Gen5ShaderInstruction instruction) =>
            instruction.Sources.All(static source => source.Kind switch
            {
                Gen5OperandKind.VectorRegister or Gen5OperandKind.LiteralConstant => true,
                Gen5OperandKind.ScalarRegister => source.Value < 105,
                Gen5OperandKind.EncodedConstant => source.Value is >= 128 and <= 248,
                _ => false,
            });

        private static bool PreservesLocalComparison(Gen5ShaderInstruction instruction)
        {
            if (instruction.Opcode is "SWaitcnt" or "SNop") return true;
            if (!HasNoMaskSource(instruction)) return false;
            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) &&
                instruction.Control is Gen5SdwaControl { ScalarDestination: <= 104 })
                return true;
            if (instruction.Opcode == "DsReadB32")
                return instruction.Control is Gen5DataShareControl { Gds: false } &&
                    instruction.Destinations.All(static destination => destination.Kind == Gen5OperandKind.VectorRegister);
            if (instruction.Control is not (null or Gen5SdwaControl { ScalarDestination: null } or
                    Gen5Vop3Control { ScalarDestination: null }) ||
                instruction.Destinations.Any(static destination => destination.Kind != Gen5OperandKind.VectorRegister))
                return false;
            return instruction.Opcode is "VMovB32" or "VAddF32" or "VSubF32" or "VMulF32" or
                "VMinF32" or "VMaxF32" or "VCndmaskB32" or "VAddLshlU32" or "VMadF32";
        }
    }
}
