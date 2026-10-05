// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitDataShareSwizzle(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var selection = control.SingleOffsetBytes;
            if (selection >= 0xE000 || instruction.Sources.Count != 1 || instruction.Destinations.Count != 1)
            {
                error = selection >= 0xE000 ? "unsupported FFT data-share swizzle" : "invalid data-share swizzle operands";
                return false;
            }
            if (IsSingleLaneStage)
            {
                error = "data-share swizzle requires subgroup lane access";
                return false;
            }

            var lane = Temp("uint", "sharpemu_lane & 31u");
            string sourceLane;
            if (selection >= 0xC000)
            {
                var rotation = (selection >> 5) & 31;
                var displacement = (selection & 0x400) != 0 ? unchecked(0u - rotation) : rotation;
                var mask = selection & 31;
                sourceLane = Temp("uint", $"({lane} & {mask}u) | (({lane} + {displacement}u) & {~mask & 31}u)");
            }
            else if ((selection & 0x8000) != 0)
                sourceLane = Temp("uint", $"({lane} & ~3u) | (({selection}u >> (2u * ({lane} & 3u))) & 3u)");
            else
                sourceLane = Temp("uint", $"(({lane} & {selection & 31}u) | {(selection >> 5) & 31}u) ^ {(selection >> 10) & 31}u");

            var activeLanes = Temp("uint", "sharpemu_ballot(exec)");
            var sourceActive = Temp("bool", $"(({activeLanes} >> {sourceLane}) & 1u) != 0u");
            var safeSourceLane = Temp("uint", $"{sourceActive} ? {sourceLane} : {lane}");
            // All host lanes must participate before the destination EXEC check.
            var value = Temp("uint", $"simd_shuffle({RawSource(instruction, 0)}, (ushort){safeSourceLane})");
            StoreVector(instruction.Destinations[0].Value, $"{sourceActive} ? {value} : 0u");
            return true;
        }

        private bool TryEmitDataShareBpermute(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count != 2 || instruction.Destinations.Count != 1)
            {
                error = "invalid data-share bpermute operands";
                return false;
            }
            if (IsSingleLaneStage)
            {
                error = "data-share bpermute requires subgroup lane access";
                return false;
            }

            var lane = Temp("uint", "sharpemu_lane");
            var address = Temp("uint", $"{RawSource(instruction, 0)} + {control.SingleOffsetBytes}u");
            var index = Temp("uint", $"({address} >> 2u) & 31u");
            var sourceLane = Temp("uint", $"({lane} & ~31u) | {index}");
            var activeLanes = Temp("uint", "sharpemu_ballot(exec)");
            var sourceActive = Temp("bool", $"(({activeLanes} >> ({sourceLane} & 31u)) & 1u) != 0u");
            var safeSourceLane = Temp("uint", $"{sourceActive} ? {sourceLane} : {lane}");
            var value = Temp("uint", $"simd_shuffle({RawSource(instruction, 1)}, (ushort){safeSourceLane})");
            StoreVector(instruction.Destinations[0].Value, $"{sourceActive} ? {value} : 0u");
            return true;
        }
    }
}
