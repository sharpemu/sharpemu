// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

public static class GuardedStoreDetector
{
    public static bool StoresOnlyUniformValues(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions;
        var targets = new HashSet<uint>();
        foreach (var instruction in instructions)
        {
            var opcode = instruction.Opcode;
            if (opcode == "SBranch" || opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var target = instruction.Pc + sizeof(uint) + (uint)((short)(instruction.Words[0] & 0xFFFF) * sizeof(uint));
                if (target <= instruction.Pc)
                {
                    return false;
                }

                targets.Add(target);
            }
            else if (opcode.Contains("Atomic", StringComparison.Ordinal) || opcode.StartsWith("Ds", StringComparison.Ordinal) ||
                     opcode.StartsWith("Image", StringComparison.Ordinal) || opcode.StartsWith("Global", StringComparison.Ordinal) ||
                     opcode.StartsWith("Flat", StringComparison.Ordinal) || opcode.StartsWith("Scratch", StringComparison.Ordinal) ||
                     opcode is "SSwappcB64" or "SSetpcB64")
            {
                return false;
            }
        }

        var stores = 0;
        for (var index = 0; index < instructions.Count; index++)
        {
            var store = instructions[index];
            if (!store.Opcode.StartsWith("BufferStore", StringComparison.Ordinal) &&
                !store.Opcode.StartsWith("TBufferStore", StringComparison.Ordinal))
            {
                continue;
            }

            if (store.Control is not Gen5BufferMemoryControl control || control.DwordCount == 0)
            {
                return false;
            }

            for (var register = control.VectorData; register < control.VectorData + control.DwordCount; register++)
            {
                if (UniformDefinition(instructions, index, register) is not { } definition ||
                    targets.Any(target => target > definition.Pc && target <= store.Pc))
                {
                    return false;
                }
            }

            stores++;
        }

        return stores != 0;
    }

    private static Gen5ShaderInstruction? UniformDefinition(IReadOnlyList<Gen5ShaderInstruction> instructions, int before, uint register)
    {
        for (var index = before - 1; index >= 0; index--)
        {
            var instruction = instructions[index];
            if (!instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.VectorRegister && destination.Value == register))
            {
                continue;
            }

            return instruction.Opcode == "VMovB32" && instruction.Sources is [{ Kind: Gen5OperandKind.ScalarRegister or
                Gen5OperandKind.EncodedConstant or Gen5OperandKind.LiteralConstant }]
                ? instruction
                : null;
        }

        return null;
    }
}
