// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.GnmMode.Shaders;

internal static class GcnTessellationProgram
{
    internal const int StageUserScalars = 16;
    private const uint ScalarLimit = 104;
    private const uint VectorLimit = 256;
    private const uint Exec = 126;
    private const uint InlineZero = 128;
    private const uint InlineSixtyFour = 192;
    private const uint InlineMinusOne = 193;
    private const uint LocalLaneCount = 7u << 16;
    private const uint HullLaneCount = 7u << 16 | 8;

    internal static bool TryJoinHull(Gen5ShaderProgram local, Gen5ShaderProgram hull, ulong localAddress, ulong hullAddress,
        out Gen5ShaderProgram program, out string error)
    {
        program = local;
        if (!GcnStageResources.TryGet(localAddress, out var localResource) || !GcnStageResources.TryGet(hullAddress, out var hullResource))
        {
            error = $"gcn-tessellation-stage-registers-unknown ls=0x{localAddress:X} hs=0x{hullAddress:X}";
            return false;
        }
        if (hullResource.OffchipLocalData || hullResource.GroupSize)
        {
            error = $"gcn-hull-system-registers-unsupported hs=0x{hullAddress:X} rsrc2=0x{hullResource.Resource2:X8}";
            return false;
        }

        var hullUserScalars = hullResource.UserScalars;
        var used = new bool[ScalarLimit];
        var reserved = Math.Min(ScalarLimit, Math.Max(2u * StageUserScalars,
            Math.Max(localResource.ScalarRegisters, hullResource.ScalarRegisters)));
        Array.Fill(used, true, 0, (int)reserved);
        var vectors = Math.Max(localResource.VectorRegisters, hullResource.VectorRegisters);
        foreach (var instruction in local.Instructions.Concat(hull.Instructions))
        {
            foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
            {
                if (operand.Kind == Gen5OperandKind.ScalarRegister)
                    MarkPair(used, operand.Value);
                else if (operand.Kind == Gen5OperandKind.VectorRegister)
                    vectors = Math.Max(vectors, operand.Value + 1);
            }
            if (instruction.Control is ShaderCallEntryControl entry)
            {
                MarkPair(used, entry.ArgumentRegister);
                MarkPair(used, entry.ReturnRegister);
            }
            else if (instruction.Control is ShaderCallMatchControl match)
            {
                MarkPair(used, match.AddressRegister);
                MarkPair(used, match.ArgumentRegister);
            }
        }
        var stash = Enumerable.Range(0, (int)ScalarLimit).Where(register => !used[register])
            .Select(register => (uint)register).Take((int)hullUserScalars + 3).ToArray();
        if (stash.Length < hullUserScalars + 3 || vectors + 2 > VectorLimit)
        {
            error = $"gcn-tessellation-no-free-registers ls=0x{localAddress:X} hs=0x{hullAddress:X} scalars={stash.Length} vectors={vectors}";
            return false;
        }
        var factor = stash[hullUserScalars];
        var laneCounts = stash[hullUserScalars + 1];
        var lanes = stash[hullUserScalars + 2];
        var patch = vectors;
        var relativeIds = vectors + 1;

        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void Add(Gen5ShaderEncoding encoding, string opcode, uint[] words, Gen5Operand[] sources, Gen5Operand[] destinations)
        {
            instructions.Add(new(pc, encoding, opcode, words, sources, destinations, null));
            pc += (uint)words.Length * sizeof(uint);
        }
        void Move(uint destination, uint source) => Add(Gen5ShaderEncoding.Sop1, "SMovB32", [0xBE800300 | destination << 16 | source],
            [Gen5Operand.Scalar(source)], [Gen5Operand.Scalar(destination)]);
        void Activate(uint field)
        {
            Add(Gen5ShaderEncoding.Sop2, "SBfeU32", [0x93800000 | lanes << 16 | 0xFF00 | laneCounts, field],
                [Gen5Operand.Scalar(laneCounts), new(Gen5OperandKind.LiteralConstant, field)], [Gen5Operand.Scalar(lanes)]);
            Add(Gen5ShaderEncoding.Sop2, "SBfmB64", [0x91FE0000 | InlineZero << 8 | lanes],
                [Gen5Operand.Scalar(lanes), new(Gen5OperandKind.EncodedConstant, InlineZero)], [Gen5Operand.Scalar(Exec)]);
            Add(Gen5ShaderEncoding.Sopc, "SCmpEqU32", [0xBF060000 | InlineSixtyFour << 8 | lanes],
                [Gen5Operand.Scalar(lanes), new(Gen5OperandKind.EncodedConstant, InlineSixtyFour)], []);
            Add(Gen5ShaderEncoding.Sop1, "SCmovB64", [0xBEFE0600 | InlineMinusOne],
                [new(Gen5OperandKind.EncodedConstant, InlineMinusOne)], [Gen5Operand.Scalar(Exec)]);
        }

        for (var index = 0u; index < hullUserScalars; index++)
            Move(stash[index], StageUserScalars + index);
        Activate(LocalLaneCount);
        var localStart = pc;
        var hullEntry = localStart + End(local);
        Append(instructions, local, localStart, 0, hullEntry, hullEntry);
        pc = hullEntry;
        Add(Gen5ShaderEncoding.Sopp, "SBarrier", [0xBF8A0000], [], []);
        for (var index = 0u; index < hullUserScalars; index++)
            Move(index, stash[index]);
        Move(hullUserScalars, factor);
        Activate(HullLaneCount);
        Add(Gen5ShaderEncoding.Vop1, "VMovB32", [0x7E000200 | 256 + patch], [Gen5Operand.Vector(patch)], [Gen5Operand.Vector(0)]);
        Add(Gen5ShaderEncoding.Vop1, "VMovB32", [0x7E020200 | 256 + relativeIds], [Gen5Operand.Vector(relativeIds)], [Gen5Operand.Vector(1)]);
        var hullStart = pc;
        var end = hullStart + End(hull);
        Append(instructions, hull, hullStart, unchecked(hullAddress - localAddress), end, null);
        program = new Gen5ShaderProgram(localAddress, instructions)
        {
            IsFusedProgram = true,
            FusedContinuationPc = hullStart,
            TessellationRegisters = new Gen5TessellationRegisters
            {
                HullOffchipScalar = null,
                HullWaveCountScalar = laneCounts,
                HullFactorScalar = factor,
                HullFactorStoreScalar = hullUserScalars,
                HullEntrySetsFactor = true,
                HullPatchVector = patch,
                HullRelativeIdVector = relativeIds,
                HullVertexVector = 0,
                HullLocalVector = 1,
                HullInstanceVectors = [2, 3],
                LocalDataRing = true,
            },
        };
        if (GnmDiagnostics.Log)
            Console.Error.WriteLine($"[GNMMODE][GPU] Joined GCN tessellation stages ls=0x{localAddress:X} hs=0x{hullAddress:X} " +
                $"ls_rsrc=0x{localResource.Resource1:X8}/0x{localResource.Resource2:X8} hs_rsrc=0x{hullResource.Resource1:X8}/0x{hullResource.Resource2:X8} " +
                $"hs_user={hullUserScalars} factor=s{hullUserScalars}<-s{factor} lanes=s{laneCounts} patch=v{patch} ids=v{relativeIds} " +
                $"instructions={instructions.Count} hull_entry=0x{hullStart:X}.");
        error = string.Empty;
        return true;
    }

    internal static bool TryPrepareDomain(Gen5ShaderProgram domain, ulong address, out Gen5ShaderProgram program, out string error)
    {
        program = domain;
        if (GcnStageResources.TryGet(address, out var resource) && resource.OffchipLocalData)
        {
            error = $"gcn-offchip-domain-unsupported ds=0x{address:X} rsrc2=0x{resource.Resource2:X8}";
            return false;
        }
        program = domain with
        {
            TessellationRegisters = new Gen5TessellationRegisters
            {
                DomainCoordinateVector = 0,
                DomainRelativePatchVector = 2,
                DomainPatchVector = 3,
                DomainOffchipScalar = null,
                LocalDataRing = true,
            },
        };
        error = string.Empty;
        return true;
    }

    private static void MarkPair(bool[] used, uint register)
    {
        for (var index = register; index < Math.Min(register + 2, ScalarLimit); index++)
            used[index] = true;
    }

    private static uint End(Gen5ShaderProgram program) =>
        program.Instructions.Count == 0 ? 0 : program.Instructions[^1].Pc + (uint)program.Instructions[^1].Words.Count * sizeof(uint);

    private static void Append(List<Gen5ShaderInstruction> instructions, Gen5ShaderProgram part, uint start, ulong addressDelta,
        uint exit, uint? endTarget)
    {
        var size = End(part);
        foreach (var instruction in part.Instructions)
        {
            var relocated = instruction with
            {
                Pc = checked(start + instruction.Pc),
                AddressOffset = unchecked(addressDelta + instruction.ProgramOffset),
            };
            if (endTarget is { } target && instruction.Opcode == "SEndpgm" && instruction.Control is null)
            {
                relocated = relocated with
                {
                    Opcode = "SBranch",
                    Words = [0xBF820000],
                    Control = new ShaderLinkedBranchControl(target),
                };
            }
            else if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var branch) &&
                instruction.Control is null or ShaderLinkedBranchControl)
            {
                relocated = relocated with
                {
                    Control = new ShaderLinkedBranchControl(branch >= size ? exit : checked(start + branch)),
                };
            }
            instructions.Add(relocated);
        }
    }
}
