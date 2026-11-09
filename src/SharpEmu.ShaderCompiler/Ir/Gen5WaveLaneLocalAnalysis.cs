// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.ShaderCompiler.Ir;

public static class Gen5WaveLaneLocalAnalysis
{
    private const int VccLo = 106;
    private const int VccHi = 107;
    private const int M0 = 124;
    private const int Scc = 125;
    private const int ExecLo = 126;
    private const int ExecHi = 127;
    private const int ScalarCount = 128;
    private const int VectorCount = 256;
    private const int MaxSteps = 100_000;
    private const int NoBlock = -1;

    private enum Family
    {
        Sop1,
        Sop2,
        Sopk,
        Sopc,
        Sopp,
        Smem,
        Vop1,
        Vop2,
        Vop3,
        Vop3p,
        Vopc,
        Mubuf,
        Mtbuf,
        Flat,
        Mimg,
        Ds,
        Other,
    }

    private enum Kind : byte
    {
        None,
        Scalar,
        Vector,
        Zero,
        AllOnes,
        Constant,
        Scc,
        VccZ,
        ExecZ,
    }

    private enum Condition : byte
    {
        None,
        ExecZero,
        ExecNonZero,
        VccZero,
        VccNonZero,
        Other,
    }

    private enum Sym : byte
    {
        Original,
        Zero,
        Ones,
        Unknown,
    }

    private readonly record struct Operand(Kind Kind, int Register = -1)
    {
        public static Operand None => new(Kind.None);

        public static Operand Scalar(int register) => new(Kind.Scalar, register);

        public int ScalarIndex => Kind == Kind.Scalar ? Register : -1;
    }

    private readonly record struct MaskFacts(bool Covers, bool Within, bool Nonzero);

    private sealed class Op
    {
        public required uint Pc { get; init; }

        public required string Opcode { get; init; }

        public required Family Family { get; init; }

        public required Operand[] Sources { get; init; }

        public required int[] Widths { get; init; }

        public Operand Destination { get; init; }

        public int DestinationCount { get; init; } = 1;

        public Operand Carry { get; init; }

        public int Select { get; init; } = -1;

        public bool LaneFetch { get; init; }

        public bool CrossRowFetch { get; init; }

        public bool PartialVector { get; init; }

        public int WrittenVectorStart { get; init; } = -1;

        public int WrittenVectorCount { get; init; }

        public bool Wide { get; init; }

        public string? Refused { get; init; }

        public bool IsBranch { get; init; }

        public bool IsConditional { get; init; }

        public bool IsTerminator { get; init; }

        public Condition Condition { get; init; }

        public Operand Source(int index) => index < Sources.Length ? Sources[index] : Operand.None;
    }

    private struct State : IEquatable<State>
    {
        public UInt128 Mask;
        public UInt128 Covers;
        public UInt128 Within;
        public UInt128 Nonzero;
        public UInt128 Lanes;
        public UInt128 UniformLow;
        public UInt128 UniformHigh;
        public bool SccMask;
        public bool Active;

        public readonly bool Uniform(int register) =>
            register < 128
                ? ((UniformLow >> register) & UInt128.One) != 0
                : register < VectorCount && ((UniformHigh >> (register - 128)) & UInt128.One) != 0;

        public void SetUniform(int register, bool value)
        {
            if (register < 128)
            {
                UniformLow = value ? UniformLow | (UInt128.One << register) : UniformLow & ~(UInt128.One << register);
            }
            else if (register < VectorCount)
            {
                var bit = UInt128.One << (register - 128);
                UniformHigh = value ? UniformHigh | bit : UniformHigh & ~bit;
            }
        }

        public void Join(in State other)
        {
            Mask |= other.Mask;
            Covers &= other.Covers;
            Within &= other.Within;
            Nonzero &= other.Nonzero;
            Lanes &= other.Lanes;
            UniformLow &= other.UniformLow;
            UniformHigh &= other.UniformHigh;
            SccMask |= other.SccMask;
            Active &= other.Active;
        }

        public readonly bool Equals(State other) =>
            Mask == other.Mask &&
            Covers == other.Covers &&
            Within == other.Within &&
            Nonzero == other.Nonzero &&
            Lanes == other.Lanes &&
            UniformLow == other.UniformLow &&
            UniformHigh == other.UniformHigh &&
            SccMask == other.SccMask &&
            Active == other.Active;

        public override readonly bool Equals(object? obj) => obj is State other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(Mask, Covers, Within, Lanes, UniformLow, UniformHigh);
    }

    private sealed class Block
    {
        public required int Start { get; init; }

        public required int End { get; init; }

        public List<int> Successors { get; } = [];

        public Condition Condition { get; set; }

        public int TrueBlock { get; set; } = NoBlock;

        public int FalseBlock { get; set; } = NoBlock;
    }

    private readonly record struct Loop(int Header, int Latch, bool[] Body);

    private static readonly HashSet<string> ScalarDestination64Opcodes = new(StringComparer.Ordinal)
    {
        "SMovB64", "SNotB64", "SWqmB64", "SQuadmaskB64", "SBitreplicateB64B32", "SGetpcB64",
        "SAndB64", "SAndn2B64", "SOrB64", "SOrn2B64", "SXorB64", "SNandB64", "SNorB64", "SXnorB64",
        "SLshlB64", "SLshrB64", "SAshrI64", "SBfeU64", "SBfeI64", "SBfmB64", "SCselectB64",
        "SBitset0B64", "SBitset1B64",
    };

    private static readonly HashSet<string> PartialWriteOpcodes = new(StringComparer.Ordinal)
    {
        "SBitset0B32", "SBitset1B32", "SBitset0B64", "SBitset1B64",
    };

    private static readonly string[] SccWriterPrefixes =
    [
        "SCmp", "SBitcmp", "SAdd", "SSub", "SMin", "SMax", "SAbs", "SAnd", "SOr", "SXor", "SNand", "SNor",
        "SXnor", "SNot", "SWqm", "SLshl", "SLshr", "SAshr", "SBfe", "SBcnt",
    ];

    private static readonly HashSet<string> SccKeepers = new(StringComparer.Ordinal)
    {
        "SMovB32", "SMovB64", "SMovkI32", "SCselectB32", "SCselectB64", "SBrevB32", "SFF1I32B32", "SFF1I32B64",
        "SFlbitI32B32", "SFlbitI32B64", "SBitset0B32", "SBitset1B32", "SBitset0B64", "SBitset1B64",
        "SMulI32", "SMulHiU32", "SMulHiI32", "SMulkI32", "SBfmB32", "SBfmB64",
        "SPackLlB32B16", "SPackLhB32B16", "SPackHhB32B16", "SGetpcB64",
    };

    private static readonly HashSet<string> SccReaders = new(StringComparer.Ordinal)
    {
        "SCselectB32", "SCselectB64", "SAddcU32", "SSubbU32", "SCbranchScc0", "SCbranchScc1",
    };

    private static readonly HashSet<string> MaskOperations = new(StringComparer.Ordinal)
    {
        "SMovB32", "SMovB64", "SNotB32", "SNotB64", "SWqmB32", "SWqmB64",
        "SAndB32", "SAndB64", "SAndn2B32", "SAndn2B64", "SOrB32", "SOrB64", "SOrn2B32", "SOrn2B64",
        "SXorB32", "SXorB64", "SNandB32", "SNandB64", "SNorB32", "SNorB64", "SXnorB32", "SXnorB64",
        "SCselectB32", "SCselectB64",
    };

    private static readonly HashSet<string> LaneSelectOpcodes = new(StringComparer.Ordinal)
    {
        "VCndmaskB32", "VAddcU32", "VAddCoCiU32", "VSubbU32", "VSubCoCiU32", "VSubbrevU32", "VSubrevCoCiU32",
    };

    private static readonly HashSet<string> CarryOutOpcodes = new(StringComparer.Ordinal)
    {
        "VAddcU32", "VAddCoCiU32", "VSubbU32", "VSubCoCiU32", "VSubbrevU32", "VSubrevCoCiU32",
        "VAddCoU32", "VSubCoU32", "VSubrevCoU32",
    };

    public static bool IsLaneLocal(Gen5ShaderProgram program, IReadOnlySet<uint> sharedFlatPcs, out string reason)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0)
        {
            reason = "control flow";
            return false;
        }

        var ops = new Op[instructions.Count];
        for (var index = 0; index < ops.Length; index++)
        {
            ops[index] = Normalize(instructions[index], sharedFlatPcs);
        }

        if (!TryBuildBlocks(instructions, ops, out var blocks))
        {
            reason = "control flow";
            return false;
        }

        return new Analysis(ops, blocks).Run(out reason);
    }

    private static Op Normalize(Gen5ShaderInstruction instruction, IReadOnlySet<uint> sharedFlatPcs)
    {
        var opcode = instruction.Opcode;
        var family = instruction.Encoding switch
        {
            Gen5ShaderEncoding.Sop1 => Family.Sop1,
            Gen5ShaderEncoding.Sop2 => Family.Sop2,
            Gen5ShaderEncoding.Sopk => Family.Sopk,
            Gen5ShaderEncoding.Sopc => Family.Sopc,
            Gen5ShaderEncoding.Sopp => Family.Sopp,
            Gen5ShaderEncoding.Smem or Gen5ShaderEncoding.Smrd => Family.Smem,
            Gen5ShaderEncoding.Vop1 => Family.Vop1,
            Gen5ShaderEncoding.Vop2 => Family.Vop2,
            Gen5ShaderEncoding.Vop3 => Family.Vop3,
            Gen5ShaderEncoding.Vop3p => Family.Vop3p,
            Gen5ShaderEncoding.Vopc => Family.Vopc,
            Gen5ShaderEncoding.Mubuf => Family.Mubuf,
            Gen5ShaderEncoding.Mtbuf => Family.Mtbuf,
            Gen5ShaderEncoding.Flat => Family.Flat,
            Gen5ShaderEncoding.Mimg => Family.Mimg,
            Gen5ShaderEncoding.Ds => Family.Ds,
            _ => Family.Other,
        };
        var wide = opcode.Contains("64", StringComparison.Ordinal);
        var mapped = instruction.Sources.Select(Map).ToList();
        var destination = instruction.Destinations.Count > 0 ? Map(instruction.Destinations[0]) : Operand.None;
        var destinationCount = 1;
        var carry = Operand.None;
        var select = -1;
        var writtenStart = -1;
        var writtenCount = 0;
        var refused = RefusedOpcode(instruction, family, sharedFlatPcs);
        List<int>? widths = null;

        switch (family)
        {
            case Family.Sop1:
            case Family.Sop2:
                if (PartialWriteOpcodes.Contains(opcode) && destination.Kind == Kind.Scalar)
                {
                    mapped.Add(destination);
                }

                break;
            case Family.Sopc:
                destination = Operand.None;
                break;
            case Family.Sopk:
            {
                var register = destination;
                if (opcode == "SSetregB32")
                {
                    destination = Operand.None;
                    mapped = [instruction.Sources.Count > 0 ? Map(instruction.Sources[0]) : Operand.None];
                }
                else if (opcode.StartsWith("SWaitcnt", StringComparison.Ordinal))
                {
                    destination = Operand.None;
                    mapped = instruction.Sources.Skip(1).Select(Map).ToList();
                }
                else if (opcode.StartsWith("SCmpk", StringComparison.Ordinal))
                {
                    destination = Operand.None;
                    mapped = [register, new Operand(Kind.Constant)];
                }
                else if (opcode == "SMovkI32")
                {
                    mapped = [new Operand(Kind.Constant)];
                }
                else
                {
                    mapped = [register, new Operand(Kind.Constant)];
                }

                break;
            }
            case Family.Sopp:
                destination = Operand.None;
                mapped = [];
                break;
            case Family.Smem:
                if (instruction.Control is Gen5GlobalMemoryControl { SourceIsScalar: true })
                {
                    destination = Operand.None;
                    widths = mapped.Select((_, index) => index == 0 ? 2 : 1).ToList();
                }
                else
                {
                    destinationCount = Math.Max(instruction.Destinations.Count, 1);
                    var baseWidth = opcode.StartsWith("SBuffer", StringComparison.Ordinal) ? 4 : 2;
                    widths = mapped.Select((_, index) => index == 0 ? baseWidth : 1).ToList();
                }

                break;
            case Family.Vop1:
                break;
            case Family.Vop2:
                if (LaneSelectOpcodes.Contains(opcode))
                {
                    select = 2;
                    if (mapped.Count < 3)
                    {
                        mapped.Add(Operand.Scalar(VccLo));
                    }
                }

                if (CarryOutOpcodes.Contains(opcode))
                {
                    carry = Operand.Scalar(VccLo);
                }

                if (Accumulates(opcode) && destination.Kind == Kind.Vector)
                {
                    mapped.Add(destination);
                }

                break;
            case Family.Vopc:
                mapped = mapped.Take(2).ToList();
                destination = CompareDestination(instruction);
                break;
            case Family.Vop3:
                if (opcode.StartsWith("VCmp", StringComparison.Ordinal))
                {
                    mapped = mapped.Take(2).ToList();
                    destination = CompareDestination(instruction);
                    break;
                }

                mapped = UsedSources(instruction, Vop3SourceCount(instruction));
                if (LaneSelectOpcodes.Contains(opcode))
                {
                    select = 2;
                }

                if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination })
                {
                    carry = Map(Gen5Operand.Scalar(scalarDestination));
                }

                if (Accumulates(opcode) && destination.Kind == Kind.Vector)
                {
                    mapped.Add(destination);
                }

                break;
            case Family.Vop3p:
                mapped = UsedSources(instruction, Vop3pSourceCount(instruction));
                if (Accumulates(opcode) && destination.Kind == Kind.Vector)
                {
                    mapped.Add(destination);
                }

                break;
            case Family.Mubuf:
            case Family.Mtbuf:
                widths = mapped.Select((_, index) => index == 1 ? 4 : 1).ToList();
                if (instruction.Control is Gen5BufferMemoryControl buffer &&
                    !opcode.Contains("Store", StringComparison.Ordinal))
                {
                    writtenStart = (int)buffer.VectorData;
                    writtenCount = (int)Math.Max(buffer.DwordCount, 1);
                }

                destination = Operand.None;
                break;
            case Family.Flat:
                widths = mapped.Select(_ => 2).ToList();
                if (instruction.Destinations.Count > 0)
                {
                    writtenStart = (int)instruction.Destinations[0].Value;
                    writtenCount = instruction.Destinations.Count;
                }

                destination = Operand.None;
                break;
            case Family.Mimg:
                widths = mapped.Select(_ => 1).ToList();
                if (instruction.Control is Gen5RayIntersectControl ray)
                {
                    widths[^1] = 4;
                    writtenStart = (int)ray.VectorData;
                    writtenCount = (int)Gen5RayIntersectControl.ResultDwords;
                }
                else if (instruction.Control is Gen5ImageControl image && widths.Count >= 2)
                {
                    widths[^2] = 8;
                    widths[^1] = UsesSampler(opcode) ? 4 : 0;
                    if (!opcode.StartsWith("ImageStore", StringComparison.Ordinal))
                    {
                        var channels = opcode.StartsWith("ImageGather", StringComparison.Ordinal)
                            ? 4
                            : Math.Max(BitOperations.PopCount(image.Dmask), 1);
                        writtenStart = (int)image.VectorData;
                        writtenCount = channels + 1;
                    }
                }

                destination = Operand.None;
                break;
            default:
                if (destination.Kind == Kind.Vector)
                {
                    writtenStart = destination.Register;
                    writtenCount = instruction.Destinations.Count;
                }

                destination = Operand.None;
                break;
        }

        widths ??= mapped
            .Select((_, index) => index == select ? 2 : SourceWidth(opcode, family, index, wide))
            .ToList();
        var sdwaPartial = instruction.Control is Gen5SdwaControl { DestinationSelect: not 6 } &&
            family is Family.Vop1 or Family.Vop2;
        var resolver = Gen5IrBranchResolver.Instance;
        return new Op
        {
            Pc = instruction.Pc,
            Opcode = opcode,
            Family = family,
            Sources = [.. mapped],
            Widths = [.. widths],
            Destination = destination,
            DestinationCount = destinationCount,
            Carry = carry,
            Select = select,
            LaneFetch = instruction.Control is Gen5DppControl or Gen5Dpp8Control,
            CrossRowFetch = instruction.Control is Gen5DppControl dpp && !LaneLocalDpp(dpp.Control),
            PartialVector = sdwaPartial ||
                (opcode.Contains("16", StringComparison.Ordinal) && !opcode.StartsWith("VPk", StringComparison.Ordinal)),
            WrittenVectorStart = writtenStart,
            WrittenVectorCount = writtenCount,
            Wide = wide,
            Refused = refused,
            IsBranch = resolver.IsBranch(instruction),
            IsConditional = resolver.IsConditional(instruction),
            IsTerminator = Gen5IrBranchResolver.IsTerminator(instruction),
            Condition = opcode switch
            {
                "SCbranchExecz" => Condition.ExecZero,
                "SCbranchExecnz" => Condition.ExecNonZero,
                "SCbranchVccz" => Condition.VccZero,
                "SCbranchVccnz" => Condition.VccNonZero,
                _ when resolver.IsConditional(instruction) => Condition.Other,
                _ => Condition.None,
            },
        };
    }

    private static int SourceWidth(string opcode, Family family, int index, bool wide)
    {
        if (family is not (Family.Vop1 or Family.Vop2 or Family.Vop3 or Family.Vopc))
        {
            return wide ? 2 : 1;
        }

        return opcode switch
        {
            "VMadU64U32" or "VMadI64I32" => index == 2 ? 2 : 1,
            "VLshlrevB64" or "VLshrrevB64" or "VAshrrevI64" => index == 1 ? 2 : 1,
            "VLdexpF64" or "VTrigPreopF64" => index == 0 ? 2 : 1,
            _ when opcode.StartsWith("VCvtF64", StringComparison.Ordinal) => 1,
            _ => wide ? 2 : 1,
        };
    }

    private static Operand Map(Gen5Operand operand) => operand.Kind switch
    {
        Gen5OperandKind.ScalarRegister => operand.Value is < ScalarCount and not Scc
            ? Operand.Scalar((int)operand.Value)
            : Operand.None,
        Gen5OperandKind.VectorRegister => new Operand(Kind.Vector, (int)operand.Value),
        Gen5OperandKind.EncodedConstant => operand.Value switch
        {
            128 => new Operand(Kind.Zero),
            193 => new Operand(Kind.AllOnes),
            251 => new Operand(Kind.VccZ),
            252 => new Operand(Kind.ExecZ),
            253 => new Operand(Kind.Scc),
            125 => Operand.None,
            _ => new Operand(Kind.Constant),
        },
        _ => new Operand(Kind.Constant),
    };

    private static Operand CompareDestination(Gen5ShaderInstruction instruction)
    {
        if (instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            return Operand.Scalar(ExecLo);
        }

        var register = instruction.Control switch
        {
            Gen5SdwaControl { ScalarDestination: { } sdwa } => sdwa,
            Gen5Vop3Control { ScalarDestination: { } vop3 } => vop3,
            _ => (uint)VccLo,
        };
        return register is < ScalarCount and not Scc ? Operand.Scalar((int)register) : Operand.None;
    }

    private static List<Operand> UsedSources(Gen5ShaderInstruction instruction, int count)
    {
        var used = new List<Operand>(instruction.Sources.Count);
        for (var index = 0; index < instruction.Sources.Count; index++)
        {
            var source = instruction.Sources[index];
            if (index < count || source is not { Kind: Gen5OperandKind.ScalarRegister, Value: 0 })
            {
                used.Add(Map(source));
            }
        }

        return used;
    }

    private static int Vop3SourceCount(Gen5ShaderInstruction instruction)
    {
        var opcode = (instruction.Words[0] >> 16) & 0x3FF;
        return opcode switch
        {
            < 0x100 => 2,
            0x101 or 0x128 or 0x129 or 0x12A => 3,
            < 0x140 => 2,
            >= 0x164 and <= 0x16C => 2,
            0x174 => 2,
            < 0x180 => 3,
            < 0x200 => 1,
            >= 0x2FF and <= 0x31F => 2,
            >= 0x360 and <= 0x36C => 2,
            0x376 or 0x37F => 2,
            _ => 3,
        };
    }

    private static int Vop3pSourceCount(Gen5ShaderInstruction instruction)
    {
        var opcode = (instruction.Words[0] >> 16) & 0x7F;
        return opcode is 0x00 or 0x09 or 0x0E or (>= 0x13 and <= 0x19) or (>= 0x20 and <= 0x22) ? 3 : 2;
    }

    private static bool Accumulates(string opcode) =>
        opcode.Contains("Mac", StringComparison.Ordinal) ||
        opcode.StartsWith("VDot2c", StringComparison.Ordinal) ||
        opcode.StartsWith("VDot4c", StringComparison.Ordinal) ||
        opcode.StartsWith("VDot8c", StringComparison.Ordinal);

    private static bool UsesSampler(string opcode) =>
        opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
        opcode.StartsWith("ImageGather", StringComparison.Ordinal) ||
        opcode == "ImageGetLod";

    private static string? RefusedOpcode(Gen5ShaderInstruction instruction, Family family, IReadOnlySet<uint> sharedFlatPcs)
    {
        var opcode = instruction.Opcode;
        switch (opcode)
        {
            case "VReadlaneB32":
                return "V_READLANE";
            case "VWritelaneB32":
                return "V_WRITELANE";
            case "VMbcntLoU32B32":
            case "VMbcntHiU32B32":
                return "V_MBCNT";
            case "SQuadmaskB64":
            case "SBitreplicateB64B32":
                return "lane-mask layout";
            case "SGetpcB64":
            case "SSetpcB64":
            case "SSwappcB64":
            case "SCbranchJoin":
            case "SCbranchGFork":
            case "SCbranchIFork":
                return "program counter";
            case "SSendmsg":
            case "STrap":
                return "wave message";
            case "ImageGetLod":
                return "compute derivatives";
        }

        if (opcode.StartsWith("VMovrel", StringComparison.Ordinal) || opcode.StartsWith("SMovrel", StringComparison.Ordinal))
        {
            return "relative register";
        }

        if (opcode.StartsWith("SSetreg", StringComparison.Ordinal))
        {
            return "wave message";
        }

        if (opcode.StartsWith("SRfe", StringComparison.Ordinal) ||
            opcode.StartsWith("SCall", StringComparison.Ordinal) ||
            opcode.StartsWith("SSubvector", StringComparison.Ordinal))
        {
            return "program counter";
        }

        if (opcode.Contains("Wrexec", StringComparison.Ordinal))
        {
            return "EXEC set from the wave's layout";
        }

        if (opcode.Contains("Raw", StringComparison.Ordinal))
        {
            return "unknown instruction";
        }

        if (family == Family.Smem && opcode.Contains("Atomic", StringComparison.Ordinal))
        {
            return "scalar atomic";
        }

        if (family == Family.Ds || (family == Family.Flat && sharedFlatPcs.Contains(instruction.Pc)))
        {
            return "LDS";
        }

        return null;
    }

    private static bool LaneLocalDpp(uint control) =>
        control <= 0xFF ||
        control is >= 0x101 and <= 0x12F ||
        control is 0x140 or 0x141 ||
        control is >= 0x150 and <= 0x16F;

    private static bool TryBuildBlocks(IReadOnlyList<Gen5ShaderInstruction> instructions, Op[] ops, out Block[] blocks)
    {
        blocks = [];
        var count = ops.Length;
        var indexByPc = new Dictionary<uint, int>(count);
        for (var index = 0; index < count; index++)
        {
            indexByPc[ops[index].Pc] = index;
        }

        var last = instructions[^1];
        var endPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
        var leaders = new bool[count + 1];
        var targets = new int[count];
        leaders[0] = true;
        for (var index = 0; index < count; index++)
        {
            targets[index] = -1;
            var op = ops[index];
            if (op.IsTerminator)
            {
                leaders[index + 1] = true;
                continue;
            }

            if (!op.IsBranch)
            {
                continue;
            }

            if (!Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[index], out var targetPc))
            {
                return false;
            }

            if (indexByPc.TryGetValue(targetPc, out var target))
            {
                targets[index] = target;
                leaders[target] = true;
            }
            else if (targetPc < endPc)
            {
                return false;
            }

            leaders[index + 1] = true;
        }

        var starts = new List<int>();
        for (var index = 0; index < count; index++)
        {
            if (leaders[index])
            {
                starts.Add(index);
            }
        }

        var blockOf = new int[count];
        var built = new Block[starts.Count];
        for (var block = 0; block < starts.Count; block++)
        {
            var start = starts[block];
            var end = block + 1 < starts.Count ? starts[block + 1] : count;
            built[block] = new Block { Start = start, End = end };
            for (var index = start; index < end; index++)
            {
                blockOf[index] = block;
            }
        }

        for (var block = 0; block < built.Length; block++)
        {
            var current = built[block];
            var lastIndex = current.End - 1;
            var op = ops[lastIndex];
            var next = block + 1 < built.Length ? block + 1 : NoBlock;
            if (op.IsTerminator)
            {
                continue;
            }

            if (!op.IsBranch)
            {
                if (next != NoBlock)
                {
                    current.Successors.Add(next);
                }

                continue;
            }

            var targetBlock = targets[lastIndex] >= 0 ? blockOf[targets[lastIndex]] : NoBlock;
            if (!op.IsConditional)
            {
                if (targetBlock != NoBlock)
                {
                    current.Successors.Add(targetBlock);
                }

                continue;
            }

            current.Condition = op.Condition;
            current.TrueBlock = targetBlock;
            current.FalseBlock = next;
            if (targetBlock != NoBlock)
            {
                current.Successors.Add(targetBlock);
            }

            if (next != NoBlock && !current.Successors.Contains(next))
            {
                current.Successors.Add(next);
            }
        }

        blocks = built;
        return true;
    }

    private static bool PairBase(int register) =>
        register >= 0 && register % 2 == 0 && (register < M0 || register == ExecLo);

    private static bool Test(UInt128 set, int register) =>
        register is >= 0 and < ScalarCount && ((set >> register) & UInt128.One) != 0;

    private static UInt128 Bit(int register) =>
        register is >= 0 and < ScalarCount ? UInt128.One << register : UInt128.Zero;

    private static void SetPair(ref UInt128 set, int register, bool value)
    {
        if (!PairBase(register) || register == ExecLo)
        {
            return;
        }

        var pair = Bit(register) | Bit(register + 1);
        set = value ? set | pair : set & ~pair;
    }

    private static bool IsSaveExec(string opcode) =>
        opcode.EndsWith("SaveexecB64", StringComparison.Ordinal) ||
        opcode.EndsWith("SaveexecB32", StringComparison.Ordinal);

    private static bool ScalarDestination64(string opcode) =>
        ScalarDestination64Opcodes.Contains(opcode) || opcode.EndsWith("SaveexecB64", StringComparison.Ordinal);

    private static bool IsMaskOperation(string opcode) =>
        MaskOperations.Contains(opcode) || IsSaveExec(opcode);

    private static bool WritesScc(string opcode)
    {
        if (opcode.EndsWith("F32", StringComparison.Ordinal) || opcode.EndsWith("F16", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in SccWriterPrefixes)
        {
            if (opcode.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompare(Op op) =>
        op.Family == Family.Vopc ||
        (op.Family == Family.Vop3 && op.Opcode.StartsWith("VCmp", StringComparison.Ordinal));

    private static bool WritesExec(Op op)
    {
        var index = op.Destination.ScalarIndex;
        return index is ExecLo or ExecHi || IsSaveExec(op.Opcode);
    }

    private static UInt128 ScalarRange(Operand operand, int count)
    {
        var register = operand.ScalarIndex;
        var set = UInt128.Zero;
        for (var offset = 0; register >= 0 && offset < count && register + offset < ScalarCount; offset++)
        {
            set |= Bit(register + offset);
        }

        return set;
    }

    private static UInt128 ScalarUses(Op op)
    {
        var uses = UInt128.Zero;
        for (var index = 0; index < op.Sources.Length; index++)
        {
            var source = op.Sources[index];
            if (source.Kind == Kind.Scc)
            {
                uses |= Bit(Scc);
            }

            uses |= ScalarRange(source, op.Widths[index]);
        }

        if (SccReaders.Contains(op.Opcode))
        {
            uses |= Bit(Scc);
        }

        if (op.Opcode is "SCbranchVccz" or "SCbranchVccnz")
        {
            uses |= Bit(VccLo) | Bit(VccHi);
        }

        return uses;
    }

    private static UInt128 ScalarDefs(Op op, bool sure)
    {
        var defs = UInt128.Zero;
        switch (op.Family)
        {
            case Family.Sop1:
            case Family.Sop2:
            case Family.Sopk:
            case Family.Sopc:
            {
                var wide = ScalarDestination64(op.Opcode) || (!sure && op.Wide);
                if (!sure || !PartialWriteOpcodes.Contains(op.Opcode))
                {
                    defs |= ScalarRange(op.Destination, wide ? 2 : 1);
                }

                if (IsSaveExec(op.Opcode))
                {
                    defs |= Bit(ExecLo);
                    if (op.Wide)
                    {
                        defs |= Bit(ExecHi);
                    }
                }

                if (!sure || WritesScc(op.Opcode))
                {
                    defs |= Bit(Scc);
                }

                break;
            }
            case Family.Smem:
                defs |= ScalarRange(op.Destination, op.DestinationCount);
                break;
            case Family.Vopc:
                defs |= ScalarRange(op.Destination, 2);
                break;
            case Family.Vop1:
            case Family.Vop2:
            case Family.Vop3:
            case Family.Vop3p:
                if (op.Opcode is "VReadfirstlaneB32" or "VReadlaneB32")
                {
                    defs |= ScalarRange(op.Destination, 1);
                }
                else if (op.Destination.Kind == Kind.Scalar)
                {
                    defs |= ScalarRange(op.Destination, 2);
                }

                defs |= ScalarRange(op.Carry, 2);
                break;
        }

        return defs;
    }

    private sealed class Analysis(Op[] ops, Block[] blocks)
    {
        private readonly Op[] _ops = ops;
        private readonly Block[] _blocks = blocks;
        private readonly List<Loop> _loops = [];

        public bool Run(out string reason)
        {
            if (!FindLoops())
            {
                reason = "control flow";
                return false;
            }

            var count = _blocks.Length;
            var input = new State[count];
            var reached = new bool[count];
            var queued = new bool[count];
            input[0].Mask = Bit(ExecLo) | Bit(ExecHi);
            input[0].Active = true;
            reached[0] = true;
            queued[0] = true;
            var work = new Queue<int>();
            work.Enqueue(0);
            for (var steps = 0; work.TryDequeue(out var block); steps++)
            {
                if (steps > MaxSteps)
                {
                    reason = "analysis did not converge";
                    return false;
                }

                queued[block] = false;
                var state = input[block];
                for (var index = _blocks[block].Start; index < _blocks[block].End; index++)
                {
                    Transfer(_ops[index], ref state);
                }

                foreach (var successor in _blocks[block].Successors)
                {
                    var edge = OnEdge(_blocks[block], successor, state);
                    if (reached[successor])
                    {
                        var merged = input[successor];
                        merged.Join(edge);
                        if (merged.Equals(input[successor]))
                        {
                            continue;
                        }

                        edge = merged;
                    }

                    reached[successor] = true;
                    input[successor] = edge;
                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        work.Enqueue(successor);
                    }
                }
            }

            var output = new State[count];
            for (var block = 0; block < count; block++)
            {
                if (!reached[block])
                {
                    continue;
                }

                var state = input[block];
                for (var index = _blocks[block].Start; index < _blocks[block].End; index++)
                {
                    if (Check(block, index, state) is { } refused)
                    {
                        reason = $"{refused} (0x{_ops[index].Pc:X})";
                        return false;
                    }

                    Transfer(_ops[index], ref state);
                }

                output[block] = state;
            }

            return WaveBranches(output, reached, out reason);
        }

        private bool FindLoops()
        {
            var count = _blocks.Length;
            var reachable = new bool[count];
            var order = new List<int>();
            var onStack = new bool[count];
            var retreating = new List<(int From, int To)>();
            var stack = new Stack<(int Block, int Next)>();
            stack.Push((0, 0));
            reachable[0] = true;
            onStack[0] = true;
            while (stack.Count > 0)
            {
                var (block, next) = stack.Pop();
                var successors = _blocks[block].Successors;
                if (next < successors.Count)
                {
                    stack.Push((block, next + 1));
                    var successor = successors[next];
                    if (onStack[successor])
                    {
                        retreating.Add((block, successor));
                    }
                    else if (!reachable[successor])
                    {
                        reachable[successor] = true;
                        onStack[successor] = true;
                        stack.Push((successor, 0));
                    }

                    continue;
                }

                onStack[block] = false;
                order.Add(block);
            }

            var predecessors = new List<int>[count];
            for (var block = 0; block < count; block++)
            {
                predecessors[block] = [];
            }

            for (var block = 0; block < count; block++)
            {
                foreach (var successor in _blocks[block].Successors)
                {
                    predecessors[successor].Add(block);
                }
            }

            var dominators = new bool[count][];
            for (var block = 0; block < count; block++)
            {
                dominators[block] = new bool[count];
                if (block == 0)
                {
                    dominators[block][0] = true;
                }
                else
                {
                    Array.Fill(dominators[block], true);
                }
            }

            order.Reverse();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var block in order)
                {
                    if (block == 0)
                    {
                        continue;
                    }

                    var next = new bool[count];
                    Array.Fill(next, true);
                    var any = false;
                    foreach (var predecessor in predecessors[block])
                    {
                        if (!reachable[predecessor])
                        {
                            continue;
                        }

                        any = true;
                        for (var other = 0; other < count; other++)
                        {
                            next[other] &= dominators[predecessor][other];
                        }
                    }

                    if (!any)
                    {
                        Array.Fill(next, false);
                    }

                    next[block] = true;
                    if (!next.AsSpan().SequenceEqual(dominators[block]))
                    {
                        dominators[block] = next;
                        changed = true;
                    }
                }
            }

            foreach (var (from, to) in retreating)
            {
                if (!dominators[from][to])
                {
                    return false;
                }

                var body = new bool[count];
                body[to] = true;
                var pending = new Stack<int>();
                if (!body[from])
                {
                    body[from] = true;
                    pending.Push(from);
                }

                while (pending.TryPop(out var block))
                {
                    foreach (var predecessor in predecessors[block])
                    {
                        if (!body[predecessor])
                        {
                            body[predecessor] = true;
                            pending.Push(predecessor);
                        }
                    }
                }

                _loops.Add(new Loop(to, from, body));
            }

            return true;
        }

        private static bool IsMask(Op op, int source, in State state) =>
            (ScalarRange(op.Sources[source], op.Widths[source]) & state.Mask) != 0;

        private static void WriteScalar(Operand operand, int count, bool mask, ref State state)
        {
            var index = operand.ScalarIndex;
            for (var offset = 0; index >= 0 && offset < count && index + offset < ScalarCount; offset++)
            {
                var register = index + offset;
                state.Mask = mask || register is ExecLo or ExecHi
                    ? state.Mask | Bit(register)
                    : state.Mask & ~Bit(register);
            }
        }

        private static bool MaskIn(Operand operand, int count, in State state) =>
            (ScalarRange(operand, count) & state.Mask) != 0;

        private static bool UniformSources(Op op, in State state)
        {
            if (op.LaneFetch)
            {
                return false;
            }

            for (var index = 0; index < op.Sources.Length; index++)
            {
                var source = op.Sources[index];
                switch (source.Kind)
                {
                    case Kind.Vector:
                        if (!state.Uniform(source.Register) || (op.Widths[index] > 1 && !state.Uniform(source.Register + 1)))
                        {
                            return false;
                        }

                        break;
                    case Kind.Zero:
                    case Kind.AllOnes:
                    case Kind.Constant:
                        break;
                    default:
                        if (source.ScalarIndex < 0 || IsMask(op, index, state))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }

        private static MaskFacts PairFacts(int register, in State state)
        {
            if (register == ExecLo)
            {
                return new MaskFacts(true, true, state.Active);
            }

            if (!PairBase(register))
            {
                return default;
            }

            return new MaskFacts(
                Test(state.Covers, register) && Test(state.Covers, register + 1),
                Test(state.Within, register) && Test(state.Within, register + 1),
                Test(state.Nonzero, register) && Test(state.Nonzero, register + 1));
        }

        private static MaskFacts Facts(Operand operand, in State state) => operand.Kind switch
        {
            Kind.Zero => new MaskFacts(true, true, false),
            Kind.AllOnes => new MaskFacts(true, false, true),
            Kind.Scalar => PairFacts(operand.Register, state),
            _ => default,
        };

        private static bool LaneMask(Operand operand, in State state)
        {
            if (operand.Kind is Kind.Zero or Kind.AllOnes)
            {
                return true;
            }

            var register = operand.ScalarIndex;
            if (register == ExecLo)
            {
                return true;
            }

            return PairBase(register) && Test(state.Lanes, register) && Test(state.Lanes, register + 1);
        }

        private static bool UpdateMaskFacts(Op op, in State before, ref State state)
        {
            var written = ScalarDefs(op, false);
            var writesExec = Test(written, ExecLo) || Test(written, ExecHi);
            var a = Facts(op.Source(0), before);
            var b = Facts(op.Source(1), before);
            var destination = op.Destination.ScalarIndex;
            var value = default(MaskFacts);
            var pair = true;
            switch (op.Opcode)
            {
                case "SMovB64":
                    value = a;
                    break;
                case "SCselectB64":
                    value = new MaskFacts(a.Covers && b.Covers, a.Within && b.Within, a.Nonzero && b.Nonzero);
                    break;
                case "SAndB64":
                    value = new MaskFacts(a.Covers && b.Covers, a.Within || b.Within, false);
                    break;
                case "SOrB64":
                    value = new MaskFacts(a.Covers && b.Covers, a.Within && b.Within, a.Nonzero || b.Nonzero);
                    break;
                case "SAndn2B64":
                    value = new MaskFacts(false, a.Within, false);
                    break;
                default:
                    pair = IsCompare(op);
                    if (pair)
                    {
                        value = new MaskFacts(UniformSources(op, before), true, false);
                    }

                    break;
            }

            var save = IsSaveExec(op.Opcode);
            var narrowing = save
                ? op.Opcode is "SAndSaveexecB64" or "SAndn1SaveexecB64"
                : destination == ExecLo && pair && value.Within;
            state.Covers &= ~written;
            state.Within &= ~written;
            state.Nonzero &= ~written;
            if (writesExec)
            {
                if (!narrowing)
                {
                    state.Covers = UInt128.Zero;
                }

                state.Within = UInt128.Zero;
                state.Active = !save && destination == ExecLo && pair && value.Nonzero;
                if (!save && destination == ExecLo)
                {
                    switch (op.Opcode)
                    {
                        case "SMovB64":
                            SetPair(ref state.Covers, op.Source(0).ScalarIndex, true);
                            SetPair(ref state.Within, op.Source(0).ScalarIndex, true);
                            break;
                        case "SAndB64":
                            SetPair(ref state.Covers, op.Source(0).ScalarIndex, true);
                            SetPair(ref state.Covers, op.Source(1).ScalarIndex, true);
                            break;
                        case "SAndn2B64":
                            SetPair(ref state.Covers, op.Source(0).ScalarIndex, true);
                            break;
                        case "SOrB64":
                            SetPair(ref state.Within, op.Source(0).ScalarIndex, true);
                            SetPair(ref state.Within, op.Source(1).ScalarIndex, true);
                            break;
                    }
                }
            }

            if (save)
            {
                SetPair(ref state.Covers, destination, narrowing);
                SetPair(ref state.Nonzero, destination, before.Active);
            }
            else if (pair && destination != ExecLo)
            {
                SetPair(ref state.Covers, destination, value.Covers);
                SetPair(ref state.Within, destination, value.Within);
                SetPair(ref state.Nonzero, destination, value.Nonzero);
            }

            return writesExec && narrowing;
        }

        private static void Transfer(Op op, ref State state)
        {
            var before = state;
            var narrowed = UpdateMaskFacts(op, before, ref state);
            if (WritesExec(op) && !narrowed)
            {
                state.UniformLow = UInt128.Zero;
                state.UniformHigh = UInt128.Zero;
            }

            state.Lanes &= ~ScalarDefs(op, false);
            switch (op.Family)
            {
                case Family.Sop1:
                case Family.Sop2:
                case Family.Sopk:
                case Family.Sopc:
                {
                    var mask = false;
                    for (var source = 0; source < op.Sources.Length; source++)
                    {
                        mask = mask || IsMask(op, source, before);
                    }

                    if (SccReaders.Contains(op.Opcode))
                    {
                        mask = mask || before.SccMask;
                    }

                    var resultMask = mask && IsMaskOperation(op.Opcode);
                    if (IsSaveExec(op.Opcode))
                    {
                        WriteScalar(op.Destination, 2, true, ref state);
                        SetPair(ref state.Lanes, op.Destination.ScalarIndex, op.Wide);
                        state.SccMask = true;
                        break;
                    }

                    if (op.Family != Family.Sopc && op.Destination.Kind == Kind.Scalar)
                    {
                        WriteScalar(
                            op.Destination,
                            ScalarDestination64(op.Opcode) ? 2 : 1,
                            resultMask,
                            ref state);
                        var lanes = IsMaskOperation(op.Opcode) && op.Wide;
                        foreach (var source in op.Sources)
                        {
                            lanes = lanes && LaneMask(source, before);
                        }

                        SetPair(ref state.Lanes, op.Destination.ScalarIndex, lanes);
                    }

                    if (WritesScc(op.Opcode))
                    {
                        state.SccMask = mask;
                    }
                    else if (!SccKeepers.Contains(op.Opcode))
                    {
                        state.SccMask = before.SccMask || mask;
                    }

                    break;
                }
                case Family.Smem:
                    WriteScalar(op.Destination, op.DestinationCount, false, ref state);
                    break;
                case Family.Vopc:
                    WriteScalar(op.Destination, 2, true, ref state);
                    SetPair(ref state.Lanes, op.Destination.ScalarIndex, true);
                    break;
                case Family.Vop1:
                case Family.Vop2:
                case Family.Vop3:
                case Family.Vop3p:
                {
                    if (op.Opcode is "VReadfirstlaneB32" or "VReadlaneB32")
                    {
                        WriteScalar(op.Destination, 1, false, ref state);
                        break;
                    }

                    if (op.Family == Family.Vop3 && op.Destination.Kind == Kind.Scalar)
                    {
                        WriteScalar(op.Destination, 2, true, ref state);
                        SetPair(ref state.Lanes, op.Destination.ScalarIndex, IsCompare(op));
                        break;
                    }

                    if (op.Carry.Kind == Kind.Scalar)
                    {
                        WriteScalar(op.Carry, 2, true, ref state);
                        SetPair(ref state.Lanes, op.Carry.ScalarIndex, true);
                    }

                    if (op.Destination.Kind == Kind.Vector)
                    {
                        var uniform = op.Select < 0 &&
                            op.Opcode is not ("VPermlane16B32" or "VPermlanex16B32") &&
                            UniformSources(op, before);
                        var register = op.Destination.Register;
                        state.SetUniform(register, uniform && (!op.PartialVector || before.Uniform(register)));
                        if (op.Wide)
                        {
                            state.SetUniform(register + 1, uniform && before.Uniform(register + 1));
                        }
                    }

                    break;
                }
                default:
                    for (var offset = 0; op.WrittenVectorStart >= 0 && offset < op.WrittenVectorCount; offset++)
                    {
                        state.SetUniform(op.WrittenVectorStart + offset, false);
                    }

                    break;
            }
        }

        private static State OnEdge(Block block, int successor, State state)
        {
            if (block.Condition == Condition.None || block.TrueBlock == block.FalseBlock)
            {
                return state;
            }

            var vccWithin = PairFacts(VccLo, state).Within;
            switch (block.Condition)
            {
                case Condition.ExecZero:
                    state.Active = state.Active || successor == block.FalseBlock;
                    break;
                case Condition.ExecNonZero:
                    state.Active = state.Active || successor == block.TrueBlock;
                    break;
                case Condition.VccZero:
                    state.Active = state.Active || (vccWithin && successor == block.FalseBlock);
                    break;
                case Condition.VccNonZero:
                    state.Active = state.Active || (vccWithin && successor == block.TrueBlock);
                    break;
            }

            return state;
        }

        private string? Check(int block, int index, in State state)
        {
            var op = _ops[index];
            if (op.Refused is { } refused)
            {
                return refused;
            }

            var written = ScalarDefs(op, false);
            var sop = op.Family is Family.Sop1 or Family.Sop2;
            if ((Test(written, ExecLo) || Test(written, ExecHi)) &&
                !IsCompare(op) &&
                !(sop && IsMaskOperation(op.Opcode) && op.Wide))
            {
                return "EXEC set from the wave's layout";
            }

            if (op.CrossRowFetch)
            {
                return "cross-row DPP";
            }

            foreach (var source in op.Sources)
            {
                if (source.Kind is Kind.VccZ or Kind.ExecZ)
                {
                    return "VCCZ/EXECZ as data";
                }

                if (source.Kind == Kind.Scc && state.SccMask)
                {
                    return "SCC made from a lane mask";
                }
            }

            if (state.SccMask && SccReaders.Contains(op.Opcode))
            {
                return "SCC made from a lane mask";
            }

            switch (op.Family)
            {
                case Family.Sop1:
                case Family.Sop2:
                case Family.Sopk:
                case Family.Sopc:
                {
                    var mask = false;
                    for (var source = 0; source < op.Sources.Length; source++)
                    {
                        mask = mask || IsMask(op, source, state);
                    }

                    if (mask && !IsMaskOperation(op.Opcode))
                    {
                        return "a lane mask used as a scalar value";
                    }

                    if (op.Destination.ScalarIndex is ExecLo or ExecHi || IsSaveExec(op.Opcode))
                    {
                        var layout = !op.Wide;
                        foreach (var source in op.Sources)
                        {
                            layout = layout || !LaneMask(source, state);
                        }

                        if (layout)
                        {
                            return "EXEC set from the wave's layout";
                        }
                    }

                    break;
                }
                case Family.Sopp:
                    if (op.Opcode is "SCbranchVccz" or "SCbranchVccnz" &&
                        (!PairFacts(VccLo, state).Covers || !Test(state.Lanes, VccLo) || !Test(state.Lanes, VccHi)))
                    {
                        return "a branch on VCC without EXEC's lanes";
                    }

                    break;
                case Family.Smem:
                case Family.Mubuf:
                case Family.Mtbuf:
                case Family.Flat:
                case Family.Mimg:
                    for (var source = 0; source < op.Sources.Length; source++)
                    {
                        if (MaskIn(op.Sources[source], op.Widths[source], state))
                        {
                            return "a lane mask used as an address or resource";
                        }
                    }

                    break;
                case Family.Vop1:
                case Family.Vop2:
                case Family.Vop3:
                case Family.Vop3p:
                case Family.Vopc:
                {
                    for (var source = 0; source < op.Sources.Length; source++)
                    {
                        if (source != op.Select && IsMask(op, source, state))
                        {
                            return "a lane mask broadcast to the lanes";
                        }
                    }

                    if (op.Select >= 0 && !LaneMask(op.Source(op.Select), state))
                    {
                        return "a lane select from the wave's layout";
                    }

                    if (op.Opcode == "VReadfirstlaneB32")
                    {
                        if (!state.Active)
                        {
                            return "V_READFIRSTLANE where a half may have no active lane";
                        }

                        var source = op.Source(0);
                        var uniform = source.Kind == Kind.Vector
                            ? state.Uniform(source.Register)
                            : op.Sources.Length == 0 || !IsMask(op, 0, state);
                        if (!uniform && !Waterfall(block, index))
                        {
                            return "V_READFIRSTLANE of a lane value outside a waterfall loop";
                        }
                    }

                    break;
                }
            }

            return null;
        }

        private bool Waterfall(int block, int index)
        {
            var head = _ops[index];
            if (head.Source(0).Kind != Kind.Vector)
            {
                return false;
            }

            var value = head.Source(0).Register;
            var scalar = head.Destination.ScalarIndex;
            if (scalar < 0)
            {
                return false;
            }

            var compareMask = -1;
            for (var position = index + 1; position < _blocks[block].End; position++)
            {
                var op = _ops[position];
                if (compareMask < 0)
                {
                    if (!op.Opcode.StartsWith("VCmpEq", StringComparison.Ordinal) &&
                        !op.Opcode.StartsWith("VCmpxEq", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var wide = op.Wide;
                    bool Matches(Operand vector, Operand uniform)
                    {
                        if (vector.Kind != Kind.Vector || uniform.ScalarIndex < 0)
                        {
                            return false;
                        }

                        var vectorDelta = value - vector.Register;
                        var scalarDelta = scalar - uniform.ScalarIndex;
                        return vectorDelta == scalarDelta && vectorDelta >= 0 && vectorDelta <= (wide ? 1 : 0);
                    }

                    if (!Matches(op.Source(0), op.Source(1)) && !Matches(op.Source(1), op.Source(0)))
                    {
                        continue;
                    }

                    compareMask = op.Destination.ScalarIndex;
                    if (compareMask == ExecLo)
                    {
                        break;
                    }

                    if (compareMask < 0)
                    {
                        return false;
                    }

                    continue;
                }

                if (IsSaveExec(op.Opcode) && op.Source(0).ScalarIndex == compareMask)
                {
                    compareMask = ExecLo;
                    break;
                }
            }

            if (compareMask != ExecLo)
            {
                return false;
            }

            foreach (var loop in _loops)
            {
                if (!loop.Body[block] && loop.Header != block)
                {
                    continue;
                }

                var latch = _blocks[loop.Latch];
                if (latch.End > latch.Start && _ops[latch.End - 1].Opcode == "SCbranchExecnz")
                {
                    return true;
                }
            }

            return false;
        }

        private static string? SymTransfer(Op op, Sym[] syms)
        {
            if (op.Opcode == "SBarrier")
            {
                return "a barrier";
            }

            var written = ScalarDefs(op, false);
            if (written == UInt128.Zero)
            {
                return null;
            }

            var destination = op.Destination.ScalarIndex;
            Sym Pair(Operand operand)
            {
                if (operand.Kind == Kind.Zero)
                {
                    return Sym.Zero;
                }

                if (operand.Kind == Kind.AllOnes)
                {
                    return Sym.Ones;
                }

                var register = operand.ScalarIndex;
                if (!PairBase(register) || syms[register] != syms[register + 1])
                {
                    return Sym.Unknown;
                }

                return syms[register] == Sym.Original && register != destination ? Sym.Unknown : syms[register];
            }

            var a = Pair(op.Source(0));
            var b = Pair(op.Source(1));
            var e = syms[ExecLo] == syms[ExecHi] ? syms[ExecLo] : Sym.Unknown;
            var value = Sym.Unknown;
            var exec = Sym.Unknown;
            switch (op.Opcode)
            {
                case "SMovB64":
                    value = a;
                    break;
                case "SCselectB64":
                    value = a == b ? a : Sym.Unknown;
                    break;
                case "SNotB64":
                    value = Not(a);
                    break;
                case "SWqmB64":
                    value = a is Sym.Zero or Sym.Ones ? a : Sym.Unknown;
                    break;
                case "SAndB64":
                    value = And(a, b);
                    break;
                case "SOrB64":
                    value = Or(a, b);
                    break;
                case "SXorB64":
                    value = Xor(a, b);
                    break;
                case "SAndn2B64":
                    value = And(a, Not(b));
                    break;
                case "SOrn2B64":
                    value = Or(a, Not(b));
                    break;
                case "SNandB64":
                    value = Not(And(a, b));
                    break;
                case "SNorB64":
                    value = Not(Or(a, b));
                    break;
                case "SXnorB64":
                    value = Not(Xor(a, b));
                    break;
                case "SAndSaveexecB64":
                    value = e;
                    exec = And(a, e);
                    break;
                case "SAndn1SaveexecB64":
                    value = e;
                    exec = And(Not(a), e);
                    break;
                case "SAndn2SaveexecB64":
                    value = e;
                    exec = And(a, Not(e));
                    break;
                case "SOrSaveexecB64":
                    value = e;
                    exec = Or(a, e);
                    break;
                case "SOrn1SaveexecB64":
                    value = e;
                    exec = Or(Not(a), e);
                    break;
                case "SOrn2SaveexecB64":
                    value = e;
                    exec = Or(a, Not(e));
                    break;
                case "SXorSaveexecB64":
                    value = e;
                    exec = Xor(a, e);
                    break;
                case "SNandSaveexecB64":
                    value = e;
                    exec = Not(And(a, e));
                    break;
                case "SNorSaveexecB64":
                    value = e;
                    exec = Not(Or(a, e));
                    break;
                case "SXnorSaveexecB64":
                    value = e;
                    exec = Not(Xor(a, e));
                    break;
                default:
                    if (IsCompare(op))
                    {
                        value = e == Sym.Zero ? Sym.Zero : Sym.Unknown;
                    }

                    break;
            }

            for (var register = 0; register < ScalarCount; register++)
            {
                if (Test(written, register))
                {
                    syms[register] = Sym.Unknown;
                }
            }

            if (IsSaveExec(op.Opcode))
            {
                syms[ExecLo] = exec;
                syms[ExecHi] = exec;
            }

            if (PairBase(destination) && (ScalarDestination64(op.Opcode) || IsCompare(op)))
            {
                syms[destination] = value;
                syms[destination + 1] = value;
            }

            return syms[ExecLo] != Sym.Zero || syms[ExecHi] != Sym.Zero ? "EXEC gaining a half's lanes" : null;
        }

        private static Sym Not(Sym value) => value switch
        {
            Sym.Zero => Sym.Ones,
            Sym.Ones => Sym.Zero,
            _ => Sym.Unknown,
        };

        private static Sym And(Sym a, Sym b) =>
            a == Sym.Zero || b == Sym.Zero ? Sym.Zero
            : a == Sym.Ones ? b
            : b == Sym.Ones ? a
            : a == b ? a
            : Sym.Unknown;

        private static Sym Or(Sym a, Sym b) =>
            a == Sym.Ones || b == Sym.Ones ? Sym.Ones
            : a == Sym.Zero ? b
            : b == Sym.Zero ? a
            : a == b ? a
            : Sym.Unknown;

        private static Sym Xor(Sym a, Sym b) =>
            a == Sym.Zero ? b
            : b == Sym.Zero ? a
            : a == Sym.Ones ? Not(b)
            : b == Sym.Ones ? Not(a)
            : Sym.Unknown;

        private bool Ways(int seed, int merge, Sym[] entry, Sym[] atMerge, out string? reason)
        {
            reason = null;
            Array.Fill(atMerge, Sym.Unknown);
            if (seed == merge)
            {
                entry.CopyTo(atMerge, 0);
                return true;
            }

            static bool JoinInto(Sym[] into, Sym[] from)
            {
                var changed = false;
                for (var register = 0; register < into.Length; register++)
                {
                    if (into[register] != from[register] && into[register] != Sym.Unknown)
                    {
                        into[register] = Sym.Unknown;
                        changed = true;
                    }
                }

                return changed;
            }

            var count = _blocks.Length;
            var merged = false;
            var input = new Sym[count][];
            var queued = new bool[count];
            var work = new Queue<int>();
            input[seed] = (Sym[])entry.Clone();
            queued[seed] = true;
            work.Enqueue(seed);
            while (work.TryDequeue(out var block))
            {
                queued[block] = false;
                var syms = (Sym[])input[block].Clone();
                for (var index = _blocks[block].Start; index < _blocks[block].End; index++)
                {
                    if (SymTransfer(_ops[index], syms) is { } refused)
                    {
                        reason = $"{refused} between a lane-mask branch and its merge (0x{_ops[index].Pc:X})";
                        return false;
                    }
                }

                foreach (var successor in _blocks[block].Successors)
                {
                    if (successor == merge)
                    {
                        if (!merged)
                        {
                            syms.CopyTo(atMerge, 0);
                        }
                        else
                        {
                            JoinInto(atMerge, syms);
                        }

                        merged = true;
                        continue;
                    }

                    if (input[successor] is null)
                    {
                        input[successor] = (Sym[])syms.Clone();
                    }
                    else if (!JoinInto(input[successor], syms))
                    {
                        continue;
                    }

                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        work.Enqueue(successor);
                    }
                }
            }

            return true;
        }

        private bool WaveBranches(State[] output, bool[] reached, out string reason)
        {
            var count = _blocks.Length;
            var use = new UInt128[count];
            var def = new UInt128[count];
            var liveIn = new UInt128[count];
            for (var block = 0; block < count; block++)
            {
                for (var index = _blocks[block].Start; index < _blocks[block].End; index++)
                {
                    use[block] |= ScalarUses(_ops[index]) & ~def[block];
                    def[block] |= ScalarDefs(_ops[index], true);
                }
            }

            for (var changed = true; changed;)
            {
                changed = false;
                for (var block = count - 1; block >= 0; block--)
                {
                    var liveOut = UInt128.Zero;
                    foreach (var successor in _blocks[block].Successors)
                    {
                        liveOut |= liveIn[successor];
                    }

                    var live = use[block] | (liveOut & ~def[block]);
                    if (live != liveIn[block])
                    {
                        liveIn[block] = live;
                        changed = true;
                    }
                }
            }

            var post = new bool[count][];
            for (var block = 0; block < count; block++)
            {
                post[block] = new bool[count];
                if (_blocks[block].Successors.Count == 0)
                {
                    post[block][block] = true;
                }
                else
                {
                    Array.Fill(post[block], true);
                }
            }

            for (var changed = true; changed;)
            {
                changed = false;
                for (var block = count - 1; block >= 0; block--)
                {
                    if (_blocks[block].Successors.Count == 0)
                    {
                        continue;
                    }

                    var next = new bool[count];
                    Array.Fill(next, true);
                    foreach (var successor in _blocks[block].Successors)
                    {
                        for (var other = 0; other < count; other++)
                        {
                            next[other] &= post[successor][other];
                        }
                    }

                    next[block] = true;
                    if (!next.AsSpan().SequenceEqual(post[block]))
                    {
                        post[block] = next;
                        changed = true;
                    }
                }
            }

            var postCounts = post.Select(set => set.Count(member => member)).ToArray();
            var half = new Sym[ScalarCount];
            var wave = new Sym[ScalarCount];
            for (var block = 0; block < count; block++)
            {
                var current = _blocks[block];
                if (!reached[block] || current.Condition == Condition.None || current.TrueBlock == current.FalseBlock)
                {
                    continue;
                }

                bool vcc;
                bool zeroTaken;
                switch (current.Condition)
                {
                    case Condition.ExecZero:
                        vcc = false;
                        zeroTaken = true;
                        break;
                    case Condition.ExecNonZero:
                        vcc = false;
                        zeroTaken = false;
                        break;
                    case Condition.VccZero:
                        vcc = true;
                        zeroTaken = true;
                        break;
                    case Condition.VccNonZero:
                        vcc = true;
                        zeroTaken = false;
                        break;
                    default:
                        continue;
                }

                var halfWay = zeroTaken ? current.TrueBlock : current.FalseBlock;
                var waveWay = zeroTaken ? current.FalseBlock : current.TrueBlock;
                if (halfWay == NoBlock || waveWay == NoBlock)
                {
                    reason = "control flow";
                    return false;
                }

                var members = 0;
                var common = new bool[count];
                for (var other = 0; other < count; other++)
                {
                    common[other] = post[halfWay][other] && post[waveWay][other];
                    if (common[other])
                    {
                        members++;
                    }
                }

                var merge = count;
                for (var other = 0; other < count && merge == count; other++)
                {
                    if (common[other] && postCounts[other] == members)
                    {
                        merge = other;
                    }
                }

                var entry = new Sym[ScalarCount];
                entry[ExecLo] = Sym.Zero;
                entry[ExecHi] = Sym.Zero;
                if (vcc)
                {
                    entry[VccLo] = Sym.Zero;
                    entry[VccHi] = Sym.Zero;
                }

                for (var register = 0; register < M0; register += 2)
                {
                    if (PairFacts(register, output[block]).Within)
                    {
                        entry[register] = Sym.Zero;
                        entry[register + 1] = Sym.Zero;
                    }
                }

                if (!Ways(halfWay, merge, entry, half, out var halfReason))
                {
                    reason = halfReason!;
                    return false;
                }

                if (!Ways(waveWay, merge, entry, wave, out var waveReason))
                {
                    reason = waveReason!;
                    return false;
                }

                if (merge == count)
                {
                    continue;
                }

                var escaping = new List<string>();
                for (var register = 0; register < ScalarCount; register++)
                {
                    if (Test(liveIn[merge], register) && (half[register] == Sym.Unknown || half[register] != wave[register]))
                    {
                        escaping.Add(ScalarName(register));
                    }
                }

                if (escaping.Count != 0)
                {
                    reason = $"scalar state escapes a lane-mask branch (0x{_ops[current.End - 1].Pc:X}: {string.Join(' ', escaping)})";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static string ScalarName(int register) => register switch
        {
            VccLo => "vcc_lo",
            VccHi => "vcc_hi",
            M0 => "m0",
            Scc => "scc",
            ExecLo => "exec_lo",
            ExecHi => "exec_hi",
            _ => $"s{register}",
        };
    }
}
