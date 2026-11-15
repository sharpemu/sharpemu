// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // A wave32 row reduction (MIN, MAX, OR, AND, XOR or ADD) computed with a DPP scan and read at the end of a row:
        //   S_AND_B32 mask, x, entry            entry holds a subset of the entry EXEC (IsEntryExecSubset)
        //   S_ORN2_SAVEEXEC_B32 saved, x        EXEC = x | ~EXEC, every lane (x holds EXEC)
        //   V_CNDMASK_B32 v, identity, value, mask   (VOP3)
        //   V_<op> v, v, v                      DPP row_shr:1, 2, 4 and 8, full masks, FI and BOUND_CTRL clear
        //   [S_MOV_B32 exec_lo, saved]
        //   V_READLANE_B32 s, v, 15 and/or V_READLANE_B32 s, v, 31
        // On the PS5 every lane of the wave exists, and V_CNDMASK gives each lane outside mask the identity, so lane 15 (31)
        // holds the operation over row 0 (1). A host subgroup can lack invocations (a fragment warp that is not full), and
        // a pixel shader's V_READLANE reads its own lane. Each read is replaced by a subgroup reduction of the V_CNDMASK
        // result over its row, the other rows taking the identity. mask selects only lanes of the entry EXEC, so a lane
        // missing from the host subgroup takes the identity on the PS5 too and leaving it out changes nothing.
        // The scan itself is emitted unchanged.
        private readonly record struct WaveReduction(int CndmaskIndex, uint Vector, SpirvOp Operation, uint Identity, int Row);

        // VCC_LO: SGPRs from here on (VCC, trap registers, M0, NULL, EXEC) are written by instructions that do not name them.
        private const uint FirstSpecialScalarRegister = 106;

        private readonly Dictionary<int, uint> _waveReductionValues = new();

        // The reductions of a block by instruction index: the V_CNDMASK (Row -1) and each replaced V_READLANE. Null when the
        // block has none or the host subgroup cannot compute them (one host subgroup must be one guest wave32).
        private Dictionary<int, WaveReduction>? FindWaveReductions(ShaderBlock block)
        {
            if (!_request.SubgroupArithmetic || _waveLaneCount != 32 || _pairedLanes || _pairedMeshLanes ||
                _subgroupInvocationIdInput == 0 || _waveLowering is not NativeWave32Lowering)
            {
                return null;
            }

            Dictionary<int, WaveReduction>? found = null;
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                TryMatchWaveReduction(block, index, ref found);
            }

            return found;
        }

        // After the instruction at index was emitted: keeps a V_CNDMASK result, or replaces a row read.
        private void ApplyWaveReduction(Dictionary<int, WaveReduction> reductions, int index)
        {
            if (!reductions.TryGetValue(index, out var reduction))
            {
                return;
            }

            if (reduction.Row < 0)
            {
                _waveReductionValues[reduction.CndmaskIndex] = LoadV(reduction.Vector);
                return;
            }

            if (!_waveReductionValues.TryGetValue(reduction.CndmaskIndex, out var value))
            {
                return;
            }

            var inRow = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                ShiftRightLogical(GuestWaveLane(), UInt(4)),
                UInt((uint)reduction.Row));
            var contribution = _module.AddInstruction(SpirvOp.Select, _uintType, inRow, value, UInt(reduction.Identity));
            _module.AddCapability(SpirvCapability.GroupNonUniform);
            _module.AddCapability(SpirvCapability.GroupNonUniformArithmetic);
            StoreS(
                _request.Program.Instructions[index].Destinations[0].Value,
                _module.AddInstruction(reduction.Operation, _uintType, UInt(3), 0u, contribution));
        }

        // index names the S_ORN2_SAVEEXEC_B32 of a reduction.
        private void TryMatchWaveReduction(ShaderBlock block, int index, ref Dictionary<int, WaveReduction>? found)
        {
            var instructions = _request.Program.Instructions;
            var saveexec = instructions[index];
            if (saveexec.Opcode != "SOrn2SaveexecB32" ||
                saveexec.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } saved] ||
                saveexec.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } execSource] ||
                !HoldsExec(block, index, execSource.Value))
            {
                return;
            }

            var at = index + 1;
            while (at < block.EndIndex && instructions[at].Opcode is "SWaitcnt" or "SNop") at++;
            // The VOP3 form, whose mask is a named SGPR: the VOP2 form reads VCC, which compares and carries write unnamed.
            if (at >= block.EndIndex ||
                instructions[at] is not { Opcode: "VCndmaskB32" } cndmask ||
                cndmask.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } scanned] ||
                cndmask.Sources is not [var identitySource, _, { Kind: Gen5OperandKind.ScalarRegister } mask] ||
                cndmask.Control is not Gen5Vop3Control { AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false } ||
                !TryGetConstantOperand(identitySource, out var identity) ||
                !SelectsEntryLanes(block, at, mask.Value))
            {
                return;
            }

            var cndmaskIndex = at;
            var vector = scanned.Value;
            string? scan = null;
            foreach (var shift in new uint[] { 1, 2, 4, 8 })
            {
                if (++at >= block.EndIndex ||
                    instructions[at] is not { Control: Gen5DppControl dpp } step ||
                    dpp is not { FetchInactive: false, BoundControl: false, AbsoluteMask: 0, NegateMask: 0, BankMask: 15, RowMask: 15 } ||
                    dpp.Control != 0x110 + shift ||
                    step.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } written] || written.Value != vector ||
                    step.Sources is not [{ Kind: Gen5OperandKind.VectorRegister } left, { Kind: Gen5OperandKind.VectorRegister } right] ||
                    left.Value != vector || right.Value != vector ||
                    (scan ??= step.Opcode) != step.Opcode)
                {
                    return;
                }
            }

            var (operation, expectedIdentity) = scan switch
            {
                "VMaxU32" => (SpirvOp.GroupNonUniformUMax, 0u),
                "VMinU32" => (SpirvOp.GroupNonUniformUMin, uint.MaxValue),
                "VMaxI32" => (SpirvOp.GroupNonUniformSMax, 0x8000_0000u),
                "VMinI32" => (SpirvOp.GroupNonUniformSMin, 0x7FFF_FFFFu),
                "VOrB32" => (SpirvOp.GroupNonUniformBitwiseOr, 0u),
                "VAndB32" => (SpirvOp.GroupNonUniformBitwiseAnd, uint.MaxValue),
                "VXorB32" => (SpirvOp.GroupNonUniformBitwiseXor, 0u),
                "VAddU32" => (SpirvOp.GroupNonUniformIAdd, 0u),
                _ => (default(SpirvOp), 1u),
            };
            if (operation == default || identity != expectedIdentity)
            {
                return;
            }

            at++;
            if (at < block.EndIndex && instructions[at] is { Opcode: "SMovB32" } restore &&
                restore.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: 126 }] &&
                restore.Sources is [{ Kind: Gen5OperandKind.ScalarRegister } restored] && restored.Value == saved.Value)
            {
                at++;
            }

            var reads = new List<(int Index, int Row)>();
            for (; at < block.EndIndex && reads.Count < 2; at++)
            {
                if (instructions[at] is not { Opcode: "VReadlaneB32" } readlane ||
                    readlane.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister }] ||
                    readlane.Sources.Count < 2 ||
                    readlane.Sources[0] is not { Kind: Gen5OperandKind.VectorRegister } source || source.Value != vector ||
                    !TryGetFixedLane(readlane.Sources[1], out var lane) || lane is not (15 or 31) ||
                    reads.Exists(read => read.Row == (int)(lane >> 4)))
                {
                    break;
                }

                reads.Add((at, (int)(lane >> 4)));
            }

            if (reads.Count == 0)
            {
                return;
            }

            found ??= new Dictionary<int, WaveReduction>();
            found[cndmaskIndex] = new WaveReduction(cndmaskIndex, vector, operation, identity, -1);
            foreach (var (readIndex, row) in reads)
            {
                found[readIndex] = new WaveReduction(cndmaskIndex, vector, operation, identity, row);
            }
        }

        // Whether the scalar register holds EXEC when the instruction at index runs: it is EXEC itself, or the last
        // instruction of the block that may write EXEC before it is S_MOV_B32 exec_lo, register and nothing between them
        // writes the register.
        private bool HoldsExec(ShaderBlock block, int index, uint register)
        {
            if (register == 126)
            {
                return true;
            }

            if (register >= FirstSpecialScalarRegister)
            {
                return false;
            }

            var instructions = _request.Program.Instructions;
            for (var at = index - 1; at >= block.StartIndex; at--)
            {
                var instruction = instructions[at];
                if (instruction is { Opcode: "SMovB32", Sources: [{ Kind: Gen5OperandKind.ScalarRegister } source] } &&
                    instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: 126 }] &&
                    source.Value == register)
                {
                    return true;
                }

                if (MayWriteExec(instruction) || MayWriteScalar(instruction, register))
                {
                    return false;
                }
            }

            return false;
        }

        // Whether the SGPR mask that V_CNDMASK reads at cndmaskIndex selects only lanes of the wave's entry EXEC: its last
        // write in the block is S_AND_B32 mask, x, entry (in either order), entry holding a subset of the entry EXEC.
        private bool SelectsEntryLanes(ShaderBlock block, int cndmaskIndex, uint mask)
        {
            if (mask >= FirstSpecialScalarRegister)
            {
                return false;
            }

            var instructions = _request.Program.Instructions;
            for (var at = cndmaskIndex - 1; at >= block.StartIndex; at--)
            {
                var instruction = instructions[at];
                if (MayWriteScalar(instruction, mask))
                {
                    return instruction is { Opcode: "SAndB32", Sources: [var left, var right] } &&
                        (IsEntryExecSubset(left, at) || IsEntryExecSubset(right, at));
                }
            }

            return false;
        }

        // Whether the operand is an SGPR that holds a subset of the wave's entry EXEC when the instruction at useIndex runs.
        // Its one copy S_MOV_B32 register, exec_lo comes before useIndex, in the entry block (which no branch reaches) and
        // before any EXEC write; every other write of it is S_AND_B32 or S_ANDN2_B32 of itself, which keeps a subset.
        private bool IsEntryExecSubset(Gen5Operand operand, int useIndex)
        {
            if (operand.Kind != Gen5OperandKind.ScalarRegister || operand.Value >= FirstSpecialScalarRegister)
            {
                return false;
            }

            var register = operand.Value;
            var instructions = _request.Program.Instructions;
            var copy = -1;
            var inEntryBlock = true;
            var execWritten = false;
            for (var at = 0; at < instructions.Count; at++)
            {
                var instruction = instructions[at];
                if (instruction is { Opcode: "SMovB32", Sources: [{ Kind: Gen5OperandKind.ScalarRegister, Value: 126 }] } &&
                    instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister } destination] &&
                    destination.Value == register)
                {
                    if (copy >= 0 || !inEntryBlock || execWritten)
                    {
                        return false;
                    }

                    copy = at;
                }
                else if (MayWriteScalar(instruction, register) && !KeepsSubset(instruction, register))
                {
                    return false;
                }

                execWritten |= MayWriteExec(instruction);
                inEntryBlock &= !IsBranch(instruction.Opcode) && instruction.Opcode is not ("SEndpgm" or "SBarrier") &&
                    instruction.Control is not ShaderCallEntryControl;
            }

            if (copy < 0 || copy >= useIndex)
            {
                return false;
            }

            // A branch into the entry block would run the copy again, after EXEC may have changed.
            for (var at = 0; at < instructions.Count; at++)
            {
                if (!IsBranch(instructions[at].Opcode) || !TryGetBranchTargetPc(instructions[at], out var target))
                {
                    continue;
                }

                for (var entry = 0; entry <= copy; entry++)
                {
                    if (instructions[entry].Pc == target)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // S_AND_B32 register, register, x (either order) and S_ANDN2_B32 register, register, x keep a subset of register.
        private static bool KeepsSubset(Gen5ShaderInstruction instruction, uint register) =>
            instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister } destination] &&
            destination.Value == register &&
            instruction switch
            {
                { Opcode: "SAndB32", Sources: [var left, var right] } => IsScalar(left, register) || IsScalar(right, register),
                { Opcode: "SAndn2B32", Sources: [var left, _] } => IsScalar(left, register),
                _ => false,
            };

        private static bool IsScalar(Gen5Operand operand, uint register) =>
            operand.Kind == Gen5OperandKind.ScalarRegister && operand.Value == register;

        // Whether the instruction may write EXEC: a named write of exec_lo or exec_hi, a SAVEEXEC/WREXEC form, or V_CMPX.
        private static bool MayWriteExec(Gen5ShaderInstruction instruction) =>
            MayWriteScalar(instruction, 126) || MayWriteScalar(instruction, 127) ||
            instruction.Opcode.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
            instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal);

        // Whether the instruction may write the SGPR register. A 64-bit scalar result writes a register pair, of which
        // SOP1, SOP2 and SOPK name only the first; a VOP3 or SDWA scalar destination is a pair in wave64. A relative move
        // writes an SGPR chosen at run time, and a call entry sets registers it does not name.
        private static bool MayWriteScalar(Gen5ShaderInstruction instruction, uint register)
        {
            if (instruction.Opcode.StartsWith("SMovrel", StringComparison.Ordinal) ||
                instruction.Control is ShaderCallEntryControl or ShaderCallMatchControl or ShaderCallGroupControl or ShaderCallFaultControl)
            {
                return true;
            }

            var pair = instruction.Opcode.Contains("64", StringComparison.Ordinal);
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                    (destination.Value == register || pair && destination.Value + 1 == register))
                {
                    return true;
                }
            }

            var secondary = instruction.Control switch
            {
                Gen5Vop3Control { ScalarDestination: { } vop3 } => vop3,
                Gen5SdwaControl { ScalarDestination: { } sdwa } => sdwa,
                _ => (uint?)null,
            };
            return secondary is { } value && (value == register || value + 1 == register);
        }

        private static bool TryGetConstantOperand(Gen5Operand operand, out uint value)
        {
            value = operand.Value;
            return operand.Kind == Gen5OperandKind.LiteralConstant ||
                   operand.Kind == Gen5OperandKind.EncodedConstant && TryDecodeInlineConstant(operand.Value, out value);
        }
    }
}
