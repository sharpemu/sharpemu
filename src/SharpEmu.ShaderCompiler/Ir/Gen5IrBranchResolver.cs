// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace SharpEmu.ShaderCompiler.Ir;

public sealed class Gen5IrBranchResolver : IIrBranchResolver
{
    public static Gen5IrBranchResolver Instance { get; } = new();

    public bool IsBranch(Gen5ShaderInstruction instruction) =>
        IsUnconditionalBranch(instruction) ||
        IsConditional(instruction) ||
        IsTerminator(instruction);

    public bool IsConditional(Gen5ShaderInstruction instruction) => instruction.Opcode switch
    {
        "SCbranchScc0" or
        "SCbranchScc1" or
        "SCbranchVccz" or
        "SCbranchVccnz" or
        "SCbranchExecz" or
        "SCbranchExecnz" or
        "SCbranchCdbgsys" or
        "SCbranchCdbguser" or
        "SCbranchCdbgsysOrUser" or
        "SCbranchCdbgsysAndUser" => true,
        _ => false,
    };

    public bool TryGetBranchTarget(Gen5ShaderInstruction instruction, out uint targetPc)
    {
        targetPc = 0;
        if (IsTerminator(instruction))
        {
            return false;
        }

        if (!IsUnconditionalBranch(instruction) && !IsConditional(instruction))
        {
            return false;
        }

        if (instruction.Encoding != Gen5ShaderEncoding.Sopp || instruction.Words.Count == 0)
        {
            return false;
        }

        var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
        var nextPc = (long)instruction.Pc + instruction.Words.Count * sizeof(uint);
        var target = nextPc + offset * sizeof(uint);
        if (target < 0 || target > uint.MaxValue)
        {
            return false;
        }

        targetPc = (uint)target;
        return true;
    }

    public static bool IsUnconditionalBranch(Gen5ShaderInstruction instruction) =>
        string.Equals(instruction.Opcode, "SBranch", StringComparison.Ordinal);

    public static bool IsTerminator(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is "SEndpgm" or "SEndpgmSaved";
}
