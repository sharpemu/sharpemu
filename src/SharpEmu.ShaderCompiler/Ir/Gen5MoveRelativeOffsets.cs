// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

// The values M0 can hold at each V_MOVRELS/V_MOVRELD/V_MOVRELSD, when they follow from
// straight-line scalar code (bit-field extracts, masks, shifts, adds and multiplies by
// constants). A backend that knows the few registers a relative move can reach selects
// among them with constant register numbers; a register file indexed at run time keeps
// the whole file out of hardware registers.
public static class Gen5MoveRelativeOffsets
{
    private const uint M0Register = 124;
    private const int MaxValues = 64;
    private const int MaxDepth = 8;
    private const int MaxScanSteps = 4096;

    // Maps the pc of every resolved relative move to its sorted M0 values; a move that
    // is missing from the map could not be bounded.
    public static IReadOnlyDictionary<uint, uint[]> Analyze(Gen5ShaderProgram program)
    {
        var result = new Dictionary<uint, uint[]>();
        var instructions = program.Instructions;
        var hasRelativeMove = false;
        foreach (var instruction in instructions)
        {
            if (IsBoundedRelativeMove(instruction.Opcode))
            {
                hasRelativeMove = true;
            }

            // A computed jump can land anywhere; no straight-line reasoning holds.
            if (instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64")
            {
                return result;
            }
        }

        if (!hasRelativeMove)
        {
            return result;
        }

        // Branch target pc -> index of the single branch reaching it, or -1 when several
        // branches do.
        var targets = new Dictionary<uint, int>();
        for (var index = 0; index < instructions.Count; index++)
        {
            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[index], out var target))
            {
                targets[target] = targets.ContainsKey(target) ? -1 : index;
            }
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            if (!IsBoundedRelativeMove(instructions[index].Opcode))
            {
                continue;
            }

            var values = new HashSet<uint>();
            if (TryResolveRegister(instructions, targets, index, M0Register, 0, values))
            {
                var sorted = values.ToArray();
                Array.Sort(sorted);
                result[instructions[index].Pc] = sorted;
            }
        }

        return result;
    }

    private static bool IsBoundedRelativeMove(string opcode) =>
        opcode is "VMovrelsB32" or "VMovreldB32" or "VMovrelsdB32";

    // The values the scalar register holds when the instruction at index executes.
    private static bool TryResolveRegister(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        Dictionary<uint, int> targets,
        int index,
        uint register,
        int depth,
        HashSet<uint> values)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        var steps = 0;
        for (var at = index - 1; at >= 0; at--)
        {
            if (++steps > MaxScanSteps)
            {
                return false;
            }

            // A join: follow its one predecessor, or give up when two paths meet.
            if (targets.TryGetValue(instructions[at + 1].Pc, out var branch))
            {
                var fallsThrough = !Gen5IrBranchResolver.IsUnconditionalBranch(instructions[at]) &&
                    !Gen5IrBranchResolver.IsTerminator(instructions[at]);
                if (branch < 0 || fallsThrough)
                {
                    return false;
                }

                at = branch;
                continue;
            }

            var instruction = instructions[at];
            if (!Writes(instruction, register, out var exact))
            {
                continue;
            }

            return exact && TryEvaluate(instructions, targets, at, instruction, depth, values);
        }

        return false;
    }

    // exact: the instruction's only effect on the register is its listed destination.
    private static bool Writes(Gen5ShaderInstruction instruction, uint register, out bool exact)
    {
        exact = false;
        var wide = instruction.Opcode.StartsWith('S') &&
            (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
             instruction.Opcode.EndsWith("U64", StringComparison.Ordinal) ||
             instruction.Opcode.EndsWith("I64", StringComparison.Ordinal));
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.ScalarRegister)
            {
                continue;
            }

            if (destination.Value == register)
            {
                exact = !wide && instruction.Destinations.Count == 1;
                return true;
            }

            if (wide && destination.Value + 1 == register)
            {
                return true;
            }
        }

        var scalarDestination = instruction.Control switch
        {
            Gen5Vop3Control control => control.ScalarDestination,
            Gen5SdwaControl control => control.ScalarDestination,
            _ => null,
        };
        if (scalarDestination is { } written && (written == register || written + 1 == register))
        {
            return true;
        }

        // Vector compares and carries write VCC or EXEC without listing them; on gfx10 a
        // V_CMPX writes EXEC only.
        if (instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            return register is 126 or 127;
        }

        if (register is 106 or 107 &&
            instruction.Opcode.StartsWith('V') &&
            (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) ||
             instruction.Opcode.Contains("Co", StringComparison.Ordinal) ||
             instruction.Opcode.Contains("Div", StringComparison.Ordinal)))
        {
            return true;
        }

        // Relative scalar moves and loads write registers picked at run time.
        return instruction.Opcode.StartsWith("SMovrel", StringComparison.Ordinal);
    }

    private static bool TryEvaluate(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        Dictionary<uint, int> targets,
        int index,
        Gen5ShaderInstruction instruction,
        int depth,
        HashSet<uint> values)
    {
        var sources = instruction.Sources;
        switch (instruction.Opcode)
        {
            case "SMovB32" when sources.Count == 1:
                return TryResolveOperand(instructions, targets, index, sources[0], depth, values);
            case "SBfeU32" when sources.Count == 2 && TryConstant(sources[1], out var field):
            {
                var width = (int)((field >> 16) & 0x7F);
                if (width > 6)
                {
                    return false;
                }

                for (var value = 0u; value < 1u << width; value++)
                {
                    values.Add(value);
                }

                return true;
            }
            case "SAndB32" when sources.Count == 2:
            {
                if (!TryConstant(sources[1], out var mask) && !TryConstant(sources[0], out mask))
                {
                    return false;
                }

                if (mask >= MaxValues)
                {
                    return false;
                }

                for (var value = 0u; value <= mask; value++)
                {
                    if ((value & ~mask) == 0)
                    {
                        values.Add(value);
                    }
                }

                return true;
            }
            case "SLshlB32" when sources.Count == 2 && TryConstant(sources[1], out var shift):
                return TryCombine(instructions, targets, index, sources[0], depth, values, value => value << (int)(shift & 31));
            case "SMulI32" or "SAddU32" or "SAddI32" when sources.Count == 2:
            {
                var multiply = instruction.Opcode == "SMulI32";
                if (TryConstant(sources[1], out var right))
                {
                    return TryCombine(instructions, targets, index, sources[0], depth, values,
                        value => multiply ? value * right : value + right);
                }

                if (TryConstant(sources[0], out var left))
                {
                    return TryCombine(instructions, targets, index, sources[1], depth, values,
                        value => multiply ? value * left : value + left);
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static bool TryCombine(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        Dictionary<uint, int> targets,
        int index,
        Gen5Operand operand,
        int depth,
        HashSet<uint> values,
        Func<uint, uint> apply)
    {
        var inputs = new HashSet<uint>();
        if (!TryResolveOperand(instructions, targets, index, operand, depth, inputs))
        {
            return false;
        }

        foreach (var input in inputs)
        {
            values.Add(unchecked(apply(input)));
        }

        return values.Count <= MaxValues;
    }

    private static bool TryResolveOperand(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        Dictionary<uint, int> targets,
        int index,
        Gen5Operand operand,
        int depth,
        HashSet<uint> values)
    {
        if (TryConstant(operand, out var constant))
        {
            values.Add(constant);
            return true;
        }

        return operand.Kind == Gen5OperandKind.ScalarRegister &&
            TryResolveRegister(instructions, targets, index, operand.Value, depth + 1, values) &&
            values.Count <= MaxValues;
    }

    private static bool TryConstant(Gen5Operand operand, out uint value)
    {
        value = 0;
        return operand.Kind switch
        {
            Gen5OperandKind.LiteralConstant => (value = operand.Value) == operand.Value,
            Gen5OperandKind.EncodedConstant => Gen5InlineConstants.TryDecode(operand.Value, out value),
            _ => false,
        };
    }
}
