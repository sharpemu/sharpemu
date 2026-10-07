// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ScalarValueGraph
{
    private readonly Dictionary<ScalarValue, uint> _bitScanInstructions = [];

    internal bool HasNonZeroBitScanInput(ScalarValue selector)
    {
        if (!_bitScanInstructions.TryGetValue(selector, out var instructionAddress) ||
            !ControlFlow.BlockByStartPc.TryGetValue(instructionAddress, out var blockIndex) || blockIndex == 0)
            return false;

        var scan = Program.Instructions.First(instruction => instruction.Pc == instructionAddress);
        if (scan.Sources.Count != 1 || scan.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
            return false;

        var predecessors = ControlFlow.Predecessors[blockIndex];
        if (predecessors.Count == 0) return false;
        foreach (var predecessor in predecessors)
        {
            // The scan must be the guarded fallthrough, with no intervening register or condition write.
            if (predecessor + 1 != blockIndex || ControlFlow.Successors[predecessor].Count != 2)
                return false;
            var range = ControlFlow.Blocks[predecessor];
            var instructions = Program.Instructions
                .Where(instruction => instruction.Pc >= range.StartPc && instruction.Pc < range.EndPc)
                .TakeLast(2).ToArray();
            if (instructions.Length != 2) return false;
            var compare = instructions[0];
            var branch = instructions[1];
            var skipsZero = (compare.Opcode is "SCmpLgU32" or "SCmpLgI32" && branch.Opcode == "SCbranchScc0") ||
                (compare.Opcode is "SCmpEqU32" or "SCmpEqI32" && branch.Opcode == "SCbranchScc1");
            if (!skipsZero || compare.Sources.Count != 2 ||
                branch.Pc + (uint)branch.Words.Count * sizeof(uint) != instructionAddress)
                return false;
            var source = scan.Sources[0];
            if (!(compare.Sources[0] == source && IsZeroOperand(compare.Sources[1])) &&
                !(compare.Sources[1] == source && IsZeroOperand(compare.Sources[0])))
                return false;
        }
        return true;
    }

    private static bool IsZeroOperand(Gen5Operand operand) =>
        operand == Gen5Operand.Source(128) ||
        operand.Kind == Gen5OperandKind.LiteralConstant && operand.Value == 0;
}
