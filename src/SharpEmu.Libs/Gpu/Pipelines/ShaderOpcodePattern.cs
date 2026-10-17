// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class ShaderOpcodePattern
{
    internal static Gen5ShaderInstruction[]? Match(
        IReadOnlyList<Gen5ShaderInstruction> source, string[] opcodes, bool ignoreMarkers)
    {
        // Patterns are fixed short sequences. Keep rejected programs off the managed heap.
        Span<int> positions = stackalloc int[opcodes.Length];
        var count = 0;
        for (var index = 0; index < source.Count; index++)
        {
            var instruction = source[index];
            if (instruction.Opcode == "SWaitcnt" || ignoreMarkers &&
                (instruction.Opcode is "STtraceData" or "SInstPrefetch" ||
                 instruction.Opcode == "SMovB32" && instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: 124 }]))
                continue;
            if (count == opcodes.Length || instruction.Opcode != opcodes[count]) return null;
            positions[count++] = index;
        }

        if (count != opcodes.Length) return null;
        var result = new Gen5ShaderInstruction[count];
        for (var index = 0; index < count; index++) result[index] = source[positions[index]];
        return result;
    }
}
