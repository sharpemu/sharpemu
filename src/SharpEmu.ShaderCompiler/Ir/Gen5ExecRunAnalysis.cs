// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

// Which instructions may share one EXEC test. A vector register write is modelled as
// select(EXEC, result, old value), so the old value of every register stays live across every
// write. A run of instructions whose only effect is their vector destination can instead run
// inside one if (EXEC) - the masked-off lanes skip the whole run, which is what the hardware does
// to them anyway, and only the join needs the old values.
//
// A run must stop at anything the wave runs even where this lane is masked off. SALU is not EXEC
// masked on the hardware and every invocation keeps its own copy of the scalar registers, so a
// lane that skipped a scalar write would carry a different value out of the region than its
// neighbours. The same goes for a compare that writes VCC or EXEC (GFX10 keeps that destination in
// the instruction's control, not in its destination list), a cross-lane read, LDS, memory, and the
// DPP encodings, which read a neighbour's register.
public static class Gen5ExecRunAnalysis
{
    public static bool IsExecMaskedVectorAlu(Gen5ShaderInstruction instruction)
    {
        var opcode = instruction.Opcode;
        if (opcode.Length == 0 || opcode[0] != 'V' ||
            // V_CMP writes a mask register, V_CMPX writes EXEC.
            opcode.StartsWith("VCmp", StringComparison.Ordinal) ||
            // Relative addressing can land on any register.
            opcode.StartsWith("VMovrel", StringComparison.Ordinal) ||
            // Cross-lane reads need the other lanes to have run.
            opcode.Contains("Readlane", StringComparison.Ordinal) ||
            opcode.Contains("Writelane", StringComparison.Ordinal) ||
            opcode.Contains("Readfirstlane", StringComparison.Ordinal) ||
            opcode.Contains("Permlane", StringComparison.Ordinal) ||
            opcode.Contains("Swap", StringComparison.Ordinal) ||
            // The carry-in/carry-out forms write VCC with no destination operand for it.
            opcode.Contains("Co", StringComparison.Ordinal) ||
            opcode.Contains("DivScale", StringComparison.Ordinal) ||
            opcode.Contains("DivFmas", StringComparison.Ordinal))
        {
            return false;
        }

        if (instruction.Destinations.Count == 0)
        {
            return false;
        }

        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.VectorRegister)
            {
                return false;
            }
        }

        return instruction.Control switch
        {
            null => true,
            Gen5Vop3Control control => control.ScalarDestination is null,
            Gen5SdwaControl control => control.ScalarDestination is null,
            Gen5Vop3pControl => true,
            _ => false,
        };
    }
}
