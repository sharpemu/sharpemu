// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public static partial class GcnShaderDecoder
{
    private static bool TryDecodeVector3(ReadOnlySpan<byte> bytes, uint pc, uint word,
        out Gen5ShaderInstruction instruction, out string error)
    {
        instruction = default!;
        error = "";
        if ((uint)bytes.Length - pc < 8)
        {
            error = $"truncated-gcn-vop3 pc=0x{pc:X}";
            return false;
        }
        var extra = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)pc + 4)..]);
        var opcode = (word >> 17) & 511;
        var name = opcode switch
        {
            < 0x100 => VectorOpcode(Gen5ShaderEncoding.Vopc, opcode),
            >= 0x100 and < 0x140 => VectorOpcode(Gen5ShaderEncoding.Vop2, opcode - 0x100),
            >= 0x180 => VectorOpcode(Gen5ShaderEncoding.Vop1, opcode - 0x180),
            0x140 => GcnAluLowering.Prefix + "MadLegacyF32",
            0x141 => "VMadF32", 0x142 => "VMadI32I24", 0x143 => "VMadU32U24",
            0x144 => "VCubeidF32", 0x145 => "VCubescF32", 0x146 => "VCubetcF32", 0x147 => "VCubemaF32",
            0x148 => "VBfeU32", 0x149 => "VBfeI32", 0x14A => "VBfiB32", 0x14B => "VFmaF32", 0x14C => "VFmaF64",
            0x14E => "VAlignbitB32", 0x14F => "VAlignbyteB32",
            0x151 => "VMin3F32", 0x152 => "VMin3I32", 0x153 => "VMin3U32",
            0x154 => "VMax3F32", 0x155 => "VMax3I32", 0x156 => "VMax3U32",
            0x157 => "VMed3F32", 0x158 => "VMed3I32", 0x159 => "VMed3U32",
            0x15D => "VSadU32", 0x15E => "VCvtPkU8F32",
            0x161 => "VLshlrevB64", 0x162 => "VLshrrevB64",
            0x164 => "VAddF64", 0x165 => "VMulF64", 0x166 => "VMinF64", 0x167 => "VMaxF64", 0x168 => "VLdexpF64",
            0x169 => "VMulLoU32", 0x16A => "VMulHiU32", 0x16B => "VMulLoI32", 0x16C => "VMulHiI32",
            0x176 => "VMadU64U32",
            _ => "",
        };
        // MADK and lane transfers have no GFX6/7 VOP3 encoding.
        if (name.Length == 0 || opcode is 0x101 or 0x102 or 0x120 or 0x121)
        {
            error = $"unsupported-gcn-instruction VOP3 pc=0x{pc:X} op=0x{opcode:X}";
            return false;
        }
        var compare = opcode < 0x100;
        var vop3b = opcode is >= 0x125 and <= 0x12A or 0x176;
        var count = opcode >= 0x180 ? 1
            : name == "VCndmaskB32" || opcode is >= 0x128 and <= 0x12A or >= 0x140 and <= 0x160 or 0x176 ? 3 : 2;
        var sources = new Gen5Operand[count];
        for (var index = 0; index < count; index++)
        {
            var source = (extra >> (index * 9)) & 511;
            if (source is >= 209 and <= 239 or >= 249 and <= 255)
            {
                error = $"unsupported-gcn-vop3-source pc=0x{pc:X} source={source}";
                return false;
            }
            sources[index] = Gen5Operand.Source(source, null);
        }
        if (opcode is 0x161 or 0x162)
            sources = [sources[1], sources[0]];
        uint? scalarDestination = compare ? word & 255 : vop3b ? (word >> 8) & 127 : null;
        var destinations = compare ? new[] { Gen5Operand.Scalar(scalarDestination!.Value) }
            : new[] { Gen5Operand.Vector(word & 255) };
        instruction = new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Vop3, name, [word, extra], sources, destinations,
            new Gen5Vop3Control(vop3b ? 0 : (word >> 8) & 7, (extra >> 29) & 7,
                (extra >> 27) & 3, !vop3b && (word & (1u << 11)) != 0, 0, scalarDestination));
        return true;
    }
}
