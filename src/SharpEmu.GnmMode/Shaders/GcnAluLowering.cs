// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public static class GcnAluLowering
{
    internal const string Prefix = "Gcn";
    internal const string CompareExec = Prefix + "Cmpx";
    private const uint Exec = 126;
    private const uint Vcc = 106;
    private const uint FloatMax = 0x7F7FFFFF;
    private const uint NegativeFloatMax = 0xFF7FFFFF;
    private static readonly Gen5Operand Zero = Gen5Operand.Source(128);
    private static readonly Gen5Operand One = Gen5Operand.Source(242);
    private static readonly Gen5Operand TwentyFour = Gen5Operand.Source(152);

    internal static bool IsLowered(string opcode) => opcode.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryLower(Gen5ShaderProgram original, IEnumerable<Gen5ShaderInstruction> registerContext,
        out Gen5ShaderProgram program, out string error)
    {
        program = original;
        error = "";
        if (!original.Instructions.Any(instruction => IsLowered(instruction.Opcode)))
            return true;
        var (scalarUsed, vectorUsed) = GcnProgramRewriter.UsedRegisters(registerContext);
        var scalar = GcnProgramRewriter.FindFree(scalarUsed, 2);
        var vector = GcnProgramRewriter.FindFree(vectorUsed, 2);
        if (scalar < 0 || vector < 0)
        {
            error = "gcn-alu-lowering-has-no-free-registers";
            return false;
        }
        return GcnProgramRewriter.TryRewrite(original,
            (Gen5ShaderInstruction instruction, Action<Gen5ShaderInstruction> add, out string failure) =>
            {
                failure = "";
                if (!IsLowered(instruction.Opcode)) return false;
                if (!Expand(instruction, (uint)scalar, (uint)vector, add))
                    failure = $"unsupported-gcn-lowering {instruction.Opcode} pc=0x{instruction.Pc:X}";
                return true;
            }, "gcn-alu-lowering-branch-overflow", out program, out error);
    }

    private static bool Expand(Gen5ShaderInstruction original, uint mask, uint temporary, Action<Gen5ShaderInstruction> add)
    {
        var control = original.Control as Gen5Vop3Control;
        var abs = control?.AbsoluteMask ?? 0;
        var neg = control?.NegateMask ?? 0;
        var omod = control?.OutputModifier ?? 0;
        var clamp = control?.Clamp ?? false;
        var finish = omod != 0 || clamp;
        var sources = original.Sources;
        var destination = original.Destinations.Count != 0 ? original.Destinations[0] : default;
        var t0 = Gen5Operand.Vector(temporary);
        var t1 = Gen5Operand.Vector(temporary + 1);
        var selector = Gen5Operand.Scalar(mask);

        Gen5ShaderInstruction Op(Gen5ShaderEncoding encoding, string name, Gen5Operand[] inputs, Gen5Operand[] outputs,
            Gen5InstructionControl? opControl) =>
            new(0, encoding, name, encoding == Gen5ShaderEncoding.Vop3 ? [0, 0] : [0], inputs, outputs, opControl)
            {
                AddressOffset = original.ProgramOffset,
            };
        void Vop3(string name, Gen5Operand[] inputs, Gen5Operand output, uint a = 0, uint n = 0, uint o = 0, bool c = false) =>
            add(Op(Gen5ShaderEncoding.Vop3, name, inputs, [output], new Gen5Vop3Control(a, n, o, c, 0, null)));
        void Vop2(string name, uint literal, Gen5Operand value, Gen5Operand output) =>
            add(Op(Gen5ShaderEncoding.Vop2, name, [new Gen5Operand(Gen5OperandKind.LiteralConstant, literal), value], [output], null));
        void Compare(string name, Gen5Operand x, Gen5Operand y) =>
            add(Op(Gen5ShaderEncoding.Vop3, name, [x, y], [selector], new Gen5Vop3Control(0, 0, 0, false, 0, mask)));
        void Select(Gen5Operand no, Gen5Operand yes, Gen5Operand output) => Vop3("VCndmaskB32", [no, yes, selector], output);
        void Finish(Gen5Operand value) => Vop3("VMulF32", [value, One], destination, 0, 0, omod, clamp);
        Gen5Operand Modified(int index, Gen5Operand scratch)
        {
            if ((((abs | neg) >> index) & 1) == 0) return sources[index];
            Vop3("VMulF32", [sources[index], One], scratch, (abs >> index) & 1, (neg >> index) & 1);
            return scratch;
        }

        switch (original.Opcode)
        {
            case Prefix + "MulLegacyF32":
            case Prefix + "MacLegacyF32":
            case Prefix + "MadLegacyF32":
            {
                var mul = original.Opcode == Prefix + "MulLegacyF32";
                Vop3("VMulF32", [sources[0], sources[1]], t0, abs & 3, neg & 3, mul ? omod : 0, mul && clamp);
                Compare("VCmpEqF32", Zero, sources[0]);
                Select(t0, Zero, t0);
                Compare("VCmpEqF32", Zero, sources[1]);
                if (mul)
                {
                    Select(t0, Zero, destination);
                    return true;
                }
                Select(t0, Zero, t0);
                var mad = original.Opcode == Prefix + "MadLegacyF32";
                Vop3("VAddF32", [t0, mad ? sources[2] : destination], destination,
                    mad ? ((abs >> 2) & 1) << 1 : 0, mad ? ((neg >> 2) & 1) << 1 : 0, omod, clamp);
                return true;
            }
            case Prefix + "MinLegacyF32":
            case Prefix + "MaxLegacyF32":
            {
                var x = Modified(0, t0);
                var y = Modified(1, t1);
                Compare(original.Opcode == Prefix + "MinLegacyF32" ? "VCmpLtF32" : "VCmpGeF32", x, y);
                Select(y, x, finish ? t0 : destination);
                if (finish) Finish(t0);
                return true;
            }
            case Prefix + "RcpLegacyF32":
            case Prefix + "RsqLegacyF32":
            {
                var x = Modified(0, t1);
                Vop3(original.Opcode == Prefix + "RcpLegacyF32" ? "VRcpF32" : "VRsqF32", [x], t0);
                Compare("VCmpEqF32", Zero, x);
                Select(t0, x, finish ? t0 : destination);
                if (finish) Finish(t0);
                return true;
            }
            case Prefix + "RcpClampF32":
            case Prefix + "RsqClampF32":
            case Prefix + "LogClampF32":
            {
                var operation = original.Opcode switch
                {
                    Prefix + "RcpClampF32" => "VRcpF32",
                    Prefix + "RsqClampF32" => "VRsqF32",
                    _ => "VLogF32",
                };
                Vop3(operation, [sources[0]], t0, abs & 1, neg & 1);
                if (operation != "VLogF32") Vop2("VMinF32", FloatMax, t0, t0);
                Vop2("VMaxF32", NegativeFloatMax, t0, finish ? t0 : destination);
                if (finish) Finish(t0);
                return true;
            }
            case Prefix + "MulHiI32I24":
                Vop3("VBfeI32", [sources[0], Zero, TwentyFour], t0);
                Vop3("VBfeI32", [sources[1], Zero, TwentyFour], t1);
                Vop3("VMulHiI32", [t0, t1], destination);
                return true;
        }

        if (!original.Opcode.StartsWith(CompareExec, StringComparison.Ordinal))
            return false;
        var vop3 = original.Encoding == Gen5ShaderEncoding.Vop3;
        if (vop3 && control?.ScalarDestination is null)
            return false;
        add(original with { Opcode = "VCmp" + original.Opcode[CompareExec.Length..], AddressOffset = original.ProgramOffset });
        add(Op(Gen5ShaderEncoding.Sop1, "SMovB64", [Gen5Operand.Scalar(vop3 ? control!.ScalarDestination!.Value : Vcc)],
            [Gen5Operand.Scalar(Exec)], null));
        return true;
    }
}
